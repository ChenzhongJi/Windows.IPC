using MessagePack;
using System;
using System.Collections.Generic;
using System.Text;

namespace Windows.IPC.UWP.Internal
{
    /// <summary>
    /// 字节数组包装类（用于序列化）
    /// </summary>
    [MessagePackObject]
    internal class ByteArrayWrapper
    {
        [Key(0)]
        public byte[] Data { get; set; } = System.Array.Empty<byte>();
    }
}
