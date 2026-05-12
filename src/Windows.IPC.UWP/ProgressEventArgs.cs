using System;
using System.Collections.Generic;
using System.Text;
using Windows.IPC.Contracts.Messages;

namespace Windows.IPC.UWP
{
    /// <summary>
    /// 进度事件参数
    /// </summary>
    public class ProgressEventArgs : EventArgs
    {
        public double Percentage { get; set; }
        public string Message { get; set; } = string.Empty;
        public int CurrentStep { get; set; }
        public int TotalSteps { get; set; }
        public string? OperationId { get; set; }
        public object? Tag { get; set; }

        /// <summary>
        /// 从通知数据创建
        /// </summary>
        internal static ProgressEventArgs FromNotification(ProgressNotification notification)
        {
            return new ProgressEventArgs
            {
                Percentage = notification.Percentage,
                Message = notification.Message,
                CurrentStep = notification.CurrentStep,
                TotalSteps = notification.TotalSteps,
                OperationId = notification.OperationId
            };
        }
    }
}
