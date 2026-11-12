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
        /// <summary>
        /// 要回显的消息。
        /// </summary>
        [Key(0)]
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// Echo 响应
    /// </summary>
    [MessagePackObject]
    public class EchoResponse
    {
        /// <summary>
        /// 回显的消息。
        /// </summary>
        [Key(0)]
        public string Echo { get; set; } = string.Empty;

        /// <summary>
        /// 服务器响应时的时间。
        /// </summary>
        [Key(1)]
        public DateTime ServerTime { get; set; }
    }
}
