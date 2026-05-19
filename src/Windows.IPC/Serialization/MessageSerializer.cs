using MessagePack;
using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Text;
using Windows.IPC.Abstractions;

namespace Windows.IPC.Serialization
{
    /// <summary>
    /// 消息序列化器（辅助类）
    /// </summary>
    public static class MessageSerializer
    {
        private static readonly MessagePackSerializerOptions Options =
            MessagePackSerializerOptions.Standard
                .WithCompression(MessagePackCompression.Lz4BlockArray)
                .WithSecurity(MessagePackSecurity.UntrustedData);

        /// <summary>
        /// 序列化对象
        /// </summary>
        public static byte[] Serialize<T>(T obj)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));
            return MessagePackSerializer.Serialize(obj, Options);
        }

        /// <summary>
        /// 反序列化对象（从 ReadOnlyMemory）
        /// </summary>
        public static T Deserialize<T>(ReadOnlyMemory<byte> bytes)
        {
            if (bytes.IsEmpty) throw new ArgumentException("Empty data", nameof(bytes));
            try
            {
                return MessagePackSerializer.Deserialize<T>(bytes, Options);
            }
            catch (MessagePackSerializationException ex)
            {
                throw new SerializationException($"Failed to deserialize {typeof(T).Name}", ex);
            }
        }
    }
}
