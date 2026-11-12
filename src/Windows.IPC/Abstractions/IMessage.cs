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
        /// <summary>请求的唯一标识符，用于匹配响应。</summary>
        [Key(0)]
        public string MessageId { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>请求的消息类型标识。</summary>
        [Key(1)]
        public string MessageType { get; set; } = string.Empty;

        /// <summary>请求的序列化负载。</summary>
        [Key(2)]
        public byte[] Payload { get; set; } = Array.Empty<byte>();

        /// <summary>请求超时时间，单位为毫秒。</summary>
        [Key(3)]
        public int TimeoutMs { get; set; } = 30000;
    }

    /// <summary>
    /// 响应消息
    /// </summary>
    [MessagePackObject]
    public class ResponseMessage : IMessage
    {
        /// <summary>对应请求的消息标识符。</summary>
        [Key(0)]
        public string MessageId { get; set; } = string.Empty;

        /// <summary>响应的消息类型标识。</summary>
        [Key(1)]
        public string MessageType { get; set; } = string.Empty;

        /// <summary>请求处理是否成功。</summary>
        [Key(2)]
        public bool IsSuccess { get; set; }

        /// <summary>响应的序列化负载。</summary>
        [Key(3)]
        public byte[] Payload { get; set; } = Array.Empty<byte>();

        /// <summary>处理失败时的可选错误信息。</summary>
        [Key(4)]
        public string? ErrorMessage { get; set; }

        /// <summary>处理失败时的可选错误代码。</summary>
        [Key(5)]
        public string? ErrorCode { get; set; }
    }

    /// <summary>
    /// 通知消息（无需响应）
    /// </summary>
    [MessagePackObject]
    public class NotificationMessage : IMessage
    {
        /// <summary>通知的唯一标识符。</summary>
        [Key(0)]
        public string MessageId { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>通知的消息类型标识。</summary>
        [Key(1)]
        public string MessageType { get; set; } = string.Empty;

        /// <summary>通知的序列化负载。</summary>
        [Key(2)]
        public byte[] Payload { get; set; } = Array.Empty<byte>();
    }
}
