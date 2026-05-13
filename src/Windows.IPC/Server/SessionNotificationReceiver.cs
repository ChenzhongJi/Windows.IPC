using Microsoft.Extensions.Logging;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Windows.IPC.Server
{
    /// <summary>
    /// Session 通知接收器（纯 Win32 消息窗口）
    /// </summary>
    internal class SessionNotificationReceiver : IDisposable
    {
        private readonly SessionManager _manager;
        private readonly ILogger? _logger;
        private IntPtr _hwnd;
        private Thread? _messageThread;
        private ManualResetEventSlim? _threadStartedEvent;
        private bool _disposed;

        private const string WINDOW_CLASS_NAME = "YourAppSessionNotificationWindow";
        private const int NOTIFY_FOR_ALL_SESSIONS = 1;
        private const uint WM_CLOSE = 0x0010;
        private const uint WM_WTSSESSION_CHANGE = 0x02B1;
        private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

        // 保持委托引用，防止被 GC
        private WndProcDelegate? _wndProcDelegate;

        public SessionNotificationReceiver(SessionManager manager, ILogger? logger)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
            _logger = logger;
        }

        /// <summary>
        /// 启动事件接收器
        /// </summary>
        public void Start()
        {
            if (_messageThread != null)
            {
                _logger?.LogWarning("Session notification receiver already started");
                return;
            }

            _threadStartedEvent = new ManualResetEventSlim(false);

            _messageThread = new Thread(MessageThreadProc)
            {
                Name = "SessionNotificationThread",
                IsBackground = true
            };
            _messageThread.SetApartmentState(ApartmentState.STA);
            _messageThread.Start();

            // 等待线程启动完成
            if (!_threadStartedEvent.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Session notification receiver failed to start within timeout");
            }

            _logger?.LogDebug("Session notification receiver started");
        }

        /// <summary>
        /// 停止事件接收器
        /// </summary>
        public void Stop()
        {
            if (_messageThread == null)
            {
                return;
            }

            _logger?.LogDebug("Stopping session notification receiver...");

            // 发送关闭消息
            if (_hwnd != IntPtr.Zero)
            {
                PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }

            // 等待线程结束
            if (!_messageThread.Join(TimeSpan.FromSeconds(5)))
            {
                _logger?.LogWarning("Session notification thread did not stop gracefully");
                try
                {
                    _messageThread.Interrupt();
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Error interrupting message thread");
                }
            }

            _messageThread = null;
            _logger?.LogDebug("Session notification receiver stopped");
        }

        /// <summary>
        /// 消息线程处理
        /// </summary>
        private void MessageThreadProc()
        {
            try
            {
                _logger?.LogTrace("Message thread started");

                // 保持委托引用
                _wndProcDelegate = WndProc;

                // 注册窗口类
                var wc = new WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf(typeof(WNDCLASSEX)),
                    lpfnWndProc = _wndProcDelegate,
                    lpszClassName = WINDOW_CLASS_NAME,
                    hInstance = GetModuleHandle(null)
                };

                var atom = RegisterClassEx(ref wc);
                if (atom == 0)
                {
                    var error = Marshal.GetLastWin32Error();
                    _logger?.LogError("Failed to register window class. Error code: {Error}", error);
                    _threadStartedEvent?.Set();
                    return;
                }

                _logger?.LogTrace("Window class registered");

                // 创建消息窗口（不可见，仅用于接收消息）
                _hwnd = CreateWindowEx(
                    0,
                    WINDOW_CLASS_NAME,
                    "Session Notification Window",
                    0,
                    0, 0, 0, 0,
                    HWND_MESSAGE,  // 消息窗口
                    IntPtr.Zero,
                    wc.hInstance,
                    IntPtr.Zero);

                if (_hwnd == IntPtr.Zero)
                {
                    var error = Marshal.GetLastWin32Error();
                    _logger?.LogError("Failed to create window. Error code: {Error}", error);
                    _threadStartedEvent?.Set();
                    return;
                }

                _logger?.LogTrace("Message window created: {Handle}", _hwnd);

                // 注册 Session 通知
                if (!WTSRegisterSessionNotification(_hwnd, NOTIFY_FOR_ALL_SESSIONS))
                {
                    var error = Marshal.GetLastWin32Error();
                    _logger?.LogError("Failed to register session notification. Error code: {Error}", error);
                    DestroyWindow(_hwnd);
                    _hwnd = IntPtr.Zero;
                    _threadStartedEvent?.Set();
                    return;
                }

                _logger?.LogTrace("Session notification registered");

                // 通知启动完成
                _threadStartedEvent?.Set();

                // 消息循环
                _logger?.LogTrace("Entering message loop");
                MSG msg;
                int result;
                while ((result = GetMessage(out msg, IntPtr.Zero, 0, 0)) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }

                if (result < 0)
                {
                    var error = Marshal.GetLastWin32Error();
                    _logger?.LogWarning("GetMessage returned error: {Error}", error);
                }

                _logger?.LogTrace("Message loop exited");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error in session notification message thread");
                _threadStartedEvent?.Set();
            }
            finally
            {
                // 清理
                if (_hwnd != IntPtr.Zero)
                {
                    WTSUnRegisterSessionNotification(_hwnd);
                    DestroyWindow(_hwnd);
                    _hwnd = IntPtr.Zero;
                }

                _logger?.LogTrace("Message thread cleanup completed");
            }
        }

        /// <summary>
        /// 窗口过程
        /// </summary>
        private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_WTSSESSION_CHANGE)
            {
                var reason = (SessionChangeReason)wParam.ToInt32();
                var sessionId = lParam.ToInt32();

                try
                {
                    _manager.HandleSessionChange(sessionId, reason);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error in WndProc handling session change");
                }

                return IntPtr.Zero;
            }

            return DefWindowProc(hwnd, msg, wParam, lParam);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();
            _threadStartedEvent?.Dispose();
            _wndProcDelegate = null;
        }

        #region Win32 API

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public int cbSize;
            public int style;
            [MarshalAs(UnmanagedType.FunctionPtr)]
            public WndProcDelegate lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(
            uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpmsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

        #endregion
    }
}
