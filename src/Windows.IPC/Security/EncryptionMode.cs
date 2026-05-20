using System;
using System.Collections.Generic;
using System.Text;

namespace Windows.IPC.Security
{
    /// <summary>
    /// 传输加密模式
    /// </summary>
    public enum EncryptionMode
    {
        /// <summary>
        /// 无加密（默认，向后兼容）
        /// </summary>
        None = 0,

        /// <summary>
        /// 使用 Windows NegotiateStream（Kerberos/NTLM）
        /// 提供 Windows 集成认证 + 加密 + 完整性
        /// 仅 Windows 可用
        /// </summary>
        NegotiateStream = 1,

        /// <summary>
        /// 使用 AES-256-CBC + HMAC-SHA256 共享密钥加密
        /// 需要 SecurityOptions.SharedSecret 配置
        /// 提供 加密 + 完整性 + 认证（通过共享密钥）
        /// </summary>
        AesCbcHmac = 2
    }
}
