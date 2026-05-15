using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Windows.IPC.Contracts;
using Windows.IPC.Contracts.Messages;
using Windows.IPC.Serialization;
using Windows.IPC.UWP;

namespace Windows.IPC.Extensions
{
    /// <summary>
    /// UWP 客户端扩展方法
    /// </summary>
    public static class UwpClientExtensions
    {
        /// <summary>
        /// 发送 Echo 请求（返回完整响应）
        /// </summary>
        public static async Task<EchoResponse> SendEchoAsync(
            this UwpIpcClient client,
            string message,
            int timeoutMs = 10000)
        {
            var request = new EchoRequest { Message = message };
            return await client.SendRequestAsync<EchoRequest, EchoResponse>(
                request,
                MessageTypes.Echo,
                timeoutMs);
        }

        /// <summary>
        /// 发送 Echo 请求（只返回字符串）
        /// </summary>
        public static async Task<string> SendEchoStringAsync(
            this UwpIpcClient client,
            string message,
            int timeoutMs = 10000)
        {
            var response = await client.SendEchoAsync(message, timeoutMs);
            return response.Echo;
        }

        /// <summary>
        /// 获取服务器信息
        /// </summary>
        public static async Task<ServerInfoResponse> GetServerInfoAsync(
            this UwpIpcClient client,
            int timeoutMs = 5000)
        {
            var request = new ServerInfoRequest();
            return await client.SendRequestAsync<ServerInfoRequest, ServerInfoResponse>(
                request,
                MessageTypes.GetServerInfo,
                timeoutMs);
        }

        /// <summary>
        /// 发送进度通知
        /// </summary>
        public static async Task SendProgressAsync(
            this UwpIpcClient client,
            double percentage,
            string message,
            int currentStep = 0,
            int totalSteps = 0,
            string operationId = null)
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
                MessageSerializer.Serialize(progress),
                MessageTypes.Progress);
        }
    }
}
