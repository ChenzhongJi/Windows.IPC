using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Windows.IPC.Client
{
    /// <summary>
    /// 重连策略
    /// </summary>
    public class ReconnectionPolicy
    {
        /// <summary>
        /// 是否启用自动重连
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// 初始重连延迟（毫秒）
        /// </summary>
        public int InitialDelayMs { get; set; } = 1000;

        /// <summary>
        /// 最大重连延迟（毫秒）
        /// </summary>
        public int MaxDelayMs { get; set; } = 30000;

        /// <summary>
        /// 最大重连次数（0表示无限）
        /// </summary>
        public int MaxAttempts { get; set; } = 0;

        /// <summary>
        /// 退避倍数
        /// </summary>
        public double BackoffMultiplier { get; set; } = 2.0;

        /// <summary>
        /// 计算下次重连延迟
        /// </summary>
        public int CalculateDelay(int attemptCount)
        {
            var delay = InitialDelayMs * Math.Pow(BackoffMultiplier, attemptCount - 1);
            return (int)Math.Min(delay, MaxDelayMs);
        }

        /// <summary>
        /// 默认策略
        /// </summary>
        public static ReconnectionPolicy Default => new ReconnectionPolicy();

        /// <summary>
        /// 禁用重连
        /// </summary>
        public static ReconnectionPolicy Disabled => new ReconnectionPolicy { Enabled = false };

        /// <summary>
        /// 快速重连（用于测试）
        /// </summary>
        public static ReconnectionPolicy Fast => new ReconnectionPolicy
        {
            InitialDelayMs = 100,
            MaxDelayMs = 1000,
            BackoffMultiplier = 1.5
        };
    }

    /// <summary>
    /// 重连器
    /// </summary>
    internal class Reconnector : IDisposable
    {
        private readonly ReconnectionPolicy _policy;
        private readonly Func<CancellationToken, Task<bool>> _reconnectFunc;
        private CancellationTokenSource? _cts;
        private Task? _reconnectTask;
        private int _attemptCount;
        private bool _disposed;

        public bool IsReconnecting => _reconnectTask != null && !_reconnectTask.IsCompleted;

        public event EventHandler<ReconnectionEventArgs>? ReconnectionStarted;
        public event EventHandler<ReconnectionEventArgs>? ReconnectionSucceeded;
        public event EventHandler<ReconnectionEventArgs>? ReconnectionFailed;

        public Reconnector(ReconnectionPolicy policy, Func<CancellationToken, Task<bool>> reconnectFunc)
        {
            _policy = policy ?? throw new ArgumentNullException(nameof(policy));
            _reconnectFunc = reconnectFunc ?? throw new ArgumentNullException(nameof(reconnectFunc));
        }

        /// <summary>
        /// 开始重连
        /// </summary>
        public void Start()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(Reconnector));

            if (IsReconnecting)
                return;

            if (!_policy.Enabled)
                return;

            _cts = new CancellationTokenSource();
            _attemptCount = 0;
            _reconnectTask = ReconnectLoopAsync(_cts.Token);
        }

        /// <summary>
        /// 停止重连
        /// </summary>
        public void Stop()
        {
            _cts?.Cancel();

            var taskToWait = _reconnectTask; // 获取当前任务的快照以避免竞态条件
            if (taskToWait != null && !taskToWait.IsCompleted)
            {
                try
                {
                    // 使用 Task.Run 避免在有同步上下文（如 UI 线程）时死锁
                    // 直接 GetAwaiter().GetResult() 在 UI 线程上会造成：
                    // 重连循环的 await 完成后需要回到 UI 线程续延，但 UI 线程被此调用阻塞
                    Task.Run(() => taskToWait).GetAwaiter().GetResult();
                }
                catch
                {
                    // 忽略重连任务的异常，因为我们正在停止它
                }
            }
            _reconnectTask = null;
        }

        /// <summary>
        /// 重连循环
        /// </summary>
        private async Task ReconnectLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                _attemptCount++;

                // 检查是否超过最大尝试次数
                if (_policy.MaxAttempts > 0 && _attemptCount > _policy.MaxAttempts)
                {
                    OnReconnectionFailed(new Exception($"Exceeded max reconnection attempts ({_policy.MaxAttempts})"));
                    return;
                }

                OnReconnectionStarted(_attemptCount);

                try
                {
                    // 尝试重连
                    var success = await _reconnectFunc(cancellationToken).ConfigureAwait(false);

                    if (success)
                    {
                        OnReconnectionSucceeded(_attemptCount);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    // 记录错误但继续尝试
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                }

                // 计算延迟
                var delay = _policy.CalculateDelay(_attemptCount);

                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private void OnReconnectionStarted(int attemptCount)
        {
            ReconnectionStarted?.Invoke(this, new ReconnectionEventArgs
            {
                AttemptCount = attemptCount
            });
        }

        private void OnReconnectionSucceeded(int attemptCount)
        {
            ReconnectionSucceeded?.Invoke(this, new ReconnectionEventArgs
            {
                AttemptCount = attemptCount
            });
        }

        private void OnReconnectionFailed(Exception exception)
        {
            ReconnectionFailed?.Invoke(this, new ReconnectionEventArgs
            {
                AttemptCount = _attemptCount,
                Exception = exception
            });
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();
            _cts?.Dispose();
        }
    }

    /// <summary>
    /// 重连事件参数
    /// </summary>
    public class ReconnectionEventArgs : EventArgs
    {
        public int AttemptCount { get; set; }
        public Exception? Exception { get; set; }
    }
}
