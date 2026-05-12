using System;
using System.Collections.Generic;
using System.Text;

namespace Windows.IPC.Transport
{
    /// <summary>
    /// 管道配置选项
    /// </summary>
    public class PipeOptions
    {
        /// <summary>
        /// 管道名称
        /// </summary>
        public string PipeName { get; set; } = string.Empty;

        /// <summary>
        /// 服务器模式下的最大连接数
        /// </summary>
        public int MaxServerInstances { get; set; } = 10;

        /// <summary>
        /// 连接超时（毫秒）
        /// </summary>
        public int ConnectionTimeoutMs { get; set; } = 5000;

        /// <summary>
        /// 读写超时（毫秒）
        /// </summary>
        public int ReadWriteTimeoutMs { get; set; } = 30000;

        /// <summary>
        /// 缓冲区大小
        /// </summary>
        public int BufferSize { get; set; } = 8192;

        /// <summary>
        /// 是否使用异步IO
        /// </summary>
        public bool UseAsync { get; set; } = true;

        /// <summary>
        /// 安全描述符（SDDL格式）
        /// </summary>
        public string? SecurityDescriptor { get; set; }

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
