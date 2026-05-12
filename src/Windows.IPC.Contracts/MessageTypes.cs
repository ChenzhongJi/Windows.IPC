using System;
using System.Collections.Generic;
using System.Text;

namespace Windows.IPC.Contracts
{
    /// <summary>
    /// 消息类型常量
    /// </summary>
    public static class MessageTypes
    {
        #region 请求类型

        /// <summary>
        /// Echo 请求
        /// </summary>
        public const string Echo = "Echo";

        /// <summary>
        /// 获取服务器信息
        /// </summary>
        public const string GetServerInfo = "GetServerInfo";

        #endregion

        #region 通知类型

        /// <summary>
        /// 进度通知
        /// </summary>
        public const string Progress = "Progress";

        /// <summary>
        /// 服务器广播
        /// </summary>
        public const string ServerBroadcast = "ServerBroadcast";

        /// <summary>
        /// 心跳
        /// </summary>
        public const string Heartbeat = "Heartbeat";

        #endregion
    }
}
