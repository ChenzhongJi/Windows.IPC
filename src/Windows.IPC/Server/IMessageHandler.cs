using MessagePack;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.IPC.Abstractions;
using Windows.IPC.Serialization;

namespace Windows.IPC.Server
{
    /// <summary>
    /// 消息处理器接口
    /// </summary>
    public interface IMessageHandler
    {
        /// <summary>
        /// 支持的消息类型
        /// </summary>
        string MessageType { get; }

        /// <summary>
        /// 处理请求
        /// </summary>
        Task<ResponseMessage> HandleRequestAsync(
            RequestMessage request,
            ConnectedClient client,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// 泛型消息处理器基类
    /// </summary>
    public abstract class MessageHandler<TRequest, TResponse> : IMessageHandler
    {
        public abstract string MessageType { get; }

        public async Task<ResponseMessage> HandleRequestAsync(
            RequestMessage request,
            ConnectedClient client,
            CancellationToken cancellationToken)
        {
            try
            {
                // 1. 反序列化请求 Payload
                var requestData = MessageSerializer.Deserialize<TRequest>(request.Payload);

                // 2. 调用业务处理方法
                var responseData = await HandleAsync(requestData, client, cancellationToken)
                    .ConfigureAwait(false);

                // 3. 序列化响应数据
                var responsePayload = MessageSerializer.Serialize(responseData);

                // 4. 直接创建响应对象
                return new ResponseMessage
                {
                    MessageId = request.MessageId,
                    MessageType = MessageType,
                    IsSuccess = true,
                    Payload = responsePayload
                };
            }
            catch (Exception ex)
            {
                // 返回错误响应
                return new ResponseMessage
                {
                    MessageId = request.MessageId,
                    MessageType = MessageType,
                    IsSuccess = false,
                    ErrorCode = ex.GetType().Name,
                    ErrorMessage = ex.Message
                };
            }
        }

        /// <summary>
        /// 处理具体的业务逻辑
        /// </summary>
        protected abstract Task<TResponse> HandleAsync(
            TRequest request,
            ConnectedClient client,
            CancellationToken cancellationToken);
    }
}
