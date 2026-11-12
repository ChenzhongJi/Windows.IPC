using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.IPC.Exceptions;

namespace Windows.IPC.Security
{
    /// <summary>
    /// 安全流工厂
    /// 根据 SecurityOptions 创建合适的安全流包装
    /// </summary>
    public static class SecureStreamFactory
    {
        /// <summary>
        /// 为服务端创建安全流
        /// 在管道连接建立后调用，执行服务端认证/加密握手
        /// </summary>
        /// <param name="pipeStream">原始管道流</param>
        /// <param name="securityOptions">安全选项</param>
        /// <param name="logger">日志</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>安全流（如果不需要加密则返回原始流）以及安全会话上下文</returns>
        public static async Task<(Stream stream, SecuritySession session)> CreateServerStreamAsync(
            PipeStream pipeStream,
            SecurityOptions securityOptions,
            ILogger? logger = null,
            CancellationToken cancellationToken = default)
        {
            if (pipeStream == null) throw new ArgumentNullException(nameof(pipeStream));
            if (securityOptions == null) throw new ArgumentNullException(nameof(securityOptions));

            if (!securityOptions.IsSecurityEnabled)
            {
                return (pipeStream, new SecuritySession(securityOptions));
            }

            switch (securityOptions.EncryptionMode)
            {
                case EncryptionMode.NegotiateStream:
                    return await CreateNegotiateStreamServerAsync(pipeStream, securityOptions, logger, cancellationToken);

                case EncryptionMode.AesCbcHmac:
                    return await CreateAesCbcHmacStreamServerAsync(pipeStream, securityOptions, logger, cancellationToken);

                default:
                    // 仅帧安全（HMAC/序列号），无流级加密
                    return (pipeStream, new SecuritySession(securityOptions));
            }
        }

        /// <summary>
        /// 为客户端创建安全流
        /// </summary>
        public static async Task<(Stream stream, SecuritySession session)> CreateClientStreamAsync(
            PipeStream pipeStream,
            SecurityOptions securityOptions,
            ILogger? logger = null,
            CancellationToken cancellationToken = default)
        {
            if (pipeStream == null) throw new ArgumentNullException(nameof(pipeStream));
            if (securityOptions == null) throw new ArgumentNullException(nameof(securityOptions));

            if (!securityOptions.IsSecurityEnabled)
            {
                return (pipeStream, new SecuritySession(securityOptions));
            }

            switch (securityOptions.EncryptionMode)
            {
                case EncryptionMode.NegotiateStream:
                    return await CreateNegotiateStreamClientAsync(pipeStream, securityOptions, logger, cancellationToken);

                case EncryptionMode.AesCbcHmac:
                    return await CreateAesCbcHmacStreamClientAsync(pipeStream, securityOptions, logger, cancellationToken);

                default:
                    return (pipeStream, new SecuritySession(securityOptions));
            }
        }

        #region NegotiateStream

        private static async Task<(Stream stream, SecuritySession session)> CreateNegotiateStreamServerAsync(
            PipeStream pipeStream,
            SecurityOptions securityOptions,
            ILogger? logger,
            CancellationToken cancellationToken)
        {
            logger?.LogInformation("Setting up NegotiateStream for server");

            var negotiateStream = new NegotiateStream(pipeStream, true);

            try
            {
                // 映射保护级别
                var protectionLevel = MapProtectionLevel(securityOptions.ProtectionLevel);

                // 服务端认证（使用 Windows 凭据）
                await negotiateStream.AuthenticateAsServerAsync(
                    credential: null, // 使用当前 Windows 凭据
                    requiredProtectionLevel: protectionLevel,
                    requiredImpersonationLevel: TokenImpersonationLevel.Identification
                ).ConfigureAwait(false);

                logger?.LogInformation(
                    "NegotiateStream server authentication succeeded. " +
                    "IsAuthenticated={IsAuth}, IsEncrypted={IsEnc}, IsSigned={IsSigned}, " +
                    "RemoteIdentity={Identity}",
                    negotiateStream.IsAuthenticated,
                    negotiateStream.IsEncrypted,
                    negotiateStream.IsSigned,
                    negotiateStream.RemoteIdentity?.Name);

                var session = new SecuritySession(securityOptions)
                {
                    IsAuthenticated = negotiateStream.IsAuthenticated,
                    IsEncrypted = negotiateStream.IsEncrypted,
                    IsSigned = negotiateStream.IsSigned,
                    RemoteIdentity = negotiateStream.RemoteIdentity?.Name,
                    EncryptionMethod = "NegotiateStream"
                };

                return (negotiateStream, session);
            }
            catch (AuthenticationException ex)
            {
                logger?.LogError(ex, "NegotiateStream server authentication failed");
                negotiateStream.Dispose();
                throw new SecurityException($"NegotiateStream authentication failed: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "NegotiateStream server setup error");
                negotiateStream.Dispose();
                throw;
            }
        }

        private static async Task<(Stream stream, SecuritySession session)> CreateNegotiateStreamClientAsync(
            PipeStream pipeStream,
            SecurityOptions securityOptions,
            ILogger? logger,
            CancellationToken cancellationToken)
        {
            logger?.LogInformation("Setting up NegotiateStream for client");

            var negotiateStream = new NegotiateStream(pipeStream, true);

            try
            {
                var protectionLevel = MapProtectionLevel(securityOptions.ProtectionLevel);

                // 客户端认证
                await negotiateStream.AuthenticateAsClientAsync(
                    credential: null, // 使用当前 Windows 凭据
                    targetName: string.Empty, // 本机 IPC 无需 SPN
                    requiredProtectionLevel: protectionLevel,
                    allowedImpersonationLevel: TokenImpersonationLevel.Identification
                ).ConfigureAwait(false);

                logger?.LogInformation(
                    "NegotiateStream client authentication succeeded. " +
                    "IsAuthenticated={IsAuth}, IsEncrypted={IsEnc}, IsSigned={IsSigned}",
                    negotiateStream.IsAuthenticated,
                    negotiateStream.IsEncrypted,
                    negotiateStream.IsSigned);

                var session = new SecuritySession(securityOptions)
                {
                    IsAuthenticated = negotiateStream.IsAuthenticated,
                    IsEncrypted = negotiateStream.IsEncrypted,
                    IsSigned = negotiateStream.IsSigned,
                    RemoteIdentity = negotiateStream.RemoteIdentity?.Name,
                    EncryptionMethod = "NegotiateStream"
                };

                return (negotiateStream, session);
            }
            catch (AuthenticationException ex)
            {
                logger?.LogError(ex, "NegotiateStream client authentication failed");
                negotiateStream.Dispose();
                throw new SecurityException($"NegotiateStream authentication failed: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "NegotiateStream client setup error");
                negotiateStream.Dispose();
                throw;
            }
        }

        private static System.Net.Security.ProtectionLevel MapProtectionLevel(ProtectionLevel level)
        {
            return level switch
            {
                Security.ProtectionLevel.None => System.Net.Security.ProtectionLevel.None,
                Security.ProtectionLevel.Sign => System.Net.Security.ProtectionLevel.Sign,
                Security.ProtectionLevel.EncryptAndSign => System.Net.Security.ProtectionLevel.EncryptAndSign,
                _ => System.Net.Security.ProtectionLevel.EncryptAndSign
            };
        }

        #endregion

        #region AES-CBC-HMAC

        private static async Task<(Stream stream, SecuritySession session)> CreateAesCbcHmacStreamServerAsync(
            PipeStream pipeStream,
            SecurityOptions securityOptions,
            ILogger? logger,
            CancellationToken cancellationToken)
        {
            logger?.LogInformation("Setting up AES-CBC-HMAC encryption for server");

            if (securityOptions.SharedSecret == null)
            {
                throw new SecurityException("SharedSecret must be configured for AesCbcHmac encryption");
            }

            try
            {
                // 1. 生成随机盐值（16 字节）
                var salt = new byte[16];
                using (var rng = RandomNumberGenerator.Create())
                {
                    rng.GetBytes(salt);
                }

                // 2. 发送盐值给客户端（明文，盐值不需要保密）
                var saltLengthBytes = new byte[4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(saltLengthBytes, salt.Length);
                await pipeStream.WriteAsync(saltLengthBytes, 0, 4, cancellationToken).ConfigureAwait(false);
                await pipeStream.WriteAsync(salt, 0, salt.Length, cancellationToken).ConfigureAwait(false);

                // 3. 从共享密钥和盐值派生加密密钥
                var (encryptionKey, hmacKey) = FrameSecurity.DeriveKeys(securityOptions.SharedSecret, salt);

                // 4. 创建加密提供程序
                var encryptionProvider = new AesCbcHmacEncryptionProvider(encryptionKey, hmacKey);

                // 5. 创建 HMAC 密钥（用于帧完整性，使用不同的派生信息）
                var frameHmacKey = FrameSecurity.DeriveHmacKey(securityOptions.SharedSecret, salt);

                // 6. 创建安全流包装
                var secureStream = new AesCbcHmacStream(pipeStream, encryptionProvider, logger);

                // 安全清除临时密钥
                Array.Clear(encryptionKey, 0, encryptionKey.Length);
                Array.Clear(hmacKey, 0, hmacKey.Length);

                var session = new SecuritySession(securityOptions)
                {
                    IsAuthenticated = true, // 共享密钥模式，知道密钥即为认证通过
                    IsEncrypted = true,
                    IsSigned = true,
                    EncryptionMethod = "AES-256-CBC-HMAC-SHA256",
                    FrameHmacKey = frameHmacKey,
                    EncryptionProvider = encryptionProvider
                };

                logger?.LogInformation("AES-CBC-HMAC server encryption established successfully");

                return (secureStream, session);
            }
            catch (Exception ex) when (!(ex is SecurityException))
            {
                logger?.LogError(ex, "Failed to establish AES-CBC-HMAC encryption on server");
                throw new SecurityException($"AES-CBC-HMAC setup failed: {ex.Message}", ex);
            }
        }

        private static async Task<(Stream stream, SecuritySession session)> CreateAesCbcHmacStreamClientAsync(
            PipeStream pipeStream,
            SecurityOptions securityOptions,
            ILogger? logger,
            CancellationToken cancellationToken)
        {
            logger?.LogInformation("Setting up AES-CBC-HMAC encryption for client");

            if (securityOptions.SharedSecret == null)
            {
                throw new SecurityException("SharedSecret must be configured for AesCbcHmac encryption");
            }

            try
            {
                // 1. 接收服务端发来的盐值
                var saltLengthBuffer = new byte[4];
                await ReadExactAsync(pipeStream, saltLengthBuffer, 0, 4, cancellationToken).ConfigureAwait(false);
                var saltLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(saltLengthBuffer);

                if (saltLength <= 0 || saltLength > 128)
                {
                    throw new SecurityException($"Invalid salt length received: {saltLength}");
                }

                var salt = new byte[saltLength];
                await ReadExactAsync(pipeStream, salt, 0, saltLength, cancellationToken).ConfigureAwait(false);

                // 2. 从共享密钥和盐值派生加密密钥
                var (encryptionKey, hmacKey) = FrameSecurity.DeriveKeys(securityOptions.SharedSecret, salt);

                // 3. 创建加密提供程序
                var encryptionProvider = new AesCbcHmacEncryptionProvider(encryptionKey, hmacKey);

                // 4. 创建 HMAC 密钥
                var frameHmacKey = FrameSecurity.DeriveHmacKey(securityOptions.SharedSecret, salt);

                // 5. 创建安全流包装
                var secureStream = new AesCbcHmacStream(pipeStream, encryptionProvider, logger);

                // 安全清除临时密钥
                Array.Clear(encryptionKey, 0, encryptionKey.Length);
                Array.Clear(hmacKey, 0, hmacKey.Length);

                var session = new SecuritySession(securityOptions)
                {
                    IsAuthenticated = true,
                    IsEncrypted = true,
                    IsSigned = true,
                    EncryptionMethod = "AES-256-CBC-HMAC-SHA256",
                    FrameHmacKey = frameHmacKey,
                    EncryptionProvider = encryptionProvider
                };

                logger?.LogInformation("AES-CBC-HMAC client encryption established successfully");

                return (secureStream, session);
            }
            catch (Exception ex) when (!(ex is SecurityException))
            {
                logger?.LogError(ex, "Failed to establish AES-CBC-HMAC encryption on client");
                throw new SecurityException($"AES-CBC-HMAC setup failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 精确读取指定字节数
        /// </summary>
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
                        $"Unexpected end of stream while reading {count} bytes. Read {totalRead} bytes.");
                }
                totalRead += bytesRead;
            }
        }

        #endregion
    }

    /// <summary>
    /// 安全会话上下文
    /// 保存连接建立后的安全状态信息
    /// </summary>
    public class SecuritySession
    {
        private readonly SecurityOptions _options;

        /// <summary>使用指定安全选项创建会话上下文。</summary>
        /// <param name='options'>会话使用的安全选项。</param>
        public SecuritySession(SecurityOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <summary>
        /// 安全选项
        /// </summary>
        public SecurityOptions Options => _options;

        /// <summary>
        /// 是否已认证
        /// </summary>
        public bool IsAuthenticated { get; set; }

        /// <summary>
        /// 是否已加密
        /// </summary>
        public bool IsEncrypted { get; set; }

        /// <summary>
        /// 是否已签名
        /// </summary>
        public bool IsSigned { get; set; }

        /// <summary>
        /// 远端身份标识（NegotiateStream 模式为 Windows 用户名）
        /// </summary>
        public string? RemoteIdentity { get; set; }

        /// <summary>
        /// 加密方法描述
        /// </summary>
        public string? EncryptionMethod { get; set; }

        /// <summary>
        /// 帧级 HMAC 密钥（用于 v2 帧完整性校验）
        /// </summary>
        internal byte[]? FrameHmacKey { get; set; }

        /// <summary>
        /// 加密提供程序（用于帧级加密/解密）
        /// </summary>
        internal AesCbcHmacEncryptionProvider? EncryptionProvider { get; set; }

        /// <summary>
        /// 创建帧安全处理器
        /// </summary>
        public FrameSecurity CreateFrameSecurity()
        {
            bool enableHmac = _options.EnableFrameIntegrity && _options.EncryptionMode == EncryptionMode.None;
            return new FrameSecurity(
                enableHmac: enableHmac,
                hmacKey: enableHmac ? FrameHmacKey : null,
                maxSequenceGap: _options.MaxSequenceGap);
        }

        /// <summary>返回会话安全状态的文本表示。</summary>
        /// <returns>包含认证、加密、签名和远端身份信息的描述。</returns>
        public override string ToString()
        {
            return $"SecuritySession(Auth={IsAuthenticated}, Enc={IsEncrypted}({EncryptionMethod}), " +
                   $"Signed={IsSigned}, Identity={RemoteIdentity ?? "N/A"})";
        }
    }
}
