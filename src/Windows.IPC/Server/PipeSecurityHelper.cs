using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Windows.IPC.Server
{
    /// <summary>
    /// 管道安全辅助类
    /// </summary>
    public static class PipeSecurityHelper
    {
        /// <summary>
        /// SDDL: Everyone 完全访问
        /// </summary>
        public const string SddlEveryoneFull = "D:(A;;GA;;;WD)";

        /// <summary>
        /// SDDL: Everyone 读写
        /// </summary>
        public const string SddlEveryoneReadWrite = "D:(A;;GRGW;;;WD)";

        /// <summary>
        /// SDDL: SYSTEM + Administrators + Interactive Users
        /// </summary>
        public const string SddlRestricted = "D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;IU)";

        /// <summary>
        /// SDDL: UWP 兼容（包含 AppContainer）
        /// </summary>
        public const string SddlUwpCompatible =
            "D:" +
            "(A;;GA;;;SY)" +                    // SYSTEM: 完全访问
            "(A;;GA;;;BA)" +                    // Administrators: 完全访问
            "(A;;GRGW;;;IU)" +                  // Interactive Users: 读写
            "(A;;GRGW;;;S-1-15-2-1)";           // ALL_APP_PACKAGES: 读写

        /// <summary>
        /// 获取安全模式对应的 SDDL 字符串
        /// </summary>
        public static string GetSddlForMode(PipeSecurityMode mode)
        {
            return mode switch
            {
                PipeSecurityMode.Relaxed => SddlEveryoneReadWrite,
                PipeSecurityMode.Restricted => SddlRestricted,
                PipeSecurityMode.UwpCompatible => SddlUwpCompatible,
                _ => null
            };
        }

        /// <summary>
        /// 创建管道安全设置
        /// </summary>
        public static PipeSecurity CreatePipeSecurity(
            PipeSecurityMode mode = PipeSecurityMode.Default,
            ILogger logger = null)
        {
            if (mode == PipeSecurityMode.Default)
                return null;

            switch (mode)
            {
                case PipeSecurityMode.Relaxed:
                    return CreateRelaxedSecurityInternal(logger);

                case PipeSecurityMode.Restricted:
                    return CreateRestrictedSecurityInternal(logger);

                case PipeSecurityMode.UwpCompatible:
                    return CreateUwpCompatibleSecurityInternal(logger);

                default:
                    return null;
            }
        }

        /// <summary>
        /// 从 SDDL 字符串创建 PipeSecurity
        /// </summary>
        public static PipeSecurity CreateFromSddl(string sddl, ILogger logger = null)
        {
            if (string.IsNullOrWhiteSpace(sddl))
            {
                logger?.LogWarning("SDDL string is empty");
                return null;
            }

            try
            {
                var pipeSecurity = new PipeSecurity();
                pipeSecurity.SetSecurityDescriptorSddlForm(sddl);

                logger?.LogDebug("Created pipe security from SDDL");
                return pipeSecurity;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to create pipe security from SDDL: {Sddl}", sddl);
                return null;
            }
        }

        #region Private Methods

        private static PipeSecurity CreateRelaxedSecurityInternal(ILogger logger)
        {
            try
            {
                var pipeSecurity = new PipeSecurity();

                var everyoneSid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
                pipeSecurity.AddAccessRule(new PipeAccessRule(
                    everyoneSid,
                    PipeAccessRights.ReadWrite,
                    AccessControlType.Allow));

                logger?.LogDebug("Created relaxed pipe security");
                return pipeSecurity;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to create relaxed pipe security");
                return null;
            }
        }

        private static PipeSecurity CreateRestrictedSecurityInternal(ILogger logger)
        {
            try
            {
                var pipeSecurity = new PipeSecurity();

                // SYSTEM
                var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                pipeSecurity.AddAccessRule(new PipeAccessRule(
                    systemSid,
                    PipeAccessRights.FullControl,
                    AccessControlType.Allow));

                // Administrators
                var adminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                pipeSecurity.AddAccessRule(new PipeAccessRule(
                    adminsSid,
                    PipeAccessRights.FullControl,
                    AccessControlType.Allow));

                // Interactive Users
                var usersSid = new SecurityIdentifier(WellKnownSidType.InteractiveSid, null);
                pipeSecurity.AddAccessRule(new PipeAccessRule(
                    usersSid,
                    PipeAccessRights.ReadWrite,
                    AccessControlType.Allow));

                logger?.LogDebug("Created restricted pipe security");
                return pipeSecurity;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to create restricted pipe security");
                return null;
            }
        }

        private static PipeSecurity CreateUwpCompatibleSecurityInternal(ILogger logger)
        {
            return CreateFromSddl(SddlUwpCompatible, logger);
        }

        #endregion
    }

    /// <summary>
    /// 管道安全模式
    /// </summary>
    public enum PipeSecurityMode
    {
        /// <summary>
        /// 默认（使用系统默认安全设置）
        /// </summary>
        Default = 0,

        /// <summary>
        /// 宽松模式 - Everyone 可访问（开发/测试用）
        /// </summary>
        Relaxed = 1,

        /// <summary>
        /// 严格模式 - 仅管理员和交互用户
        /// </summary>
        Restricted = 2,

        /// <summary>
        /// UWP 兼容模式 - 包含 AppContainer 权限
        /// </summary>
        UwpCompatible = 3
    }
}
