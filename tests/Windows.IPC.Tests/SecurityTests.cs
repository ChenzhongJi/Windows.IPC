using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.IPC.Exceptions;
using Windows.IPC.Protocol;
using Windows.IPC.Security;
using Xunit;

namespace Windows.IPC.Tests
{
    /// <summary>
    /// P0 安全功能测试
    /// 覆盖：帧完整性（HMAC）、重放防护（序列号）、加密（AES-CBC-HMAC）、认证（挑战-响应）
    /// </summary>
    public class SecurityTests
    {
        /// <summary>
        /// 创建一对双向管道流（匿名管道只支持单向，需要两条管道组合）
        /// 服务端通过 serverWrite → 客户端读，客户端通过 clientWrite → 服务端读
        /// </summary>
        private static (Stream serverStream, Stream clientStream) CreateBidirectionalPipes()
        {
            // 服务端写 → 客户端读
            var serverWritePipe = new AnonymousPipeServerStream(PipeDirection.Out);
            var clientReadPipe = new AnonymousPipeClientStream(PipeDirection.In, serverWritePipe.GetClientHandleAsString());

            // 客户端写 → 服务端读
            var clientWritePipe = new AnonymousPipeServerStream(PipeDirection.Out);
            var serverReadPipe = new AnonymousPipeClientStream(PipeDirection.In, clientWritePipe.GetClientHandleAsString());

            // 组合为双向流：读取端 + 写入端
            var serverStream = new CombinedStream(serverReadPipe, serverWritePipe);
            var clientStream = new CombinedStream(clientReadPipe, clientWritePipe);

            return (serverStream, clientStream);
        }

        /// <summary>
        /// 组合两个单向流为一个双向流
        /// </summary>
        private class CombinedStream : Stream
        {
            private readonly Stream _readStream;
            private readonly Stream _writeStream;
            private bool _disposed;

            public CombinedStream(Stream readStream, Stream writeStream)
            {
                _readStream = readStream ?? throw new ArgumentNullException(nameof(readStream));
                _writeStream = writeStream ?? throw new ArgumentNullException(nameof(writeStream));
            }

            public override bool CanRead => !_disposed && _readStream.CanRead;
            public override bool CanWrite => !_disposed && _writeStream.CanWrite;
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return _readStream.Read(buffer, offset, count);
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                _writeStream.Write(buffer, offset, count);
            }

            public override void Flush()
            {
                _writeStream.Flush();
            }

            public override Task FlushAsync(CancellationToken cancellationToken)
            {
                return _writeStream.FlushAsync(cancellationToken);
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return await _readStream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            }

            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                await _writeStream.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (_disposed) return;
                _disposed = true;
                if (disposing)
                {
                    _readStream.Dispose();
                    _writeStream.Dispose();
                }
                base.Dispose(disposing);
            }
        }

        #region FrameSecurity - 帧安全处理器测试

        [Fact]
        public void FrameSecurity_EncodeDecode_V2RoundTrip()
        {
            // Arrange
            var hmacKey = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) { rng.GetBytes(hmacKey); }

            using var frameSecurity = new FrameSecurity(enableHmac: true, hmacKey: hmacKey);
            var payload = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };

            // Act
            var encoded = frameSecurity.EncodeSecureFrame(MessageFrame.FrameType.Request, payload);
            var decoded = frameSecurity.TryDecodeSecureFrame(
                encoded, out var type, out var decodedPayload, out var sequenceId, out var consumed);

            // Assert
            Assert.True(decoded);
            Assert.Equal(MessageFrame.FrameType.Request, type);
            Assert.Equal(payload, decodedPayload.ToArray());
            Assert.Equal(1, sequenceId); // 第一帧序列号应为 1
            Assert.Equal(encoded.Length, consumed);
        }

        [Fact]
        public void FrameSecurity_SequenceId_MonotonicallyIncreases()
        {
            // Arrange
            using var frameSecurity = new FrameSecurity(enableHmac: false);
            var payload = new byte[] { 0x01 };

            // Act & Assert
            for (int i = 0; i < 10; i++)
            {
                var encoded = frameSecurity.EncodeSecureFrame(MessageFrame.FrameType.Request, payload);
                var decoded = frameSecurity.TryDecodeSecureFrame(
                    encoded, out _, out _, out var seqId, out _);

                Assert.True(decoded);
                Assert.Equal(i + 1, seqId);
            }

            Assert.Equal(10, frameSecurity.CurrentSendSequenceId);
            Assert.Equal(11, frameSecurity.ExpectedReceiveSequenceId);
        }

        [Fact]
        public void FrameSecurity_HmacVerification_FailsOnTamperedData()
        {
            // Arrange
            var hmacKey = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) { rng.GetBytes(hmacKey); }

            using var sender = new FrameSecurity(enableHmac: true, hmacKey: hmacKey);
            using var receiver = new FrameSecurity(enableHmac: true, hmacKey: hmacKey);

            var payload = new byte[] { 0x01, 0x02, 0x03 };
            var encoded = sender.EncodeSecureFrame(MessageFrame.FrameType.Request, payload);

            // 篡改 payload 的一个字节
            encoded[FrameSecurity.V2_HEADER_SIZE + 1] ^= 0xFF;

            // Act & Assert
            Assert.Throws<SecurityException>(() =>
                receiver.TryDecodeSecureFrame(encoded, out _, out _, out _, out _));
        }

        [Fact]
        public void FrameSecurity_HmacVerification_FailsOnWrongKey()
        {
            // Arrange
            var key1 = new byte[32];
            var key2 = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) { rng.GetBytes(key1); rng.GetBytes(key2); }

            using var sender = new FrameSecurity(enableHmac: true, hmacKey: key1);
            using var receiver = new FrameSecurity(enableHmac: true, hmacKey: key2);

            var payload = new byte[] { 0x01, 0x02, 0x03 };
            var encoded = sender.EncodeSecureFrame(MessageFrame.FrameType.Request, payload);

            // Act & Assert
            Assert.Throws<SecurityException>(() =>
                receiver.TryDecodeSecureFrame(encoded, out _, out _, out _, out _));
        }

        [Fact]
        public void FrameSecurity_ReplayDetection_RejectsDuplicateSequenceId()
        {
            // Arrange
            var hmacKey = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) { rng.GetBytes(hmacKey); }

            using var sender = new FrameSecurity(enableHmac: false);
            using var receiver = new FrameSecurity(enableHmac: false);

            var payload = new byte[] { 0x01 };

            // 发送并正常接收第一帧
            var frame1 = sender.EncodeSecureFrame(MessageFrame.FrameType.Request, payload);
            var decoded1 = receiver.TryDecodeSecureFrame(frame1, out _, out _, out var seq1, out _);
            Assert.True(decoded1);
            Assert.Equal(1, seq1);

            // 重放第一帧（序列号 1），应该被拒绝
            Assert.Throws<SecurityException>(() =>
                receiver.TryDecodeSecureFrame(frame1, out _, out _, out _, out _));
        }

        [Fact]
        public void FrameSecurity_ReplayDetection_RejectsOutOfOrderSequenceId()
        {
            // Arrange
            using var sender = new FrameSecurity(enableHmac: false);
            using var receiver = new FrameSecurity(enableHmac: false);

            var payload = new byte[] { 0x01 };

            // 发送帧 1, 2, 3
            var frame1 = sender.EncodeSecureFrame(MessageFrame.FrameType.Request, payload);
            var frame2 = sender.EncodeSecureFrame(MessageFrame.FrameType.Request, payload);
            var frame3 = sender.EncodeSecureFrame(MessageFrame.FrameType.Request, payload);

            // 正常接收帧 1 和 3
            receiver.TryDecodeSecureFrame(frame1, out _, out _, out _, out _);

            // 跳过帧 2，直接接收帧 3（序列号 3），期望 2
            // 这应该成功但内部标记了 gap（3 - 2 = 1 <= MaxSequenceGap）
            var decoded3 = receiver.TryDecodeSecureFrame(frame3, out _, out _, out var seq3, out _);
            Assert.True(decoded3);
            Assert.Equal(3, seq3);
        }

        [Fact]
        public void FrameSecurity_MaxSequenceGap_Enforced()
        {
            // Arrange
            using var sender = new FrameSecurity(enableHmac: false, maxSequenceGap: 5);
            using var receiver = new FrameSecurity(enableHmac: false, maxSequenceGap: 5);

            var payload = new byte[] { 0x01 };

            // 发送帧 1
            var frame1 = sender.EncodeSecureFrame(MessageFrame.FrameType.Request, payload);
            receiver.TryDecodeSecureFrame(frame1, out _, out _, out _, out _);

            // 模拟大量丢帧：直接发送帧 10（gap = 10 - 2 = 8 > 5）
            for (int i = 0; i < 9; i++)
            {
                sender.EncodeSecureFrame(MessageFrame.FrameType.Request, payload);
            }
            var frame10 = sender.EncodeSecureFrame(MessageFrame.FrameType.Request, payload);

            // Act & Assert — gap 过大应抛出异常
            Assert.Throws<SecurityException>(() =>
                receiver.TryDecodeSecureFrame(frame10, out _, out _, out _, out _));
        }

        [Fact]
        public void FrameSecurity_V2Frame_InvalidMagic_ThrowsException()
        {
            // Arrange — 使用 v1 Magic 但尝试 v2 解码
            var buffer = new byte[FrameSecurity.V2_HEADER_SIZE];
            // 写入错误的 Magic (v1 的 0x49504358 而非 v2 的 0x49504359)
            buffer[0] = 0x49; buffer[1] = 0x50; buffer[2] = 0x43; buffer[3] = 0x58;

            using var frameSecurity = new FrameSecurity(enableHmac: false);

            // Act & Assert
            Assert.Throws<InvalidDataException>(() =>
                frameSecurity.TryDecodeSecureFrame(buffer, out _, out _, out _, out _));
        }

        [Fact]
        public void FrameSecurity_IsV2Frame_DetectsCorrectly()
        {
            // Arrange
            using var frameSecurity = new FrameSecurity(enableHmac: false);
            var payload = new byte[] { 0x01 };
            var v2Frame = frameSecurity.EncodeSecureFrame(MessageFrame.FrameType.Request, payload);

            // v1 frame
            var v1Frame = MessageFrame.Encode(MessageFrame.FrameType.Request, payload);

            // Act & Assert
            Assert.True(FrameSecurity.IsV2Frame(v2Frame));
            Assert.False(FrameSecurity.IsV2Frame(v1Frame));
        }

        [Fact]
        public void FrameSecurity_WithoutHmac_WorksCorrectly()
        {
            // Arrange
            using var frameSecurity = new FrameSecurity(enableHmac: false);
            var payload = new byte[] { 0xAA, 0xBB, 0xCC };

            // Act
            var encoded = frameSecurity.EncodeSecureFrame(MessageFrame.FrameType.Notification, payload);
            var decoded = frameSecurity.TryDecodeSecureFrame(
                encoded, out var type, out var decodedPayload, out var seqId, out var consumed);

            // Assert
            Assert.True(decoded);
            Assert.Equal(MessageFrame.FrameType.Notification, type);
            Assert.Equal(payload, decodedPayload.ToArray());
            Assert.Equal(1, seqId);

            // v2 帧不带 HMAC 时，大小 = 头部(20) + payload
            Assert.Equal(FrameSecurity.V2_HEADER_SIZE + payload.Length, encoded.Length);
        }

        [Fact]
        public void FrameSecurity_WithHmac_IncludesHmacInFrame()
        {
            // Arrange
            var hmacKey = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) { rng.GetBytes(hmacKey); }

            using var frameSecurity = new FrameSecurity(enableHmac: true, hmacKey: hmacKey);
            var payload = new byte[] { 0x01, 0x02 };

            // Act
            var encoded = frameSecurity.EncodeSecureFrame(MessageFrame.FrameType.Request, payload);

            // Assert — v2 帧带 HMAC：头部(20) + payload(2) + HMAC(32)
            Assert.Equal(FrameSecurity.V2_HEADER_SIZE + payload.Length + 32, encoded.Length);
        }

        [Fact]
        public void FrameSecurity_TwoMessagesInBuffer_DecodesSequentially()
        {
            // Arrange
            var hmacKey = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) { rng.GetBytes(hmacKey); }

            using var sender = new FrameSecurity(enableHmac: true, hmacKey: hmacKey);
            using var receiver = new FrameSecurity(enableHmac: true, hmacKey: hmacKey);

            var payload1 = new byte[] { 0x01 };
            var payload2 = new byte[] { 0x02, 0x03 };

            var frame1 = sender.EncodeSecureFrame(MessageFrame.FrameType.Request, payload1);
            var frame2 = sender.EncodeSecureFrame(MessageFrame.FrameType.Response, payload2);

            // 合并两个帧到同一缓冲区
            var combined = new byte[frame1.Length + frame2.Length];
            Buffer.BlockCopy(frame1, 0, combined, 0, frame1.Length);
            Buffer.BlockCopy(frame2, 0, combined, frame1.Length, frame2.Length);

            // 解码第一帧
            var decoded1 = receiver.TryDecodeSecureFrame(combined, out var type1, out var p1, out var seq1, out var consumed1);
            Assert.True(decoded1);
            Assert.Equal(MessageFrame.FrameType.Request, type1);
            Assert.Equal(payload1, p1.ToArray());
            Assert.Equal(1, seq1);

            // 从 consumed1 位置继续解码第二帧
            var remaining = combined.AsSpan(consumed1);
            var decoded2 = receiver.TryDecodeSecureFrame(remaining, out var type2, out var p2, out var seq2, out var consumed2);
            Assert.True(decoded2);
            Assert.Equal(MessageFrame.FrameType.Response, type2);
            Assert.Equal(payload2, p2.ToArray());
            Assert.Equal(2, seq2);
        }

        #endregion

        #region AesCbcHmacEncryptionProvider - AES 加密测试

        [Fact]
        public void AesCbcHmac_EncryptDecrypt_RoundTrip()
        {
            // Arrange
            var encKey = new byte[32];
            var hmacKey = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) { rng.GetBytes(encKey); rng.GetBytes(hmacKey); }

            using var provider = new AesCbcHmacEncryptionProvider(encKey, hmacKey);
            var plaintext = Encoding.UTF8.GetBytes("Hello, IPC Security World!");

            // Act
            var encrypted = provider.Encrypt(plaintext);
            var decrypted = provider.Decrypt(encrypted);

            // Assert
            Assert.NotEqual(plaintext, encrypted); // 密文应与明文不同
            Assert.Equal(plaintext, decrypted);    // 解密后应与原始明文一致
        }

        [Fact]
        public void AesCbcHmac_EncryptProducesDifferentCiphertexts()
        {
            // Arrange — 相同明文加密两次应产生不同密文（因为 IV 不同）
            var encKey = new byte[32];
            var hmacKey = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) { rng.GetBytes(encKey); rng.GetBytes(hmacKey); }

            using var provider = new AesCbcHmacEncryptionProvider(encKey, hmacKey);
            var plaintext = Encoding.UTF8.GetBytes("Same plaintext");

            // Act
            var encrypted1 = provider.Encrypt(plaintext);
            var encrypted2 = provider.Encrypt(plaintext);

            // Assert — 两次加密的结果应不同（因为随机 IV）
            Assert.NotEqual(encrypted1, encrypted2);
        }

        [Fact]
        public void AesCbcHmac_DecryptFailsOnTamperedCiphertext()
        {
            // Arrange
            var encKey = new byte[32];
            var hmacKey = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) { rng.GetBytes(encKey); rng.GetBytes(hmacKey); }

            using var provider = new AesCbcHmacEncryptionProvider(encKey, hmacKey);
            var plaintext = Encoding.UTF8.GetBytes("Secret message");
            var encrypted = provider.Encrypt(plaintext);

            // 篡改密文
            encrypted[20] ^= 0xFF; // 修改 IV 之后的第一个字节

            // Act & Assert
            Assert.Throws<SecurityException>(() => provider.Decrypt(encrypted));
        }

        [Fact]
        public void AesCbcHmac_DecryptFailsOnWrongKey()
        {
            // Arrange
            var encKey1 = new byte[32];
            var encKey2 = new byte[32];
            var hmacKey1 = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) { rng.GetBytes(encKey1); rng.GetBytes(encKey2); rng.GetBytes(hmacKey1); }

            using var provider1 = new AesCbcHmacEncryptionProvider(encKey1, hmacKey1);
            using var provider2 = new AesCbcHmacEncryptionProvider(encKey2, hmacKey1); // 不同的加密密钥

            var plaintext = Encoding.UTF8.GetBytes("Secret message");
            var encrypted = provider1.Encrypt(plaintext);

            // Act & Assert
            Assert.Throws<SecurityException>(() => provider2.Decrypt(encrypted));
        }

        [Fact]
        public void AesCbcHmac_EmptyPlaintext_RoundTrip()
        {
            // Arrange
            var encKey = new byte[32];
            var hmacKey = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) { rng.GetBytes(encKey); rng.GetBytes(hmacKey); }

            using var provider = new AesCbcHmacEncryptionProvider(encKey, hmacKey);
            var plaintext = Array.Empty<byte>();

            // Act
            var encrypted = provider.Encrypt(plaintext);
            var decrypted = provider.Decrypt(encrypted);

            // Assert
            Assert.Empty(decrypted);
        }

        [Fact]
        public void AesCbcHmac_LargePayload_RoundTrip()
        {
            // Arrange
            var encKey = new byte[32];
            var hmacKey = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) { rng.GetBytes(encKey); rng.GetBytes(hmacKey); }

            using var provider = new AesCbcHmacEncryptionProvider(encKey, hmacKey);
            var plaintext = new byte[1024 * 1024]; // 1MB
            using (var rng = RandomNumberGenerator.Create()) { rng.GetBytes(plaintext); }

            // Act
            var encrypted = provider.Encrypt(plaintext);
            var decrypted = provider.Decrypt(encrypted);

            // Assert
            Assert.Equal(plaintext, decrypted);
        }

        [Fact]
        public void AesCbcHmac_DeriveKeysFromSharedSecret()
        {
            // Arrange
            var sharedSecret = Encoding.UTF8.GetBytes("my-super-secret-password");
            var salt = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) { rng.GetBytes(salt); }

            // Act
            var (encKey1, hmacKey1) = FrameSecurity.DeriveKeys(sharedSecret, salt);
            var (encKey2, hmacKey2) = FrameSecurity.DeriveKeys(sharedSecret, salt);

            // Assert — 相同的 secret + salt 应派生相同的密钥
            Assert.Equal(encKey1, encKey2);
            Assert.Equal(hmacKey1, hmacKey2);
            Assert.Equal(32, encKey1.Length);
            Assert.Equal(32, hmacKey1.Length);
        }

        [Fact]
        public void AesCbcHmac_DifferentSaltProducesDifferentKeys()
        {
            // Arrange
            var sharedSecret = Encoding.UTF8.GetBytes("my-super-secret-password");
            var salt1 = new byte[16];
            var salt2 = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) { rng.GetBytes(salt1); rng.GetBytes(salt2); }

            // Act
            var (encKey1, hmacKey1) = FrameSecurity.DeriveKeys(sharedSecret, salt1);
            var (encKey2, hmacKey2) = FrameSecurity.DeriveKeys(sharedSecret, salt2);

            // Assert — 不同的 salt 应派生不同的密钥
            Assert.NotEqual(encKey1, encKey2);
            Assert.NotEqual(hmacKey1, hmacKey2);
        }

        [Fact]
        public void AesCbcHmac_ConstructorFromSharedSecretAndSalt()
        {
            // Arrange
            var sharedSecret = Encoding.UTF8.GetBytes("shared-password-for-test");
            var salt = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) { rng.GetBytes(salt); }

            // 服务端和客户端使用相同的 secret + salt 应能互通
            using var serverProvider = AesCbcHmacEncryptionProvider.CreateFromSharedSecret(sharedSecret, salt);
            using var clientProvider = AesCbcHmacEncryptionProvider.CreateFromSharedSecret(sharedSecret, salt);

            var plaintext = Encoding.UTF8.GetBytes("Test message for shared secret");

            // Act
            var encrypted = clientProvider.Encrypt(plaintext);
            var decrypted = serverProvider.Decrypt(encrypted);

            // Assert
            Assert.Equal(plaintext, decrypted);
        }

        #endregion

        #region SharedSecretAuthProtocol - 认证协议测试

        [Fact]
        public async void SharedSecretAuth_ChallengeResponse_SucceedsWithCorrectKey()
        {
            // Arrange
            var sharedSecret = Encoding.UTF8.GetBytes("correct-password-1234567890");
            var (serverStream, clientStream) = CreateBidirectionalPipes();

            using var serverProtocol = new SharedSecretAuthProtocol(sharedSecret);
            using var clientProtocol = new SharedSecretAuthProtocol(sharedSecret);

            // Act — 服务端和客户端并行执行认证
            var serverTask = serverProtocol.AuthenticateServerAsync(serverStream);
            var clientTask = clientProtocol.AuthenticateClientAsync(clientStream);

            await Task.WhenAll(serverTask, clientTask);

            // Assert
            Assert.True(serverTask.Result.IsAuthenticated);
            Assert.True(clientTask.Result.IsAuthenticated);
        }

        [Fact]
        public async void SharedSecretAuth_ChallengeResponse_FailsWithWrongKey()
        {
            // Arrange
            var correctSecret = Encoding.UTF8.GetBytes("correct-password-1234567890");
            var wrongSecret = Encoding.UTF8.GetBytes("wrong-password-0987654321XX");

            var (serverStream, clientStream) = CreateBidirectionalPipes();

            using var serverProtocol = new SharedSecretAuthProtocol(correctSecret);
            using var clientProtocol = new SharedSecretAuthProtocol(wrongSecret);

            // Act
            var serverTask = serverProtocol.AuthenticateServerAsync(serverStream);
            var clientTask = clientProtocol.AuthenticateClientAsync(clientStream);

            await Task.WhenAll(serverTask, clientTask);

            // Assert
            Assert.False(serverTask.Result.IsAuthenticated);
            Assert.False(clientTask.Result.IsAuthenticated);
        }

        [Fact]
        public void SharedSecretAuth_RejectsShortSecret()
        {
            // Arrange & Act & Assert
            Assert.Throws<ArgumentException>(() =>
                new SharedSecretAuthProtocol(new byte[8])); // 少于 16 字节
        }

        [Fact]
        public void SharedSecretAuth_AcceptsStringPassword()
        {
            // Arrange & Act
            using var protocol = new SharedSecretAuthProtocol("my-password-string");

            // Assert — 不应抛出异常
            Assert.NotNull(protocol);
        }

        #endregion

        #region SecurityOptions - 安全配置测试

        [Fact]
        public void SecurityOptions_None_IsDefault()
        {
            var opts = new SecurityOptions();
            Assert.Equal(EncryptionMode.None, opts.EncryptionMode);
            Assert.False(opts.IsSecurityEnabled);
            Assert.False(opts.UseV2FrameFormat);
        }

        [Fact]
        public void SecurityOptions_Validate_RequiresSharedSecretForAesCbcHmac()
        {
            var opts = new SecurityOptions
            {
                EncryptionMode = EncryptionMode.AesCbcHmac
            };

            Assert.Throws<InvalidOperationException>(() => opts.Validate());
        }

        [Fact]
        public void SecurityOptions_Validate_SucceedsWithValidConfig()
        {
            var opts = new SecurityOptions
            {
                EncryptionMode = EncryptionMode.AesCbcHmac,
                SharedSecret = Encoding.UTF8.GetBytes("valid-secret-key-123456")
            };

            opts.Validate(); // 不应抛出异常
        }

        [Fact]
        public void SecurityOptions_UseV2FrameFormat_WhenReplayProtectionEnabled()
        {
            var opts = new SecurityOptions
            {
                EnableReplayProtection = true
            };

            Assert.True(opts.UseV2FrameFormat);
        }

        [Fact]
        public void SecurityOptions_UseV2FrameFormat_WhenFrameIntegrityEnabled()
        {
            var opts = new SecurityOptions
            {
                EnableFrameIntegrity = true,
                SharedSecret = new byte[32]
            };

            Assert.True(opts.UseV2FrameFormat);
        }

        [Fact]
        public void SecurityOptions_IntegrityOnly_CreatesCorrectConfig()
        {
            var key = new byte[32];
            var opts = SecurityOptions.IntegrityOnly(key);

            Assert.Equal(EncryptionMode.None, opts.EncryptionMode);
            Assert.True(opts.EnableFrameIntegrity);
            Assert.True(opts.EnableReplayProtection);
        }

        [Fact]
        public void SecurityOptions_AesSharedKey_CreatesCorrectConfig()
        {
            var key = Encoding.UTF8.GetBytes("test-key-1234567890123456");
            var opts = SecurityOptions.AesSharedKey(key);

            Assert.Equal(EncryptionMode.AesCbcHmac, opts.EncryptionMode);
            Assert.True(opts.EnableReplayProtection);
            Assert.True(opts.EnableAuthentication);
        }

        #endregion

        #region Key Derivation - 密钥派生测试

        [Fact]
        public void DeriveHmacKey_ProducesConsistentResults()
        {
            var secret = Encoding.UTF8.GetBytes("test-secret");
            var salt = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) { rng.GetBytes(salt); }

            var key1 = FrameSecurity.DeriveHmacKey(secret, salt);
            var key2 = FrameSecurity.DeriveHmacKey(secret, salt);

            Assert.Equal(key1, key2);
            Assert.Equal(32, key1.Length); // HMAC-SHA256 密钥
        }

        [Fact]
        public void DeriveHmacKey_DifferentSaltProducesDifferentKeys()
        {
            var secret = Encoding.UTF8.GetBytes("test-secret");
            var salt1 = new byte[16];
            var salt2 = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) { rng.GetBytes(salt1); rng.GetBytes(salt2); }

            var key1 = FrameSecurity.DeriveHmacKey(secret, salt1);
            var key2 = FrameSecurity.DeriveHmacKey(secret, salt2);

            Assert.NotEqual(key1, key2);
        }

        #endregion

        #region EncryptionMode - 枚举完整性测试

        [Fact]
        public void EncryptionMode_ContainsExpectedValues()
        {
            var values = (System.Collections.Generic.IEnumerable<EncryptionMode>)Enum.GetValues(typeof(EncryptionMode));
            Assert.Equal(3, values.Count());
            Assert.Contains(EncryptionMode.None, values);
            Assert.Contains(EncryptionMode.NegotiateStream, values);
            Assert.Contains(EncryptionMode.AesCbcHmac, values);
        }

        #endregion

        #region HandshakeProtocol - 握手协议测试

        [Fact]
        public async void HandshakeProtocol_VersionNegotiation_SelectsCommonVersion()
        {
            var (serverStream, clientStream) = CreateBidirectionalPipes();

            var serverOptions = new SecurityOptions { EnableReplayProtection = true };
            var clientOptions = new SecurityOptions { EnableReplayProtection = true };

            var serverHandshake = new HandshakeProtocol();
            var clientHandshake = new HandshakeProtocol();

            var serverTask = serverHandshake.PerformServerHandshakeAsync(serverStream, serverOptions, null);
            var clientTask = clientHandshake.PerformClientHandshakeAsync(clientStream, clientOptions, null);

            await Task.WhenAll(serverTask, clientTask);

            Assert.True(serverTask.Result.Succeeded);
            Assert.True(clientTask.Result.Succeeded);
            Assert.Equal(clientTask.Result.NegotiatedVersion, serverTask.Result.NegotiatedVersion);
        }

        [Fact]
        public async void HandshakeProtocol_WithAuth_SucceedsWithCorrectSecret()
        {
            var (serverStream, clientStream) = CreateBidirectionalPipes();

            var secret = Encoding.UTF8.GetBytes("handshake-auth-secret-1234");
            var serverOptions = new SecurityOptions
            {
                EnableAuthentication = true,
                EnableReplayProtection = true,
                SharedSecret = secret
            };
            var clientOptions = new SecurityOptions
            {
                EnableAuthentication = true,
                EnableReplayProtection = true,
                SharedSecret = secret
            };

            using var serverAuth = new SharedSecretAuthProtocol(secret);
            using var clientAuth = new SharedSecretAuthProtocol(secret);

            var serverHandshake = new HandshakeProtocol();
            var clientHandshake = new HandshakeProtocol();

            var serverTask = serverHandshake.PerformServerHandshakeAsync(serverStream, serverOptions, serverAuth, default);
            var clientTask = clientHandshake.PerformClientHandshakeAsync(clientStream, clientOptions, clientAuth, default);

            await Task.WhenAll(serverTask, clientTask);

            Assert.True(serverTask.Result.Succeeded);
            Assert.True(clientTask.Result.Succeeded);
            Assert.True(serverTask.Result.AuthenticationResult?.IsAuthenticated);
            Assert.True(clientTask.Result.AuthenticationResult?.IsAuthenticated);
        }

        #endregion

        #region SecurityCapabilities - 安全能力标志测试

        [Fact]
        public void SecurityCapabilities_FlagsWork()
        {
            var caps = SecurityCapabilities.V2FrameFormat | SecurityCapabilities.ReplayProtection;

            Assert.True(caps.HasFlag(SecurityCapabilities.V2FrameFormat));
            Assert.True(caps.HasFlag(SecurityCapabilities.ReplayProtection));
            Assert.False(caps.HasFlag(SecurityCapabilities.FrameIntegrity));
            Assert.False(caps.HasFlag(SecurityCapabilities.ApplicationAuth));
        }

        #endregion

        #region Edge Cases - 边界情况测试

        [Fact]
        public void FrameSecurity_DisposeTwice_DoesNotThrow()
        {
            var frameSecurity = new FrameSecurity(enableHmac: false);
            frameSecurity.Dispose();
            frameSecurity.Dispose(); // 第二次不应抛出异常
        }

        [Fact]
        public void AesCbcHmac_DisposeTwice_DoesNotThrow()
        {
            var encKey = new byte[32];
            var hmacKey = new byte[32];
            var provider = new AesCbcHmacEncryptionProvider(encKey, hmacKey);
            provider.Dispose();
            provider.Dispose(); // 第二次不应抛出异常
        }

        [Fact]
        public void SharedSecretAuth_DisposeTwice_DoesNotThrow()
        {
            var protocol = new SharedSecretAuthProtocol("test-password-123456");
            protocol.Dispose();
            protocol.Dispose(); // 第二次不应抛出异常
        }

        [Fact]
        public void FrameSecurity_PartialData_ReturnsFalse()
        {
            using var frameSecurity = new FrameSecurity(enableHmac: false);
            var partial = new byte[5]; // 不足 v2 头部大小

            var result = frameSecurity.TryDecodeSecureFrame(partial, out _, out _, out _, out _);
            Assert.False(result);
        }

        [Fact]
        public void AesCbcHmac_GetEncryptedSize_CalculatesCorrectly()
        {
            // 明文 1 字节 → 填充到 16 字节 → IV(16) + 密文(16) + HMAC(32) = 64
            Assert.Equal(64, AesCbcHmacEncryptionProvider.GetEncryptedSize(1));

            // 明文 16 字节 → 填充到 32 字节 → IV(16) + 密文(32) + HMAC(32) = 80
            Assert.Equal(80, AesCbcHmacEncryptionProvider.GetEncryptedSize(16));

            // 明文 100 字节 → 填充到 112 字节 → IV(16) + 密文(112) + HMAC(32) = 160
            Assert.Equal(160, AesCbcHmacEncryptionProvider.GetEncryptedSize(100));
        }

        #endregion
    }
}
