# 安全说明

## 报告漏洞

请勿在公开 issue、pull request 或日志中发布可利用的漏洞细节、凭据或密钥。如果仓库启用了 GitHub 私密漏洞报告，请通过仓库的 **Security → Advisories → Report a vulnerability** 联系维护者。若该入口不可用，请先通过公开 issue 请求私密联系方式，不要附带漏洞细节。

报告时请尽量提供受影响版本、复现步骤、攻击前提和潜在影响；请不要发送真实生产密钥或敏感数据。

## 配置注意事项

- 默认安全选项不启用传输加密、应用层认证、帧完整性校验或重放防护；管道 ACL 是独立的访问控制层。
- 根据运行账户、用户会话和应用容器需求选择最小权限的 ACL；不要将宽松 ACL 当作身份认证。
- 为实际部署选择适合的认证和传输保护。`SecurityOptions` 提供 `NegotiateStream`、`AesSharedKey` 和 `IntegrityOnly` 等配置入口；仅完整性保护不提供保密性。
- 在客户端和服务端配置兼容的安全选项。共享密钥应使用安全生成与存储方式，不要提交到代码仓库或日志中。
- 启用重放防护会使用 v2 帧格式，需考虑与旧客户端的兼容性。
- 根据业务限制消息大小、并发请求和队列容量，避免将默认值视作适用于所有负载。

这些配置建议不能替代针对实际部署环境的安全评估。具体默认值和验证逻辑请参考 [SecurityOptions](src/Windows.IPC/Security/SecurityOptions.cs) 与 [IpcPipeOptions](src/Windows.IPC/Transport/IpcPipeOptions.cs)。
