using MessagePack;
using System;
using System.Collections.Generic;
using System.Text;

namespace Windows.IPC.Contracts.Messages
{
    /// <summary>
    /// 服务器信息请求
    /// </summary>
    [MessagePackObject]
    public class ServerInfoRequest : EmptyRequest
    {
    }

    /// <summary>
    /// 服务器信息响应
    /// </summary>
    [MessagePackObject]
    public class ServerInfoResponse
    {
        /// <summary>
        /// 服务器版本。
        /// </summary>
        [Key(0)]
        public string Version { get; set; } = string.Empty;

        /// <summary>
        /// 已连接的客户端数量。
        /// </summary>
        [Key(1)]
        public int ConnectedClients { get; set; }

        /// <summary>
        /// 服务器启动时间。
        /// </summary>
        [Key(2)]
        public DateTime ServerStartTime { get; set; }

        /// <summary>
        /// 服务器运行时长。
        /// </summary>
        [Key(3)]
        public TimeSpan Uptime { get; set; }

        /// <summary>
        /// 服务器所在计算机的名称。
        /// </summary>
        [Key(4)]
        public string MachineName { get; set; } = string.Empty;

        /// <summary>
        /// 服务器进程的标识符。
        /// </summary>
        [Key(5)]
        public int ProcessId { get; set; }
    }
}
