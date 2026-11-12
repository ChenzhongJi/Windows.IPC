using MessagePack;
using System;
using System.Collections.Generic;
using System.Text;

namespace Windows.IPC.Contracts.Messages
{
    /// <summary>
    /// 进度通知数据
    /// </summary>
    [MessagePackObject]
    public class ProgressNotification
    {
        /// <summary>
        /// 进度百分比 (0-100)
        /// </summary>
        [Key(0)]
        public double Percentage { get; set; }

        /// <summary>
        /// 进度消息
        /// </summary>
        [Key(1)]
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// 当前步骤
        /// </summary>
        [Key(2)]
        public int CurrentStep { get; set; }

        /// <summary>
        /// 总步骤数
        /// </summary>
        [Key(3)]
        public int TotalSteps { get; set; }

        /// <summary>
        /// 操作ID（用于区分不同的操作）
        /// </summary>
        [Key(4)]
        public string? OperationId { get; set; }
    }

    /// <summary>
    /// 服务器广播消息
    /// </summary>
    [MessagePackObject]
    public class BroadcastNotification
    {
        /// <summary>
        /// 广播消息的内容。
        /// </summary>
        [Key(0)]
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// 广播消息的时间戳。
        /// </summary>
        [Key(1)]
        public System.DateTime Timestamp { get; set; }

        /// <summary>
        /// 广播消息的可选类别。
        /// </summary>
        [Key(2)]
        public string? Category { get; set; }
    }
}
