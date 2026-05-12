using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.IPC;
using Windows.IPC.Client;
using Windows.IPC.Contracts.Messages;
using Windows.IPC.Transport;
using Windows.IPC.UWP.Internal;

namespace Windows.IPC.UWP
{
    /// <summary>
    /// UWP 兼容的 IPC 客户端
    /// </summary>
    public class UwpIpcClient : IDisposable
    {
        private readonly IpcClient _client;
        private bool _disposed;

        /// <summary>
        /// 连接状态变更事件
        /// </summary>
        public event EventHandler<UwpConnectionStateChangedEventArgs>? StateChanged;

        /// <summary>
        /// 收到通知消息事件
        /// </summary>
        public event EventHandler<UwpNotificationReceivedEventArgs>? NotificationReceived;

        /// <summary>
        /// 进度通知事件
        /// </summary>
        public event EventHandler<ProgressEventArgs>? ProgressChanged;

        /// <summary>
        /// 当前连接状态
        /// </summary>
        public UwpConnectionState State { get; private set; }

        /// <summary>
        /// 是否已连接
        /// </summary>
        public bool IsConnected => _client.IsConnected;

        public UwpIpcClient(string pipeName)
        {
            var options = new IpcClientOptions
            {
                PipeOptions = new PipeOptions
                {
                    PipeName = pipeName,
                    ConnectionTimeoutMs = 5000
                },
                ReconnectionPolicy = ReconnectionPolicy.Default,
                DefaultRequestTimeoutMs = 30000,
                HeartbeatIntervalMs = 0  // UWP 应用通常不需要心跳
            };

            _client = new IpcClient(options, logger: null);

            // 订阅事件
            _client.StateChanged += OnClientStateChanged;
            _client.NotificationReceived += OnClientNotificationReceived;
            _client.NotificationReceived += OnClientNotificationReceived;

            State = UwpConnectionState.Disconnected;
        }

        /// <summary>
        /// 连接到服务端
        /// </summary>
        public async Task ConnectAsync()
        {
            ThrowIfDisposed();
            await _client.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>
        /// 断开连接
        /// </summary>
        public async Task DisconnectAsync()
        {
            ThrowIfDisposed();
            await _client.DisconnectAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// 发送请求（泛型版本 - 需要预定义消息类型）
        /// </summary>
        public async Task<TResponse> SendRequestAsync<TRequest, TResponse>(
            TRequest request,
            string messageType,
            int timeoutMs = 30000)
        {
            ThrowIfDisposed();
            return await _client.SendRequestAsync<TRequest, TResponse>(
                request,
                messageType,
                timeoutMs,
                CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>
        /// 发送请求（字节数组版本 - UWP 友好）
        /// </summary>
        public async Task<byte[]> SendRequestAsync(
            byte[] requestData,
            string messageType,
            int timeoutMs = 30000)
        {
            ThrowIfDisposed();

            // 使用 byte[] 包装类
            var request = new ByteArrayWrapper { Data = requestData };
            var response = await _client.SendRequestAsync<ByteArrayWrapper, ByteArrayWrapper>(
                request,
                messageType,
                timeoutMs,
                CancellationToken.None).ConfigureAwait(false);

            return response.Data;
        }

        /// <summary>
        /// 发送通知
        /// </summary>
        public async Task SendNotificationAsync(
            byte[] notificationData,
            string messageType)
        {
            ThrowIfDisposed();

            if (string.IsNullOrEmpty(messageType))
            {
                throw new ArgumentException("MessageType cannot be null or empty", nameof(messageType));
            }

            var notification = new ByteArrayWrapper { Data = notificationData };
            await _client.SendNotificationAsync(
                notification,
                messageType,
                CancellationToken.None).ConfigureAwait(false);
        }

        private void OnClientStateChanged(object? sender, Abstractions.ConnectionStateChangedEventArgs e)
        {
            State = ConvertState(e.NewState);

            StateChanged?.Invoke(this, new UwpConnectionStateChangedEventArgs
            {
                OldState = ConvertState(e.OldState),
                NewState = ConvertState(e.NewState)
            });
        }

        private void OnClientNotificationReceived(object? sender, Client.NotificationReceivedEventArgs e)
        {
            // 处理进度通知
            if (e.MessageType == "Progress")
            {
                try
                {
                    var progressData = MessagePack.MessagePackSerializer.Deserialize<ProgressNotification>(e.Payload);
                    ProgressChanged?.Invoke(this, new ProgressEventArgs
                    {
                        Percentage = progressData.Percentage,
                        Message = progressData.Message,
                        CurrentStep = progressData.CurrentStep,
                        TotalSteps = progressData.TotalSteps
                    });
                }
                catch
                {
                    // 忽略解析错误
                }
            }

            // 触发原始通知事件
            NotificationReceived?.Invoke(this, new UwpNotificationReceivedEventArgs
            {
                MessageType = e.MessageType,
                Payload = e.Payload
            });
        }

        private UwpConnectionState ConvertState(Abstractions.ConnectionState state)
        {
            return state switch
            {
                Abstractions.ConnectionState.Disconnected => UwpConnectionState.Disconnected,
                Abstractions.ConnectionState.Connecting => UwpConnectionState.Connecting,
                Abstractions.ConnectionState.Connected => UwpConnectionState.Connected,
                Abstractions.ConnectionState.Reconnecting => UwpConnectionState.Reconnecting,
                Abstractions.ConnectionState.Faulted => UwpConnectionState.Faulted,
                _ => UwpConnectionState.Disconnected
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _client?.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(UwpIpcClient));
            }
        }
    }

    /// <summary>
    /// UWP 连接状态
    /// </summary>
    public enum UwpConnectionState
    {
        Disconnected = 0,
        Connecting = 1,
        Connected = 2,
        Reconnecting = 3,
        Faulted = 4
    }

    /// <summary>
    /// UWP 连接状态变更事件参数
    /// </summary>
    public class UwpConnectionStateChangedEventArgs : EventArgs
    {
        public UwpConnectionState OldState { get; set; }
        public UwpConnectionState NewState { get; set; }
    }

    /// <summary>
    /// UWP 通知接收事件参数
    /// </summary>
    public class UwpNotificationReceivedEventArgs : EventArgs
    {
        public string MessageType { get; set; } = string.Empty;
        public byte[] Payload { get; set; } = Array.Empty<byte>();
    }
}
