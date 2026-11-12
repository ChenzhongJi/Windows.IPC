using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.IPC.Transport;

namespace Windows.IPC.Server
{
    /// <summary>
    /// 多 Session IPC 服务器
    /// </summary>
    public class MultiSessionIpcServer : IDisposable
    {
        private readonly IpcServerOptions _baseOptions;
        private readonly ILogger? _logger;
        private readonly SessionManager _sessionManager;
        private readonly ConcurrentDictionary<int, IpcServer> _sessionServers = new();
        private readonly Func<IMessageHandler>[] _handlerFactories;
        private bool _disposed;
        private bool _isRunning;

        /// <summary>用于监控 Windows 会话的管理器。</summary>
        public SessionManager SessionManager => _sessionManager;

        /// <summary>某个会话服务器有客户端连接时触发。</summary>
        public event EventHandler<ClientConnectedEventArgs>? ClientConnected;

        /// <summary>某个会话服务器有客户端断开连接时触发。</summary>
        public event EventHandler<ClientDisconnectedEventArgs>? ClientDisconnected;

        /// <summary>某个会话服务器收到客户端通知时触发。</summary>
        public event EventHandler<NotificationReceivedEventArgs>? NotificationReceived;

        /// <summary>所有会话服务器的已连接客户端总数。</summary>
        public int TotalConnectedClients => _sessionServers.Values.Sum(s => s.ConnectedClientCount);

        /// <summary>创建多会话 IPC 服务器。</summary>
        /// <param name='baseOptions'>用于各会话服务器的基础选项。</param>
        /// <param name='logger'>可选的日志记录器。</param>
        /// <param name='handlerFactories'>为各会话服务器创建消息处理器的工厂。</param>
        public MultiSessionIpcServer(
            IpcServerOptions baseOptions,
            ILogger? logger = null,
            params Func<IMessageHandler>[] handlerFactories)
        {
            _baseOptions = baseOptions ?? throw new ArgumentNullException(nameof(baseOptions));
            _logger = logger;
            _handlerFactories = handlerFactories ?? Array.Empty<Func<IMessageHandler>>();

            _sessionManager = new SessionManager(logger);
            _sessionManager.SessionCreated += OnSessionCreated;
            _sessionManager.SessionTerminated += OnSessionTerminated;
        }

        /// <summary>
        /// 启动多 Session 服务器
        /// </summary>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (_isRunning)
            {
                _logger?.LogWarning("Multi-session server is already running");
                return;
            }

            _logger?.LogInformation("Starting multi-session IPC server...");

            // 启动 Session 监控
            _sessionManager.StartMonitoring();

            // 为现有的活动 Session 创建服务器
            var activeSessions = _sessionManager.GetActiveSessions();
            foreach (var session in activeSessions)
            {
                await CreateServerForSessionAsync(session, cancellationToken);
            }

            _isRunning = true;
            _logger?.LogInformation(
            "Multi-session IPC server started. Monitoring {Count} sessions",
            _sessionServers.Count);
        }

        /// <summary>
        /// 停止多 Session 服务器
        /// </summary>
        public async Task StopAsync()
        {
            if (!_isRunning)
            {
                return;
            }

            _logger?.LogInformation("Stopping multi-session IPC server...");

            // 停止 Session 监控
            _sessionManager.StopMonitoring();

            // 停止所有 Session 服务器
            var stopTasks = _sessionServers.Values.Select(s => s.StopAsync());
            await Task.WhenAll(stopTasks);

            _sessionServers.Clear();

            _isRunning = false;
            _logger?.LogInformation("Multi-session IPC server stopped");
        }

        /// <summary>
        /// 向指定 Session 的客户端广播
        /// </summary>
        public async Task<int> BroadcastToSessionAsync<T>(
            int sessionId,
            T notification,
            string messageType,
            CancellationToken cancellationToken = default)
        {
            if (_sessionServers.TryGetValue(sessionId, out var server))
            {
                return await server.BroadcastNotificationAsync(notification, messageType, null, cancellationToken);
            }

            _logger?.LogWarning("No server found for session {SessionId}", sessionId);
            return 0;
        }

        /// <summary>
        /// 向所有 Session 广播
        /// </summary>
        public async Task<int> BroadcastToAllSessionsAsync<T>(
            T notification,
            string messageType,
            CancellationToken cancellationToken = default)
        {
            var tasks = _sessionServers.Values
                .Select(server => server.BroadcastNotificationAsync(notification, messageType, null, cancellationToken));

            var results = await Task.WhenAll(tasks);
            return results.Sum();
        }

        /// <summary>
        /// 获取所有 Session 服务器信息
        /// </summary>
        public Dictionary<int, SessionServerInfo> GetSessionServers()
        {
            var result = new Dictionary<int, SessionServerInfo>();

            foreach (var kvp in _sessionServers)
            {
                var sessionInfo = _sessionManager.GetSession(kvp.Key);
                result[kvp.Key] = new SessionServerInfo
                {
                    SessionId = kvp.Key,
                    UserName = sessionInfo?.UserName ?? "Unknown",
                    ConnectedClients = kvp.Value.ConnectedClientCount,
                    IsRunning = kvp.Value.IsRunning
                };
            }

            return result;
        }

        /// <summary>
        /// 将消息从一个 Session 路由到另一个 Session
        /// </summary>
        public async Task<bool> RouteMessageAsync<T>(int fromSessionId, int toSessionId, T message, string messageType)
        {
            if (_sessionServers.TryGetValue(toSessionId, out var targetServer))
            {
                var count = await targetServer.BroadcastNotificationAsync(message, messageType);
                return count > 0;
            }

            return false;
        }

        /// <summary>
        /// 向特定用户的 Session 发送消息
        /// </summary>
        public async Task<int> BroadcastToUserAsync<T>(string userName, T notification, string messageType)
        {
            var targetSessions = _sessionManager.GetActiveSessions()
                .Where(s => s.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.SessionId);

            var tasks = targetSessions
                .Where(sid => _sessionServers.ContainsKey(sid))
                .Select(sid => BroadcastToSessionAsync(sid, notification, messageType));

            var results = await Task.WhenAll(tasks);
            return results.Sum();
        }

        private async Task CreateServerForSessionAsync(SessionInfo session, CancellationToken cancellationToken)
        {
            if (_sessionServers.ContainsKey(session.SessionId))
            {
                _logger?.LogWarning("Server for session {SessionId} already exists", session.SessionId);
                return;
            }

            try
            {
                // 为每个 Session 创建唯一的管道名称
                var pipeName = $"{_baseOptions.PipeOptions.PipeName}_Session{session.SessionId}";

                var sessionOptions = new IpcServerOptions
                {
                    PipeOptions =
                        new IpcPipeOptions
                        {
                            // ⭐ 关键：使用 Global\ 前缀使管道跨 Session 可见
                            PipeName = $"Global\\{pipeName}",
                            BufferSize = _baseOptions.PipeOptions.BufferSize,
                            SecurityDescriptor = _baseOptions.PipeOptions.SecurityDescriptor,
                            // ⭐ 安全配置必须传播到子会话服务器，否则加密/认证/完整性全部失效
                            SecurityOptions = _baseOptions.PipeOptions.SecurityOptions
                        },
                    MaxConcurrentClients = _baseOptions.MaxConcurrentClients,
                    AutoHandleHeartbeat = _baseOptions.AutoHandleHeartbeat,
                    SecurityMode = _baseOptions.SecurityMode,
                    ClientAuthenticator = _baseOptions.ClientAuthenticator,
                    // ⭐ 应用层认证协议也必须传播
                    AuthenticationProtocol = _baseOptions.AuthenticationProtocol,
                    LogAuthenticationFailures = _baseOptions.LogAuthenticationFailures
                };

                var server = new IpcServer(sessionOptions, _logger);

                // 注册消息处理器
                foreach (var handlerFactory in _handlerFactories)
                {
                    server.RegisterHandler(handlerFactory());
                }

                // 转发事件
                server.ClientConnected += (s, e) => ClientConnected?.Invoke(this, e);
                server.ClientDisconnected += (s, e) => ClientDisconnected?.Invoke(this, e);
                server.NotificationReceived += (s, e) => NotificationReceived?.Invoke(this, e);

                await server.StartAsync(cancellationToken);

                _sessionServers[session.SessionId] = server;

                _logger?.LogInformation(
                "Created IPC server for session {SessionId} ({UserName}) on pipe {PipeName}",
                session.SessionId,
                session.UserName,
                pipeName);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to create server for session {SessionId}", session.SessionId);
            }
        }

        private void OnSessionCreated(object? sender, SessionEventArgs e)
        {
            _logger?.LogInformation("Session {SessionId} created, creating IPC server...", e.Session.SessionId);

            // 使用 Task.Run 包装异步操作，避免 async void 导致未观察异常崩溃进程
            // async void 的异常会直接抛到同步上下文导致进程崩溃，
            // 改为 fire-and-forget Task 并在内部捕获所有异常
            _ = Task.Run(async () =>
            {
                try
                {
                    await CreateServerForSessionAsync(e.Session, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to create server for session {SessionId}", e.Session.SessionId);
                }
            });
        }

        private void OnSessionTerminated(object? sender, SessionEventArgs e)
        {
            _logger?.LogInformation("Session {SessionId} terminated, stopping IPC server...", e.Session.SessionId);

            _ = Task.Run(async () =>
            {
                if (_sessionServers.TryRemove(e.Session.SessionId, out var server))
                {
                    try
                    {
                        await server.StopAsync().ConfigureAwait(false);
                        server.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Error stopping server for session {SessionId}", e.Session.SessionId);
                    }
                }
            });
        }

        /// <summary>停止服务器并释放会话监控资源。</summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            // 使用 Task.Run 避免 UI/STA 线程上的同步死锁
            // 与 IpcServer.Dispose 和 IpcClient.Dispose 保持一致
            Task.Run(() => StopAsync()).GetAwaiter().GetResult();

            foreach (var server in _sessionServers.Values)
            {
                server.Dispose();
            }

            _sessionManager?.Dispose();
        }
    }

    /// <summary>单个会话服务器的状态信息。</summary>
    public class SessionServerInfo
    {
        /// <summary>Windows 会话标识符。</summary>
        public int SessionId { get; set; }

        /// <summary>会话所属用户名。</summary>
        public string UserName { get; set; } = string.Empty;

        /// <summary>已连接的客户端数量。</summary>
        public int ConnectedClients { get; set; }

        /// <summary>会话服务器是否正在运行。</summary>
        public bool IsRunning { get; set; }
    }
}
