using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Windows.IPC.Server
{
    /// <summary>
    /// 基于签名的鉴权器
    /// </summary>
    public class SignatureAuthenticator : IClientAuthenticator
    {
        private readonly HashSet<string> _allowedThumbprints;
        private readonly ILogger? _logger;
        private readonly bool _requireSigned;

        public SignatureAuthenticator(
            IEnumerable<string> allowedThumbprints,
            bool requireSigned = true,
            ILogger? logger = null)
        {
            _allowedThumbprints = new HashSet<string>(
                allowedThumbprints.Select(t => t.Replace(" ", "").ToUpperInvariant()),
                StringComparer.OrdinalIgnoreCase);
            _requireSigned = requireSigned;
            _logger = logger;
        }

        public Task<AuthenticationResult> AuthenticateAsync(
            ClientIdentity identity,
            CancellationToken cancellationToken = default)
        {
            // 检查是否已签名
            if (identity.Signature == null || !identity.Signature.IsSigned)
            {
                if (_requireSigned)
                {
                    _logger?.LogWarning("Client {ProcessName} (PID: {ProcessId}) is not signed",
                        identity.ProcessName, identity.ProcessId);
                    return Task.FromResult(AuthenticationResult.Reject("Client executable is not digitally signed"));
                }
                else
                {
                    _logger?.LogWarning("Client {ProcessName} (PID: {ProcessId}) is not signed but allowed",
                        identity.ProcessName, identity.ProcessId);
                    return Task.FromResult(AuthenticationResult.Success());
                }
            }

            // 检查签名是否有效
            if (!identity.Signature.IsValid)
            {
                _logger?.LogWarning("Client {ProcessName} (PID: {ProcessId}) has invalid signature",
                    identity.ProcessName, identity.ProcessId);
                return Task.FromResult(AuthenticationResult.Reject("Digital signature is invalid"));
            }

            // 检查证书指纹
            var thumbprint = identity.Signature.Thumbprint.Replace(" ", "").ToUpperInvariant();
            if (!_allowedThumbprints.Contains(thumbprint))
            {
                _logger?.LogWarning("Client {ProcessName} (PID: {ProcessId}) signature not in whitelist. Thumbprint: {Thumbprint}",
                    identity.ProcessName, identity.ProcessId, thumbprint);
                return Task.FromResult(AuthenticationResult.Reject($"Certificate thumbprint not authorized: {thumbprint}"));
            }

            // 检查证书有效期
            if (identity.Signature.NotBefore.HasValue && DateTime.Now < identity.Signature.NotBefore.Value)
            {
                return Task.FromResult(AuthenticationResult.Reject("Certificate not yet valid"));
            }

            if (identity.Signature.NotAfter.HasValue && DateTime.Now > identity.Signature.NotAfter.Value)
            {
                return Task.FromResult(AuthenticationResult.Reject("Certificate has expired"));
            }

            _logger?.LogInformation("Client {ProcessName} (PID: {ProcessId}) authenticated successfully. Signer: {Signer}",
                identity.ProcessName, identity.ProcessId, identity.Signature.SignerName);

            return Task.FromResult(AuthenticationResult.Success());
        }
    }

    /// <summary>
    /// 基于进程路径的鉴权器
    /// </summary>
    public class PathWhitelistAuthenticator : IClientAuthenticator
    {
        private readonly HashSet<string> _allowedPaths;
        private readonly ILogger? _logger;

        public PathWhitelistAuthenticator(
            IEnumerable<string> allowedPaths,
            ILogger? logger = null)
        {
            _allowedPaths = new HashSet<string>(
                allowedPaths.Select(p => p.ToLowerInvariant()),
                StringComparer.OrdinalIgnoreCase);
            _logger = logger;
        }

        public Task<AuthenticationResult> AuthenticateAsync(
            ClientIdentity identity,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(identity.ExecutablePath))
            {
                _logger?.LogWarning("Client process path is unknown");
                return Task.FromResult(AuthenticationResult.Reject("Client process path is unknown"));
            }

            var normalizedPath = identity.ExecutablePath.ToLowerInvariant();

            if (!_allowedPaths.Any(allowed => normalizedPath.StartsWith(allowed)))
            {
                _logger?.LogWarning("Client {ProcessName} path {Path} not in whitelist",
                    identity.ProcessName, identity.ExecutablePath);
                return Task.FromResult(AuthenticationResult.Reject($"Process path not authorized: {identity.ExecutablePath}"));
            }

            _logger?.LogInformation("Client {ProcessName} (PID: {ProcessId}) authenticated by path",
                identity.ProcessName, identity.ProcessId);

            return Task.FromResult(AuthenticationResult.Success());
        }
    }

    /// <summary>
    /// 基于公司名称的鉴权器
    /// </summary>
    public class CompanyAuthenticator : IClientAuthenticator
    {
        private readonly HashSet<string> _allowedCompanies;
        private readonly ILogger? _logger;

        public CompanyAuthenticator(
            IEnumerable<string> allowedCompanies,
            ILogger? logger = null)
        {
            _allowedCompanies = new HashSet<string>(allowedCompanies, StringComparer.OrdinalIgnoreCase);
            _logger = logger;
        }

        public Task<AuthenticationResult> AuthenticateAsync(
            ClientIdentity identity,
            CancellationToken cancellationToken = default)
        {
            var companyName = identity.VersionInfo?.CompanyName;

            if (companyName == null || companyName.Length == 0)
            {
                _logger?.LogWarning("Client {ProcessName} has no company name in version info",
                    identity.ProcessName);
                return Task.FromResult(AuthenticationResult.Reject("No company name in version info"));
            }

            if (!_allowedCompanies.Contains(companyName))
            {
                _logger?.LogWarning("Client {ProcessName} company {Company} not in whitelist",
                    identity.ProcessName, companyName);
                return Task.FromResult(AuthenticationResult.Reject($"Company not authorized: {companyName}"));
            }

            _logger?.LogInformation("Client {ProcessName} (PID: {ProcessId}) authenticated by company name: {Company}",
                identity.ProcessName, identity.ProcessId, companyName);

            return Task.FromResult(AuthenticationResult.Success());
        }
    }

    /// <summary>
    /// 组合鉴权器（所有鉴权器都必须通过）
    /// </summary>
    public class CompositeAuthenticator : IClientAuthenticator
    {
        private readonly IClientAuthenticator[] _authenticators;
        private readonly ILogger? _logger;

        public CompositeAuthenticator(ILogger? logger, params IClientAuthenticator[] authenticators)
        {
            _authenticators = authenticators ?? throw new ArgumentNullException(nameof(authenticators));
            _logger = logger;
        }

        public async Task<AuthenticationResult> AuthenticateAsync(
            ClientIdentity identity,
            CancellationToken cancellationToken = default)
        {
            foreach (var authenticator in _authenticators)
            {
                var result = await authenticator.AuthenticateAsync(identity, cancellationToken);
                if (!result.IsAuthenticated)
                {
                    _logger?.LogWarning("Client authentication failed at {AuthenticatorType}: {Reason}",
                        authenticator.GetType().Name, result.RejectReason);
                    return result;
                }
            }

            return AuthenticationResult.Success();
        }
    }

    /// <summary>
    /// 总是允许的鉴权器（用于开发/测试）
    /// </summary>
    public class AllowAllAuthenticator : IClientAuthenticator
    {
        private readonly ILogger? _logger;

        public AllowAllAuthenticator(ILogger? logger = null)
        {
            _logger = logger;
        }

        public Task<AuthenticationResult> AuthenticateAsync(
            ClientIdentity identity,
            CancellationToken cancellationToken = default)
        {
            _logger?.LogWarning("AllowAllAuthenticator: Accepting client {ProcessName} (PID: {ProcessId}) without validation",
                identity.ProcessName, identity.ProcessId);

            return Task.FromResult(AuthenticationResult.Success());
        }
    }
}
