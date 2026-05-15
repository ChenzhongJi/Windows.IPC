using Microsoft.Extensions.Logging;
using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Windows.IPC.Abstractions;
using Windows.IPC.Exceptions;
using Windows.IPC.Protocol;
using Windows.IPC.Serialization;
using Windows.IPC.Transport;

namespace Windows.IPC.Client
{
    /// <summary>
    /// IPC 客户端
    /// </summary>
    public class IpcClient : IDisposable, IAsyncDisposable
    {
        private readonly IpcClientOptions _options;
        private readonly ILogger? _logger;
        private readonly RequestManager _requestManager;
        private readonly Reconnector _reconnector;

        private NamedPipeConnection? _connection;
        private NamedPipeClientStream? _pipeStream;
        private Task? _receiveLoopTask;
        private CancellationTokenSource? _receiveCts;
        private Timer? _heartbeatTimer;

        private ConnectionState _state = ConnectionState.Disconnected;
        private bool _disposed;
        private int _disposeState = 0; // 0 = 未释放, 1 = 正在释放, 2 = 已释放
        private readonly object _stateLock = new object();

        public ConnectionState State
        {
            get
            {
                lock (_stateLock)
                {
                    return _state;
                }
            }
            private set
            {
                lock (_stateLock)
                {
                    if (_state != value)
                    {
                        var oldState = _state;
                        _state = value;
                        StateChanged?.Invoke(
                        this,
                        new ConnectionStateChangedEventArgs { OldState = oldState, NewState = value });
                    }
                }
            }
        }

        public bool IsConnected => State == ConnectionState.Connected && _connection?.IsConnected == true;

        /// <summary>
        /// 连接状态变更事件
        /// </summary>
        public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

        /// <summary>
        /// 收到通知消息事件
        /// </summary>
        public event EventHandler<NotificationReceivedEventArgs>? NotificationReceived;

        public IpcClient(IpcClientOptions? options = null, ILogger? logger = null)
        {
            _options = options ?? new IpcClientOptions();
            _logger = logger;
            _requestManager = new RequestManager(_logger);
            _reconnector = new Reconnector(_options.ReconnectionPolicy, ReconnectAsync);

            // 订阅重连事件
            _reconnector.ReconnectionStarted += OnReconnectionStarted;
            _reconnector.ReconnectionSucceeded += OnReconnectionSucceeded;
            _reconnector.ReconnectionFailed += OnReconnectionFailed;
        }

        /// <summary>
        /// 连接到服务端
        /// </summary>
        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            if (IsConnected)
            {
                _logger?.LogWarning("Already connected");
                return;
            }

            State = ConnectionState.Connecting;

            try
            {
                // 创建命名管道客户端
                _pipeStream = new NamedPipeClientStream(
                    ".",
                    _options.PipeOptions.PipeName,
                    PipeDirection.InOut,
                    System.IO.Pipes.PipeOptions.Asynchronous);

                // 连接
                await _pipeStream.ConnectAsync(_options.PipeOptions.ConnectionTimeoutMs, cancellationToken)
                    .ConfigureAwait(false);

                _pipeStream.ReadMode = PipeTransmissionMode.Byte;

                // 创建连接包装器
                _connection = new NamedPipeConnection(_pipeStream, _logger);
                _connection.StateChanged += OnConnectionStateChanged;

                // 启动接收循环
                _receiveCts = new CancellationTokenSource();
                _receiveLoopTask = ReceiveLoopAsync(_receiveCts.Token);

                // 启动心跳
                StartHeartbeat();

                State = ConnectionState.Connected;
                _logger?.LogInformation("Connected to {PipeName}", _options.PipeOptions.PipeName);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to connect");
                State = ConnectionState.Disconnected;

                Cleanup();

                throw new ConnectionException("Failed to connect to server", ex);
            }
        }

        /// <summary>
        /// 断开连接
        /// </summary>
        public async Task DisconnectAsync()
        {
            ThrowIfDisposed();

            if (State == ConnectionState.Disconnected)
            {
                return;
            }

            _logger?.LogInformation("Disconnecting...");

            await DisconnectCoreAsync(isDisposing: false).ConfigureAwait(false);

            _logger?.LogInformation("Disconnected");
        }

        /// <summary>
        /// 核心断开逻辑
        /// </summary>
        private async Task DisconnectCoreAsync(bool isDisposing)
        {
            // 停止重连
            _reconnector?.Stop();

            // 停止心跳
            StopHeartbeat();

            // 停止接收循环
            _receiveCts?.Cancel();
            if (_receiveLoopTask != null)
            {
                try
                {
                    await _receiveLoopTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Error in receive loop during disconnect");
                }
            }

            // 取消所有待处理的请求
            var exception = isDisposing
                ? new ObjectDisposedException(nameof(IpcClient))
                : (Exception)new ConnectionException("Client disconnected");

            _requestManager?.CancelAllRequests(exception);

            // 清理连接
            Cleanup();

            State = ConnectionState.Disconnected;
        }

        /// <summary>
        /// 发送请求并等待响应
        /// </summary>
        public async Task<TResponse> SendRequestAsync<TRequest, TResponse>(
            TRequest request,
            string messageType,
            int? timeoutMs = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            if (!IsConnected)
            {
                throw new ConnectionException("Not connected to server");
            }

            var timeout = timeoutMs ?? _options.DefaultRequestTimeoutMs;

            // 1. 序列化请求数据
            var requestPayload = MessageSerializer.Serialize(request);

            // 2. 创建请求消息
            var requestMessage = new RequestMessage
            {
                MessageId = Guid.NewGuid().ToString("N"),
                MessageType = messageType,
                Payload = requestPayload,
                TimeoutMs = timeout
            };

            // 3. 注册待处理请求
            var pendingRequest = _requestManager.RegisterRequest(requestMessage.MessageId, timeout, cancellationToken);

            try
            {
                // 4. 发送请求
                await _connection!.SendAsync(requestMessage, cancellationToken).ConfigureAwait(false);

                _logger?.LogDebug(
                    "Sent request {MessageId} of type {MessageType}",
                    requestMessage.MessageId,
                    messageType);

                // 5. 等待响应
                var responseMessage = await pendingRequest.Task.ConfigureAwait(false);

                // 6. 检查响应状态
                if (!responseMessage.IsSuccess)
                {
                    throw new Exceptions.RemoteException(
                        responseMessage.ErrorCode ?? "UNKNOWN_ERROR",
                        responseMessage.ErrorMessage ?? "Unknown error occurred");
                }

                // 7. 反序列化响应数据
                var responseData = MessageSerializer.Deserialize<TResponse>(responseMessage.Payload);

                return responseData;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Request {MessageId} failed", requestMessage.MessageId);
                throw;
            }
        }

        /// <summary>
        /// 发送通知（不等待响应）
        /// </summary>
        public async Task SendNotificationAsync<T>(
            T notification,
            string messageType,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            if (!IsConnected)
            {
                throw new ConnectionException("Not connected to server");
            }

            if (string.IsNullOrEmpty(messageType))
            {
                throw new ArgumentException("MessageType cannot be null or empty", nameof(messageType));
            }

            // 序列化通知数据
            byte[] payload;
            try
            {
                payload = MessageSerializer.Serialize(notification);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to serialize notification of type {Type}", typeof(T).Name);
                throw;
            }

            // 创建通知消息
            var notificationMessage = new NotificationMessage
            {
                MessageId = Guid.NewGuid().ToString("N"),
                MessageType = messageType,
                Payload = payload
            };

            await _connection!.SendAsync(notificationMessage, cancellationToken).ConfigureAwait(false);

            _logger?.LogDebug("Sent notification of type {MessageType}", messageType);
        }

        /// <summary>
        /// 接收循环
        /// </summary>
        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            _logger?.LogDebug("Receive loop started");

            try
            {
                while (!cancellationToken.IsCancellationRequested && _connection != null)
                {
                    var message = await _connection.ReceiveAsync(cancellationToken).ConfigureAwait(false);

                    if (message == null)
                    {
                        _logger?.LogWarning("Connection closed by server");
                        State = ConnectionState.Disconnected;
                        break;
                    }

                    // 处理不同类型的消息
                    if (message is ResponseMessage response)
                    {
                        _requestManager.HandleResponse(response);
                    }
                    else if (message is NotificationMessage notification)
                    {
                        OnNotificationReceived(notification);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger?.LogDebug("Receive loop cancelled");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Receive loop error");

                // 触发重连
                if (_options.ReconnectionPolicy.Enabled)
                {
                    State = ConnectionState.Reconnecting;
                    _reconnector.Start();
                }
                else
                {
                    State = ConnectionState.Faulted;
                }
            }

            _logger?.LogDebug("Receive loop stopped");
        }

        /// <summary>
        /// 重连逻辑
        /// </summary>
        private async Task<bool> ReconnectAsync(CancellationToken cancellationToken)
        {
            try
            {
                // 清理旧连接
                Cleanup();

                // 尝试重新连接
                await ConnectAsync(cancellationToken).ConfigureAwait(false);

                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Reconnection attempt failed");
                return false;
            }
        }

        /// <summary>
        /// 启动心跳
        /// </summary>
        private void StartHeartbeat()
        {
            if (_options.HeartbeatIntervalMs <= 0)
            {
                return;
            }

            _heartbeatTimer = new Timer(
                async _ => await SendHeartbeatAsync(),
                null,
                TimeSpan.FromMilliseconds(_options.HeartbeatIntervalMs),
                TimeSpan.FromMilliseconds(_options.HeartbeatIntervalMs));

            _logger?.LogDebug("Heartbeat started with interval {IntervalMs}ms", _options.HeartbeatIntervalMs);
        }

        /// <summary>
        /// 停止心跳
        /// </summary>
        private void StopHeartbeat()
        {
            _heartbeatTimer?.Dispose();
            _heartbeatTimer = null;
            _logger?.LogDebug("Heartbeat stopped");
        }

        /// <summary>
        /// 发送心跳
        /// </summary>
        private async Task SendHeartbeatAsync()
        {
            if (!IsConnected || _connection == null)
            {
                return;
            }

            try
            {
                // 发送心跳请求并等待响应，验证连接仍然活跃
                await SendRequestAsync<object, object>(
                    new object(), // 空载荷
                    "Heartbeat",
                    5000, // 5秒超时
                    CancellationToken.None).ConfigureAwait(false);

                _logger?.LogTrace("Heartbeat received response at {Time}", DateTime.Now);
            }
            catch (ObjectDisposedException)
            {
                // 连接已释放，停止心跳
                StopHeartbeat();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Heartbeat failed");
                // 不要抛出异常，心跳失败不应该中断程序
            }
        }

        /// <summary>
        /// 清理资源
        /// </summary>
        private void Cleanup()
        {
            _connection?.Dispose();
            _connection = null;

            _pipeStream?.Dispose();
            _pipeStream = null;

            _receiveCts?.Dispose();
            _receiveCts = null;
        }

        #region Event Handlers
        private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
        { _logger?.LogDebug("Connection state changed: {OldState} -> {NewState}", e.OldState, e.NewState); }

        private void OnReconnectionStarted(object? sender, ReconnectionEventArgs e)
        { _logger?.LogInformation("Reconnection attempt {AttemptCount} started", e.AttemptCount); }

        private void OnReconnectionSucceeded(object? sender, ReconnectionEventArgs e)
        {
            _logger?.LogInformation("Reconnection succeeded after {AttemptCount} attempts", e.AttemptCount);
            State = ConnectionState.Connected;
        }

        private void OnReconnectionFailed(object? sender, ReconnectionEventArgs e)
        {
            _logger?.LogError(e.Exception, "Reconnection failed after {AttemptCount} attempts", e.AttemptCount);
            State = ConnectionState.Faulted;
        }

        private void OnNotificationReceived(NotificationMessage notification)
        {
            NotificationReceived?.Invoke(
            this,
            new NotificationReceivedEventArgs { MessageType = notification.MessageType, Payload = notification.Payload });
        }
        #endregion

        /// <summary>
        /// 同步释放
        /// </summary>
        public void Dispose()
        {
            // 使用 Interlocked 确保只执行一次
            if (Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0)
            {
                return; // 已经在释放或已释放
            }

            try
            {
                _logger?.LogDebug("Disposing IpcClient...");

                // 同步执行清理
                try
                {
                    DisconnectCoreAsync(isDisposing: true).ConfigureAwait(false).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Error during disconnect in Dispose");
                }

                // 释放其他资源
                try
                {
                    _reconnector?.Dispose();
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Error disposing reconnector");
                }

                try
                {
                    _requestManager?.Dispose();
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Error disposing request manager");
                }

                try
                {
                    _heartbeatTimer?.Dispose();
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Error disposing heartbeat timer");
                }

                _logger?.LogDebug("IpcClient disposed");
            }
            finally
            {
                // 标记为已释放
                Interlocked.Exchange(ref _disposeState, 2);
                _disposed = true;
            }
        }

        /// <summary>
        /// 异步释放（可选，用于需要异步清理的场景）
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0)
            {
                return;
            }

            try
            {
                _logger?.LogDebug("Disposing IpcClient (async)...");

                await Task.Run(async () => await DisconnectCoreAsync(isDisposing: true).ConfigureAwait(false)).ConfigureAwait(false);

                _reconnector?.Dispose();
                _requestManager?.Dispose();
                _heartbeatTimer?.Dispose();

                _logger?.LogDebug("IpcClient disposed (async)");
            }
            finally
            {
                Interlocked.Exchange(ref _disposeState, 2);
                _disposed = true;
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(IpcClient));
            }
        }
    }

    /// <summary>
    /// 通知接收事件参数
    /// </summary>
    public class NotificationReceivedEventArgs : EventArgs
    {
        public string MessageType { get; set; } = string.Empty;

        public byte[] Payload { get; set; } = Array.Empty<byte>();
    }
}
