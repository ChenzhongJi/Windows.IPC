using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Windows.IPC.Abstractions;
using Windows.IPC.Contracts.Messages;
using Windows.IPC.Exceptions;
using Windows.IPC.Protocol;
using Windows.IPC.Security;
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
        private int _heartbeatInProgress = 0; // 用于防止心跳定时器重入

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

                // === 安全程式建立 ===
                System.IO.Stream activeStream = _pipeStream;
                SecuritySession? securitySession = null;
                var secOpts = _options.PipeOptions.SecurityOptions;

                // 1. 建立加密流
                if (secOpts.IsSecurityEnabled && secOpts.EncryptionMode != EncryptionMode.None)
                {
                    var (encryptedStream, session) = await SecureStreamFactory.CreateClientStreamAsync(
                        _pipeStream, secOpts, _logger, cancellationToken).ConfigureAwait(false);

                    activeStream = encryptedStream;
                    securitySession = session;

                    _logger?.LogInformation("Secure stream established: {Session}", securitySession);
                }

                // 2. 执行握手协议
                if (secOpts.IsSecurityEnabled)
                {
                    var handshake = new HandshakeProtocol(_logger);
                    var authProtocol = ResolveAuthProtocol(secOpts);
                    var handshakeResult = await handshake.PerformClientHandshakeAsync(
                        activeStream, secOpts, authProtocol, cancellationToken).ConfigureAwait(false);

                    if (!handshakeResult.Succeeded)
                    {
                        throw new SecurityException(
                            $"Handshake failed: {handshakeResult.FailureReason}");
                    }

                    _logger?.LogInformation("Handshake succeeded: Version={Version}, Caps={Caps}",
                        handshakeResult.NegotiatedVersion, handshakeResult.Capabilities);

                    if (securitySession == null)
                    {
                        securitySession = new SecuritySession(secOpts);
                    }
                }

                // 3. 创建帧安全处理器
                FrameSecurity? frameSecurity = null;
                bool useV2FrameFormat = false;
                if (securitySession != null && secOpts.UseV2FrameFormat)
                {
                    frameSecurity = securitySession.CreateFrameSecurity();
                    useV2FrameFormat = true;
                }

                // 创建连接包装器
                _connection = new NamedPipeConnection(
                    activeStream,
                    _pipeStream,
                    frameSecurity,
                    useV2FrameFormat,
                    _logger,
                    maxMessageSize: _options.PipeOptions.MaxMessageSize,
                    maxReceiveBufferSize: _options.PipeOptions.MaxReceiveBufferSize,
                    maxSendQueueSize: _options.PipeOptions.MaxSendQueueSize,
                    maxReceiveQueueSize: _options.PipeOptions.MaxReceiveQueueSize);
                _connection.StateChanged += OnConnectionStateChanged;

                // 注意：NamedPipeConnection 内部已启动接收循环（读取管道 → 写入 Channel），
                // 此处启动的是消息分发循环（从 Channel 读取 → 分发给 RequestManager/事件）
                _receiveCts = new CancellationTokenSource();
                _receiveLoopTask = DispatchLoopAsync(_receiveCts.Token);

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
        /// 解析认证协议
        /// </summary>
        private IAuthenticationProtocol? ResolveAuthProtocol(SecurityOptions secOpts)
        {
            if (!secOpts.EnableAuthentication)
            {
                return null;
            }

            if (secOpts.EncryptionMode == EncryptionMode.NegotiateStream)
            {
                return null;
            }

            if (_options.AuthenticationProtocol != null)
            {
                return _options.AuthenticationProtocol;
            }

            if (secOpts.SharedSecret != null)
            {
                return new SharedSecretAuthProtocol(secOpts.SharedSecret);
            }

            return null;
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
        /// 消息分发循环
        /// 
        /// NamedPipeConnection 内部的接收循环负责从管道读取原始数据、解码帧、
        /// 写入接收 Channel。此分发循环从 Channel 取出已解码的消息，
        /// 分发给 RequestManager（响应消息）或 NotificationReceived 事件（通知消息）。
        /// 
        /// 这种架构实现了端到端背压：
        /// 当消息消费速度 < 生产速度时，接收 Channel 积满 →
        /// 读取循环暂停 → 管道缓冲区填满 → 发送端 WriteAsync 阻塞 →
        /// 发送端自然降速。
        /// </summary>
        private async Task DispatchLoopAsync(CancellationToken cancellationToken)
        {
            _logger?.LogDebug("Dispatch loop started");

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

                    // 分发消息
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
                _logger?.LogDebug("Dispatch loop cancelled");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Dispatch loop error");

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

            _logger?.LogDebug("Dispatch loop stopped");
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

            // 使用 Change(dueTime, period) 模式：每次心跳完成后才调度下一次
            // 避免前一次心跳尚未完成时定时器再次触发导致重入堆积
            _heartbeatTimer = new Timer(
                OnHeartbeatTimerCallback,
                null,
                TimeSpan.FromMilliseconds(_options.HeartbeatIntervalMs),
                Timeout.InfiniteTimeSpan); // 仅触发一次，在回调中重新调度

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
        /// 心跳定时器回调
        /// 使用 Interlocked 防止重入：如果上一次心跳尚未完成，跳过本次触发
        /// </summary>
        private void OnHeartbeatTimerCallback(object? state)
        {
            // 防止重入：如果前一次心跳还在执行，跳过本次
            if (Interlocked.CompareExchange(ref _heartbeatInProgress, 1, 0) != 0)
            {
                _logger?.LogTrace("Heartbeat skipped: previous heartbeat still in progress");
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await SendHeartbeatAsync().ConfigureAwait(false);
                }
                finally
                {
                    // 标记心跳完成
                    Interlocked.Exchange(ref _heartbeatInProgress, 0);

                    // 无论成功或失败，重新调度下一次心跳
                    // 这样心跳间隔是从上一次完成后开始计算，避免重入堆积
                    try
                    {
                        _heartbeatTimer?.Change(
                            TimeSpan.FromMilliseconds(_options.HeartbeatIntervalMs),
                            Timeout.InfiniteTimeSpan);
                    }
                    catch (ObjectDisposedException)
                    {
                        // 定时器已释放，忽略
                    }
                }
            });
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
                var heartbeatResponse = await SendRequestAsync<HeartbeatRequest, HeartbeatResponse>(
                    new HeartbeatRequest(),
                    "Heartbeat",
                    _options.HeartbeatTimeoutMs,
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
                // 使用 Task.Run 避免在有同步上下文（如 UI 线程）时死锁
                // 直接 .GetAwaiter().GetResult() 在 UI 线程上会造成：
                // DisconnectCoreAsync 的 await 完成后需要回到 UI 线程续延，但 UI 线程被 GetResult 阻塞
                try
                {
                    Task.Run(() => DisconnectCoreAsync(isDisposing: true)).GetAwaiter().GetResult();
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