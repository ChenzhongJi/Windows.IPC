using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.IPC.Abstractions;

namespace Windows.IPC.Client
{
    /// <summary>
    /// 待处理的请求
    /// </summary>
    internal class PendingRequest : IDisposable
    {
        private readonly TaskCompletionSource<ResponseMessage> _tcs;
        private readonly CancellationTokenSource _timeoutCts;
        private readonly CancellationTokenRegistration _cancellationRegistration;
        private bool _disposed;

        public string MessageId { get; }
        public DateTime CreatedAt { get; }
        public Task<ResponseMessage> Task => _tcs.Task;

        public PendingRequest(string messageId, int timeoutMs, CancellationToken cancellationToken)
        {
            MessageId = messageId;
            CreatedAt = DateTime.UtcNow;
            _tcs = new TaskCompletionSource<ResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            _timeoutCts = new CancellationTokenSource(timeoutMs);

            // 超时处理
            _timeoutCts.Token.Register(() =>
            {
                _tcs.TrySetException(new Exceptions.TimeoutException(
                    $"Request {messageId} timed out after {timeoutMs}ms"));
            });

            // 外部取消处理
            _cancellationRegistration = cancellationToken.Register(() =>
            {
                _tcs.TrySetCanceled(cancellationToken);
            });
        }

        /// <summary>
        /// 设置响应结果
        /// </summary>
        public void SetResult(ResponseMessage response)
        {
            _tcs.TrySetResult(response);
        }

        /// <summary>
        /// 设置异常
        /// </summary>
        public void SetException(Exception exception)
        {
            _tcs.TrySetException(exception);
        }

        /// <summary>
        /// 取消请求
        /// </summary>
        public void Cancel()
        {
            _tcs.TrySetCanceled();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _timeoutCts?.Dispose();
            _cancellationRegistration.Dispose();
        }
    }
}
