using System;
using System.Collections.Generic;
using System.Text;
using Windows.IPC.Security;

namespace Windows.IPC.Transport
{
    /// <summary>
    /// 管道配置选项
    /// </summary>
    public class IpcPipeOptions
    {
        /// <summary>
        /// 管道名称
        /// </summary>
        public string PipeName { get; set; } = string.Empty;

        /// <summary>
        /// 连接超时（毫秒）
        /// </summary>
        public int ConnectionTimeoutMs { get; set; } = 5000;

        /// <summary>
        /// 缓冲区大小
        /// </summary>
        public int BufferSize { get; set; } = 8192;

        /// <summary>
        /// 安全描述符（SDDL格式）
        /// </summary>
        public string? SecurityDescriptor { get; set; }

        /// <summary>
        /// 安全配置选项
        /// 配置加密、认证、完整性校验和重放防护
        /// 默认为 SecurityOptions.None（无安全功能，向后兼容）
        /// </summary>
        public SecurityOptions SecurityOptions { get; set; } = new SecurityOptions();

        /// <summary>
        /// 管道模拟级别
        /// 控制客户端可以模拟服务端的安全上下文到何种程度
        /// 默认 Identification（仅识别身份，不允许模拟）
        /// 重要：设为 Delegation 或 Impersonation 可能导致安全风险
        /// </summary>
        public System.Security.Principal.TokenImpersonationLevel ImpersonationLevel { get; set; }
            = System.Security.Principal.TokenImpersonationLevel.Identification;

        /// <summary>
        /// 最大消息大小（字节）
        /// 默认 10MB，超过此大小的消息将被拒绝
        /// 可根据业务需求降低此值以防止内存耗尽攻击
        /// </summary>
        public int MaxMessageSize { get; set; } = 10 * 1024 * 1024;

        /// <summary>
        /// 接收缓冲区最大容量（字节）
        /// 当缓冲区累积数据超过此值时，连接将被视为恶意并断开
        /// 默认 16MB，应大于 MaxMessageSize + 帧头开销
        /// </summary>
        public int MaxReceiveBufferSize { get; set; } = 16 * 1024 * 1024;

        /// <summary>
        /// 最大并发请求数（每个连接）
        /// 限制单个客户端同时在处理中的请求数量，防止资源耗尽
        /// 默认 100，设为 0 表示不限制
        /// </summary>
        public int MaxConcurrentRequestsPerClient { get; set; } = 100;

        /// <summary>
        /// 发送队列最大容量
        /// 当待发送消息数超过此值时，新的发送请求将等待（背压）
        /// 默认 1000，设为 0 表示不限制（不推荐）
        /// 
        /// 背压传导链：
        /// 调用者 → SendAsync（Channel满时阻塞）→ 发送循环 → 管道写入
        /// 当管道写入慢时，Channel 积满，SendAsync 开始阻塞，压力传导给调用者
        /// </summary>
        public int MaxSendQueueSize { get; set; } = 1000;

        /// <summary>
        /// 接收队列最大容量（背压阈值）
        /// 当已解码但未被消费的消息数超过此值时，读取循环暂停从管道读取，
        /// 利用传输层背压将压力传导给发送端
        /// 默认 256，设为 0 表示不限制（不推荐，失去接收端背压保护）
        /// 
        /// 端到端背压传导：
        /// 接收 Channel 满 → 读取循环暂停 → 管道内部缓冲区填满 →
        /// 发送端 WriteAsync 阻塞 → 发送端 SendAsync 阻塞 → 发送端自然降速
        /// </summary>
        public int MaxReceiveQueueSize { get; set; } = 256;

        /// <summary>
        /// 获取完整管道名称（包含前缀）
        /// </summary>
        public string GetFullPipeName()
        {
            // UWP 需要使用特殊的命名空间
            return $@"\\.\pipe\{PipeName}";
        }

        /// <summary>
        /// 默认安全配置（允许所有用户和AppContainer）
        /// </summary>
        public static string GetDefaultSecurityDescriptor()
        {
            // D: DACL
            // (A;;GA;;;SY) - SYSTEM 全部权限
            // (A;;GA;;;BA) - Administrators 全部权限
            // (A;;GRGW;;;IU) - Interactive Users 读写权限
            // (A;;GRGW;;;AC) - All App Containers 读写权限
            return "D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;IU)(A;;GRGW;;;AC)";
        }

        /// <summary>
        /// 宽松安全配置（用于跨Session通信）
        /// </summary>
        public static string GetRelaxedSecurityDescriptor()
        {
            // 额外允许：
            // (A;;GRGW;;;WD) - Everyone 读写权限
            return "D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;IU)(A;;GRGW;;;AC)(A;;GRGW;;;WD)";
        }
    }
}
