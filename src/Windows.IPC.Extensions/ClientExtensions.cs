using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.IPC.Client;
using Windows.IPC.Contracts;
using Windows.IPC.Contracts.Messages;

namespace Windows.IPC.Extensions
{
    /// <summary>
    /// IPC 客户端扩展方法
    /// </summary>
    public static class ClientExtensions
    {
        /// <summary>
        /// 发送 Echo 请求
        /// </summary>
        public static async Task<EchoResponse> SendEchoAsync(
            this IpcClient client,
            string message,
            int timeoutMs = 10000,
            CancellationToken cancellationToken = default)
        {
            var request = new EchoRequest { Message = message };
            return await client.SendRequestAsync<EchoRequest, EchoResponse>(
                request,
                MessageTypes.Echo,
                timeoutMs,
                cancellationToken);
        }

        /// <summary>
        /// 发送 Echo 请求（简化版，只返回字符串）
        /// </summary>
        public static async Task<string> SendEchoStringAsync(
            this IpcClient client,
            string message,
            int timeoutMs = 10000,
            CancellationToken cancellationToken = default)
        {
            var response = await client.SendEchoAsync(message, timeoutMs, cancellationToken);
            return response.Echo;
        }

        /// <summary>
        /// 获取服务器信息
        /// </summary>
        public static async Task<ServerInfoResponse> GetServerInfoAsync(
            this IpcClient client,
            int timeoutMs = 5000,
            CancellationToken cancellationToken = default)
        {
            var request = new ServerInfoRequest();
            return await client.SendRequestAsync<ServerInfoRequest, ServerInfoResponse>(
                request,
                MessageTypes.GetServerInfo,
                timeoutMs,
                cancellationToken);
        }

        /// <summary>
        /// 发送进度通知
        /// </summary>
        public static async Task SendProgressAsync(
            this IpcClient client,
            double percentage,
            string message,
            int currentStep = 0,
            int totalSteps = 0,
            string? operationId = null,
            CancellationToken cancellationToken = default)
        {
            var progress = new ProgressNotification
            {
                Percentage = percentage,
                Message = message,
                CurrentStep = currentStep,
                TotalSteps = totalSteps,
                OperationId = operationId
            };

            await client.SendNotificationAsync(
                progress,
                MessageTypes.Progress,
                cancellationToken);
        }
    }
}
