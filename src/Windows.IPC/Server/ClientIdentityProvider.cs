using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;

namespace Windows.IPC.Server
{
    /// <summary>
    /// 客户端身份信息提供者
    /// </summary>
    public static class ClientIdentityProvider
    {
        /// <summary>
        /// 从命名管道连接获取客户端身份信息
        /// </summary>
        public static ClientIdentity GetClientIdentity(
            NamedPipeServerStream pipeStream,
            ILogger? logger = null)
        {
            var identity = new ClientIdentity
            {
                ConnectedAt = DateTime.UtcNow
            };

            try
            {
                // 1. 获取进程 ID
                var processId = GetClientProcessId(pipeStream);
                if (processId == 0)
                {
                    logger?.LogWarning("Failed to get client process ID");
                    return identity;
                }

                identity.ProcessId = processId;

                // 2. 获取进程信息
                using var process = Process.GetProcessById(processId);
                identity.ProcessName = process.ProcessName;

                try
                {
                    identity.ExecutablePath = process.MainModule?.FileName ?? string.Empty;
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Failed to get executable path for process {ProcessId}", processId);
                }

                // 3. 获取用户信息
                try
                {
                    var userInfo = GetProcessUser(process);
                    identity.UserName = userInfo.UserName;
                    identity.UserDomain = userInfo.Domain;
                    identity.IsElevated = userInfo.IsElevated;
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Failed to get user info for process {ProcessId}", processId);
                }

                // 4. 获取文件版本信息
                if (!string.IsNullOrEmpty(identity.ExecutablePath) && File.Exists(identity.ExecutablePath))
                {
                    try
                    {
                        var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(identity.ExecutablePath);
                        identity.VersionInfo = new FileVersionInfo
                        {
                            FileVersion = versionInfo.FileVersion ?? string.Empty,
                            ProductVersion = versionInfo.ProductVersion ?? string.Empty,
                            CompanyName = versionInfo.CompanyName ?? string.Empty,
                            ProductName = versionInfo.ProductName ?? string.Empty,
                            FileDescription = versionInfo.FileDescription ?? string.Empty
                        };
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "Failed to get version info for {Path}", identity.ExecutablePath);
                    }

                    // 5. 获取数字签名信息
                    try
                    {
                        identity.Signature = GetDigitalSignature(identity.ExecutablePath);
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "Failed to get signature for {Path}", identity.ExecutablePath);
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error getting client identity");
            }

            return identity;
        }

        /// <summary>
        /// 获取客户端进程 ID
        /// </summary>
        private static int GetClientProcessId(NamedPipeServerStream pipeStream)
        {
            try
            {
                // 使用 Windows API 获取客户端进程 ID
                if (GetNamedPipeClientProcessId(pipeStream.SafePipeHandle.DangerousGetHandle(), out var processId))
                {
                    return (int)processId;
                }
            }
            catch
            {
                // Fallback: 返回 0 表示失败
            }

            return 0;
        }

        /// <summary>
        /// 获取进程用户信息
        /// </summary>
        private static (string UserName, string Domain, bool IsElevated) GetProcessUser(Process process)
        {
            try
            {
                var handle = process.Handle;
                if (OpenProcessToken(handle, TOKEN_QUERY, out var tokenHandle))
                {
                    try
                    {
                        // 获取用户信息
                        var userName = string.Empty;
                        var domain = string.Empty;

                        using (var identity = new WindowsIdentity(tokenHandle))
                        {
                            var parts = identity.Name.Split('\\');
                            if (parts.Length == 2)
                            {
                                domain = parts[0];
                                userName = parts[1];
                            }
                            else
                            {
                                userName = identity.Name;
                            }
                        }

                        // 检查是否提升权限
                        var isElevated = IsProcessElevated(tokenHandle);

                        return (userName, domain, isElevated);
                    }
                    finally
                    {
                        CloseHandle(tokenHandle);
                    }
                }
            }
            catch
            {
                // Ignore
            }

            return (string.Empty, string.Empty, false);
        }

        /// <summary>
        /// 检查进程是否以提升权限运行
        /// </summary>
        private static bool IsProcessElevated(IntPtr tokenHandle)
        {
            var elevationType = TOKEN_ELEVATION_TYPE.TokenElevationTypeDefault;
            var elevationTypeSize = Marshal.SizeOf((int)elevationType);
            var elevationTypePtr = Marshal.AllocHGlobal(elevationTypeSize);

            try
            {
                if (GetTokenInformation(tokenHandle, TOKEN_INFORMATION_CLASS.TokenElevationType,
                    elevationTypePtr, (uint)elevationTypeSize, out _))
                {
                    elevationType = (TOKEN_ELEVATION_TYPE)Marshal.ReadInt32(elevationTypePtr);
                    return elevationType == TOKEN_ELEVATION_TYPE.TokenElevationTypeFull;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(elevationTypePtr);
            }

            return false;
        }

        /// <summary>
        /// 获取文件的数字签名信息
        /// </summary>
        private static DigitalSignature? GetDigitalSignature(string filePath)
        {
            try
            {
                var certificate = X509Certificate.CreateFromSignedFile(filePath);
                var cert2 = new X509Certificate2(certificate);

                return new DigitalSignature
                {
                    IsSigned = true,
                    IsValid = cert2.Verify(),
                    SignerName = cert2.GetNameInfo(X509NameType.SimpleName, false),
                    Issuer = cert2.Issuer,
                    Subject = cert2.Subject,
                    SerialNumber = cert2.SerialNumber,
                    Thumbprint = cert2.Thumbprint,
                    NotBefore = cert2.NotBefore,
                    NotAfter = cert2.NotAfter
                };
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // 文件未签名
                return new DigitalSignature
                {
                    IsSigned = false,
                    IsValid = false
                };
            }
            catch
            {
                return null;
            }
        }

        #region Windows API

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientProcessId(
            IntPtr pipe,
            out uint clientProcessId);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(
            IntPtr processHandle,
            uint desiredAccess,
            out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(
            IntPtr tokenHandle,
            TOKEN_INFORMATION_CLASS tokenInformationClass,
            IntPtr tokenInformation,
            uint tokenInformationLength,
            out uint returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private const uint TOKEN_QUERY = 0x0008;

        private enum TOKEN_INFORMATION_CLASS
        {
            TokenElevationType = 18
        }

        private enum TOKEN_ELEVATION_TYPE
        {
            TokenElevationTypeDefault = 1,
            TokenElevationTypeFull,
            TokenElevationTypeLimited
        }

        #endregion
    }
}
