# Windows.IPC

Windows.IPC 是基于 Windows 命名管道的 .NET 进程间通信库。项目包含请求与响应、通知、连接管理、重连、消息序列化，以及可配置的管道访问控制和传输安全功能。

## 项目结构

- `src/Windows.IPC`：客户端、服务端、传输、协议和安全配置。
- `src/Windows.IPC.Contracts`：共享消息类型。
- `src/Windows.IPC.Extensions`：客户端扩展方法。
- `src/Windows.IPC.UWP`：UWP 客户端适配代码。
- `samples/`：控制台客户端、服务端和 UWP 客户端示例。
- `tests/`：单元、集成及性能测试。

## 环境要求

- Windows；核心库面向 .NET Standard 2.0，但使用了 Windows 命名管道和安全 API。
- .NET 9 SDK 用于以下构建与测试命令。控制台服务端和测试项目需要 .NET Framework 4.8 开发包；控制台客户端需要 .NET 8 运行时。
- UWP 示例面向 `net9.0-windows10.0.26100.0`，需要额外的 Windows SDK、应用打包工具和相应开发环境。UWP 示例及完整解决方案目前不在 CI 验证范围内。

以上是项目配置和当前验证范围，不代表所有 Windows、.NET 或应用模型组合均已验证。

## 快速开始

项目当前提供源码引用方式。打开两个 PowerShell 窗口，在仓库根目录分别运行：

```powershell
dotnet run --project samples/SimpleServer/SimpleServer.csproj
```

```powershell
dotnet run --project samples/SimpleClient/SimpleClient.csproj
```

先启动服务端，再在客户端输入消息；输入 `quit` 退出客户端。完整实现参见 [服务端示例](samples/SimpleServer/Program.cs) 和 [客户端示例](samples/SimpleClient/Program.cs)。示例侧重演示，部署前应检查管道访问权限和安全配置。

## 构建和测试

在 Windows 上从仓库根目录运行：

```powershell
dotnet build src/Windows.IPC/Windows.IPC.csproj --configuration Release
dotnet build samples/SimpleServer/SimpleServer.csproj --configuration Release
dotnet build samples/SimpleClient/SimpleClient.csproj --configuration Release
dotnet test tests/Windows.IPC.Tests/Windows.IPC.Tests.csproj --configuration Release
dotnet test tests/Windows.IPC.IntegrationTests/Windows.IPC.IntegrationTests.csproj --configuration Release
```

首次运行需要还原 NuGet 依赖。测试项目共享部分构建输出，应顺序运行；性能测试不包含在上述常规测试和 CI 中。

## 安全

默认 `SecurityOptions` 不启用加密、应用层认证、帧完整性校验或重放防护。命名管道的访问控制与这些选项是不同层面的保护；默认配置不应被视为适用于所有生产环境。

部署前请根据威胁模型配置管道 ACL、客户端身份验证及传输安全，保护共享密钥，并在客户端和服务端使用兼容的安全设置。请阅读 [安全说明](SECURITY.md) 和 [安全选项](src/Windows.IPC/Security/SecurityOptions.cs)。

## 文档与参与

- [技术文档](docs/README.md)：历史文档，包含尚待核对的旧名称和示例，请以当前源码及示例为准。
- [贡献指南](CONTRIBUTING.md)：开发、测试与提交建议。
- [安全说明](SECURITY.md)：漏洞报告与安全配置注意事项。

## 许可证

本项目采用 [MIT 许可证](LICENSE)。
