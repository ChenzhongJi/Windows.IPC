using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Windows.IPC.Security
{
    /// <summary>
    /// 认证协议接口
    /// 定义在连接建立后进行应用层认证的协议
    /// </summary>
    public interface IAuthenticationProtocol : IDisposable
    {
        /// <summary>
        /// 服务端执行认证
        /// </summary>
        /// <param name="stream">已建立连接的流</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>认证结果</returns>
        Task<AuthenticationResult> AuthenticateServerAsync(
            System.IO.Stream stream,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 客户端执行认证
        /// </summary>
        /// <param name="stream">已建立连接的流</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>认证结果</returns>
        Task<AuthenticationResult> AuthenticateClientAsync(
            System.IO.Stream stream,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// 认证结果
    /// </summary>
    public class AuthenticationResult
    {
        /// <summary>
        /// 是否认证成功
        /// </summary>
        public bool IsAuthenticated { get; set; }

        /// <summary>
        /// 拒绝原因
        /// </summary>
        public string? RejectReason { get; set; }

        /// <summary>
        /// 认证通过后的客户端标识
        /// </summary>
        public string? ClientIdentifier { get; set; }

        /// <summary>
        /// 认证通过后的角色
        /// </summary>
        public string? Role { get; set; }

        /// <summary>
        /// 自定义元数据
        /// </summary>
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();

        /// <summary>创建认证成功结果。</summary>
        /// <param name='clientIdentifier'>可选的客户端标识。</param>
        /// <param name='role'>可选的客户端角色。</param>
        /// <returns>认证成功结果。</returns>
        public static AuthenticationResult Success(string? clientIdentifier = null, string? role = null)
        {
            return new AuthenticationResult
            {
                IsAuthenticated = true,
                ClientIdentifier = clientIdentifier,
                Role = role
            };
        }

        /// <summary>创建认证拒绝结果。</summary>
        /// <param name='reason'>拒绝原因。</param>
        /// <returns>认证失败结果。</returns>
        public static AuthenticationResult Reject(string reason)
        {
            return new AuthenticationResult
            {
                IsAuthenticated = false,
                RejectReason = reason
            };
        }
    }

    /// <summary>
    /// 共享密钥 HMAC 挑战-响应认证协议
    /// 
    /// 流程：
    /// 1. Server → Client: 发送随机挑战码（32 字节）
    /// 2. Client → Server: 计算 HMAC-SHA256(sharedSecret, challenge) 并发送
    /// 3. Server: 验证 HMAC，如果匹配则认证通过
    /// 4. Server → Client: 发送认证结果
    /// 
    /// 此协议证明客户端拥有共享密钥，而不在网络上传输密钥本身
    /// </summary>
    public class SharedSecretAuthProtocol : IAuthenticationProtocol
    {
        private readonly byte[] _sharedSecret;
        private readonly string? _clientIdentifier;
        private readonly string? _role;
        private bool _disposed;

        /// <summary>
        /// 创建共享密钥认证协议
        /// </summary>
        /// <param name="sharedSecret">共享密钥（至少 16 字节）</param>
        /// <param name="clientIdentifier">客户端标识（可选，用于服务端识别客户端）</param>
        /// <param name="role">客户端角色（可选）</param>
        public SharedSecretAuthProtocol(byte[] sharedSecret, string? clientIdentifier = null, string? role = null)
        {
            if (sharedSecret == null || sharedSecret.Length < 16)
            {
                throw new ArgumentException("Shared secret must be at least 16 bytes", nameof(sharedSecret));
            }

            _sharedSecret = (byte[])sharedSecret.Clone();
            _clientIdentifier = clientIdentifier;
            _role = role;
        }

        /// <summary>
        /// 创建共享密钥认证协议（从字符串密码）
        /// </summary>
        public SharedSecretAuthProtocol(string password, string? clientIdentifier = null, string? role = null)
            : this(Encoding.UTF8.GetBytes(password ?? throw new ArgumentNullException(nameof(password))),
                   clientIdentifier, role)
        {
        }

        /// <inheritdoc />
        public async Task<AuthenticationResult> AuthenticateServerAsync(
            System.IO.Stream stream,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            try
            {
                // 1. 生成随机挑战码（32 字节）
                byte[] challenge = new byte[32];
                using (var rng = RandomNumberGenerator.Create())
                {
                    rng.GetBytes(challenge);
                }

                // 2. 发送挑战码：[挑战长度 4B][挑战数据 32B]
                var challengeLength = new byte[4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(challengeLength, challenge.Length);
                await stream.WriteAsync(challengeLength, 0, 4, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(challenge, 0, challenge.Length, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

                // 3. 计算期望的 HMAC
                byte[] expectedHmac;
                using (var hmac = new HMACSHA256(_sharedSecret))
                {
                    expectedHmac = hmac.ComputeHash(challenge);
                }

                // 4. 接收客户端的 HMAC 响应：[HMAC长度 4B][HMAC数据 32B]
                var responseLengthBuffer = new byte[4];
                await ReadExactAsync(stream, responseLengthBuffer, 0, 4, cancellationToken).ConfigureAwait(false);
                var responseLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(responseLengthBuffer);

                if (responseLength != 32)
                {
                    return AuthenticationResult.Reject($"Invalid HMAC response length: {responseLength}, expected 32");
                }

                var receivedHmac = new byte[32];
                await ReadExactAsync(stream, receivedHmac, 0, 32, cancellationToken).ConfigureAwait(false);

                // 5. 验证 HMAC（常量时间比较，防止时序攻击）
                bool hmacValid = ConstantTimeEquals(receivedHmac, expectedHmac);

                // 6. 发送认证结果：[结果标志 1B][消息长度 4B][消息 NB]
                var result = hmacValid
                    ? AuthenticationResult.Success(_clientIdentifier, _role)
                    : AuthenticationResult.Reject("HMAC verification failed: incorrect shared secret");

                await SendAuthResultAsync(stream, result, cancellationToken).ConfigureAwait(false);

                return result;
            }
            catch (Exception ex) when (!(ex is Exceptions.SecurityException))
            {
                return AuthenticationResult.Reject($"Authentication protocol error: {ex.Message}");
            }
        }

        /// <inheritdoc />
        public async Task<AuthenticationResult> AuthenticateClientAsync(
            System.IO.Stream stream,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            try
            {
                // 1. 接收服务端挑战码：[挑战长度 4B][挑战数据 NB]
                var challengeLengthBuffer = new byte[4];
                await ReadExactAsync(stream, challengeLengthBuffer, 0, 4, cancellationToken).ConfigureAwait(false);
                var challengeLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(challengeLengthBuffer);

                if (challengeLength <= 0 || challengeLength > 256)
                {
                    return AuthenticationResult.Reject($"Invalid challenge length: {challengeLength}");
                }

                var challenge = new byte[challengeLength];
                await ReadExactAsync(stream, challenge, 0, challengeLength, cancellationToken).ConfigureAwait(false);

                // 2. 计算 HMAC 响应
                byte[] hmacResponse;
                using (var hmac = new HMACSHA256(_sharedSecret))
                {
                    hmacResponse = hmac.ComputeHash(challenge);
                }

                // 3. 发送 HMAC 响应：[HMAC长度 4B][HMAC数据 32B]
                var hmacLength = new byte[4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(hmacLength, hmacResponse.Length);
                await stream.WriteAsync(hmacLength, 0, 4, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(hmacResponse, 0, hmacResponse.Length, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

                // 4. 接收服务端认证结果
                var result = await ReceiveAuthResultAsync(stream, cancellationToken).ConfigureAwait(false);

                return result;
            }
            catch (Exception ex) when (!(ex is Exceptions.SecurityException))
            {
                return AuthenticationResult.Reject($"Authentication protocol error: {ex.Message}");
            }
        }

        /// <summary>
        /// 发送认证结果
        /// 格式：[标志 1B][消息长度 4B][消息 NB]
        /// </summary>
        private static async Task SendAuthResultAsync(
            System.IO.Stream stream, AuthenticationResult result,
            CancellationToken cancellationToken)
        {
            // 标志：0x01 = 成功, 0x00 = 失败
            var flag = result.IsAuthenticated ? (byte)0x01 : (byte)0x00;
            var message = result.IsAuthenticated
                ? (result.ClientIdentifier ?? "authenticated")
                : (result.RejectReason ?? "rejected");
            var messageBytes = Encoding.UTF8.GetBytes(message);

            // 写入：[flag 1B][length 4B][message NB]
            var lengthBuffer = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(lengthBuffer, messageBytes.Length);

            var buffer = new byte[1 + 4 + messageBytes.Length];
            buffer[0] = flag;
            Buffer.BlockCopy(lengthBuffer, 0, buffer, 1, 4);
            Buffer.BlockCopy(messageBytes, 0, buffer, 5, messageBytes.Length);

            await stream.WriteAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 接收认证结果
        /// </summary>
        private static async Task<AuthenticationResult> ReceiveAuthResultAsync(
            System.IO.Stream stream, CancellationToken cancellationToken)
        {
            // 读取标志
            var flagBuffer = new byte[1];
            await ReadExactAsync(stream, flagBuffer, 0, 1, cancellationToken).ConfigureAwait(false);

            // 读取消息长度
            var lengthBuffer = new byte[4];
            await ReadExactAsync(stream, lengthBuffer, 0, 4, cancellationToken).ConfigureAwait(false);
            var messageLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(lengthBuffer);

            // 读取消息
            string message = string.Empty;
            if (messageLength > 0 && messageLength < 4096)
            {
                var messageBuffer = new byte[messageLength];
                await ReadExactAsync(stream, messageBuffer, 0, messageLength, cancellationToken).ConfigureAwait(false);
                message = Encoding.UTF8.GetString(messageBuffer);
            }

            if (flagBuffer[0] == 0x01)
            {
                return AuthenticationResult.Success(message);
            }
            else
            {
                return AuthenticationResult.Reject(message);
            }
        }

        private static async Task ReadExactAsync(
            System.IO.Stream stream, byte[] buffer, int offset, int count,
            CancellationToken cancellationToken)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int bytesRead = await stream.ReadAsync(buffer, offset + totalRead, count - totalRead, cancellationToken)
                    .ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    throw new System.IO.EndOfStreamException(
                        $"Unexpected end of stream. Expected {count} bytes, got {totalRead}.");
                }
                totalRead += bytesRead;
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoOptimization)]
        private static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int result = 0;
            for (int i = 0; i < a.Length; i++)
            {
                result |= a[i] ^ b[i];
            }
            return result == 0;
        }

        /// <summary>释放协议实例并清除其共享密钥副本。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_sharedSecret != null)
            {
                Array.Clear(_sharedSecret, 0, _sharedSecret.Length);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SharedSecretAuthProtocol));
            }
        }
    }
}
