using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Windows.IPC.Server
{
    /// <summary>
    /// 客户端身份信息
    /// </summary>
    public class ClientIdentity
    {
        /// <summary>
        /// 进程 ID
        /// </summary>
        public int ProcessId { get; set; }

        /// <summary>
        /// 进程名称
        /// </summary>
        public string ProcessName { get; set; } = string.Empty;

        /// <summary>
        /// 可执行文件路径
        /// </summary>
        public string ExecutablePath { get; set; } = string.Empty;

        /// <summary>
        /// 文件版本信息
        /// </summary>
        public FileVersionInfo? VersionInfo { get; set; }

        /// <summary>
        /// 数字签名信息
        /// </summary>
        public DigitalSignature? Signature { get; set; }

        /// <summary>
        /// 用户名
        /// </summary>
        public string UserName { get; set; } = string.Empty;

        /// <summary>
        /// 用户域
        /// </summary>
        public string UserDomain { get; set; } = string.Empty;

        /// <summary>
        /// 是否是管理员权限
        /// </summary>
        public bool IsElevated { get; set; }

        /// <summary>
        /// 连接时间
        /// </summary>
        public DateTime ConnectedAt { get; set; }
    }

    /// <summary>
    /// 文件版本信息
    /// </summary>
    public class FileVersionInfo
    {
        /// <summary>文件版本。</summary>
        public string FileVersion { get; set; } = string.Empty;
        /// <summary>产品版本。</summary>
        public string ProductVersion { get; set; } = string.Empty;
        /// <summary>文件版本元数据中的公司名称。</summary>
        public string CompanyName { get; set; } = string.Empty;
        /// <summary>产品名称。</summary>
        public string ProductName { get; set; } = string.Empty;
        /// <summary>文件描述。</summary>
        public string FileDescription { get; set; } = string.Empty;
    }

    /// <summary>
    /// 数字签名信息
    /// </summary>
    public class DigitalSignature
    {
        /// <summary>
        /// 是否已签名
        /// </summary>
        public bool IsSigned { get; set; }

        /// <summary>
        /// 签名是否有效
        /// </summary>
        public bool IsValid { get; set; }

        /// <summary>
        /// 签名者名称
        /// </summary>
        public string SignerName { get; set; } = string.Empty;

        /// <summary>
        /// 证书颁发者
        /// </summary>
        public string Issuer { get; set; } = string.Empty;

        /// <summary>
        /// 证书主题
        /// </summary>
        public string Subject { get; set; } = string.Empty;

        /// <summary>
        /// 证书序列号
        /// </summary>
        public string SerialNumber { get; set; } = string.Empty;

        /// <summary>
        /// 证书指纹 (SHA1)
        /// </summary>
        public string Thumbprint { get; set; } = string.Empty;

        /// <summary>
        /// 证书有效期开始
        /// </summary>
        public DateTime? NotBefore { get; set; }

        /// <summary>
        /// 证书有效期结束
        /// </summary>
        public DateTime? NotAfter { get; set; }
    }

    /// <summary>
    /// 鉴权结果
    /// </summary>
    public class AuthenticationResult
    {
        /// <summary>
        /// 是否通过鉴权
        /// </summary>
        public bool IsAuthenticated { get; set; }

        /// <summary>
        /// 拒绝原因
        /// </summary>
        public string? RejectReason { get; set; }

        /// <summary>
        /// 客户端角色/权限（可选）
        /// </summary>
        public string? Role { get; set; }

        /// <summary>
        /// 自定义元数据
        /// </summary>
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        /// <summary>
        /// 成功结果
        /// </summary>
        public static AuthenticationResult Success(string? role = null)
        {
            return new AuthenticationResult
            {
                IsAuthenticated = true,
                Role = role
            };
        }

        /// <summary>
        /// 失败结果
        /// </summary>
        public static AuthenticationResult Reject(string reason)
        {
            return new AuthenticationResult
            {
                IsAuthenticated = false,
                RejectReason = reason
            };
        }
    }

    /// <summary>
    /// 客户端鉴权器接口
    /// </summary>
    public interface IClientAuthenticator
    {
        /// <summary>
        /// 验证客户端
        /// </summary>
        /// <param name="identity">客户端身份信息</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>鉴权结果</returns>
        Task<AuthenticationResult> AuthenticateAsync(
            ClientIdentity identity,
            CancellationToken cancellationToken = default);
    }
}
