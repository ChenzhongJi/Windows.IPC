using System;
using Windows.IPC.Security;
using Windows.IPC.Transport;

namespace Windows.IPC.Server
{
    /// <summary>
    /// IPC服务端配置
    /// </summary>
    public class IpcServerOptions
    {
        private int _maxConcurrentClients = 10;

        /// <summary>
        /// 管道选项
        /// </summary>
        public IpcPipeOptions PipeOptions { get; set; } = new IpcPipeOptions();

        /// <summary>
        /// 最大并发客户端数
        /// </summary>
        public int MaxConcurrentClients
        {
            get => _maxConcurrentClients;
            set
            {
                if (value < 1 || value > 254)
                {
                    throw new ArgumentOutOfRangeException(nameof(value),
                        $"MaxConcurrentClient must be between 1 and 255. Value: {value}");
                }
                _maxConcurrentClients = value;
            }
        }

        /// <summary>
        /// 是否自动处理心跳消息
        /// </summary>
        public bool AutoHandleHeartbeat { get; set; } = true;

        /// <summary>
        /// 管道安全模式
        /// </summary>
        public PipeSecurityMode SecurityMode { get; set; } = PipeSecurityMode.Default;

        /// <summary>
        /// 客户端鉴权器（null 表示不鉴权）
        /// </summary>
        public IClientAuthenticator? ClientAuthenticator { get; set; }

        /// <summary>
        /// 鉴权失败时是否记录详细信息
        /// </summary>
        public bool LogAuthenticationFailures { get; set; } = true;

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
