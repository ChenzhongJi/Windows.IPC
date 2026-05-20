using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.IPC.Server;
using Xunit;

namespace Windows.IPC.Tests
{
    /// <summary>
    /// 认证器单元测试
    /// 纯逻辑测试，不需要命名管道
    /// </summary>
    public class AuthenticatorTests
    {
        #region SignatureAuthenticator

        [Fact]
        public async Task SignatureAuthenticator_UnsignedClient_RequireSigned_Rejects()
        {
            var authenticator = new SignatureAuthenticator(
                new[] { "ABC123" }, requireSigned: true);

            var identity = new ClientIdentity
            {
                ProcessName = "TestApp",
                ProcessId = 1234,
                Signature = null // 未签名
            };

            var result = await authenticator.AuthenticateAsync(identity);
            Assert.False(result.IsAuthenticated);
            Assert.Contains("not digitally signed", result.RejectReason);
        }

        [Fact]
        public async Task SignatureAuthenticator_UnsignedClient_AllowUnsigned_Succeeds()
        {
            var authenticator = new SignatureAuthenticator(
                new[] { "ABC123" }, requireSigned: false);

            var identity = new ClientIdentity
            {
                ProcessName = "TestApp",
                ProcessId = 1234,
                Signature = null
            };

            var result = await authenticator.AuthenticateAsync(identity);
            Assert.True(result.IsAuthenticated);
        }

        [Fact]
        public async Task SignatureAuthenticator_InvalidSignature_Rejects()
        {
            var authenticator = new SignatureAuthenticator(
                new[] { "ABC123" }, requireSigned: true);

            var identity = new ClientIdentity
            {
                ProcessName = "TestApp",
                ProcessId = 1234,
                Signature = new DigitalSignature
                {
                    IsSigned = true,
                    IsValid = false, // 签名无效
                    Thumbprint = "ABC123"
                }
            };

            var result = await authenticator.AuthenticateAsync(identity);
            Assert.False(result.IsAuthenticated);
            Assert.Contains("invalid", result.RejectReason);
        }

        [Fact]
        public async Task SignatureAuthenticator_ThumbprintNotInWhitelist_Rejects()
        {
            var authenticator = new SignatureAuthenticator(
                new[] { "ABC123" }, requireSigned: true);

            var identity = new ClientIdentity
            {
                ProcessName = "TestApp",
                ProcessId = 1234,
                Signature = new DigitalSignature
                {
                    IsSigned = true,
                    IsValid = true,
                    Thumbprint = "XYZ789" // 不在白名单中
                }
            };

            var result = await authenticator.AuthenticateAsync(identity);
            Assert.False(result.IsAuthenticated);
            Assert.Contains("not authorized", result.RejectReason);
        }

        [Fact]
        public async Task SignatureAuthenticator_ValidThumbprintInWhitelist_Succeeds()
        {
            var authenticator = new SignatureAuthenticator(
                new[] { "ABC123" }, requireSigned: true);

            var identity = new ClientIdentity
            {
                ProcessName = "TestApp",
                ProcessId = 1234,
                Signature = new DigitalSignature
                {
                    IsSigned = true,
                    IsValid = true,
                    Thumbprint = "ABC123"
                }
            };

            var result = await authenticator.AuthenticateAsync(identity);
            Assert.True(result.IsAuthenticated);
        }

        [Fact]
        public async Task SignatureAuthenticator_ExpiredCertificate_Rejects()
        {
            var authenticator = new SignatureAuthenticator(
                new[] { "ABC123" }, requireSigned: true);

            var identity = new ClientIdentity
            {
                ProcessName = "TestApp",
                ProcessId = 1234,
                Signature = new DigitalSignature
                {
                    IsSigned = true,
                    IsValid = true,
                    Thumbprint = "ABC123",
                    NotBefore = DateTime.Now.AddDays(-100),
                    NotAfter = DateTime.Now.AddDays(-1) // 已过期
                }
            };

            var result = await authenticator.AuthenticateAsync(identity);
            Assert.False(result.IsAuthenticated);
            Assert.Contains("expired", result.RejectReason);
        }

        [Fact]
        public async Task SignatureAuthenticator_NotYetValidCertificate_Rejects()
        {
            var authenticator = new SignatureAuthenticator(
                new[] { "ABC123" }, requireSigned: true);

            var identity = new ClientIdentity
            {
                ProcessName = "TestApp",
                ProcessId = 1234,
                Signature = new DigitalSignature
                {
                    IsSigned = true,
                    IsValid = true,
                    Thumbprint = "ABC123",
                    NotBefore = DateTime.Now.AddDays(30) // 尚未生效
                }
            };

            var result = await authenticator.AuthenticateAsync(identity);
            Assert.False(result.IsAuthenticated);
            Assert.Contains("not yet valid", result.RejectReason);
        }

        #endregion

        #region PathWhitelistAuthenticator

        [Fact]
        public async Task PathWhitelist_UnknownPath_Rejects()
        {
            var authenticator = new PathWhitelistAuthenticator(
                new[] { @"C:\Program Files\MyApp\" });

            var identity = new ClientIdentity
            {
                ProcessName = "Malware",
                ProcessId = 1234,
                ExecutablePath = @"C:\Temp\malware.exe"
            };

            var result = await authenticator.AuthenticateAsync(identity);
            Assert.False(result.IsAuthenticated);
        }

        [Fact]
        public async Task PathWhitelist_WhitelistedPath_Succeeds()
        {
            var authenticator = new PathWhitelistAuthenticator(
                new[] { @"C:\Program Files\MyApp\" });

            var identity = new ClientIdentity
            {
                ProcessName = "MyApp",
                ProcessId = 1234,
                ExecutablePath = @"C:\Program Files\MyApp\myapp.exe"
            };

            var result = await authenticator.AuthenticateAsync(identity);
            Assert.True(result.IsAuthenticated);
        }

        [Fact]
        public async Task PathWhitelist_EmptyPath_Rejects()
        {
            var authenticator = new PathWhitelistAuthenticator(
                new[] { @"C:\Program Files\MyApp\" });

            var identity = new ClientIdentity
            {
                ProcessName = "Unknown",
                ProcessId = 1234,
                ExecutablePath = ""
            };

            var result = await authenticator.AuthenticateAsync(identity);
            Assert.False(result.IsAuthenticated);
        }

        #endregion

        #region CompanyAuthenticator

        [Fact]
        public async Task CompanyAuthenticator_UnknownCompany_Rejects()
        {
            var authenticator = new CompanyAuthenticator(
                new[] { "Contoso Ltd" });

            var identity = new ClientIdentity
            {
                ProcessName = "TestApp",
                ProcessId = 1234,
                VersionInfo = new FileVersionInfo
                {
                    CompanyName = "Evil Corp"
                }
            };

            var result = await authenticator.AuthenticateAsync(identity);
            Assert.False(result.IsAuthenticated);
        }

        [Fact]
        public async Task CompanyAuthenticator_KnownCompany_Succeeds()
        {
            var authenticator = new CompanyAuthenticator(
                new[] { "Contoso Ltd" });

            var identity = new ClientIdentity
            {
                ProcessName = "MyApp",
                ProcessId = 1234,
                VersionInfo = new FileVersionInfo
                {
                    CompanyName = "Contoso Ltd"
                }
            };

            var result = await authenticator.AuthenticateAsync(identity);
            Assert.True(result.IsAuthenticated);
        }

        [Fact]
        public async Task CompanyAuthenticator_NoCompanyName_Rejects()
        {
            var authenticator = new CompanyAuthenticator(
                new[] { "Contoso Ltd" });

            var identity = new ClientIdentity
            {
                ProcessName = "TestApp",
                ProcessId = 1234,
                VersionInfo = new FileVersionInfo
                {
                    CompanyName = "" // 空公司名
                }
            };

            var result = await authenticator.AuthenticateAsync(identity);
            Assert.False(result.IsAuthenticated);
        }

        [Fact]
        public async Task CompanyAuthenticator_CaseInsensitive_Matches()
        {
            var authenticator = new CompanyAuthenticator(
                new[] { "Contoso Ltd" });

            var identity = new ClientIdentity
            {
                ProcessName = "MyApp",
                ProcessId = 1234,
                VersionInfo = new FileVersionInfo
                {
                    CompanyName = "contoso ltd" // 大小写不同
                }
            };

            var result = await authenticator.AuthenticateAsync(identity);
            Assert.True(result.IsAuthenticated);
        }

        #endregion

        #region CompositeAuthenticator

        [Fact]
        public async Task CompositeAuthenticator_AllPass_Succeeds()
        {
            var authenticator = new CompositeAuthenticator(null,
                new AllowAllAuthenticator(),
                new AllowAllAuthenticator());

            var identity = new ClientIdentity
            {
                ProcessName = "TestApp",
                ProcessId = 1234
            };

            var result = await authenticator.AuthenticateAsync(identity);
            Assert.True(result.IsAuthenticated);
        }

        [Fact]
        public async Task CompositeAuthenticator_FirstFails_ShortCircuits()
        {
            // 第一个拒绝，第二个不应被调用
            var authenticator = new CompositeAuthenticator(null,
                new SignatureAuthenticator(new[] { "ABC" }, requireSigned: true),
                new AllowAllAuthenticator());

            var identity = new ClientIdentity
            {
                ProcessName = "TestApp",
                ProcessId = 1234,
                Signature = null // 未签名，第一个鉴权器会拒绝
            };

            var result = await authenticator.AuthenticateAsync(identity);
            Assert.False(result.IsAuthenticated);
            Assert.Contains("not digitally signed", result.RejectReason);
        }

        #endregion

        #region AllowAllAuthenticator

        [Fact]
        public async Task AllowAllAuthenticator_AlwaysSucceeds()
        {
            var authenticator = new AllowAllAuthenticator();

            var identity = new ClientIdentity
            {
                ProcessName = "Anything",
                ProcessId = 0
            };

            var result = await authenticator.AuthenticateAsync(identity);
            Assert.True(result.IsAuthenticated);
        }

        #endregion

        #region AuthenticationResult 工厂方法

        [Fact]
        public void AuthenticationResult_Success_WithRole()
        {
            var result = AuthenticationResult.Success("Admin");
            Assert.True(result.IsAuthenticated);
            Assert.Equal("Admin", result.Role);
            Assert.Null(result.RejectReason);
        }

        [Fact]
        public void AuthenticationResult_Success_WithoutRole()
        {
            var result = AuthenticationResult.Success();
            Assert.True(result.IsAuthenticated);
            Assert.Null(result.Role);
        }

        [Fact]
        public void AuthenticationResult_Reject_WithReason()
        {
            var result = AuthenticationResult.Reject("Not authorized");
            Assert.False(result.IsAuthenticated);
            Assert.Equal("Not authorized", result.RejectReason);
        }

        #endregion
    }
}
