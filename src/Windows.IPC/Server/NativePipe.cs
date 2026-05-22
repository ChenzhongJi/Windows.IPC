using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Windows.IPC.Server
{
    /// <summary>
    /// 原生管道创建辅助类（用于 .NET Framework / netstandard2.0 兼容）
    /// 
    /// 提供同时支持 SDDL 访问控制和 ImpersonationLevel 模拟级别的管道创建能力，
    /// 两者是正交的安全维度，应同时生效：
    /// - SDDL (安全描述符)：控制"谁能连接"管道（ACL 访问控制）
    /// - ImpersonationLevel：控制客户端连接后"能模拟服务端身份到什么程度"
    /// </summary>
    internal static class NativePipe
    {
        private const uint PIPE_ACCESS_DUPLEX = 0x00000003;
        private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        private const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;
        private const uint PIPE_TYPE_BYTE = 0x00000000;
        private const uint PIPE_READMODE_BYTE = 0x00000000;
        private const uint PIPE_WAIT = 0x00000000;
        private const uint PIPE_UNLIMITED_INSTANCES = 255;
        private const uint PIPE_REJECT_REMOTE_CLIENTS = 0x00000008;
        private const uint PIPE_ACCEPT_REMOTE_CLIENTS = 0x00000000;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafePipeHandle CreateNamedPipe(
            string lpName,
            uint dwOpenMode,
            uint dwPipeMode,
            uint nMaxInstances,
            uint nOutBufferSize,
            uint nInBufferSize,
            uint nDefaultTimeOut,
            IntPtr lpSecurityAttributes);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
            string StringSecurityDescriptor,
            uint StringSDRevision,
            out IntPtr SecurityDescriptor,
            out uint SecurityDescriptorSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool LocalFree(IntPtr hMem);

        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public int bInheritHandle;
        }

        /// <summary>
        /// 根据 ImpersonationLevel 计算 dwOpenMode 中的远程客户端标志
        /// </summary>
        private static uint GetRemoteClientsFlag(TokenImpersonationLevel impersonationLevel)
        {
            // TokenImpersonationLevel.None 表示不进行任何模拟，安全策略最严格
            // 设置 PIPE_REJECT_REMOTE_CLIENTS 阻止远程客户端通过 SMB 连接管道
            // 其他级别允许远程客户端（但模拟令牌权限不同）
            return impersonationLevel == TokenImpersonationLevel.None
                ? PIPE_REJECT_REMOTE_CLIENTS
                : PIPE_ACCEPT_REMOTE_CLIENTS;
        }

        /// <summary>
        /// 创建命名管道的核心实现（SDDL + ImpersonationLevel 同时生效）
        /// 
        /// 此方法通过 Win32 CreateNamedPipe API 创建管道，同时支持：
        /// - SDDL 安全描述符：控制管道的 DACL，决定哪些用户/组有权连接
        /// - ImpersonationLevel：控制客户端模拟令牌级别，影响 PIPE_REJECT_REMOTE_CLIENTS 标志
        /// 
        /// 对比 .NET 6+ 的 NamedPipeServerStream 构造函数仅支持 ImpersonationLevel，
        /// 此方法实现了 SDDL + ImpersonationLevel 的完整组合。
        /// </summary>
        /// <param name="pipeName">管道名称（不含 \\.\pipe\ 前缀）</param>
        /// <param name="maxInstances">最大实例数</param>
        /// <param name="bufferSize">缓冲区大小</param>
        /// <param name="impersonationLevel">模拟级别，影响远程客户端标志</param>
        /// <param name="sddl">SDDL 安全描述符（可选，为 null 时不设置 ACL）</param>
        /// <returns>创建的 NamedPipeServerStream</returns>
        public static NamedPipeServerStream Create(
            string pipeName,
            int maxInstances,
            int bufferSize,
            TokenImpersonationLevel impersonationLevel,
            string? sddl = null)
        {
            IntPtr securityDescriptor = IntPtr.Zero;
            SECURITY_ATTRIBUTES sa = new SECURITY_ATTRIBUTES();
            bool hasSddl = !string.IsNullOrEmpty(sddl);

            try
            {
                // 如果有 SDDL，转换为安全描述符
                if (hasSddl)
                {
                    if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                        sddl!,
                        1, // SDDL_REVISION_1
                        out securityDescriptor,
                        out _))
                    {
                        throw new InvalidOperationException(
                            $"Failed to convert SDDL. Error: {Marshal.GetLastWin32Error()}");
                    }

                    sa.nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES));
                    sa.lpSecurityDescriptor = securityDescriptor;
                    sa.bInheritHandle = 0;
                }

                // 构建 dwOpenMode 标志组合：
                // - PIPE_ACCESS_DUPLEX：双向通信
                // - FILE_FLAG_OVERLAPPED：异步 I/O
                // - FILE_FLAG_WRITE_THROUGH：写入不经过系统缓存（与 PipeOptions.WriteThrough 对应）
                // - PIPE_REJECT_REMOTE_CLIENTS / PIPE_ACCEPT_REMOTE_CLIENTS：由 ImpersonationLevel 决定
                uint openMode = PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | FILE_FLAG_WRITE_THROUGH
                    | GetRemoteClientsFlag(impersonationLevel);

                uint pipeMode = PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT;

                // 固定 SECURITY_ATTRIBUTES（仅在需要时）
                GCHandle saHandle = default;
                IntPtr saPtr = IntPtr.Zero;

                if (hasSddl)
                {
                    saHandle = GCHandle.Alloc(sa, GCHandleType.Pinned);
                    saPtr = saHandle.AddrOfPinnedObject();
                }

                try
                {
                    var handle = CreateNamedPipe(
                        $@"\\.\pipe\{pipeName}",
                        openMode,
                        pipeMode,
                        (uint)maxInstances,
                        (uint)bufferSize,
                        (uint)bufferSize,
                        0,
                        saPtr);

                    if (handle.IsInvalid)
                    {
                        throw new InvalidOperationException(
                            $"Failed to create named pipe" +
                            $" (ImpersonationLevel={impersonationLevel}, SDDL={(hasSddl ? "provided" : "none")})" +
                            $". Error: {Marshal.GetLastWin32Error()}");
                    }

                    return new NamedPipeServerStream(
                        PipeDirection.InOut,
                        true,  // isAsync
                        false, // isConnected
                        handle);
                }
                finally
                {
                    if (hasSddl)
                    {
                        saHandle.Free();
                    }
                }
            }
            finally
            {
                if (securityDescriptor != IntPtr.Zero)
                {
                    LocalFree(securityDescriptor);
                }
            }
        }

        /// <summary>
        /// 使用 SDDL + ImpersonationLevel 创建命名管道
        /// </summary>
        /// <param name="pipeName">管道名称（不含 \\.\pipe\ 前缀）</param>
        /// <param name="maxInstances">最大实例数</param>
        /// <param name="bufferSize">缓冲区大小</param>
        /// <param name="sddl">SDDL 安全描述符</param>
        /// <param name="impersonationLevel">模拟级别（默认 Identification）</param>
        /// <returns>创建的 NamedPipeServerStream</returns>
        public static NamedPipeServerStream CreateWithSddl(
            string pipeName,
            int maxInstances,
            int bufferSize,
            string sddl,
            TokenImpersonationLevel impersonationLevel = TokenImpersonationLevel.Identification)
        {
            return Create(pipeName, maxInstances, bufferSize, impersonationLevel, sddl);
        }

        /// <summary>
        /// 使用 PipeSecurity + ImpersonationLevel 创建命名管道
        /// </summary>
        /// <param name="pipeName">管道名称（不含 \\.\pipe\ 前缀）</param>
        /// <param name="maxInstances">最大实例数</param>
        /// <param name="bufferSize">缓冲区大小</param>
        /// <param name="pipeSecurity">管道安全配置</param>
        /// <param name="impersonationLevel">模拟级别（默认 Identification）</param>
        /// <returns>创建的 NamedPipeServerStream</returns>
        public static NamedPipeServerStream CreateWithSecurity(
            string pipeName,
            int maxInstances,
            int bufferSize,
            PipeSecurity pipeSecurity,
            TokenImpersonationLevel impersonationLevel = TokenImpersonationLevel.Identification)
        {
            var sddl = pipeSecurity.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            return Create(pipeName, maxInstances, bufferSize, impersonationLevel, sddl);
        }

        /// <summary>
        /// 仅使用 ImpersonationLevel 创建命名管道（无自定义 SDDL）
        /// </summary>
        /// <param name="pipeName">管道名称（不含 \\.\pipe\ 前缀）</param>
        /// <param name="maxInstances">最大实例数</param>
        /// <param name="bufferSize">缓冲区大小</param>
        /// <param name="impersonationLevel">模拟级别</param>
        /// <returns>创建的 NamedPipeServerStream</returns>
        public static NamedPipeServerStream CreateWithImpersonationLevel(
            string pipeName,
            int maxInstances,
            int bufferSize,
            TokenImpersonationLevel impersonationLevel)
        {
            return Create(pipeName, maxInstances, bufferSize, impersonationLevel, sddl: null);
        }
    }
}
