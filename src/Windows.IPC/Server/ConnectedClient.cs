using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.IPC.Abstractions;
using Windows.IPC.Transport;

namespace Windows.IPC.Server
{
    /// <summary>
    /// 已连接的客户端
    /// </summary>
    public class ConnectedClient : IDisposable
    {
        private readonly NamedPipeConnection _connection;
        private bool _disposed;

        public string ClientId => _connection.ConnectionId;
        public DateTime ConnectedAt { get; }
        public ConnectionState State => _connection.State;
        public bool IsConnected => _connection.IsConnected;

        /// <summary>
        /// 客户端元数据（可扩展）
        /// </summary>
        public ClientMetadata Metadata { get; }

        public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

        public ConnectedClient(NamedPipeConnection connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            ConnectedAt = DateTime.UtcNow;
            Metadata = new ClientMetadata();

            _connection.StateChanged += OnConnectionStateChanged;
        }

        /// <summary>
        /// 发送消息给客户端
        /// </summary>
        public Task SendAsync(IMessage message, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return _connection.SendAsync(message, cancellationToken);
        }

        /// <summary>
        /// 接收来自客户端的消息
        /// </summary>
        public Task<IMessage?> ReceiveAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return _connection.ReceiveAsync(cancellationToken);
        }

        private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
        {
            StateChanged?.Invoke(this, e);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _connection?.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ConnectedClient));
            }
        }
    }

    /// <summary>
    /// 客户端元数据
    /// </summary>
    public class ClientMetadata
    {
        private long _lastHeartbeatTicks = -1; // 使用 ticks 存储 DateTime?，-1 表示 null
        private long _lastActivityTicks; // 使用 ticks 存储 DateTime
        private readonly object _dateTimeLock = new object();

        /// <summary>
        /// 客户端名称（可选）
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// 客户端版本（可选）
        /// </summary>
        public string? Version { get; set; }

        /// <summary>
        /// 进程ID（如果可获取）
        /// </summary>
        public int? ProcessId { get; set; }

        /// <summary>
        /// 最后一次心跳时间
        /// </summary>
        public DateTime? LastHeartbeat
        {
            get
            {
                lock (_dateTimeLock)
                {
                    var ticks = _lastHeartbeatTicks;
                    return ticks == -1 ? null : new DateTime(ticks);
                }
            }
            set
            {
                lock (_dateTimeLock)
                {
                    var ticks = value.HasValue ? value.Value.Ticks : -1;
                    _lastHeartbeatTicks = ticks;
                }
            }
        }

        /// <summary>
        /// 最后一次活动时间
        /// </summary>
        public DateTime LastActivity
        {
            get
            {
                lock (_dateTimeLock)
                {
                    return new DateTime(_lastActivityTicks);
                }
            }
            set
            {
                lock (_dateTimeLock)
                {
                    _lastActivityTicks = value.Ticks;
                }
            }
        }

        /// <summary>
        /// 自定义属性
        /// </summary>
        public ConcurrentDictionary<string, object> CustomProperties { get; } =
            new ConcurrentDictionary<string, object>();
    }
}
