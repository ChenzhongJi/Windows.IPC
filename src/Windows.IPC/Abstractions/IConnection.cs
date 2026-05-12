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
        Disconnected,
        Connecting,
        Connected,
        Reconnecting,
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

    public class ConnectionStateChangedEventArgs : EventArgs
    {
        public ConnectionState OldState { get; set; }
        public ConnectionState NewState { get; set; }
        public Exception? Exception { get; set; }
    }
}
