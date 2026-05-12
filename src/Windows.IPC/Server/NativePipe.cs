using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;

namespace Windows.IPC.Server
{
    /// <summary>
    /// 原生管道创建辅助类（用于 .NET Framework）
    /// </summary>
    internal static class NativePipe
    {
        private const uint PIPE_ACCESS_DUPLEX = 0x00000003;
        private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        private const uint PIPE_TYPE_BYTE = 0x00000000;
        private const uint PIPE_READMODE_BYTE = 0x00000000;
        private const uint PIPE_WAIT = 0x00000000;
        private const uint PIPE_UNLIMITED_INSTANCES = 255;

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
        /// 使用 SDDL 创建命名管道（.NET Framework 兼容）
        /// </summary>
        public static NamedPipeServerStream CreateWithSddl(
            string pipeName,
            int maxInstances,
            int bufferSize,
            string sddl)
        {
            IntPtr securityDescriptor = IntPtr.Zero;
            SECURITY_ATTRIBUTES sa = new SECURITY_ATTRIBUTES();

            try
            {
                // 将 SDDL 字符串转换为安全描述符
                if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                    sddl,
                    1, // SDDL_REVISION_1
                    out securityDescriptor,
                    out _))
                {
                    throw new InvalidOperationException(
                        $"Failed to convert SDDL. Error: {Marshal.GetLastWin32Error()}");
                }

                // 设置安全属性
                sa.nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES));
                sa.lpSecurityDescriptor = securityDescriptor;
                sa.bInheritHandle = 0;

                // 固定 SECURITY_ATTRIBUTES 结构
                GCHandle saHandle = GCHandle.Alloc(sa, GCHandleType.Pinned);
                try
                {
                    // 创建命名管道
                    var handle = CreateNamedPipe(
                        $@"\\.\pipe\{pipeName}",
                        PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
                        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                        (uint)maxInstances,
                        (uint)bufferSize,
                        (uint)bufferSize,
                        0,
                        saHandle.AddrOfPinnedObject());

                    if (handle.IsInvalid)
                    {
                        throw new InvalidOperationException(
                            $"Failed to create named pipe. Error: {Marshal.GetLastWin32Error()}");
                    }

                    // 使用 SafePipeHandle 创建 NamedPipeServerStream
                    return new NamedPipeServerStream(
                        PipeDirection.InOut,
                        true,  // isAsync
                        false, // isConnected
                        handle);
                }
                finally
                {
                    saHandle.Free();
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
        /// 使用 PipeSecurity 创建命名管道（.NET Framework 兼容）
        /// </summary>
        public static NamedPipeServerStream CreateWithSecurity(
            string pipeName,
            int maxInstances,
            int bufferSize,
            PipeSecurity pipeSecurity)
        {
            // 将 PipeSecurity 转换为 SDDL 字符串
            var sddl = pipeSecurity.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            return CreateWithSddl(pipeName, maxInstances, bufferSize, sddl);
        }
    }
}
