using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Windows.IPC.Abstractions;

namespace Windows.IPC.Client
{
    /// <summary>
    /// 请求管理器 - 负责请求-响应匹配
    /// </summary>
    internal class RequestManager : IDisposable
    {
        private readonly ConcurrentDictionary<string, PendingRequest> _pendingRequests;
        private readonly ILogger? _logger;
        private readonly Timer _cleanupTimer;
        private bool _disposed;

        public RequestManager(ILogger? logger = null)
        {
            _pendingRequests = new ConcurrentDictionary<string, PendingRequest>();
            _logger = logger;

            // 每30秒清理一次过期请求
            _cleanupTimer = new Timer(CleanupExpiredRequests, null,
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        /// <summary>
        /// 注册请求
        /// </summary>
        public PendingRequest RegisterRequest(string messageId, int timeoutMs, CancellationToken cancellationToken)
        {
            var pendingRequest = new PendingRequest(messageId, timeoutMs, cancellationToken);

            if (!_pendingRequests.TryAdd(messageId, pendingRequest))
            {
                pendingRequest.Dispose();
                throw new InvalidOperationException($"Request {messageId} already registered");
            }

            _logger?.LogTrace("Registered request {MessageId}, timeout {TimeoutMs}ms", messageId, timeoutMs);
            return pendingRequest;
        }

        /// <summary>
        /// 处理响应
        /// </summary>
        public bool HandleResponse(ResponseMessage response)
        {
            if (_pendingRequests.TryRemove(response.MessageId, out var pendingRequest))
            {
                using (pendingRequest)
                {
                    if (response.IsSuccess)
                    {
                        pendingRequest.SetResult(response);
                        _logger?.LogTrace("Completed request {MessageId}", response.MessageId);
                    }
                    else
                    {
                        var exception = new Exceptions.RemoteException(
                            response.ErrorCode ?? "UNKNOWN_ERROR",
                            response.ErrorMessage ?? "Unknown error occurred");
                        pendingRequest.SetException(exception);
                        _logger?.LogWarning("Request {MessageId} failed: {ErrorCode} - {ErrorMessage}",
                            response.MessageId, response.ErrorCode, response.ErrorMessage);
                    }
                }
                return true;
            }

            _logger?.LogWarning("Received response for unknown request {MessageId}", response.MessageId);
            return false;
        }

        /// <summary>
        /// 取消所有待处理的请求
        /// </summary>
        public void CancelAllRequests(Exception? exception = null)
        {
            foreach (var kvp in _pendingRequests.ToArray())
            {
                if (_pendingRequests.TryRemove(kvp.Key, out var pendingRequest))
                {
                    using (pendingRequest)
                    {
                        if (exception != null)
                        {
                            pendingRequest.SetException(exception);
                        }
                        else
                        {
                            pendingRequest.Cancel();
                        }
                    }
                }
            }

            _logger?.LogDebug("Cancelled all pending requests");
        }

        /// <summary>
        /// 清理过期请求
        /// </summary>
        private void CleanupExpiredRequests(object? state)
        {
            try
            {
                var expiredRequests = _pendingRequests
                    .Where(kvp => kvp.Value.Task.IsCompleted)
                    .Select(kvp => kvp.Key)
                    .ToArray();

                foreach (var messageId in expiredRequests)
                {
                    if (_pendingRequests.TryRemove(messageId, out var pendingRequest))
                    {
                        pendingRequest.Dispose();
                    }
                }

                if (expiredRequests.Length > 0)
                {
                    _logger?.LogDebug("Cleaned up {Count} expired requests", expiredRequests.Length);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error during cleanup of expired requests");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _cleanupTimer?.Dispose();
            CancelAllRequests(new ObjectDisposedException(nameof(RequestManager)));

            _logger?.LogDebug("RequestManager disposed");
        }
    }
}
