using MessagePack;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Windows.IPC.UWP
{
    /// <summary>
    /// UWP IPC 扩展方法
    /// </summary>
    public static class UwpIpcExtensions
    {
        /// <summary>
        /// 发送 JSON 请求
        /// </summary>
        public static async Task<string> SendJsonRequestAsync(
            this UwpIpcClient client,
            string jsonRequest,
            string messageType,
            int timeoutMs = 30000)
        {
            var requestBytes = Encoding.UTF8.GetBytes(jsonRequest);
            var responseBytes = await client.SendRequestAsync(requestBytes, messageType, timeoutMs)
                .ConfigureAwait(false);
            return Encoding.UTF8.GetString(responseBytes);
        }

        /// <summary>
        /// 发送 MessagePack 序列化的请求
        /// </summary>
        public static async Task<TResponse> SendMessagePackRequestAsync<TRequest, TResponse>(
            this UwpIpcClient client,
            TRequest request,
            string messageType,
            int timeoutMs = 30000)
        {
            var requestBytes = MessagePackSerializer.Serialize(request);
            var responseBytes = await client.SendRequestAsync(requestBytes, messageType, timeoutMs)
                .ConfigureAwait(false);
            return MessagePackSerializer.Deserialize<TResponse>(responseBytes);
        }

        /// <summary>
        /// 发送 JSON 通知
        /// </summary>
        public static async Task SendJsonNotificationAsync(
            this UwpIpcClient client,
            string jsonNotification,
            string messageType)
        {
            var notificationBytes = Encoding.UTF8.GetBytes(jsonNotification);
            await client.SendNotificationAsync(notificationBytes, messageType)
                .ConfigureAwait(false);
        }
    }
}
