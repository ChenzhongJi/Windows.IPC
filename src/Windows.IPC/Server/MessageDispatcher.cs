using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.IPC.Abstractions;
using Windows.IPC.Server;

namespace Windows.IPC.Server
{
    /// <summary>
    /// 消息分发器
    /// </summary>
    internal class MessageDispatcher : IDisposable
    {
        private readonly ConcurrentDictionary<string, IMessageHandler> _handlers;
        private readonly ILogger? _logger;
        private bool _disposed;

        public MessageDispatcher(ILogger? logger = null)
        {
            _handlers = new ConcurrentDictionary<string, IMessageHandler>();
            _logger = logger;
        }

        /// <summary>
        /// 注册消息处理器
        /// </summary>
        public void RegisterHandler(IMessageHandler handler)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            if (_handlers.TryAdd(handler.MessageType, handler))
            {
                _logger?.LogDebug("Registered handler for message type: {MessageType}", handler.MessageType);
            }
            else
            {
                _logger?.LogWarning("Handler for message type {MessageType} already registered", handler.MessageType);
            }
        }

        /// <summary>
        /// 注销消息处理器
        /// </summary>
        public bool UnregisterHandler(string messageType)
        {
            if (_handlers.TryRemove(messageType, out _))
            {
                _logger?.LogDebug("Unregistered handler for message type: {MessageType}", messageType);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 分发请求消息
        /// </summary>
        public async Task<ResponseMessage> DispatchRequestAsync(
            RequestMessage request,
            ConnectedClient client,
            CancellationToken cancellationToken)
        {
            // 检查是否有对应的处理器
            if (!_handlers.TryGetValue(request.MessageType, out var handler))
            {
                _logger?.LogWarning("No handler registered for message type: {MessageType}", request.MessageType);

                // 直接创建错误响应对象，不要通过序列化器
                return new ResponseMessage
                {
                    MessageId = request.MessageId,
                    MessageType = request.MessageType,
                    IsSuccess = false,
                    ErrorCode = "NO_HANDLER",
                    ErrorMessage = $"No handler registered for message type: {request.MessageType}"
                };
            }

            try
            {
                _logger?.LogTrace("Dispatching request {MessageId} of type {MessageType} to handler",
                    request.MessageId, request.MessageType);

                // 调用处理器
                var response = await handler.HandleRequestAsync(request, client, cancellationToken)
                    .ConfigureAwait(false);

                _logger?.LogTrace("Request {MessageId} handled successfully", request.MessageId);

                return response;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error handling request {MessageId} of type {MessageType}",
                    request.MessageId, request.MessageType);

                // 直接创建错误响应对象
                return new ResponseMessage
                {
                    MessageId = request.MessageId,
                    MessageType = request.MessageType,
                    IsSuccess = false,
                    ErrorCode = "HANDLER_ERROR",
                    ErrorMessage = $"Error processing request: {ex.Message}"
                };
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _handlers.Clear();
        }
    }
}
