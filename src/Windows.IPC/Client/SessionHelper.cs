using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Windows.IPC.Client
{
    /// <summary>
    /// Session 辅助类
    /// </summary>
    public static class SessionHelper
    {
        /// <summary>
        /// 获取当前进程的 Session ID
        /// </summary>
        public static int GetCurrentSessionId()
        {
            if (ProcessIdToSessionId(GetCurrentProcessId(), out var sessionId))
            {
                return (int)sessionId;
            }

            return 0;
        }

        /// <summary>
        /// 生成当前 Session 的管道名称
        /// </summary>
        public static string GetSessionPipeName(string basePipeName)
        {
            var sessionId = GetCurrentSessionId();
            return $"Global\\{basePipeName}_Session{sessionId}";
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ProcessIdToSessionId(uint dwProcessId, out uint pSessionId);
    }
}
