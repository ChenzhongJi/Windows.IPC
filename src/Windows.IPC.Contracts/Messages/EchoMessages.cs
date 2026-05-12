using MessagePack;
using System;
using System.Collections.Generic;
using System.Text;

namespace Windows.IPC.Contracts.Messages
{
    /// <summary>
    /// Echo 请求
    /// </summary>
    [MessagePackObject]
    public class EchoRequest
    {
        [Key(0)]
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Echo 响应
    /// </summary>
    [MessagePackObject]
    public class EchoResponse
    {
        [Key(0)]
        public string Echo { get; set; } = string.Empty;

        [Key(1)]
        public DateTime ServerTime { get; set; }
    }
}
