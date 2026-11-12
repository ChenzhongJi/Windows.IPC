# Windows.IPC 入门指南

本指南以仓库中的项目和控制台示例为准，介绍源码引用、运行示例和基本消息流程。项目目前不提供 NuGet 发布包；构建时仍需还原项目依赖的 NuGet 包。

## 环境

- Windows 和 .NET 9 SDK。
- 服务端示例及测试使用 .NET Framework 4.8，需要相应开发包；客户端示例使用 .NET 8，需要相应运行时。
- UWP 示例需要额外的 Windows SDK 和打包工具，不在当前 CI 验证范围内。具体要求见[项目首页](../README.md)。

## 运行控制台示例

在仓库根目录打开两个 PowerShell 窗口，先启动服务端：

```powershell
dotnet run --project samples/SimpleServer/SimpleServer.csproj
```

再启动客户端：

```powershell
dotnet run --project samples/SimpleClient/SimpleClient.csproj
```

两端示例均使用 `YourAppPipe` 管道。客户端连接后会请求服务端信息；输入文本可发送 Echo 请求，输入 `quit` 退出客户端。完整代码见[服务端](../samples/SimpleServer/Program.cs)和[客户端](../samples/SimpleClient/Program.cs)。

## 在项目中引用

将仓库保留在本地，从项目中添加对 [Windows.IPC.csproj](../src/Windows.IPC/Windows.IPC.csproj) 的项目引用。核心项目已引用共享消息契约；若要使用示例中的客户端扩展方法，也可引用 [Windows.IPC.Extensions.csproj](../src/Windows.IPC.Extensions/Windows.IPC.Extensions.csproj)。可参考两个控制台示例的项目文件中的 `ProjectReference` 配置。

核心库面向 .NET Standard 2.0，但使用 Windows 命名管道及安全 API。请结合目标运行环境自行验证兼容性；不要将示例环境视为完整兼容性列表。

## 请求与响应

1. 服务端实现 `MessageHandler<TRequest, TResponse>`，指定 `MessageType`，并在 `HandleAsync` 中处理请求。参考示例中的 `EchoHandler`。
2. 使用 `IpcServer.RegisterHandler` 注册处理器，然后调用 `StartAsync`。服务端和客户端的 `IpcPipeOptions.PipeName` 必须一致。
3. 客户端使用 `IpcClient.ConnectAsync` 连接，随后通过 `SendRequestAsync<TRequest, TResponse>` 发送请求。请求类型、响应类型和消息类型需要与服务端处理器一致。
4. 如果引用扩展项目，也可使用示例中的 `SendEchoStringAsync` 和 `GetServerInfoAsync`。结束时断开连接并释放客户端和服务端资源。

服务端可以通过 `BroadcastNotificationAsync` 发送通知，客户端通过 `NotificationReceived` 接收；通知负载需要按消息类型反序列化。相关处理见[控制台服务端示例](../samples/SimpleServer/Program.cs)和[客户端示例](../samples/SimpleClient/Program.cs)。

## 安全与配置

- 默认安全选项不启用加密、应用层认证、帧完整性校验或重放防护。管道访问控制是独立的保护层；示例配置不应直接视为生产配置。
- 根据部署环境配置管道 ACL、客户端鉴权和传输保护；客户端与服务端应使用兼容的安全选项。
- 根据实际负载配置连接超时、消息大小、并发数和队列容量。设置说明见 [IpcPipeOptions](../src/Windows.IPC/Transport/IpcPipeOptions.cs)。
- 路径鉴权使用字符串前缀匹配，文件版本中的公司名称也不能单独证明发布者身份。请根据威胁模型选择鉴权方式。

更多注意事项见[安全说明](../SECURITY.md)及 [SecurityOptions](../src/Windows.IPC/Security/SecurityOptions.cs)。

## 测试与其他示例

构建和测试命令见[项目首页](../README.md#构建和测试)。单元测试和集成测试应顺序运行；性能测试不在常规 CI 中。[UWP 客户端示例](../samples/UwpClient/)需要单独配置开发环境，其构建和运行未由当前 CI 验证。

2026 年 11 月 12 日，本地使用 .NET SDK 9.0.321 尝试以下命令时，UWP 示例构建失败，报告缺少入口点、`InitializeComponent` 及其他 XAML 生成成员。该结果仅说明这次本地构建未成功，不足以判断所有工具链环境的结果；在确认项目配置和完整构建链之前，不建议将此构建步骤加入必需 CI。

```powershell
dotnet build samples/UwpClient/UwpClient.csproj --configuration Release -p:Platform=x64
```

[旧技术文档](README.md)仅供历史参考，其中的旧项目名、安装命令和 API 可能不适用于当前版本。
