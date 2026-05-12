using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Windows.IPC.Protocol
{
    /// <summary>
    /// 消息帧格式
    /// +--------+--------+------+----------+
    /// | Magic  | Length | Type | Payload  |
    /// | 4bytes | 4bytes | 2b   | N bytes  |
    /// +--------+--------+------+----------+
    /// </summary>
    public static class MessageFrame
    {
        private const uint MAGIC_NUMBER = 0x49504358; // "IPCX"
        private const int HEADER_SIZE = 10; // 4 + 4 + 2
        private const int MAX_MESSAGE_SIZE = 10 * 1024 * 1024; // 10MB

        public enum FrameType : ushort
        {
            Request = 1,
            Response = 2,
            Notification = 3,
            Heartbeat = 4,
            Handshake = 5
        }

        /// <summary>
        /// 编码消息帧
        /// </summary>
        public static byte[] Encode(FrameType type, ReadOnlySpan<byte> payload)
        {
            if (payload.Length > MAX_MESSAGE_SIZE)
            {
                throw new ArgumentException($"Payload too large: {payload.Length} bytes");
            }

            var totalSize = HEADER_SIZE + payload.Length;
            var buffer = new byte[totalSize];

            // Magic
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(0, 4), MAGIC_NUMBER);

            // Length (payload only)
            BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(4, 4), payload.Length);

            // Type
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(8, 2), (ushort)type);

            // Payload
            payload.CopyTo(buffer.AsSpan(HEADER_SIZE));

            return buffer;
        }

        /// <summary>
        /// 解码消息帧
        /// </summary>
        public static bool TryDecode(ReadOnlySpan<byte> buffer, out FrameType type, out ReadOnlyMemory<byte> payload, out int consumedBytes)
        {
            type = default;
            payload = default;
            consumedBytes = 0;

            // 至少需要完整头部
            if (buffer.Length < HEADER_SIZE)
            {
                return false;
            }

            // 验证 Magic
            var magic = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(0, 4));
            if (magic != MAGIC_NUMBER)
            {
                throw new InvalidDataException($"Invalid magic number: 0x{magic:X8}");
            }

            // 读取长度
            var payloadLength = BinaryPrimitives.ReadInt32BigEndian(buffer.Slice(4, 4));
            if (payloadLength < 0 || payloadLength > MAX_MESSAGE_SIZE)
            {
                throw new InvalidDataException($"Invalid payload length: {payloadLength}");
            }

            var totalSize = HEADER_SIZE + payloadLength;

            // 检查是否有完整消息
            if (buffer.Length < totalSize)
            {
                return false;
            }

            // 读取类型
            type = (FrameType)BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(8, 2));

            // 提取 Payload
            payload = buffer.Slice(HEADER_SIZE, payloadLength).ToArray();

            consumedBytes = totalSize;
            return true;
        }
    }
}
