using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Windows.IPC.Security
{
    /// <summary>
    /// 握手协议
    /// 在连接建立后、消息交换前执行，用于：
    /// 1. 协议版本协商
    /// 2. 安全能力交换
    /// 3. 应用层认证（可选）
    /// 
    /// 握手流程：
    /// Client → Server: HandshakeInit { version, capabilities }
    /// Server → Client: HandshakeResponse { version, accepted, reason? }
    /// [如果需要认证]
    /// Server → Client: AuthChallenge
    /// Client → Server: AuthResponse
    /// Server → Client: AuthResult
    /// 
    /// 所有握手消息使用简单文本协议（不在 MessageFrame 内），
    /// 格式：[长度 4B][JSON 数据]
    /// </summary>
    public class HandshakeProtocol
    {
        /// <summary>
        /// 当前协议版本
        /// </summary>
        public const int CurrentProtocolVersion = 2;

        /// <summary>
        /// 最低支持的协议版本
        /// </summary>
        public const int MinSupportedVersion = 1;

        private readonly ILogger? _logger;

        public HandshakeProtocol(ILogger? logger = null)
        {
            _logger = logger;
        }

        /// <summary>
        /// 服务端执行握手
        /// </summary>
        public async Task<HandshakeResult> PerformServerHandshakeAsync(
            Stream stream,
            SecurityOptions securityOptions,
            IAuthenticationProtocol? authProtocol,
            CancellationToken cancellationToken = default)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (securityOptions == null) throw new ArgumentNullException(nameof(securityOptions));

            try
            {
                // 1. 读取客户端握手初始化消息
                var initMessage = await ReadHandshakeMessageAsync<HandshakeInitMessage>(stream, cancellationToken)
                    .ConfigureAwait(false);

                _logger?.LogDebug("Received handshake init: Version={Version}, Capabilities={Caps}",
                    initMessage.Version, initMessage.Capabilities);

                // 2. 版本协商
                int negotiatedVersion = NegotiateVersion(initMessage.Version);

                // 3. 确定安全能力
                var capabilities = NegotiateCapabilities(initMessage.Capabilities, securityOptions);

                // 4. 发送握手响应
                var response = new HandshakeResponseMessage
                {
                    Version = negotiatedVersion,
                    Accepted = true,
                    Capabilities = capabilities,
                    RequiresAuthentication = securityOptions.EnableAuthentication &&
                                             securityOptions.EncryptionMode != EncryptionMode.NegotiateStream
                };

                await WriteHandshakeMessageAsync(stream, response, cancellationToken).ConfigureAwait(false);

                _logger?.LogInformation(
                    "Handshake completed: Version={Version}, Capabilities={Caps}, AuthRequired={AuthRequired}",
                    negotiatedVersion, capabilities, response.RequiresAuthentication);

                // 5. 如果需要认证，执行认证协议
                AuthenticationResult? authResult = null;
                if (response.RequiresAuthentication && authProtocol != null)
                {
                    authResult = await authProtocol.AuthenticateServerAsync(stream, cancellationToken)
                        .ConfigureAwait(false);

                    if (!authResult.IsAuthenticated)
                    {
                        _logger?.LogWarning("Client authentication failed: {Reason}", authResult.RejectReason);
                        return new HandshakeResult
                        {
                            Succeeded = false,
                            FailureReason = authResult.RejectReason ?? "Authentication failed",
                            NegotiatedVersion = negotiatedVersion,
                            Capabilities = capabilities
                        };
                    }

                    _logger?.LogInformation("Client authenticated: {Identity}", authResult.ClientIdentifier);
                }

                return new HandshakeResult
                {
                    Succeeded = true,
                    NegotiatedVersion = negotiatedVersion,
                    Capabilities = capabilities,
                    AuthenticationResult = authResult,
                    UseV2FrameFormat = capabilities.HasFlag(SecurityCapabilities.V2FrameFormat)
                };
            }
            catch (Exception ex) when (!(ex is Exceptions.SecurityException))
            {
                _logger?.LogError(ex, "Handshake failed");

                // 尝试发送失败响应
                try
                {
                    var failResponse = new HandshakeResponseMessage
                    {
                        Version = CurrentProtocolVersion,
                        Accepted = false,
                        Reason = ex.Message
                    };
                    await WriteHandshakeMessageAsync(stream, failResponse, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // 如果连失败响应都发不出去，忽略
                }

                return new HandshakeResult
                {
                    Succeeded = false,
                    FailureReason = ex.Message
                };
            }
        }

        /// <summary>
        /// 客户端执行握手
        /// </summary>
        public async Task<HandshakeResult> PerformClientHandshakeAsync(
            Stream stream,
            SecurityOptions securityOptions,
            IAuthenticationProtocol? authProtocol,
            CancellationToken cancellationToken = default)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (securityOptions == null) throw new ArgumentNullException(nameof(securityOptions));

            try
            {
                // 1. 发送握手初始化消息
                var capabilities = DetermineClientCapabilities(securityOptions);

                var initMessage = new HandshakeInitMessage
                {
                    Version = CurrentProtocolVersion,
                    MinVersion = MinSupportedVersion,
                    Capabilities = capabilities
                };

                await WriteHandshakeMessageAsync(stream, initMessage, cancellationToken).ConfigureAwait(false);

                _logger?.LogDebug("Sent handshake init: Version={Version}, Capabilities={Caps}",
                    initMessage.Version, initMessage.Capabilities);

                // 2. 读取服务端响应
                var response = await ReadHandshakeMessageAsync<HandshakeResponseMessage>(stream, cancellationToken)
                    .ConfigureAwait(false);

                if (!response.Accepted)
                {
                    _logger?.LogWarning("Handshake rejected: {Reason}", response.Reason);
                    return new HandshakeResult
                    {
                        Succeeded = false,
                        FailureReason = response.Reason ?? "Server rejected handshake",
                        NegotiatedVersion = response.Version
                    };
                }

                _logger?.LogInformation(
                    "Handshake accepted: Version={Version}, Capabilities={Caps}, AuthRequired={AuthRequired}",
                    response.Version, response.Capabilities, response.RequiresAuthentication);

                // 3. 如果需要认证，执行认证协议
                AuthenticationResult? authResult = null;
                if (response.RequiresAuthentication && authProtocol != null)
                {
                    authResult = await authProtocol.AuthenticateClientAsync(stream, cancellationToken)
                        .ConfigureAwait(false);

                    if (!authResult.IsAuthenticated)
                    {
                        _logger?.LogWarning("Authentication failed: {Reason}", authResult.RejectReason);
                        return new HandshakeResult
                        {
                            Succeeded = false,
                            FailureReason = authResult.RejectReason ?? "Authentication failed",
                            NegotiatedVersion = response.Version,
                            Capabilities = response.Capabilities
                        };
                    }

                    _logger?.LogInformation("Authenticated successfully: {Identity}", authResult.ClientIdentifier);
                }

                return new HandshakeResult
                {
                    Succeeded = true,
                    NegotiatedVersion = response.Version,
                    Capabilities = response.Capabilities,
                    AuthenticationResult = authResult,
                    UseV2FrameFormat = response.Capabilities.HasFlag(SecurityCapabilities.V2FrameFormat)
                };
            }
            catch (Exception ex) when (!(ex is Exceptions.SecurityException))
            {
                _logger?.LogError(ex, "Client handshake failed");
                return new HandshakeResult
                {
                    Succeeded = false,
                    FailureReason = ex.Message
                };
            }
        }

        #region Version & Capability Negotiation

        private int NegotiateVersion(int clientVersion)
        {
            // 选择双方都支持的最高版本
            int negotiated = Math.Min(clientVersion, CurrentProtocolVersion);

            if (negotiated < MinSupportedVersion)
            {
                throw new Exceptions.SecurityException(
                    $"Incompatible protocol version: client={clientVersion}, " +
                    $"server min={MinSupportedVersion}, server max={CurrentProtocolVersion}");
            }

            return negotiated;
        }

        private SecurityCapabilities NegotiateCapabilities(SecurityCapabilities clientCaps, SecurityOptions options)
        {
            var result = SecurityCapabilities.None;

            // v2 帧格式（序列号 + HMAC）
            if (clientCaps.HasFlag(SecurityCapabilities.V2FrameFormat) && options.UseV2FrameFormat)
            {
                result |= SecurityCapabilities.V2FrameFormat;
            }

            // 帧完整性
            if (clientCaps.HasFlag(SecurityCapabilities.FrameIntegrity) && options.EnableFrameIntegrity)
            {
                result |= SecurityCapabilities.FrameIntegrity;
            }

            // 重放防护
            if (clientCaps.HasFlag(SecurityCapabilities.ReplayProtection) && options.EnableReplayProtection)
            {
                result |= SecurityCapabilities.ReplayProtection;
            }

            // 应用层认证
            if (options.EnableAuthentication && options.EncryptionMode != EncryptionMode.NegotiateStream)
            {
                result |= SecurityCapabilities.ApplicationAuth;
            }

            return result;
        }

        private SecurityCapabilities DetermineClientCapabilities(SecurityOptions options)
        {
            var caps = SecurityCapabilities.None;

            if (options.UseV2FrameFormat)
            {
                caps |= SecurityCapabilities.V2FrameFormat;
            }

            if (options.EnableFrameIntegrity)
            {
                caps |= SecurityCapabilities.FrameIntegrity;
            }

            if (options.EnableReplayProtection)
            {
                caps |= SecurityCapabilities.ReplayProtection;
            }

            // 客户端总是声明支持应用层认证（如果配置了）
            if (options.EnableAuthentication)
            {
                caps |= SecurityCapabilities.ApplicationAuth;
            }

            return caps;
        }

        #endregion

        #region Message Serialization

        /// <summary>
        /// 握手消息使用简单的文本协议
        /// 格式：[长度 4B][key=value\n...]
        /// </summary>
        private static async Task WriteHandshakeMessageAsync<T>(
            Stream stream, T message, CancellationToken cancellationToken)
        {
            var text = SerializeMessage(message);
            var bytes = Encoding.UTF8.GetBytes(text);

            var lengthBuffer = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(lengthBuffer, bytes.Length);

            await stream.WriteAsync(lengthBuffer, 0, 4, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private static async Task<T> ReadHandshakeMessageAsync<T>(
            Stream stream, CancellationToken cancellationToken) where T : new()
        {
            // 读取长度
            var lengthBuffer = new byte[4];
            await ReadExactAsync(stream, lengthBuffer, 0, 4, cancellationToken).ConfigureAwait(false);
            var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(lengthBuffer);

            if (length <= 0 || length > 4096)
            {
                throw new Exceptions.ProtocolException($"Invalid handshake message length: {length}");
            }

            // 读取消息体
            var messageBuffer = new byte[length];
            await ReadExactAsync(stream, messageBuffer, 0, length, cancellationToken).ConfigureAwait(false);

            var text = Encoding.UTF8.GetString(messageBuffer);
            return DeserializeMessage<T>(text);
        }

        private static string SerializeMessage<T>(T message)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"type={typeof(T).Name}");

            foreach (var prop in typeof(T).GetProperties())
            {
                var value = prop.GetValue(message);
                if (value != null)
                {
                    if (value is Enum e)
                    {
                        sb.AppendLine($"{prop.Name}={Convert.ToInt32(e)}");
                    }
                    else if (value is bool b)
                    {
                        sb.AppendLine($"{prop.Name}={(b ? "1" : "0")}");
                    }
                    else
                    {
                        sb.AppendLine($"{prop.Name}={value}");
                    }
                }
            }

            return sb.ToString();
        }

        private static T DeserializeMessage<T>(string text) where T : new()
        {
            var result = new T();
            var type = typeof(T);

            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || !trimmed.Contains("="))
                {
                    continue;
                }

                var eqIndex = trimmed.IndexOf('=');
                var key = trimmed.Substring(0, eqIndex).Trim();
                var value = trimmed.Substring(eqIndex + 1).Trim();

                if (key == "type") continue;

                var prop = type.GetProperty(key);
                if (prop == null || !prop.CanWrite) continue;

                try
                {
                    if (prop.PropertyType == typeof(int))
                    {
                        prop.SetValue(result, int.Parse(value));
                    }
                    else if (prop.PropertyType == typeof(bool))
                    {
                        prop.SetValue(result, value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));
                    }
                    else if (prop.PropertyType == typeof(string))
                    {
                        prop.SetValue(result, value);
                    }
                    else if (prop.PropertyType == typeof(SecurityCapabilities))
                    {
                        prop.SetValue(result, (SecurityCapabilities)int.Parse(value));
                    }
                    else if (prop.PropertyType.IsEnum)
                    {
                        prop.SetValue(result, Enum.ToObject(prop.PropertyType, int.Parse(value)));
                    }
                }
                catch
                {
                    // 忽略解析错误，使用默认值
                }
            }

            return result;
        }

        private static async Task ReadExactAsync(
            Stream stream, byte[] buffer, int offset, int count,
            CancellationToken cancellationToken)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int bytesRead = await stream.ReadAsync(buffer, offset + totalRead, count - totalRead, cancellationToken)
                    .ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    throw new EndOfStreamException(
                        $"Unexpected end of stream. Expected {count} bytes, got {totalRead}.");
                }
                totalRead += bytesRead;
            }
        }

        #endregion
    }

    #region Handshake Message Types

    /// <summary>
    /// 握手初始化消息（客户端发送）
    /// </summary>
    public class HandshakeInitMessage
    {
        /// <summary>
        /// 客户端请求的协议版本
        /// </summary>
        public int Version { get; set; } = HandshakeProtocol.CurrentProtocolVersion;

        /// <summary>
        /// 客户端最低支持的协议版本
        /// </summary>
        public int MinVersion { get; set; } = HandshakeProtocol.MinSupportedVersion;

        /// <summary>
        /// 客户端支持的安全能力
        /// </summary>
        public SecurityCapabilities Capabilities { get; set; } = SecurityCapabilities.None;
    }

    /// <summary>
    /// 握手响应消息（服务端发送）
    /// </summary>
    public class HandshakeResponseMessage
    {
        /// <summary>
        /// 服务端选择的协议版本
        /// </summary>
        public int Version { get; set; }

        /// <summary>
        /// 是否接受握手
        /// </summary>
        public bool Accepted { get; set; }

        /// <summary>
        /// 拒绝原因（如果 Accepted=false）
        /// </summary>
        public string? Reason { get; set; }

        /// <summary>
        /// 协商后的安全能力
        /// </summary>
        public SecurityCapabilities Capabilities { get; set; } = SecurityCapabilities.None;

        /// <summary>
        /// 是否需要应用层认证
        /// </summary>
        public bool RequiresAuthentication { get; set; }
    }

    #endregion

    #region Handshake Result & Capabilities

    /// <summary>
    /// 握手结果
    /// </summary>
    public class HandshakeResult
    {
        /// <summary>
        /// 握手是否成功
        /// </summary>
        public bool Succeeded { get; set; }

        /// <summary>
        /// 失败原因
        /// </summary>
        public string? FailureReason { get; set; }

        /// <summary>
        /// 协商后的协议版本
        /// </summary>
        public int NegotiatedVersion { get; set; } = 1;

        /// <summary>
        /// 协商后的安全能力
        /// </summary>
        public SecurityCapabilities Capabilities { get; set; }

        /// <summary>
        /// 认证结果（如果执行了认证）
        /// </summary>
        public AuthenticationResult? AuthenticationResult { get; set; }

        /// <summary>
        /// 是否使用 v2 帧格式
        /// </summary>
        public bool UseV2FrameFormat { get; set; }
    }

    /// <summary>
    /// 安全能力标志
    /// </summary>
    [Flags]
    public enum SecurityCapabilities
    {
        /// <summary>
        /// 无安全能力
        /// </summary>
        None = 0,

        /// <summary>
        /// 支持 v2 帧格式（带序列号和 HMAC）
        /// </summary>
        V2FrameFormat = 0x0001,

        /// <summary>
        /// 支持帧完整性校验（HMAC-SHA256）
        /// </summary>
        FrameIntegrity = 0x0002,

        /// <summary>
        /// 支持重放防护（序列号校验）
        /// </summary>
        ReplayProtection = 0x0004,

        /// <summary>
        /// 支持应用层认证（挑战-响应）
        /// </summary>
        ApplicationAuth = 0x0008
    }

    #endregion
}
