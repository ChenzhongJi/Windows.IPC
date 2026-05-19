using MessagePack;
using System;
using System.Collections.Generic;
using System.Text;

namespace Windows.IPC.Contracts.Messages
{
    /// <summary>
    /// 空请求（用于无参数的请求）
    /// </summary>
    [MessagePackObject]
    public class EmptyRequest
    {
    }

    /// <summary>
    /// 空响应（用于无返回值的请求）
    /// </summary>
    [MessagePackObject]
    public class EmptyResponse
    {
        [Key(0)]
        public bool Success { get; set; } = true;
    }

    /// <summary>
    /// heartbeat request
    /// </summary>
    [MessagePackObject]
    public class HeartbeatyRequest
    {
    }

    /// <summary>
    /// heartbeat response
    /// </summary>
    [MessagePackObject]
    public class HeartbeatResponse
    {
        // Empty class - just needs to exist for proper serialization
    }
}
