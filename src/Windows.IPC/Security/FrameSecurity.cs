using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace Windows.IPC.Security
{
    /// <summary>
    /// 帧安全处理器
    /// 负责 v2 帧格式的序列号管理和 HMAC 完整性校验
    /// 
    /// v2 帧格式：
    /// +--------+--------+------+-------+------------+----------+--------+
    /// | Magic  | Length | Type | Flags | SequenceId | Payload  | HMAC   |
    /// | 4bytes | 4bytes | 2b   | 2B    | 8bytes     | N bytes  | 32B*   |
    /// +--------+--------+------+-------+------------+----------+--------+
    /// 
    /// Magic: 0x49504359 ("IPCY") — v2 协议标识
    /// Length: Payload 长度（不含头部和 HMAC）
    /// Type: 帧类型（同 v1）
    /// Flags: 标志位（Bit0=HasHMAC, Bit1-15=保留）
    /// SequenceId: 单调递增序列号（用于重放防护）
    /// Payload: 消息负载
    /// HMAC: HMAC-SHA256 签名（条件性，仅当 Flags.HasHMAC=1）
    /// 
    /// v2 头部大小：20 字节（v1 为 10 字节）
    /// </summary>
    public class FrameSecurity : IDisposable
    {
        private const uint V2_MAGIC_NUMBER = 0x49504359; // "IPCY"
        internal const int V2_HEADER_SIZE = 20; // 4 + 4 + 2 + 2 + 8
        internal const int HMAC_SIZE = 32; // HMAC-SHA256

        /// <summary>
        /// 帧标志位
        /// </summary>
        [Flags]
        public enum FrameFlags : ushort
        {
            /// <summary>
            /// 无标志
            /// </summary>
            None = 0,

            /// <summary>
            /// 帧尾附带 HMAC-SHA256 签名
            /// </summary>
            HasHmac = 0x0001,

            /// <summary>
            /// 负载已加密（由流层设置，帧层仅记录）
            /// </summary>
            Encrypted = 0x0002
        }

        private long _sendSequenceId;
        private long _receiveSequenceId;
        private readonly int _maxSequenceGap;
        private readonly byte[]? _hmacKey;
        private readonly bool _enableHmac;
        private readonly object _sequenceLock = new object();
        private bool _disposed;

        /// <summary>
        /// 当前发送序列号（用于诊断）
        /// </summary>
        public long CurrentSendSequenceId
        {
            get
            {
                lock (_sequenceLock) { return _sendSequenceId; }
            }
        }

        /// <summary>
        /// 期望的下一个接收序列号（用于诊断）
        /// </summary>
        public long ExpectedReceiveSequenceId
        {
            get
            {
                lock (_sequenceLock) { return _receiveSequenceId; }
            }
        }

        /// <summary>
        /// 创建帧安全处理器
        /// </summary>
        /// <param name="enableHmac">是否启用 HMAC 完整性校验</param>
        /// <param name="hmacKey">HMAC 密钥（启用时必须提供，至少 32 字节）</param>
        /// <param name="maxSequenceGap">最大允许的序列号跳跃距离</param>
        public FrameSecurity(bool enableHmac = false, byte[]? hmacKey = null, int maxSequenceGap = 100)
        {
            if (enableHmac && (hmacKey == null || hmacKey.Length < 16))
            {
                throw new ArgumentException("HMAC key must be at least 16 bytes when HMAC is enabled", nameof(hmacKey));
            }

            _enableHmac = enableHmac;
            _hmacKey = enableHmac ? (byte[])hmacKey!.Clone() : null;
            _maxSequenceGap = maxSequenceGap;
            _sendSequenceId = 0;
            _receiveSequenceId = 1; // 期望的第一个序列号是 1
        }

        /// <summary>
        /// 编码 v2 安全帧
        /// </summary>
        /// <param name="type">帧类型</param>
        /// <param name="payload">消息负载</param>
        /// <returns>编码后的完整帧字节</returns>
        public byte[] EncodeSecureFrame(Protocol.MessageFrame.FrameType type, ReadOnlySpan<byte> payload)
        {
            ThrowIfDisposed();

            // 获取下一个序列号
            long sequenceId;
            lock (_sequenceLock)
            {
                _sendSequenceId++;
                sequenceId = _sendSequenceId;
            }

            // 确定标志
            var flags = FrameFlags.None;
            if (_enableHmac)
            {
                flags |= FrameFlags.HasHmac;
            }

            // 计算总大小
            int hmacLength = _enableHmac ? HMAC_SIZE : 0;
            int totalSize = V2_HEADER_SIZE + payload.Length + hmacLength;
            var buffer = new byte[totalSize];

            // 写入头部
            int offset = 0;

            // Magic (4 bytes, big-endian)
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset, 4), V2_MAGIC_NUMBER);
            offset += 4;

            // Length (4 bytes, big-endian) — payload length only
            BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(offset, 4), payload.Length);
            offset += 4;

            // Type (2 bytes, big-endian)
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), (ushort)type);
            offset += 2;

            // Flags (2 bytes, big-endian)
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), (ushort)flags);
            offset += 2;

            // SequenceId (8 bytes, big-endian)
            BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(offset, 8), sequenceId);
            offset += 8;

            // Payload
            payload.CopyTo(buffer.AsSpan(offset));
            offset += payload.Length;

            // HMAC (conditional)
            if (_enableHmac && _hmacKey != null)
            {
                // HMAC 覆盖范围：头部 + payload（不含 HMAC 自身）
                var hmacInput = buffer.AsSpan(0, offset);
                var hmac = ComputeHmac(_hmacKey, hmacInput);
                hmac.CopyTo(buffer.AsSpan(offset, HMAC_SIZE));
            }

            return buffer;
        }

        /// <summary>
        /// 尝试解码 v2 安全帧
        /// </summary>
        /// <param name="buffer">输入缓冲区</param>
        /// <param name="type">帧类型</param>
        /// <param name="payload">消息负载</param>
        /// <param name="sequenceId">序列号</param>
        /// <param name="consumedBytes">已消费字节数</param>
        /// <returns>是否成功解码完整帧</returns>
        public bool TryDecodeSecureFrame(
            ReadOnlySpan<byte> buffer,
            out Protocol.MessageFrame.FrameType type,
            out ReadOnlyMemory<byte> payload,
            out long sequenceId,
            out int consumedBytes)
        {
            ThrowIfDisposed();

            type = default;
            payload = default;
            sequenceId = 0;
            consumedBytes = 0;

            // 至少需要完整 v2 头部
            if (buffer.Length < V2_HEADER_SIZE)
            {
                return false;
            }

            // 验证 Magic
            var magic = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(0, 4));
            if (magic != V2_MAGIC_NUMBER)
            {
                throw new InvalidDataException(
                    $"Invalid v2 magic number: 0x{magic:X8}, expected 0x{V2_MAGIC_NUMBER:X8}");
            }

            // 读取长度
            var payloadLength = BinaryPrimitives.ReadInt32BigEndian(buffer.Slice(4, 4));
            if (payloadLength < 0 || payloadLength > 10 * 1024 * 1024) // 10MB max
            {
                throw new InvalidDataException($"Invalid payload length: {payloadLength}");
            }

            // 读取标志
            var flags = (FrameFlags)BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(10, 2));
            int hmacLength = flags.HasFlag(FrameFlags.HasHmac) ? HMAC_SIZE : 0;

            var totalSize = V2_HEADER_SIZE + payloadLength + hmacLength;

            // 检查是否有完整消息
            if (buffer.Length < totalSize)
            {
                return false;
            }

            // 读取类型
            type = (Protocol.MessageFrame.FrameType)BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(8, 2));

            // 读取序列号
            sequenceId = BinaryPrimitives.ReadInt64BigEndian(buffer.Slice(12, 8));

            // 验证 HMAC（如果存在）
            if (flags.HasFlag(FrameFlags.HasHmac) && _enableHmac && _hmacKey != null)
            {
                var hmacInput = buffer.Slice(0, V2_HEADER_SIZE + payloadLength);
                var receivedHmac = buffer.Slice(V2_HEADER_SIZE + payloadLength, HMAC_SIZE);
                var computedHmac = ComputeHmac(_hmacKey, hmacInput);

                if (!ConstantTimeEquals(receivedHmac, computedHmac))
                {
                    throw new Exceptions.SecurityException(
                        "HMAC verification failed: message integrity check failed. " +
                        "The message may have been tampered with or the HMAC key is incorrect.");
                }
            }

            // 验证序列号（重放防护）
            lock (_sequenceLock)
            {
                if (sequenceId < _receiveSequenceId)
                {
                    throw new Exceptions.SecurityException(
                        $"Replay attack detected: received SequenceId {sequenceId} " +
                        $"is less than expected {_receiveSequenceId}. " +
                        "Possible replay attack or out-of-order delivery.");
                }

                if (sequenceId - _receiveSequenceId > _maxSequenceGap)
                {
                    throw new Exceptions.SecurityException(
                        $"Sequence gap too large: received SequenceId {sequenceId}, " +
                        $"expected {_receiveSequenceId}. Gap exceeds maximum allowed ({_maxSequenceGap}). " +
                        "Possible frame injection or loss.");
                }

                _receiveSequenceId = sequenceId + 1;
            }

            // 提取 Payload
            payload = buffer.Slice(V2_HEADER_SIZE, payloadLength).ToArray();

            consumedBytes = totalSize;
            return true;
        }

        /// <summary>
        /// 检测缓冲区中的帧格式版本
        /// </summary>
        /// <param name="buffer">至少 4 字节的缓冲区</param>
        /// <returns>true 表示 v2 帧格式，false 表示 v1 帧格式</returns>
        public static bool IsV2Frame(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length < 4)
            {
                return false;
            }

            var magic = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(0, 4));
            return magic == V2_MAGIC_NUMBER;
        }

        /// <summary>
        /// 计算 HMAC-SHA256
        /// </summary>
        private static byte[] ComputeHmac(byte[] key, ReadOnlySpan<byte> data)
        {
            using (var hmac = new HMACSHA256(key))
            {
                return hmac.ComputeHash(data.ToArray());
            }
        }

        /// <summary>
        /// 常量时间比较（防止时序攻击）
        /// </summary>
        [MethodImpl(MethodImplOptions.NoOptimization)]
        private static bool ConstantTimeEquals(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            if (a.Length != b.Length)
            {
                return false;
            }

            int result = 0;
            for (int i = 0; i < a.Length; i++)
            {
                result |= a[i] ^ b[i];
            }

            return result == 0;
        }

        /// <summary>
        /// 从共享密钥派生 HMAC 密钥
        /// 使用 HKDF-like 方案：HMAC-SHA256(sharedSecret, salt || "ipc-hmac-key")
        /// </summary>
        public static byte[] DeriveHmacKey(byte[] sharedSecret, byte[]? salt = null)
        {
            if (sharedSecret == null || sharedSecret.Length == 0)
            {
                throw new ArgumentException("Shared secret cannot be null or empty", nameof(sharedSecret));
            }

            // 构造 info = salt || "ipc-hmac-key"
            var infoBytes = System.Text.Encoding.UTF8.GetBytes("ipc-hmac-key");
            var input = new byte[(salt?.Length ?? 0) + infoBytes.Length];
            int offset = 0;
            if (salt != null)
            {
                Buffer.BlockCopy(salt, 0, input, offset, salt.Length);
                offset += salt.Length;
            }
            Buffer.BlockCopy(infoBytes, 0, input, offset, infoBytes.Length);

            using (var hmac = new HMACSHA256(sharedSecret))
            {
                return hmac.ComputeHash(input);
            }
        }

        /// <summary>
        /// PBKDF2-HMAC-SHA256 实现
        /// 兼容 netstandard2.0（Rfc2898DeriveBytes 的 4 参数构造函数需要 netstandard2.1+）
        /// </summary>
        /// <param name="password">密码</param>
        /// <param name="salt">盐值</param>
        /// <param name="iterations">迭代次数</param>
        /// <param name="outputBytes">输出字节数</param>
        /// <returns>派生密钥</returns>
        private static byte[] Pbkdf2Sha256(byte[] password, byte[] salt, int iterations, int outputBytes)
        {
            // PBKDF2 with HMAC-SHA256: DK = T1 || T2 || ... || Tn
            // Ti = F(Password, Salt, iterations, i)
            // F(Password, Salt, c, i) = U1 ^ U2 ^ ... ^ Uc
            // U1 = PRF(Password, Salt || INT(i)), U2 = PRF(Password, U1), ...

            var result = new byte[outputBytes];
            int blockCount = (outputBytes + 31) / 32; // SHA256 输出 32 字节

            for (int blockIndex = 1; blockIndex <= blockCount; blockIndex++)
            {
                // U1 = HMAC-SHA256(Password, Salt || INT_32_BE(blockIndex))
                var saltWithBlock = new byte[salt.Length + 4];
                Buffer.BlockCopy(salt, 0, saltWithBlock, 0, salt.Length);
                saltWithBlock[salt.Length] = (byte)(blockIndex >> 24);
                saltWithBlock[salt.Length + 1] = (byte)(blockIndex >> 16);
                saltWithBlock[salt.Length + 2] = (byte)(blockIndex >> 8);
                saltWithBlock[salt.Length + 3] = (byte)blockIndex;

                byte[] u;
                using (var hmac = new HMACSHA256(password))
                {
                    u = hmac.ComputeHash(saltWithBlock);
                }

                var t = (byte[])u.Clone();

                // U2..Uc
                for (int iter = 1; iter < iterations; iter++)
                {
                    using (var hmac = new HMACSHA256(password))
                    {
                        u = hmac.ComputeHash(u);
                    }

                    for (int k = 0; k < 32; k++)
                    {
                        t[k] ^= u[k];
                    }
                }

                int copyLength = Math.Min(32, outputBytes - (blockIndex - 1) * 32);
                Buffer.BlockCopy(t, 0, result, (blockIndex - 1) * 32, copyLength);
            }

            return result;
        }

        /// <summary>
        /// 从共享密钥派生加密密钥（AES-256）和 HMAC 密钥
        /// 使用 PBKDF2 进行密钥派生
        /// </summary>
        public static (byte[] encryptionKey, byte[] hmacKey) DeriveKeys(byte[] sharedSecret, byte[] salt)
        {
            if (sharedSecret == null || sharedSecret.Length == 0)
            {
                throw new ArgumentException("Shared secret cannot be null or empty", nameof(sharedSecret));
            }

            if (salt == null || salt.Length < 8)
            {
                throw new ArgumentException("Salt must be at least 8 bytes", nameof(salt));
            }

            // 派生 64 字节：前 32 字节作为 AES-256 密钥，后 32 字节作为 HMAC-SHA256 密钥
            // 注意：netstandard2.0 的 Rfc2898DeriveBytes 仅支持 3 参数构造函数（默认 HMAC-SHA1），
            // 因此使用 HMAC-SHA256 手动实现 PBKDF2 以获得更强的安全性。
            var encryptionKey = Pbkdf2Sha256(sharedSecret, salt, 10000, 32);
            var hmacKey = Pbkdf2Sha256(sharedSecret, salt, 10001, 32); // 不同的迭代计数，确保密钥分离
            return (encryptionKey, hmacKey);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // 清除 HMAC 密钥
            if (_hmacKey != null)
            {
                Array.Clear(_hmacKey, 0, _hmacKey.Length);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(FrameSecurity));
            }
        }
    }
}
