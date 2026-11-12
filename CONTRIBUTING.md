# 贡献指南

感谢关注 Windows.IPC。提交修改前，请先阅读项目的 [MIT 许可证](LICENSE)，并在较大改动前通过 issue 讨论设计和兼容性影响。

## 开发环境

使用 Windows、.NET 9 SDK 和 .NET Framework 4.8 开发包。核心库面向 .NET Standard 2.0；其他示例可能有不同的运行时和 Windows SDK 要求，详见 [README](README.md)。

## 验证修改

在仓库根目录顺序运行相关测试：

```powershell
dotnet test tests/Windows.IPC.Tests/Windows.IPC.Tests.csproj --configuration Release
dotnet test tests/Windows.IPC.IntegrationTests/Windows.IPC.IntegrationTests.csproj --configuration Release
```

需要时也构建相关示例。涉及管道 ACL、身份验证或应用容器的修改，应说明使用的 Windows 版本、账户权限和验证方式。UWP 示例与性能测试不属于当前 CI 的验证范围，涉及这些部分时请单独说明测试结果和环境。

## 提交修改

- 保持改动聚焦，并为行为变化增加或更新测试。
- 修改公共 API、协议或安全默认值时，说明兼容性影响并同步文档。
- 在 pull request 中描述动机、实现、测试结果及已知限制。
- 不要提交凭据、密钥、构建输出或用户专属配置。
- 安全漏洞请遵循 [安全说明](SECURITY.md)，不要在公开 issue 中披露细节。
