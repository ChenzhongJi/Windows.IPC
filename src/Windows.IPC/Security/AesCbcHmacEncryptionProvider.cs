using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Windows.IPC.Security
{
    /// <summary>
    /// AES-256-CBC + HMAC-SHA256 加密提供程序
    /// 采用 Encrypt-then-MAC 模式（先加密后签名），这是公认最安全的组合模式
    /// 
    /// 加密帧格式：
    /// +----------+-----------+------------+
    /// | IV 16B   | Ciphertext| HMAC 32B   |
    /// +----------+-----------+------------+
    /// 
    /// HMAC 覆盖范围：IV + Ciphertext
    /// </summary>
    public class AesCbcHmacEncryptionProvider : IDisposable
    {
        private readonly byte[] _encryptionKey;  // AES-256 key (32 bytes)
        private readonly byte[] _hmacKey;        // HMAC-SHA256 key (32 bytes)
        private bool _disposed;

        /// <summary>
        /// AES-256-CBC IV 大小
        /// </summary>
        public const int IvSize = 16;

        /// <summary>
        /// HMAC-SHA256 签名大小
        /// </summary>
        public const int HmacSize = 32;

        /// <summary>
        /// 使用独立的加密密钥和 HMAC 密钥创建加密提供程序
        /// </summary>
        /// <param name="encryptionKey">AES-256 加密密钥（32 字节）</param>
        /// <param name="hmacKey">HMAC-SHA256 签名密钥（32 字节）</param>
        public AesCbcHmacEncryptionProvider(byte[] encryptionKey, byte[] hmacKey)
        {
            if (encryptionKey == null || encryptionKey.Length != 32)
            {
                throw new ArgumentException("Encryption key must be 32 bytes (AES-256)", nameof(encryptionKey));
            }

            if (hmacKey == null || hmacKey.Length != 32)
            {
                throw new ArgumentException("HMAC key must be 32 bytes", nameof(hmacKey));
            }

            _encryptionKey = (byte[])encryptionKey.Clone();
            _hmacKey = (byte[])hmacKey.Clone();
        }

        /// <summary>
        /// 使用共享密钥和盐值创建加密提供程序
        /// 通过 PBKDF2 从共享密钥派生加密密钥和 HMAC 密钥
        /// </summary>
        /// <param name="sharedSecret">共享密钥</param>
        /// <param name="salt">盐值（至少 8 字节）</param>
        /// <returns>从共享密钥派生的加密提供程序</returns>
        public static AesCbcHmacEncryptionProvider CreateFromSharedSecret(byte[] sharedSecret, byte[] salt)
        {
            if (sharedSecret == null || sharedSecret.Length == 0)
            {
                throw new ArgumentException("Shared secret cannot be null or empty", nameof(sharedSecret));
            }

            if (salt == null || salt.Length < 8)
            {
                throw new ArgumentException("Salt must be at least 8 bytes", nameof(salt));
            }

            var (encKey, hmacKey) = FrameSecurity.DeriveKeys(sharedSecret, salt);
            return new AesCbcHmacEncryptionProvider(encKey, hmacKey);
        }

        /// <summary>
        /// 加密数据
        /// </summary>
        /// <param name="plaintext">明文数据</param>
        /// <returns>加密后的数据：[IV 16B][Ciphertext NB][HMAC 32B]</returns>
        public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
        {
            ThrowIfDisposed();

            using (var aes = Aes.Create())
            {
                aes.Key = _encryptionKey;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.GenerateIV();

                byte[] ciphertext;
                using (var encryptor = aes.CreateEncryptor())
                {
                    ciphertext = encryptor.TransformFinalBlock(plaintext.ToArray(), 0, plaintext.Length);
                }

                // 构造加密帧：[IV][Ciphertext]
                var ivAndCiphertext = new byte[IvSize + ciphertext.Length];
                Buffer.BlockCopy(aes.IV, 0, ivAndCiphertext, 0, IvSize);
                Buffer.BlockCopy(ciphertext, 0, ivAndCiphertext, IvSize, ciphertext.Length);

                // 计算 HMAC（覆盖 IV + Ciphertext，Encrypt-then-MAC）
                byte[] hmac;
                using (var hmacSha256 = new HMACSHA256(_hmacKey))
                {
                    hmac = hmacSha256.ComputeHash(ivAndCiphertext);
                }

                // 最终输出：[IV][Ciphertext][HMAC]
                var result = new byte[ivAndCiphertext.Length + HmacSize];
                Buffer.BlockCopy(ivAndCiphertext, 0, result, 0, ivAndCiphertext.Length);
                Buffer.BlockCopy(hmac, 0, result, ivAndCiphertext.Length, HmacSize);

                return result;
            }
        }

        /// <summary>
        /// 解密数据
        /// </summary>
        /// <param name="encryptedData">加密数据：[IV 16B][Ciphertext NB][HMAC 32B]</param>
        /// <returns>解密后的明文</returns>
        public byte[] Decrypt(ReadOnlySpan<byte> encryptedData)
        {
            ThrowIfDisposed();

            // 最小有效加密数据 = IV(16) + PKCS7 最小填充(1 block=16) + HMAC(32) = 64
            if (encryptedData.Length < IvSize + HmacSize + 16)
            {
                throw new Exceptions.SecurityException(
                    $"Encrypted data too short: {encryptedData.Length} bytes, " +
                    $"minimum is {IvSize + HmacSize + 16} bytes");
            }

            // 提取各部分
            var iv = encryptedData.Slice(0, IvSize);
            var hmacReceived = encryptedData.Slice(encryptedData.Length - HmacSize, HmacSize);
            var ciphertext = encryptedData.Slice(IvSize, encryptedData.Length - IvSize - HmacSize);

            // 验证 HMAC（先验证再解密，Encrypt-then-MAC 安全模式）
            var ivAndCiphertext = encryptedData.Slice(0, encryptedData.Length - HmacSize).ToArray();
            byte[] hmacComputed;
            using (var hmacSha256 = new HMACSHA256(_hmacKey))
            {
                hmacComputed = hmacSha256.ComputeHash(ivAndCiphertext);
            }

            if (!ConstantTimeEquals(hmacReceived.ToArray(), hmacComputed))
            {
                throw new Exceptions.SecurityException(
                    "HMAC verification failed during decryption. " +
                    "The data may have been tampered with or the key is incorrect.");
            }

            // HMAC 验证通过，解密
            using (var aes = Aes.Create())
            {
                aes.Key = _encryptionKey;
                aes.IV = iv.ToArray();
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using (var decryptor = aes.CreateDecryptor())
                {
                    try
                    {
                        return decryptor.TransformFinalBlock(ciphertext.ToArray(), 0, ciphertext.Length);
                    }
                    catch (CryptographicException ex)
                    {
                        throw new Exceptions.SecurityException(
                            "Decryption failed: invalid ciphertext or padding. " +
                            "This may indicate data corruption or an incorrect encryption key.", ex);
                    }
                }
            }
        }

        /// <summary>
        /// 计算加密后的数据大小
        /// </summary>
        /// <param name="plaintextLength">明文长度</param>
        /// <returns>密文长度</returns>
        public static int GetEncryptedSize(int plaintextLength)
        {
            // PKCS7 填充：向上取整到 16 字节的倍数，最少填充 1 字节
            int paddedLength = ((plaintextLength / 16) + 1) * 16;
            return IvSize + paddedLength + HmacSize;
        }

        /// <summary>
        /// 常量时间比较（防止时序攻击）
        /// </summary>
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

        /// <summary>
        /// 检查对象是否已释放
        /// </summary>
        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(AesCbcHmacEncryptionProvider));
            }
        }
        /// <summary>释放加密提供程序并清除其密钥副本。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // 安全清除密钥
            if (_encryptionKey != null) Array.Clear(_encryptionKey, 0, _encryptionKey.Length);
            if (_hmacKey != null) Array.Clear(_hmacKey, 0, _hmacKey.Length);
        }
    }
}
