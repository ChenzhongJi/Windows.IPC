using System;
using System.Collections.Generic;
using System.Text;

namespace Windows.IPC.Security
{
    /// <summary>
    /// IPC 安全配置选项
    /// 集中管理加密、认证、完整性校验和重放防护的所有配置
    /// </summary>
    public class SecurityOptions
    {
        /// <summary>
        /// 加密模式
        /// 默认 None（向后兼容，不加密）
        /// </summary>
        public EncryptionMode EncryptionMode { get; set; } = EncryptionMode.None;

        /// <summary>
        /// 共享密钥（用于 AesCbcHmac 模式和 SharedSecretAuthProtocol）
        /// 推荐 32 字节（256 位）或更长的密钥
        /// 也可传入密码字符串，将由 PBKDF2 派生为加密密钥
        /// </summary>
        public byte[]? SharedSecret { get; set; }

        /// <summary>
        /// 共享密钥字符串（便捷属性，内部转为 byte[]）
        /// 如果设置了 SharedSecretBytes，此属性被忽略
        /// </summary>
        public string? SharedSecretString
        {
            get => _sharedSecretString;
            set
            {
                _sharedSecretString = value;
                if (!string.IsNullOrEmpty(value))
                {
                    SharedSecret = System.Text.Encoding.UTF8.GetBytes(value);
                }
            }
        }

        private string? _sharedSecretString;

        /// <summary>
        /// 是否启用帧完整性校验（HMAC-SHA256）
        /// 当 EncryptionMode 不为 None 时，完整性由加密层保证，此选项自动忽略
        /// 当 EncryptionMode 为 None 时，可单独启用此选项以保证消息完整性
        /// </summary>
        public bool EnableFrameIntegrity { get; set; } = false;

        /// <summary>
        /// 是否启用重放攻击防护（帧序列号）
        /// 启用后，每帧携带递增的 SequenceId，接收方校验单调递增
        /// 注意：启用此选项将使用 v2 帧格式，与 v1 客户端不兼容
        /// </summary>
        public bool EnableReplayProtection { get; set; } = false;

        /// <summary>
        /// 是否启用应用层认证协议（挑战-响应）
        /// 当 EncryptionMode 为 NegotiateStream 时，认证由 NegotiateStream 处理，此选项可关闭
        /// 当 EncryptionMode 为 None 或 AesCbcHmac 时，建议启用此选项
        /// </summary>
        public bool EnableAuthentication { get; set; } = false;

        /// <summary>
        /// 认证超时（毫秒）
        /// 在此时间内客户端必须完成认证握手，否则连接被拒绝
        /// </summary>
        public int AuthenticationTimeoutMs { get; set; } = 10000;

        /// <summary>
        /// NegotiateStream 保护级别
        /// 仅在 EncryptionMode 为 NegotiateStream 时有效
        /// </summary>
        public ProtectionLevel ProtectionLevel { get; set; } = ProtectionLevel.EncryptAndSign;

        /// <summary>
        /// 允许的最大序列号跳跃距离
        /// 用于检测丢帧或恶意插入
        /// 如果收到的 SequenceId 与期望值的差超过此值，连接将被视为可疑
        /// 默认 100
        /// </summary>
        public int MaxSequenceGap { get; set; } = 100;

        /// <summary>
        /// 快捷方法：创建默认安全配置（无安全功能，向后兼容）
        /// </summary>
        public static SecurityOptions None => new SecurityOptions();

        /// <summary>
        /// 快捷方法：创建 NegotiateStream 安全配置
        /// </summary>
        public static SecurityOptions NegotiateStream => new SecurityOptions
        {
            EncryptionMode = EncryptionMode.NegotiateStream,
            EnableReplayProtection = true
        };

        /// <summary>
        /// 快捷方法：创建 AES 共享密钥安全配置
        /// </summary>
        public static SecurityOptions AesSharedKey(byte[] sharedSecret) => new SecurityOptions
        {
            EncryptionMode = EncryptionMode.AesCbcHmac,
            SharedSecret = sharedSecret,
            EnableReplayProtection = true,
            EnableAuthentication = true
        };

        /// <summary>
        /// 快捷方法：仅完整性校验（无加密，适合可信环境但需防篡改）
        /// </summary>
        public static SecurityOptions IntegrityOnly(byte[] hmacKey) => new SecurityOptions
        {
            EnableFrameIntegrity = true,
            SharedSecret = hmacKey,
            EnableReplayProtection = true
        };

        /// <summary>
        /// 验证安全配置的有效性
        /// </summary>
        public void Validate()
        {
            if (EncryptionMode == EncryptionMode.AesCbcHmac && SharedSecret == null)
            {
                throw new InvalidOperationException(
                    "SharedSecret must be set when EncryptionMode is AesCbcHmac");
            }

            if (EnableAuthentication && SharedSecret == null && EncryptionMode != EncryptionMode.NegotiateStream)
            {
                throw new InvalidOperationException(
                    "SharedSecret must be set when EnableAuthentication is true and EncryptionMode is not NegotiateStream");
            }

            if (EnableFrameIntegrity && SharedSecret == null && EncryptionMode == EncryptionMode.None)
            {
                throw new InvalidOperationException(
                    "SharedSecret must be set when EnableFrameIntegrity is true and EncryptionMode is None");
            }

            if (AuthenticationTimeoutMs <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(AuthenticationTimeoutMs),
                    "AuthenticationTimeoutMs must be positive");
            }

            if (MaxSequenceGap <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxSequenceGap),
                    "MaxSequenceGap must be positive");
            }
        }

        /// <summary>
        /// 判断是否启用了任何安全功能（非 None 模式）
        /// </summary>
        public bool IsSecurityEnabled =>
            EncryptionMode != EncryptionMode.None ||
            EnableFrameIntegrity ||
            EnableReplayProtection ||
            EnableAuthentication;

        /// <summary>
        /// 判断是否使用 v2 帧格式
        /// 当启用重放防护或帧完整性时，需要 v2 帧格式
        /// </summary>
        public bool UseV2FrameFormat => EnableReplayProtection || EnableFrameIntegrity;
    }

    /// <summary>
    /// 保护级别（用于 NegotiateStream）
    /// </summary>
    public enum ProtectionLevel
    {
        /// <summary>
        /// 仅认证，不加密不签名
        /// </summary>
        None = 0,

        /// <summary>
        /// 认证 + 签名（完整性校验）
        /// </summary>
        Sign = 1,

        /// <summary>
        /// 认证 + 加密 + 签名（最高安全级别）
        /// </summary>
        EncryptAndSign = 2
    }
}
