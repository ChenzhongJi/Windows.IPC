using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Windows.IPC.Server
{
    /// <summary>
    /// Session 信息
    /// </summary>
    public class SessionInfo
    {
        private long _createdAt; // 使用 ticks 存储 DateTime
        private long _lastSeenAt; // 使用 ticks 存储 DateTime
        private readonly object _dateTimeLock = new object();

        /// <summary>Windows 会话标识符。</summary>
        public int SessionId { get; set; }
        /// <summary>会话所属用户名。</summary>
        public string UserName { get; set; } = string.Empty;
        /// <summary>会话用户所属的域名。</summary>
        public string DomainName { get; set; } = string.Empty;
        /// <summary>管理器记录的会话状态。</summary>
        public SessionState State { get; set; }
        /// <summary>创建此会话信息记录的时间，不一定是系统会话的创建时间。</summary>
        public DateTime CreatedAt
        {
            get
            {
                lock (_dateTimeLock)
                {
                    return new DateTime(_createdAt);
                }
            }
            set
            {
                lock (_dateTimeLock)
                {
                    _createdAt = value.Ticks;
                }
            }
        }
        /// <summary>最近一次观察或更新此会话的时间。</summary>
        public DateTime LastSeenAt
        {
            get
            {
                lock (_dateTimeLock)
                {
                    return new DateTime(_lastSeenAt);
                }
            }
            set
            {
                lock (_dateTimeLock)
                {
                    _lastSeenAt = value.Ticks;
                }
            }
        }
    }

    /// <summary>管理器记录的会话状态。</summary>
    public enum SessionState
    {
        /// <summary>会话处于活动状态。</summary>
        Active,
        /// <summary>会话已连接。</summary>
        Connected,
        /// <summary>会话已断开连接。</summary>
        Disconnected,
        /// <summary>会话已锁定。</summary>
        Locked
    }

    /// <summary>
    /// Windows Session 管理器（事件驱动 + 定时扫描混合模式）
    /// </summary>
    public class SessionManager : IDisposable
    {
        private readonly ILogger? _logger;
        private readonly ConcurrentDictionary<int, SessionInfo> _sessions = new();
        private SessionNotificationReceiver? _eventReceiver;
        private Timer? _backupScanTimer;
        private bool _disposed;

        // 统计信息
        private long _eventNotificationCount;
        private long _backupScanCount;
        private long _backupScanFoundCount;

        /// <summary>管理器发现或登记会话时触发。</summary>
        public event EventHandler<SessionEventArgs>? SessionCreated;
        /// <summary>管理器移除已结束的会话时触发。</summary>
        public event EventHandler<SessionEventArgs>? SessionTerminated;
        /// <summary>处理到会话登录通知时触发。</summary>
        public event EventHandler<SessionEventArgs>? SessionLogon;
        /// <summary>处理到已跟踪会话的注销通知时触发。</summary>
        public event EventHandler<SessionEventArgs>? SessionLogoff;
        /// <summary>处理到已跟踪会话的锁定通知时触发。</summary>
        public event EventHandler<SessionEventArgs>? SessionLock;
        /// <summary>处理到已跟踪会话的解锁通知时触发。</summary>
        public event EventHandler<SessionEventArgs>? SessionUnlock;

        /// <summary>创建会话管理器。</summary>
        /// <param name='logger'>可选的日志记录器。</param>
        public SessionManager(ILogger? logger = null)
        {
            _logger = logger;
        }

        /// <summary>
        /// 开始监控（混合模式）
        /// </summary>
        public void StartMonitoring()
        {
            if (_eventReceiver != null || _backupScanTimer != null)
            {
                _logger?.LogWarning("Session monitoring is already started");
                return;
            }

            _logger?.LogInformation("Starting session monitoring (hybrid mode)...");

            // 1. 初始扫描现有 Session
            _logger?.LogDebug("Performing initial session scan...");
            ScanExistingSessions();

            // 2. 启动事件驱动监听（主要机制）
            try
            {
                _eventReceiver = new SessionNotificationReceiver(this, _logger);
                _eventReceiver.Start();
                _logger?.LogInformation("✓ Event-driven session monitoring started");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "✗ Failed to start event-driven monitoring, will rely on backup scanning");
            }

            // 3. 启动备份扫描定时器（兜底机制）
            _backupScanTimer = new Timer(
                _ => PerformBackupScan(),
                null,
                TimeSpan.FromMinutes(1),    // 首次延迟 1 分钟
                TimeSpan.FromMinutes(5));   // 之后每 5 分钟

            _logger?.LogInformation("✓ Backup scanning started (interval: 5 minutes)");
            _logger?.LogInformation("Session monitoring started successfully");
        }

        /// <summary>
        /// 停止监控
        /// </summary>
        public void StopMonitoring()
        {
            if (_eventReceiver == null && _backupScanTimer == null)
            {
                return;
            }

            _logger?.LogInformation("Stopping session monitoring...");

            // 停止事件监听
            if (_eventReceiver != null)
            {
                try
                {
                    _eventReceiver.Stop();
                    _eventReceiver.Dispose();
                    _eventReceiver = null;
                    _logger?.LogInformation("✓ Event receiver stopped");
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error stopping event receiver");
                }
            }

            // 停止备份扫描
            if (_backupScanTimer != null)
            {
                _backupScanTimer.Dispose();
                _backupScanTimer = null;
                _logger?.LogInformation("✓ Backup scanning stopped");
            }

            LogStatistics();
            _logger?.LogInformation("Session monitoring stopped");
        }

        /// <summary>
        /// 初始扫描现有 Session
        /// </summary>
        private void ScanExistingSessions()
        {
            try
            {
                var sessions = EnumerateSessions();
                _logger?.LogInformation("Found {Count} existing sessions", sessions.Count);

                foreach (var session in sessions)
                {
                    _sessions[session.SessionId] = session;
                    _logger?.LogInformation("  - Session {SessionId}: {Domain}\\{UserName}",
                        session.SessionId, session.DomainName, session.UserName);

                    OnSessionCreated(session);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error scanning existing sessions");
            }
        }

        /// <summary>
        /// 备份扫描（兜底机制）
        /// </summary>
        private void PerformBackupScan()
        {
            Interlocked.Increment(ref _backupScanCount);

            try
            {
                _logger?.LogDebug("Performing backup session scan #{ScanCount}...", _backupScanCount);

                var currentSessions = EnumerateSessions();
                var currentIds = new HashSet<int>(currentSessions.Select(s => s.SessionId));
                var trackedIds = new HashSet<int>(_sessions.Keys);

                var foundIssues = false;

                // 检查是否有遗漏的 Session（事件未触发）
                foreach (var session in currentSessions)
                {
                    if (!trackedIds.Contains(session.SessionId))
                    {
                        foundIssues = true;
                        Interlocked.Increment(ref _backupScanFoundCount);

                        _logger?.LogWarning("⚠ Backup scan found untracked session: {SessionId} ({UserName}) - event was missed!",
                            session.SessionId, session.UserName);

                        _sessions[session.SessionId] = session;
                        OnSessionCreated(session);
                    }
                    else
                    {
                        // 更新最后见到时间
                        if (_sessions.TryGetValue(session.SessionId, out var existing))
                        {
                            existing.LastSeenAt = DateTime.UtcNow;
                        }
                    }
                }

                // 检查是否有已结束但未清理的 Session
                foreach (var sessionId in trackedIds)
                {
                    if (!currentIds.Contains(sessionId))
                    {
                        foundIssues = true;
                        Interlocked.Increment(ref _backupScanFoundCount);

                        _logger?.LogWarning("⚠ Backup scan found orphaned session: {SessionId} - cleanup was missed!",
                            sessionId);

                        if (_sessions.TryRemove(sessionId, out var orphanedSession))
                        {
                            OnSessionTerminated(orphanedSession);
                        }
                    }
                }

                if (foundIssues)
                {
                    _logger?.LogWarning("Backup scan found and fixed {Count} issues", _backupScanFoundCount);
                }
                else
                {
                    _logger?.LogDebug("✓ Backup scan completed, no issues found");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error in backup scan");
            }
        }

        /// <summary>
        /// 处理 Session 变更事件（由事件接收器调用）
        /// </summary>
        internal void HandleSessionChange(int sessionId, SessionChangeReason reason)
        {
            Interlocked.Increment(ref _eventNotificationCount);

            _logger?.LogDebug("⚡ Session event #{EventCount}: SessionId={SessionId}, Reason={Reason}",
                _eventNotificationCount, sessionId, reason);

            try
            {
                switch (reason)
                {
                    case SessionChangeReason.SessionLogon:
                        HandleSessionLogon(sessionId);
                        break;

                    case SessionChangeReason.SessionLogoff:
                        HandleSessionLogoff(sessionId);
                        break;

                    case SessionChangeReason.SessionLock:
                        HandleSessionLock(sessionId);
                        break;

                    case SessionChangeReason.SessionUnlock:
                        HandleSessionUnlock(sessionId);
                        break;

                    case SessionChangeReason.ConsoleConnect:
                        _logger?.LogDebug("Session {SessionId} console connected", sessionId);
                        break;

                    case SessionChangeReason.ConsoleDisconnect:
                        _logger?.LogDebug("Session {SessionId} console disconnected", sessionId);
                        break;

                    case SessionChangeReason.RemoteConnect:
                        _logger?.LogDebug("Session {SessionId} remote connected", sessionId);
                        break;

                    case SessionChangeReason.RemoteDisconnect:
                        _logger?.LogDebug("Session {SessionId} remote disconnected", sessionId);
                        break;

                    case SessionChangeReason.SessionRemoteControl:
                        _logger?.LogDebug("Session {SessionId} remote control status changed", sessionId);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error handling session change for session {SessionId}", sessionId);
            }
        }

        private void HandleSessionLogon(int sessionId)
        {
            try
            {
                var sessionInfo = GetSessionInfo(sessionId);
                if (sessionInfo == null)
                {
                    _logger?.LogDebug("Session {SessionId} has no user info (likely Session 0 or system)", sessionId);
                    return;
                }

                _sessions[sessionId] = sessionInfo;

                _logger?.LogInformation("⚡ Session {SessionId} logged on: {Domain}\\{UserName}",
                    sessionId, sessionInfo.DomainName, sessionInfo.UserName);

                OnSessionCreated(sessionInfo);
                OnSessionLogon(sessionInfo);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error handling session logon for {SessionId}", sessionId);
            }
        }

        private void HandleSessionLogoff(int sessionId)
        {
            if (_sessions.TryRemove(sessionId, out var session))
            {
                _logger?.LogInformation("⚡ Session {SessionId} logged off: {UserName}",
                    sessionId, session.UserName);

                OnSessionLogoff(session);
                OnSessionTerminated(session);
            }
            else
            {
                _logger?.LogWarning("Received logoff for untracked session {SessionId}", sessionId);
            }
        }

        private void HandleSessionLock(int sessionId)
        {
            if (_sessions.TryGetValue(sessionId, out var session))
            {
                var oldState = session.State;
                session.State = SessionState.Locked;
                session.LastSeenAt = DateTime.UtcNow;

                _logger?.LogInformation("⚡ Session {SessionId} locked (was: {OldState})",
                    sessionId, oldState);

                OnSessionLock(session);
            }
        }

        private void HandleSessionUnlock(int sessionId)
        {
            if (_sessions.TryGetValue(sessionId, out var session))
            {
                session.State = SessionState.Active;
                session.LastSeenAt = DateTime.UtcNow;

                _logger?.LogInformation("⚡ Session {SessionId} unlocked", sessionId);

                OnSessionUnlock(session);
            }
        }

        /// <summary>
        /// 获取指定 Session 的详细信息
        /// </summary>
        private SessionInfo? GetSessionInfo(int sessionId)
        {
            IntPtr serverHandle = IntPtr.Zero;
            try
            {
                serverHandle = WTSOpenServer("localhost");

                var userName = GetSessionValue(serverHandle, sessionId, WTS_INFO_CLASS.WTSUserName);
                var domainName = GetSessionValue(serverHandle, sessionId, WTS_INFO_CLASS.WTSDomainName);

                // Session 0 或系统 Session 没有用户名
                if (string.IsNullOrEmpty(userName))
                {
                    return null;
                }

                return new SessionInfo
                {
                    SessionId = sessionId,
                    UserName = userName,
                    DomainName = domainName,
                    State = SessionState.Active,
                    CreatedAt = DateTime.UtcNow,
                    LastSeenAt = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to get session info for session {SessionId}", sessionId);
                return null;
            }
            finally
            {
                if (serverHandle != IntPtr.Zero)
                {
                    WTSCloseServer(serverHandle);
                }
            }
        }

        private string GetSessionValue(IntPtr serverHandle, int sessionId, WTS_INFO_CLASS infoClass)
        {
            IntPtr buffer = IntPtr.Zero;
            try
            {
                if (WTSQuerySessionInformation(serverHandle, sessionId, infoClass, out buffer, out _))
                {
                    return Marshal.PtrToStringUni(buffer) ?? string.Empty;
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    WTSFreeMemory(buffer);
                }
            }
            return string.Empty;
        }

        /// <summary>
        /// 枚举所有活动 Session
        /// </summary>
        private List<SessionInfo> EnumerateSessions()
        {
            var sessions = new List<SessionInfo>();
            IntPtr serverHandle = IntPtr.Zero;

            try
            {
                serverHandle = WTSOpenServer("localhost");
                IntPtr sessionInfoPtr = IntPtr.Zero;
                int count = 0;

                if (WTSEnumerateSessions(serverHandle, 0, 1, ref sessionInfoPtr, ref count))
                {
                    try
                    {
                        var dataSize = Marshal.SizeOf(typeof(WTS_SESSION_INFO));
                        var current = sessionInfoPtr;

                        for (int i = 0; i < count; i++)
                        {
                            var sessionInfo = Marshal.PtrToStructure<WTS_SESSION_INFO>(current);

                            // 只关注用户 Session (跳过 Session 0)
                            if (sessionInfo.SessionId > 0)
                            {
                                var info = GetSessionInfo(sessionInfo.SessionId);
                                if (info != null)
                                {
                                    sessions.Add(info);
                                }
                            }

                            current = IntPtr.Add(current, dataSize);
                        }
                    }
                    finally
                    {
                        WTSFreeMemory(sessionInfoPtr);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error enumerating sessions");
            }
            finally
            {
                if (serverHandle != IntPtr.Zero)
                {
                    WTSCloseServer(serverHandle);
                }
            }

            return sessions;
        }

        /// <summary>
        /// 获取当前跟踪的所有 Session，包括可能不处于活动状态的会话。
        /// </summary>
        public IReadOnlyList<SessionInfo> GetActiveSessions()
        {
            return _sessions.Values.ToList();
        }

        /// <summary>
        /// 获取指定 Session
        /// </summary>
        public SessionInfo? GetSession(int sessionId)
        {
            _sessions.TryGetValue(sessionId, out var session);
            return session;
        }

        /// <summary>
        /// 获取监控统计信息
        /// </summary>
        public SessionMonitorStatistics GetStatistics()
        {
            return new SessionMonitorStatistics
            {
                EventNotificationCount = Interlocked.Read(ref _eventNotificationCount),
                BackupScanCount = Interlocked.Read(ref _backupScanCount),
                IssuesFoundByBackupScan = Interlocked.Read(ref _backupScanFoundCount),
                CurrentSessionCount = _sessions.Count,
                IsEventReceiverRunning = _eventReceiver != null,
                IsBackupScanRunning = _backupScanTimer != null
            };
        }

        private void LogStatistics()
        {
            var stats = GetStatistics();
            _logger?.LogInformation("Session monitoring statistics:");
            _logger?.LogInformation("  Event notifications received: {Count}", stats.EventNotificationCount);
            _logger?.LogInformation("  Backup scans performed: {Count}", stats.BackupScanCount);
            _logger?.LogInformation("  Issues found by backup scan: {Count}", stats.IssuesFoundByBackupScan);
            _logger?.LogInformation("  Effectiveness: {Percentage:F2}%",
                stats.EventNotificationCount > 0
                    ? (1.0 - (double)stats.IssuesFoundByBackupScan / stats.EventNotificationCount) * 100
                    : 100.0);
        }

        #region Event Triggers

        private void OnSessionCreated(SessionInfo session)
        {
            SessionCreated?.Invoke(this, new SessionEventArgs { Session = session });
        }

        private void OnSessionTerminated(SessionInfo session)
        {
            SessionTerminated?.Invoke(this, new SessionEventArgs { Session = session });
        }

        private void OnSessionLogon(SessionInfo session)
        {
            SessionLogon?.Invoke(this, new SessionEventArgs { Session = session });
        }

        private void OnSessionLogoff(SessionInfo session)
        {
            SessionLogoff?.Invoke(this, new SessionEventArgs { Session = session });
        }

        private void OnSessionLock(SessionInfo session)
        {
            SessionLock?.Invoke(this, new SessionEventArgs { Session = session });
        }

        private void OnSessionUnlock(SessionInfo session)
        {
            SessionUnlock?.Invoke(this, new SessionEventArgs { Session = session });
        }

        #endregion

        /// <summary>停止监控并清除跟踪的会话。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            StopMonitoring();
            _sessions.Clear();
        }

        #region Windows API

        [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr WTSOpenServer(string pServerName);

        [DllImport("wtsapi32.dll")]
        private static extern void WTSCloseServer(IntPtr hServer);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSEnumerateSessions(
            IntPtr hServer, int Reserved, int Version,
            ref IntPtr ppSessionInfo, ref int pCount);

        [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool WTSQuerySessionInformation(
            IntPtr hServer, int sessionId, WTS_INFO_CLASS wtsInfoClass,
            out IntPtr ppBuffer, out int pBytesReturned);

        [DllImport("wtsapi32.dll")]
        private static extern void WTSFreeMemory(IntPtr pMemory);

        [StructLayout(LayoutKind.Sequential)]
        private struct WTS_SESSION_INFO
        {
            public int SessionId;
            public IntPtr pWinStationName;
            public WTS_CONNECTSTATE_CLASS State;
        }

        private enum WTS_CONNECTSTATE_CLASS
        {
            WTSActive,
            WTSConnected,
            WTSConnectQuery,
            WTSShadow,
            WTSDisconnected,
            WTSIdle,
            WTSListen,
            WTSReset,
            WTSDown,
            WTSInit
        }

        private enum WTS_INFO_CLASS
        {
            WTSUserName = 5,
            WTSDomainName = 7
        }

        #endregion
    }

    /// <summary>
    /// Session 事件参数
    /// </summary>
    public class SessionEventArgs : EventArgs
    {
        /// <summary>事件关联的会话信息。</summary>
        public SessionInfo Session { get; set; } = null!;
    }

    /// <summary>
    /// Session 变更原因
    /// </summary>
    public enum SessionChangeReason
    {
        /// <summary>连接到控制台会话。</summary>
        ConsoleConnect = 0x1,
        /// <summary>断开控制台会话连接。</summary>
        ConsoleDisconnect = 0x2,
        /// <summary>连接到远程会话。</summary>
        RemoteConnect = 0x3,
        /// <summary>断开远程会话连接。</summary>
        RemoteDisconnect = 0x4,
        /// <summary>用户登录会话。</summary>
        SessionLogon = 0x5,
        /// <summary>用户注销会话。</summary>
        SessionLogoff = 0x6,
        /// <summary>会话已锁定。</summary>
        SessionLock = 0x7,
        /// <summary>会话已解锁。</summary>
        SessionUnlock = 0x8,
        /// <summary>会话的远程控制状态发生变化。</summary>
        SessionRemoteControl = 0x9
    }

    /// <summary>
    /// 监控统计信息
    /// </summary>
    public class SessionMonitorStatistics
    {
        /// <summary>收到的会话变更通知次数。</summary>
        public long EventNotificationCount { get; set; }
        /// <summary>执行备份扫描的次数。</summary>
        public long BackupScanCount { get; set; }
        /// <summary>备份扫描发现的未跟踪或未清理会话数量。</summary>
        public long IssuesFoundByBackupScan { get; set; }
        /// <summary>当前跟踪的会话数量。</summary>
        public int CurrentSessionCount { get; set; }
        /// <summary>管理器是否持有事件接收器。</summary>
        public bool IsEventReceiverRunning { get; set; }
        /// <summary>管理器是否持有备份扫描定时器。</summary>
        public bool IsBackupScanRunning { get; set; }
    }
}
