using MessagePack;
using System;
using System.Collections.Generic;
using System.Text;

namespace Windows.IPC.Abstractions
{
    /// <summary>
    /// 消息基接口
    /// </summary>
    public interface IMessage
    {
        /// <summary>
        /// 消息唯一ID（用于请求-响应匹配）
        /// </summary>
        string MessageId { get; }

        /// <summary>
        /// 消息类型标识
        /// </summary>
        string MessageType { get; }
    }

    /// <summary>
    /// 请求消息
    /// </summary>
    [MessagePackObject]
    public class RequestMessage : IMessage
    {
        [Key(0)]
        public string MessageId { get; set; } = Guid.NewGuid().ToString("N");

        [Key(1)]
        public string MessageType { get; set; } = string.Empty;

        [Key(2)]
        public byte[] Payload { get; set; } = Array.Empty<byte>();

        [Key(3)]
        public int TimeoutMs { get; set; } = 30000;
    }

    /// <summary>
    /// 响应消息
    /// </summary>
    [MessagePackObject]
    public class ResponseMessage : IMessage
    {
        [Key(0)]
        public string MessageId { get; set; } = string.Empty;

        [Key(1)]
        public string MessageType { get; set; } = string.Empty;

        [Key(2)]
        public bool IsSuccess { get; set; }

        [Key(3)]
        public byte[] Payload { get; set; } = Array.Empty<byte>();

        [Key(4)]
        public string? ErrorMessage { get; set; }

        [Key(5)]
        public string? ErrorCode { get; set; }
    }

    /// <summary>
    /// 通知消息（无需响应）
    /// </summary>
    [MessagePackObject]
    public class NotificationMessage : IMessage
    {
        [Key(0)]
        public string MessageId { get; set; } = Guid.NewGuid().ToString("N");

        [Key(1)]
        public string MessageType { get; set; } = string.Empty;

        [Key(2)]
        public byte[] Payload { get; set; } = Array.Empty<byte>();
    }
}
