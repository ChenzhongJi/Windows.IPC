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
        [Key(0)]
        public string Version { get; set; } = string.Empty;

        [Key(1)]
        public int ConnectedClients { get; set; }

        [Key(2)]
        public DateTime ServerStartTime { get; set; }

        [Key(3)]
        public TimeSpan Uptime { get; set; }

        [Key(4)]
        public string MachineName { get; set; } = string.Empty;

        [Key(5)]
        public int ProcessId { get; set; }
    }
}
