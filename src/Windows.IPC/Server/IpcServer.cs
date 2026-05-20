using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.IPC.Abstractions;
using Windows.IPC.Contracts.Messages;
using Windows.IPC.Exceptions;
using Windows.IPC.Security;
using Windows.IPC.Serialization;
using Windows.IPC.Transport;

namespace Windows.IPC.Server
{
    /// <summary>
    /// IPC 服务端
    /// </summary>
    public class IpcServer : IDisposable
    {
        private readonly IpcServerOptions _options;
        private readonly ILogger? _logger;
        private readonly ClientManager _clientManager;
        private readonly MessageDispatcher _dispatcher;

        private readonly ConcurrentBag<Task> _listenerTasks = new ConcurrentBag<Task>();
        private readonly ConcurrentDictionary<string, Task> _requestTasks = new ConcurrentDictionary<string, Task>();
        private CancellationTokenSource? _serverCts;
        private bool _isRunning;
        private bool _disposed;
        private int _disposeState = 0;  // 0 = 未释放, 1 = 正在释放, 2 = 已释放
        private int _listenerCount;  // 当前监听器数量，用于计算 maxNumberOfServerInstances
        private readonly object _lock = new object();

        public bool IsRunning
        {
            get
            {
                lock (_lock)
                {
                    return _isRunning;
                }
            }
            private set
            {
                lock (_lock)
                {
                    _isRunning = value;
                }
            }
        }

        public int ConnectedClientCount => _clientManager.ClientCount;

        /// <summary>
        /// 客户端连接事件
        /// </summary>
        public event EventHandler<ClientConnectedEventArgs>? ClientConnected;

        /// <summary>
        /// 客户端断开事件
        /// </summary>
        public event EventHandler<ClientDisconnectedEventArgs>? ClientDisconnected;

        /// <summary>
        /// 收到通知消息事件
        /// </summary>
        public event EventHandler<NotificationReceivedEventArgs>? NotificationReceived;

        /// <summary>
        /// 客户端鉴权失败事件
        /// </summary>
        public event EventHandler<ClientAuthenticationEventArgs>? ClientAuthenticationFailed;
        /// <summary>
        /// 客户端鉴权成功事件
        /// </summary>
        public event EventHandler<ClientAuthenticationEventArgs>? ClientAuthenticationSucceeded;


        public IpcServer(IpcServerOptions? options = null, ILogger? logger = null)
        {
            _options = options ?? new IpcServerOptions();
            _logger = logger;
            _clientManager = new ClientManager(_logger);
            _dispatcher = new MessageDispatcher(_logger);

            // 订阅客户端事件
            _clientManager.ClientConnected += OnClientConnected;
            _clientManager.ClientDisconnected += OnClientDisconnected;

            // 注册默认的心跳处理器
            if (_options.AutoHandleHeartbeat)
            {
                RegisterHandler(new HeartbeatHandler());
            }
        }

        /// <summary>
        /// 注册消息处理器
        /// </summary>
        public void RegisterHandler(IMessageHandler handler)
        {
            _dispatcher.RegisterHandler(handler);
        }

        /// <summary>
        /// 注销消息处理器
        /// </summary>
        public bool UnregisterHandler(string messageType)
        {
            return _dispatcher.UnregisterHandler(messageType);
        }

        /// <summary>
        /// 启动服务端
        /// </summary>
        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            if (IsRunning)
            {
                _logger?.LogWarning("Server is already running");
                return Task.CompletedTask;
            }

            _logger?.LogInformation("Starting IPC server on pipe: {PipeName}", _options.PipeOptions.PipeName);

            _serverCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            IsRunning = true;

            // 启动足够多的监听器以应对并发连接
            // 每个监听器维护一个等待中的管道实例，监听器数量决定了同一时刻能同时接受的连接数
            // 对于高并发场景（50-100 客户端同时连接），需要足够的等待管道实例
            _listenerCount = Math.Min(Math.Max(8, _options.MaxConcurrentClients), 64);

            for (int i = 0; i < _listenerCount; i++)
            {
                var listenerTask = Task.Run(() => ListenerLoopAsync(i, _serverCts.Token), _serverCts.Token);
                _listenerTasks.Add(listenerTask);
            }

            _logger?.LogInformation("IPC server started with {Count} listener(s)", _listenerCount);

            return Task.CompletedTask;
        }


        /// <summary>
        /// 停止服务端
        /// </summary>
        public async Task StopAsync()
        {
            ThrowIfDisposed();

            if (!IsRunning)
            {
                return;
            }

            await StopCoreAsync(isDisposing: false).ConfigureAwait(false);
        }

        private async Task StopCoreAsync(bool isDisposing)
        {
            _logger?.LogInformation("Stopping IPC server...");

            IsRunning = false;

            // 取消所有监听器
            _serverCts?.Cancel();

            // 等待所有监听器完成
            if (_listenerTasks.Count > 0)
            {
                try
                {
                    await Task.WhenAll(_listenerTasks).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Error stopping listeners");
                }
            }

            // 清空监听器任务列表
            while (_listenerTasks.TryTake(out _)) { }

            // 断开所有客户端
            var reason = isDisposing ? "Server disposing" : "Server stopping";

            // 等待正在处理的请求任务完成（最多等待5秒，避免无限期阻塞）
            if (!_requestTasks.IsEmpty)
            {
                _logger?.LogInformation("Waiting for {Count} in-flight request(s) to complete...", _requestTasks.Count);
                try
                {
                    var allTasks = _requestTasks.Values.ToArray();
                    await Task.WhenAny(Task.WhenAll(allTasks), Task.Delay(5000)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Error waiting for request tasks to complete");
                }
                _requestTasks.Clear();
            }

            _clientManager?.DisconnectAll(reason);

            _serverCts?.Dispose();
            _serverCts = null;

            _logger?.LogInformation("IPC server stopped");
        }

        /// <summary>
        /// 向指定客户端发送通知
        /// </summary>
        public Task<bool> SendNotificationAsync<T>(
            string clientId,
            T notification,
            string messageType,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

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

            _logger?.LogTrace("Sending notification {MessageType} to client {ClientId}", messageType, clientId);

            return _clientManager.SendToClientAsync(clientId, notificationMessage, cancellationToken);
        }

        /// <summary>
        /// 广播通知给所有客户端
        /// </summary>
        public async Task<int> BroadcastNotificationAsync<T>(
            T notification,
            string messageType,
            Func<ConnectedClient, bool>? predicate = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

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

            _logger?.LogTrace("Broadcasting notification {MessageType}", messageType);

            var count = await _clientManager.BroadcastAsync(notificationMessage, predicate, cancellationToken)
                .ConfigureAwait(false);

            _logger?.LogDebug("Broadcast {MessageType} sent to {Count} clients", messageType, count);

            return count;
        }

        /// <summary>
        /// 获取所有已连接的客户端
        /// </summary>
        public IReadOnlyList<ConnectedClient> GetConnectedClients()
        {
            return _clientManager.GetAllClients();
        }

        /// <summary>
        /// 监听器循环
        /// </summary>
        private async Task ListenerLoopAsync(int listenerId, CancellationToken cancellationToken)
        {
            _logger?.LogDebug("Listener {ListenerId} started", listenerId);

            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream? pipeServer = null;

                try
                {
                    // 创建新的管道实例
                    pipeServer = CreatePipeServer();

                    _logger?.LogTrace("Listener {ListenerId} waiting for connection...", listenerId);

                    // 等待客户端连接
                    await pipeServer.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                    if (cancellationToken.IsCancellationRequested)
                    {
                        pipeServer?.Dispose();
                        break;
                    }

                    _logger?.LogDebug("Listener {ListenerId} accepted connection", listenerId);

                    // 将已接受的管道转交后台任务处理（鉴权 + 连接设置 + 消息处理），
                    // 监听器自身立即回到循环顶部创建下一个等待实例，
                    // 这样监听器不会因为连接设置工作而阻塞，最大化并发接受吞吐量。
                    var acceptedPipe = pipeServer;
                    pipeServer = null; // 防止 catch 块中误释放
                    _ = SetupAndHandleConnectionAsync(acceptedPipe, cancellationToken);

                    // 立即继续循环，创建新的管道实例等待下一个连接
                }
                catch (OperationCanceledException)
                {
                    _logger?.LogDebug("Listener {ListenerId} cancelled", listenerId);
                    pipeServer?.Dispose();
                    break;
                }
                catch (IOException ex) when (ex.Message.Contains("管道已结束") || ex.Message.Contains("pipe has been ended"))
                {
                    // 正常的管道关闭，继续循环
                    _logger?.LogTrace("Listener {ListenerId} pipe ended normally", listenerId);
                    pipeServer?.Dispose();
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Listener {ListenerId} error: {Message}", listenerId, ex.Message);

                    pipeServer?.Dispose();

                    // 发生错误时短暂延迟
                    try
                    {
                        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }

            _logger?.LogDebug("Listener {ListenerId} stopped", listenerId);
        }

        /// <summary>
        /// 设置已接受的连接并开始处理客户端消息
        /// 从监听器循环中剥离出来，避免阻塞监听器继续接受下一个连接
        /// </summary>
        private async Task SetupAndHandleConnectionAsync(NamedPipeServerStream pipeServer, CancellationToken cancellationToken)
        {
            Stream activeStream = pipeServer;
            SecuritySession? securitySession = null;

            try
            {
                // === 安全程式建立 ===
                var secOpts = _options.PipeOptions.SecurityOptions;

                // 1. 建立加密流（NegotiateStream 或 AES-CBC-HMAC）
                if (secOpts.IsSecurityEnabled && secOpts.EncryptionMode != EncryptionMode.None)
                {
                    var (encryptedStream, session) = await SecureStreamFactory.CreateServerStreamAsync(
                        pipeServer, secOpts, _logger, cancellationToken).ConfigureAwait(false);

                    activeStream = encryptedStream;
                    securitySession = session;

                    _logger?.LogInformation("Secure stream established: {Session}", securitySession);
                }

                // 2. 执行握手协议（版本协商 + 能力交换）
                if (secOpts.IsSecurityEnabled)
                {
                    var handshake = new HandshakeProtocol(_logger);
                    var authProtocol = ResolveAuthProtocol(secOpts);
                    var handshakeResult = await handshake.PerformServerHandshakeAsync(
                        activeStream, secOpts, authProtocol, cancellationToken).ConfigureAwait(false);

                    if (!handshakeResult.Succeeded)
                    {
                        _logger?.LogWarning("Handshake failed: {Reason}", handshakeResult.FailureReason);
                        pipeServer.Dispose();
                        return;
                    }

                    _logger?.LogInformation("Handshake succeeded: Version={Version}, Caps={Caps}",
                        handshakeResult.NegotiatedVersion, handshakeResult.Capabilities);

                    // 更新安全会话以反映握手结果
                    if (securitySession == null)
                    {
                        securitySession = new SecuritySession(secOpts);
                    }
                }

                // 3. 客户端鉴权（基于 Windows 进程身份）
                // 客户端鉴权
                if (_options.ClientAuthenticator != null)
                {
                    var authResult = await AuthenticateClientAsync(pipeServer, cancellationToken)
                        .ConfigureAwait(false);

                    if (!authResult.IsAuthenticated)
                    {
                        _logger?.LogWarning("Client authentication failed: {Reason}. Rejecting connection.",
                            authResult.RejectReason);

                        pipeServer.Dispose();
                        return;
                    }

                    _logger?.LogInformation("Client authenticated successfully");
                }

                // 4. 创建帧安全处理器（如果启用了 v2 帧格式）
                FrameSecurity? frameSecurity = null;
                bool useV2FrameFormat = false;
                if (securitySession != null && secOpts.UseV2FrameFormat)
                {
                    frameSecurity = securitySession.CreateFrameSecurity();
                    useV2FrameFormat = true;
                }

                // 创建连接对象
                // 注意：如果使用了加密流，activeStream 是加密流；否则是 pipeServer
                // pipeStream 参数保留用于 IsConnected 检查
                var connection = new NamedPipeConnection(
                    activeStream,
                    pipeServer,
                    frameSecurity,
                    useV2FrameFormat,
                    _logger);

                var client = new ConnectedClient(connection);

                // 添加到客户端管理器
                if (_clientManager.AddClient(client))
                {
                    // pipeServer 现在由 connection 管理，不要释放
                    await HandleClientAsync(client, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    _logger?.LogWarning("Failed to add client {ClientId}", client.ClientId);
                    client.Dispose();
                }
            }
            catch (SecurityException ex)
            {
                _logger?.LogError(ex, "Security error during connection setup");
                pipeServer?.Dispose();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                pipeServer?.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error setting up accepted connection");
                pipeServer?.Dispose();
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

            // NegotiateStream 模式下，认证由 NegotiateStream 处理
            if (secOpts.EncryptionMode == EncryptionMode.NegotiateStream)
            {
                return null;
            }

            // 优先使用用户自定义协议
            if (_options.AuthenticationProtocol != null)
            {
                return _options.AuthenticationProtocol;
            }

            // 自动从 SharedSecret 创建 SharedSecretAuthProtocol
            if (secOpts.SharedSecret != null)
            {
                return new SharedSecretAuthProtocol(secOpts.SharedSecret);
            }

            return null;
        }

        /// <summary>
        /// 鉴权客户端
        /// </summary>
        private async Task<AuthenticationResult> AuthenticateClientAsync(
            NamedPipeServerStream pipeServer,
            CancellationToken cancellationToken)
        {
            try
            {
                // 获取客户端身份信息
                var identity = ClientIdentityProvider.GetClientIdentity(pipeServer, _logger);

                _logger?.LogDebug("Client identity: ProcessName={ProcessName}, ProcessId={ProcessId}, Path={Path}",
                    identity.ProcessName, identity.ProcessId, identity.ExecutablePath);

                if (identity.Signature != null)
                {
                    _logger?.LogDebug("Client signature: IsSigned={IsSigned}, IsValid={IsValid}, Signer={Signer}",
                        identity.Signature.IsSigned, identity.Signature.IsValid, identity.Signature.SignerName);
                }

                // 执行鉴权
                var result = await _options.ClientAuthenticator!.AuthenticateAsync(identity, cancellationToken)
                    .ConfigureAwait(false);

                if (result.IsAuthenticated)
                {
                    ClientAuthenticationSucceeded?.Invoke(this, new ClientAuthenticationEventArgs
                    {
                        Identity = identity,
                        Result = result
                    });
                }
                else
                {
                    ClientAuthenticationFailed?.Invoke(this, new ClientAuthenticationEventArgs
                    {
                        Identity = identity,
                        Result = result
                    });
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error during client authentication");
                return AuthenticationResult.Reject($"Authentication error: {ex.Message}");
            }
        }

        /// <summary>
        /// 创建命名管道服务器
        /// </summary>
        private NamedPipeServerStream CreatePipeServer()
        {
            // 优先使用自定义 SDDL
            string? sddl = _options.PipeOptions.SecurityDescriptor;

            // 如果没有自定义 SDDL，根据安全模式获取
            if (string.IsNullOrEmpty(sddl) && _options.SecurityMode != PipeSecurityMode.Default)
            {
                sddl = PipeSecurityHelper.GetSddlForMode(_options.SecurityMode);
            }

            var effectiveMaxInstances = _options.MaxConcurrentClients;
            // 如果有 SDDL，使用原生 API 创建
            if (!string.IsNullOrEmpty(sddl))
            {
                try
                {
                    _logger?.LogDebug("Creating pipe with SDDL security: {Mode}", _options.SecurityMode);

                    // SDDL 路径同样需要容纳活跃连接 + 等待实例
                    effectiveMaxInstances = Math.Min(_options.MaxConcurrentClients + _listenerCount, 254);

                    var pipeServer = NativePipe.CreateWithSddl(
                        _options.PipeOptions.PipeName,
                        effectiveMaxInstances,
                        _options.PipeOptions.BufferSize,
                        sddl!);

                    _logger?.LogInformation("Successfully created pipe with custom security");
                    return pipeServer;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to create pipe with custom security, falling back to default");
                }
            }

            // 降级方案：使用默认安全设置
            _logger?.LogInformation("Creating pipe with default security settings");

            // maxNumberOfServerInstances 必须容纳：活跃连接数 + 等待中的管道实例数（监听器数量）
            // 例如 MaxConcurrentClients=101，监听器=64 时，需要 101+64=165 个实例
            // Windows 命名管道最大支持 254 个实例
            effectiveMaxInstances = Math.Min(_options.MaxConcurrentClients + _listenerCount, 254);

            var defaultPipeServer = new NamedPipeServerStream(
                pipeName: _options.PipeOptions.PipeName,
                direction: PipeDirection.InOut,
                maxNumberOfServerInstances: effectiveMaxInstances,
                transmissionMode: PipeTransmissionMode.Byte,
                options: System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.WriteThrough,
                inBufferSize: _options.PipeOptions.BufferSize,
                outBufferSize: _options.PipeOptions.BufferSize);

            return defaultPipeServer;
        }

        /// <summary>
        /// 处理客户端消息
        /// </summary>
        private async Task HandleClientAsync(ConnectedClient client, CancellationToken cancellationToken)
        {
            _logger?.LogDebug("Started handling client {ClientId}", client.ClientId);

            try
            {
                while (!_disposed && !cancellationToken.IsCancellationRequested && client.IsConnected)
                {
                    var message = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);

                    if (message == null)
                    {
                        _logger?.LogDebug("Client {ClientId} connection closed", client.ClientId);
                        break;
                    }

                    // 处理不同类型的消息
                    if (message is RequestMessage request)
                    {
                        // 启动请求处理任务并跟踪，确保服务器关闭时能等待正在处理的请求完成
                        var requestTask = Task.Run(async () =>
                        {
                            try
                            {
                                var response = await _dispatcher.DispatchRequestAsync(request, client, cancellationToken)
                                    .ConfigureAwait(false);

                                await client.SendAsync(response, cancellationToken).ConfigureAwait(false);

                                // 如果是心跳请求，更新客户端最后心跳时间
                                if (request.MessageType == "Heartbeat")
                                {
                                    client.Metadata.LastHeartbeat = DateTime.UtcNow;
                                    _logger?.LogTrace("Heartbeat updated for client {ClientId}", client.ClientId);
                                }
                                else
                                {
                                    client.Metadata.LastActivity = DateTime.UtcNow;
                                    _logger?.LogTrace("Activity updated for client {ClientId}", client.ClientId);
                                }
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                // 服务器正在关闭，无需发送错误响应
                                _logger?.LogDebug("Request processing cancelled for client {ClientId}", client.ClientId);
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogError(ex, "Error processing request from client {ClientId}", client.ClientId);

                                // 确保客户端总能收到响应，避免客户端永久等待超时
                                try
                                {
                                    var errorResponse = new ResponseMessage
                                    {
                                        MessageId = request.MessageId,
                                        MessageType = request.MessageType,
                                        IsSuccess = false,
                                        ErrorCode = "INTERNAL_ERROR",
                                        ErrorMessage = $"Failed to process request: {ex.Message}"
                                    };
                                    await client.SendAsync(errorResponse, cancellationToken).ConfigureAwait(false);
                                }
                                catch (Exception sendEx)
                                {
                                    _logger?.LogWarning(sendEx,
                                        "Failed to send error response to client {ClientId}", client.ClientId);
                                }
                            }
                        }, cancellationToken);

                        // 跟踪请求处理任务，完成后自动移除
                        _requestTasks[request.MessageId] = requestTask;
                        _ = requestTask.ContinueWith(t =>
                        {
                            _requestTasks.TryRemove(request.MessageId, out _);
                        }, TaskContinuationOptions.ExecuteSynchronously);
                    }
                    else if (message is NotificationMessage notification)
                    {
                        OnNotificationReceived(client, notification);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger?.LogDebug("Client {ClientId} handling cancelled", client.ClientId);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error handling client {ClientId}", client.ClientId);
            }
            finally
            {
                _clientManager.RemoveClient(client.ClientId, "Handler completed");
            }

            _logger?.LogDebug("Stopped handling client {ClientId}", client.ClientId);
        }

        #region Event Handlers

        private void OnClientConnected(object? sender, ClientConnectedEventArgs e)
        {
            _logger?.LogInformation("Client {ClientId} connected", e.ClientId);
            ClientConnected?.Invoke(this, e);
        }

        private void OnClientDisconnected(object? sender, ClientDisconnectedEventArgs e)
        {
            _logger?.LogInformation("Client {ClientId} disconnected. Reason: {Reason}",
                e.ClientId, e.Reason);
            ClientDisconnected?.Invoke(this, e);
        }

        private void OnNotificationReceived(ConnectedClient client, NotificationMessage notification)
        {
            NotificationReceived?.Invoke(this, new NotificationReceivedEventArgs
            {
                ClientId = client.ClientId,
                MessageType = notification.MessageType,
                Payload = notification.Payload
            });
        }

        #endregion

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0)
            {
                return;
            }

            try
            {
                // 使用 Task.Run 避免在有同步上下文（如 UI 线程）时死锁
                Task.Run(() => StopCoreAsync(isDisposing: true)).GetAwaiter().GetResult();

                _dispatcher?.Dispose();
                _clientManager?.Dispose();

                _logger?.LogDebug("IpcServer disposed");
            }
            finally
            {
                Interlocked.Exchange(ref _disposeState, 2);
                _disposed = true;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0)
            {
                return;
            }

            try
            {
                await StopCoreAsync(isDisposing: true).ConfigureAwait(false);

                _dispatcher?.Dispose();
                _clientManager?.Dispose();

                _logger?.LogDebug("IpcServer disposed (async)");
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
                throw new ObjectDisposedException(nameof(IpcServer));
            }
        }
    }

    /// <summary>
    /// 通知接收事件参数（服务端）
    /// </summary>
    public class NotificationReceivedEventArgs : EventArgs
    {
        public string ClientId { get; set; } = string.Empty;
        public string MessageType { get; set; } = string.Empty;
        public byte[] Payload { get; set; } = Array.Empty<byte>();
    }

    /// <summary>
    /// 鉴权结果事件参数（服务端）
    /// </summary>
    public class ClientAuthenticationEventArgs : EventArgs
    {
        public ClientIdentity Identity { get; set; }
        public AuthenticationResult Result { get; set; }
    }

    /// <summary>
    /// 默认的心跳处理器
    /// </summary>
    internal class HeartbeatHandler : MessageHandler<HeartbeatRequest, HeartbeatResponse>
    {
        public override string MessageType => "Heartbeat";

        protected override Task<HeartbeatResponse> HandleAsync(
            HeartbeatRequest request,
            ConnectedClient client,
            CancellationToken cancellationToken)
        {
            // 心跳不需要响应数据
            return Task.FromResult(new HeartbeatResponse());
        }
    }
}
