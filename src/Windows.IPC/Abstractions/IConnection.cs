using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.IPC.Abstractions;

namespace Windows.IPC.Abstractions
{
    /// <summary>
    /// 连接状态
    /// </summary>
    public enum ConnectionState
    {
        /// <summary>未连接。</summary>
        Disconnected,
        /// <summary>正在建立连接。</summary>
        Connecting,
        /// <summary>已连接。</summary>
        Connected,
        /// <summary>正在重新建立连接。</summary>
        Reconnecting,
        /// <summary>连接发生故障。</summary>
        Faulted
    }

    /// <summary>
    /// 连接接口
    /// </summary>
    public interface IConnection : IDisposable
    {
        /// <summary>
        /// 连接ID
        /// </summary>
        string ConnectionId { get; }

        /// <summary>
        /// 当前状态
        /// </summary>
        ConnectionState State { get; }

        /// <summary>
        /// 是否已连接
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// 发送消息
        /// </summary>
        Task SendAsync(IMessage message, CancellationToken cancellationToken = default);

        /// <summary>
        /// 接收消息
        /// </summary>
        Task<IMessage?> ReceiveAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 状态变更事件
        /// </summary>
        event EventHandler<ConnectionStateChangedEventArgs> StateChanged;
    }

    /// <summary>
    /// 连接状态变更事件的数据。
    /// </summary>
    public class ConnectionStateChangedEventArgs : EventArgs
    {
        /// <summary>变更前的连接状态。</summary>
        public ConnectionState OldState { get; set; }
        /// <summary>变更后的连接状态。</summary>
        public ConnectionState NewState { get; set; }
        /// <summary>与状态变更关联的异常；没有异常时为 null。</summary>
        public Exception? Exception { get; set; }
    }
}
