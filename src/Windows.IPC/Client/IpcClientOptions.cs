using System;
using System.Collections.Generic;
using System.Text;
using Windows.IPC.Client;
using Windows.IPC.Security;
using Windows.IPC.Transport;

namespace Windows.IPC.Client
{
    /// <summary>
    /// IPC客户端配置
    /// </summary>
    public class IpcClientOptions
    {
        /// <summary>
        /// 管道选项
        /// </summary>
        public IpcPipeOptions PipeOptions { get; set; } = new IpcPipeOptions();

        /// <summary>
        /// 重连策略
        /// </summary>
        public ReconnectionPolicy ReconnectionPolicy { get; set; } = ReconnectionPolicy.Default;

        /// <summary>
        /// 默认请求超时（毫秒）
        /// </summary>
        public int DefaultRequestTimeoutMs { get; set; } = 30000;

        /// <summary>
        /// 心跳间隔（毫秒，0表示禁用）
        /// </summary>
        public int HeartbeatIntervalMs { get; set; } = 30000;

        /// <summary>
        /// 心跳超时（毫秒）
        /// </summary>
        public int HeartbeatTimeoutMs { get; set; } = 10000;

        /// <summary>
        /// 安全选项快捷访问
        /// 等同于 PipeOptions.SecurityOptions
        /// </summary>
        public SecurityOptions SecurityOptions
        {
            get => PipeOptions.SecurityOptions;
            set => PipeOptions.SecurityOptions = value;
        }

        /// <summary>
        /// 应用层认证协议（用于 SharedSecret 等挑战-响应认证）
        /// 仅在 SecurityOptions.EnableAuthentication 为 true 时使用
        /// 如果未设置且需要认证，将自动从 SharedSecret 创建 SharedSecretAuthProtocol
        /// </summary>
        public IAuthenticationProtocol? AuthenticationProtocol { get; set; }
    }
}
