> **历史文档：**本页使用了旧项目名，部分安装方式、API 和示例可能已过时。请使用[入门指南](quick-start.md)，并以[项目首页](../README.md)、当前源码和示例为准；不要直接复制本页中的 NuGet 安装命令。

YourApp.IPC 技术文档
目录
项目概述
快速开始
架构设计
API 参考
消息定义
安全配置
最佳实践
故障排除
性能优化
FAQ
1. 项目概述
1.1 简介
YourApp.IPC 是一个基于命名管道的高性能进程间通信（IPC）库，专为 Windows 平台设计，完全支持 UWP/WinUI3 应用。

核心特性
✅ 类型安全 - 强类型消息定义，编译时检查
✅ 异步优先 - 全异步 API，高并发支持
✅ 自动重连 - 内置重连机制，指数退避策略
✅ UWP 兼容 - 完整支持 AppContainer 沙箱
✅ 安全可控 - 灵活的 ACL 配置
✅ 易于集成 - 清晰的 API，丰富的扩展方法
技术栈
组件	技术	版本
核心库	.NET Standard	2.0
序列化	MessagePack	2.5+
传输层	Named Pipes	-
UWP 支持	.NET Standard	2.0
最低 Windows 版本	Windows 10	1809 (17763)
1.2 适用场景
✅ 适合使用
Windows 服务与客户端应用通信
UWP/WinUI3 应用与后台服务通信
多进程架构的桌面应用
需要高性能本地通信的场景
需要跨用户会话通信
❌ 不适合使用
跨网络通信（使用 TCP/gRPC 替代）
跨平台需求（使用 gRPC/REST 替代）
浏览器与本地应用通信（使用 WebSocket 替代）
2. 快速开始
2.1 安装
方式 1: 项目引用
XML

<!-- 在你的 .csproj 中 -->
<ItemGroup>
  <ProjectReference Include="..\YourApp.IPC\src\YourApp.IPC\YourApp.IPC.csproj" />
  <ProjectReference Include="..\YourApp.IPC\src\YourApp.IPC.Contracts\YourApp.IPC.Contracts.csproj" />
</ItemGroup>
方式 2: NuGet 包（内部源）
Bash

# 添加包引用
dotnet add package YourApp.IPC
dotnet add package YourApp.IPC.Contracts

# UWP 项目额外需要
dotnet add package YourApp.IPC.Uwp
dotnet add package YourApp.IPC.Extensions
2.2 五分钟示例
服务端
csharp

using YourApp.IPC.Server;
using YourApp.IPC.Contracts;
using YourApp.IPC.Contracts.Messages;

// 1. 定义处理器
public class MyHandler : MessageHandler<EchoRequest, EchoResponse>
{
    public override string MessageType => MessageTypes.Echo;

    protected override Task<EchoResponse> HandleAsync(
        EchoRequest request,
        ConnectedClient client,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new EchoResponse
        {
            Echo = $"服务器收到: {request.Message}",
            ServerTime = DateTime.Now
        });
    }
}

// 2. 启动服务
var server = new IpcServer(new IpcServerOptions
{
    PipeOptions = new PipeOptions { PipeName = "MyApp" },
    SecurityMode = PipeSecurityMode.UwpCompatible
});

server.RegisterHandler(new MyHandler());
await server.StartAsync();

Console.WriteLine("服务端已启动，等待连接...");
客户端
csharp

using YourApp.IPC.Client;
using YourApp.IPC.Contracts.Messages;
using YourApp.IPC.Extensions;

// 1. 创建客户端
var client = new IpcClient(new IpcClientOptions
{
    PipeOptions = new PipeOptions { PipeName = "MyApp" }
});

// 2. 连接
await client.ConnectAsync();

// 3. 发送请求（使用扩展方法）
var response = await client.SendEchoStringAsync("你好");
Console.WriteLine(response); // 输出: 服务器收到: 你好
UWP 客户端
csharp

using YourApp.IPC.Uwp;
using YourApp.IPC.Extensions;

// 1. 创建客户端
var client = new UwpIpcClient("MyApp");

// 2. 订阅事件
client.StateChanged += (s, e) => 
    Debug.WriteLine($"状态: {e.NewState}");

// 3. 连接并发送
await client.ConnectAsync();
var response = await client.SendEchoStringAsync("你好");
3. 架构设计
3.1 分层架构
text

┌─────────────────────────────────────────────────┐
│            应用层（你的代码）                      │
├─────────────────────────────────────────────────┤
│         YourApp.IPC.Extensions                   │
│         （业务扩展方法）                          │
├─────────────────────────────────────────────────┤
│    YourApp.IPC.Uwp    │    YourApp.IPC          │
│    （UWP 适配）        │    （核心库）            │
├─────────────────────────────────────────────────┤
│         YourApp.IPC.Contracts                    │
│         （消息契约）                              │
├─────────────────────────────────────────────────┤
│              MessagePack                         │
│              （序列化）                           │
├─────────────────────────────────────────────────┤
│           Named Pipes (Win32 API)               │
└─────────────────────────────────────────────────┘
3.2 核心组件
传输层
csharp

// 命名管道抽象
public interface IConnection
{
    Task SendAsync(IMessage message);
    Task<IMessage?> ReceiveAsync();
    ConnectionState State { get; }
}

// 消息帧格式
[Magic(4)] [Length(4)] [Type(2)] [Payload(N)]
消息层
csharp

// 三种消息类型
public class RequestMessage : IMessage      // 请求-响应
public class ResponseMessage : IMessage     // 响应
public class NotificationMessage : IMessage // 通知（单向）
客户端层
csharp

IpcClient
├── ConnectionManager    // 连接管理
├── RequestManager       // 请求-响应匹配
└── Reconnector         // 重连机制
服务端层
csharp

IpcServer
├── ClientManager       // 客户端连接池
├── MessageDispatcher   // 消息路由
└── MessageHandler<>    // 业务处理器
3.3 消息流程
请求-响应流程
text

┌─────────┐                              ┌─────────┐
│ Client  │                              │ Server  │
└────┬────┘                              └────┬────┘
     │                                        │
     │  1. SendRequestAsync()                │
     ├────────────────────────────────────>  │
     │     RequestMessage {                  │
     │       MessageId: "abc123"             │
     │       MessageType: "Echo"             │
     │       Payload: [...]                  │
     │     }                                 │
     │                                       │
     │                    2. DispatchRequest │
     │                    3. HandleAsync()   │
     │                                       │
     │  4. ResponseMessage                   │
     │  <────────────────────────────────────┤
     │     {                                 │
     │       MessageId: "abc123"             │
     │       IsSuccess: true                 │
     │       Payload: [...]                  │
     │     }                                 │
     │                                       │
     │  5. Match by MessageId                │
     │  6. Complete Task                     │
     │                                       │
通知流程
text

┌─────────┐                              ┌─────────┐
│ Server  │                              │ Client  │
└────┬────┘                              └────┬────┘
     │                                        │
     │  BroadcastNotificationAsync()         │
     ├────────────────────────────────────>  │
     │     NotificationMessage {              │
     │       MessageType: "Progress"          │
     │       Payload: [...]                   │
     │     }                                  │
     │                                       │
     │           Trigger NotificationReceived │
     │                                       │
3.4 线程模型
服务端
监听线程 (2-3个): 接受新连接
处理线程 (ThreadPool): 处理每个客户端消息
广播是并行的: 使用 Task.WhenAll
客户端
接收线程 (1个): 持续接收消息
发送是线程安全的: 使用 SemaphoreSlim
重连线程 (1个): 后台重连
4. API 参考
4.1 服务端 API
IpcServer
csharp

public class IpcServer : IDisposable
{
    // 构造函数
    public IpcServer(IpcServerOptions options, ILogger? logger = null);

    // 生命周期
    public Task StartAsync(CancellationToken cancellationToken = default);
    public Task StopAsync();

    // 消息处理
    public void RegisterHandler(IMessageHandler handler);
    public bool UnregisterHandler(string messageType);

    // 通知
    public Task<bool> SendNotificationAsync<T>(
        string clientId, T notification, string messageType, 
        CancellationToken cancellationToken = default);
    
    public Task<int> BroadcastNotificationAsync<T>(
        T notification, string messageType,
        Func<ConnectedClient, bool>? predicate = null,
        CancellationToken cancellationToken = default);

    // 状态
    public bool IsRunning { get; }
    public int ConnectedClientCount { get; }
    public IReadOnlyList<ConnectedClient> GetConnectedClients();

    // 事件
    public event EventHandler<ClientConnectedEventArgs> ClientConnected;
    public event EventHandler<ClientDisconnectedEventArgs> ClientDisconnected;
    public event EventHandler<NotificationReceivedEventArgs> NotificationReceived;
}
IpcServerOptions
csharp

public class IpcServerOptions
{
    // 管道配置
    public PipeOptions PipeOptions { get; set; }
    
    // 最大并发客户端数
    public int MaxConcurrentClients { get; set; } = 10;
    
    // 是否自动处理心跳
    public bool AutoHandleHeartbeat { get; set; } = true;
    
    // 安全模式
    public PipeSecurityMode SecurityMode { get; set; } = PipeSecurityMode.Default;
}
MessageHandler<TRequest, TResponse>
csharp

public abstract class MessageHandler<TRequest, TResponse> : IMessageHandler
{
    // 消息类型标识
    public abstract string MessageType { get; }

    // 业务逻辑
    protected abstract Task<TResponse> HandleAsync(
        TRequest request,
        ConnectedClient client,
        CancellationToken cancellationToken);
}
示例:

csharp

public class GetUserHandler : MessageHandler<GetUserRequest, GetUserResponse>
{
    public override string MessageType => "GetUser";

    protected override async Task<GetUserResponse> HandleAsync(
        GetUserRequest request,
        ConnectedClient client,
        CancellationToken cancellationToken)
    {
        var user = await _userService.GetUserAsync(request.UserId);
        return new GetUserResponse { User = user };
    }
}
4.2 客户端 API
IpcClient
csharp

public class IpcClient : IDisposable
{
    // 构造函数
    public IpcClient(IpcClientOptions options, ILogger? logger = null);

    // 连接管理
    public Task ConnectAsync(CancellationToken cancellationToken = default);
    public Task DisconnectAsync();

    // 请求-响应
    public Task<TResponse> SendRequestAsync<TRequest, TResponse>(
        TRequest request, string messageType,
        int? timeoutMs = null,
        CancellationToken cancellationToken = default);

    // 通知
    public Task SendNotificationAsync<T>(
        T notification, string messageType,
        CancellationToken cancellationToken = default);

    // 状态
    public ConnectionState State { get; }
    public bool IsConnected { get; }

    // 事件
    public event EventHandler<ConnectionStateChangedEventArgs> StateChanged;
    public event EventHandler<NotificationReceivedEventArgs> NotificationReceived;
}
IpcClientOptions
csharp

public class IpcClientOptions
{
    // 管道配置
    public PipeOptions PipeOptions { get; set; }
    
    // 重连策略
    public ReconnectionPolicy ReconnectionPolicy { get; set; } = ReconnectionPolicy.Default;
    
    // 默认请求超时（毫秒）
    public int DefaultRequestTimeoutMs { get; set; } = 30000;
    
    // 心跳间隔（0 表示禁用）
    public int HeartbeatIntervalMs { get; set; } = 0;
}
ReconnectionPolicy
csharp

public class ReconnectionPolicy
{
    public bool Enabled { get; set; } = true;
    public int InitialDelayMs { get; set; } = 1000;
    public int MaxDelayMs { get; set; } = 30000;
    public int MaxAttempts { get; set; } = 0;  // 0 = 无限
    public double BackoffMultiplier { get; set; } = 2.0;

    // 预定义策略
    public static ReconnectionPolicy Default { get; }
    public static ReconnectionPolicy Disabled { get; }
    public static ReconnectionPolicy Fast { get; }
}
4.3 UWP API
UwpIpcClient
csharp

public class UwpIpcClient : IDisposable
{
    public UwpIpcClient(string pipeName);

    // 连接
    public Task ConnectAsync();
    public Task DisconnectAsync();

    // 请求-响应（泛型）
    public Task<TResponse> SendRequestAsync<TRequest, TResponse>(
        TRequest request, string messageType, int timeoutMs = 30000);

    // 请求-响应（字节数组）
    public Task<byte[]> SendRequestAsync(
        byte[] requestData, string messageType, int timeoutMs = 30000);

    // 通知
    public Task SendNotificationAsync(
        byte[] notificationData, string messageType);

    // 状态
    public UwpConnectionState State { get; }
    public bool IsConnected { get; }

    // 事件
    public event EventHandler<UwpConnectionStateChangedEventArgs> StateChanged;
    public event EventHandler<UwpNotificationReceivedEventArgs> NotificationReceived;
    public event EventHandler<ProgressEventArgs> ProgressChanged;
}
4.4 扩展方法 API
ClientExtensions
csharp

public static class ClientExtensions
{
    // Echo
    public static Task<EchoResponse> SendEchoAsync(
        this IpcClient client, string message, int timeoutMs = 10000);
    
    public static Task<string> SendEchoStringAsync(
        this IpcClient client, string message, int timeoutMs = 10000);

    // 服务器信息
    public static Task<ServerInfoResponse> GetServerInfoAsync(
        this IpcClient client, int timeoutMs = 5000);

    // 进度
    public static Task SendProgressAsync(
        this IpcClient client, double percentage, string message,
        int currentStep = 0, int totalSteps = 0, string? operationId = null);
}
UwpClientExtensions
csharp

public static class UwpClientExtensions
{
    public static Task<EchoResponse> SendEchoAsync(
        this UwpIpcClient client, string message, int timeoutMs = 10000);
    
    public static Task<string> SendEchoStringAsync(
        this UwpIpcClient client, string message, int timeoutMs = 10000);

    public static Task<ServerInfoResponse> GetServerInfoAsync(
        this UwpIpcClient client, int timeoutMs = 5000);

    public static Task SendProgressAsync(
        this UwpIpcClient client, double percentage, string message,
        int currentStep = 0, int totalSteps = 0, string operationId = null);
}
5. 消息定义
5.1 消息类型常量
YourApp.IPC.Contracts/MessageTypes.cs

csharp

public static class MessageTypes
{
    // 请求类型
    public const string Echo = "Echo";
    public const string GetServerInfo = "GetServerInfo";
    
    // 通知类型
    public const string Progress = "Progress";
    public const string ServerBroadcast = "ServerBroadcast";
    public const string Heartbeat = "Heartbeat";
}
5.2 预定义消息
Echo 消息
csharp

[MessagePackObject]
public class EchoRequest
{
    [Key(0)]
    public string Message { get; set; } = string.Empty;
}

[MessagePackObject]
public class EchoResponse
{
    [Key(0)]
    public string Echo { get; set; } = string.Empty;

    [Key(1)]
    public DateTime ServerTime { get; set; }
}
服务器信息消息
csharp

[MessagePackObject]
public class ServerInfoRequest : EmptyRequest { }

[MessagePackObject]
public class ServerInfoResponse
{
    [Key(0)] public string Version { get; set; }
    [Key(1)] public int ConnectedClients { get; set; }
    [Key(2)] public DateTime ServerStartTime { get; set; }
    [Key(3)] public TimeSpan Uptime { get; set; }
    [Key(4)] public string MachineName { get; set; }
    [Key(5)] public int ProcessId { get; set; }
}
进度通知
csharp

[MessagePackObject]
public class ProgressNotification
{
    [Key(0)] public double Percentage { get; set; }
    [Key(1)] public string Message { get; set; }
    [Key(2)] public int CurrentStep { get; set; }
    [Key(3)] public int TotalSteps { get; set; }
    [Key(4)] public string? OperationId { get; set; }
}
广播消息
csharp

[MessagePackObject]
public class BroadcastNotification
{
    [Key(0)] public string Message { get; set; }
    [Key(1)] public DateTime Timestamp { get; set; }
    [Key(2)] public string? Category { get; set; }
}
5.3 自定义消息
定义消息
csharp

// 1. 在 Contracts/Messages/ 中创建新文件
[MessagePackObject]
public class GetUserRequest
{
    [Key(0)]
    public int UserId { get; set; }
}

[MessagePackObject]
public class GetUserResponse
{
    [Key(0)]
    public UserDto User { get; set; }
    
    [Key(1)]
    public bool Success { get; set; }
}

[MessagePackObject]
public class UserDto
{
    [Key(0)] public int Id { get; set; }
    [Key(1)] public string Name { get; set; }
    [Key(2)] public string Email { get; set; }
}
注册消息类型
csharp

// 2. 在 MessageTypes.cs 中添加常量
public static class MessageTypes
{
    public const string GetUser = "GetUser";
}
实现处理器
csharp

// 3. 实现处理器
public class GetUserHandler : MessageHandler<GetUserRequest, GetUserResponse>
{
    private readonly IUserService _userService;

    public GetUserHandler(IUserService userService)
    {
        _userService = userService;
    }

    public override string MessageType => MessageTypes.GetUser;

    protected override async Task<GetUserResponse> HandleAsync(
        GetUserRequest request,
        ConnectedClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            var user = await _userService.GetUserByIdAsync(request.UserId);
            
            return new GetUserResponse
            {
                User = new UserDto
                {
                    Id = user.Id,
                    Name = user.Name,
                    Email = user.Email
                },
                Success = true
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get user {UserId}", request.UserId);
            throw;
        }
    }
}
使用消息
csharp

// 4. 客户端调用
var request = new GetUserRequest { UserId = 123 };
var response = await client.SendRequestAsync<GetUserRequest, GetUserResponse>(
    request, 
    MessageTypes.GetUser,
    timeoutMs: 5000);

if (response.Success)
{
    Console.WriteLine($"User: {response.User.Name}");
}
5.4 消息设计规范
✅ 良好实践
csharp

// 1. 使用明确的命名
public class GetUserByIdRequest { }  // ✅ 好
public class Request1 { }            // ❌ 差

// 2. 使用 MessagePack 属性
[MessagePackObject]                  // ✅ 必需
public class MyMessage
{
    [Key(0)]                         // ✅ 明确指定 Key
    public string Name { get; set; }
}

// 3. 设置默认值
[Key(0)]
public string Name { get; set; } = string.Empty;  // ✅ 好

// 4. 使用可空类型表示可选字段
[Key(1)]
public string? OptionalField { get; set; }  // ✅ 好

// 5. 复杂对象也要标记
[MessagePackObject]
public class Address
{
    [Key(0)] public string Street { get; set; }
    [Key(1)] public string City { get; set; }
}
❌ 避免的做法
csharp

// 1. 不要使用未标记的类
public class MyMessage  // ❌ 缺少 [MessagePackObject]
{
    public string Name { get; set; }
}

// 2. 不要遗漏 Key 属性
[MessagePackObject]
public class MyMessage
{
    public string Name { get; set; }  // ❌ 缺少 [Key(0)]
}

// 3. 不要使用不可序列化的类型
[Key(0)]
public Stream Data { get; set; }  // ❌ Stream 不能序列化

// 4. 避免循环引用
[Key(0)]
public MyMessage Parent { get; set; }  // ⚠️ 小心循环引用
6. 安全配置
6.1 安全模式
Default (默认模式)
csharp

SecurityMode = PipeSecurityMode.Default
特点:

使用系统默认 ACL
仅当前用户可访问
最安全，但不支持跨用户/UWP
适用:

同一用户的进程通信
不需要 UWP 支持
Relaxed (宽松模式)
csharp

SecurityMode = PipeSecurityMode.Relaxed
ACL 配置:

text

Everyone: Read/Write
适用:

开发和测试环境
所有用户可访问
⚠️ 不推荐用于生产
Restricted (严格模式)
csharp

SecurityMode = PipeSecurityMode.Restricted
ACL 配置:

text

SYSTEM: Full Control
Administrators: Full Control
Interactive Users: Read/Write
适用:

生产环境
仅信任用户可访问
不支持 UWP
UwpCompatible (UWP 兼容模式) ⭐
csharp

SecurityMode = PipeSecurityMode.UwpCompatible
ACL 配置:

text

SYSTEM: Full Control
Administrators: Full Control
Interactive Users: Read/Write
ALL APPLICATION PACKAGES: Read/Write
适用:

需要 UWP/WinUI3 支持
推荐的生产配置
兼顾安全和功能
6.2 自定义 SDDL
使用自定义 SDDL 字符串
csharp

var options = new IpcServerOptions
{
    PipeOptions = new PipeOptions
    {
        PipeName = "MyApp",
        // 自定义 SDDL 字符串
        SecurityDescriptor = "D:(A;;GA;;;WD)(A;;GA;;;SY)"
    },
    SecurityMode = PipeSecurityMode.Default  // 将被 SDDL 覆盖
};
SDDL 语法参考
text

格式：
D:              DACL (访问控制列表)
(A;;权限;;;SID)  访问控制条目 (ACE)

权限代码：
GA  - Generic All (完全控制)
GR  - Generic Read (读)
GW  - Generic Write (写)
GRGW - Read + Write

常用 SID：
WD  - Everyone (所有人)
SY  - SYSTEM
BA  - Administrators (管理员组)
IU  - Interactive Users (交互用户)
AC  - ALL APPLICATION PACKAGES (所有应用包)
S-1-15-2-1 - ALL_APP_PACKAGES (UWP)
SDDL 示例
csharp

// 1. 仅 SYSTEM 和管理员
"D:(A;;GA;;;SY)(A;;GA;;;BA)"

// 2. SYSTEM + Administrators + 当前用户
"D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;IU)"

// 3. UWP 兼容（推荐）
"D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;IU)(A;;GRGW;;;AC)(A;;GRGW;;;S-1-15-2-1)"

// 4. 所有人可访问（仅测试）
"D:(A;;GA;;;WD)"
6.3 UWP 特殊配置
Package.appxmanifest 配置
XML

<?xml version="1.0" encoding="utf-8"?>
<Package xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities">
  
  <Capabilities>
    <!-- 必需：私有网络访问 -->
    <Capability Name="privateNetworkClientServer" />
    
    <!-- 可选：完全信任运行 -->
    <rescap:Capability Name="runFullTrust" />
  </Capabilities>
</Package>
Loopback 豁免
某些情况下需要添加 Loopback 豁免：

PowerShell

# 1. 获取 UWP 应用的包系列名
Get-AppxPackage | Where-Object {$_.Name -like "*YourApp*"} | Select-Object PackageFamilyName

# 2. 添加豁免
CheckNetIsolation.exe LoopbackExempt -a -n="YourApp_1234567890abc"

# 3. 验证
CheckNetIsolation.exe LoopbackExempt -s
自动化脚本:

PowerShell

# Setup-UwpLoopback.ps1
param([string]$AppNamePattern = "*YourApp*")

$apps = Get-AppxPackage | Where-Object { $_.Name -like $AppNamePattern }

foreach ($app in $apps) {
    Write-Host "Adding loopback exemption for: $($app.PackageFamilyName)"
    CheckNetIsolation.exe LoopbackExempt -a -n="$($app.PackageFamilyName)"
}
6.4 跨用户通信
场景
text

User1 Desktop App → Windows Service (SYSTEM) → User2 Desktop App
配置
csharp

// 服务端（Windows 服务，运行在 SYSTEM 下）
var options = new IpcServerOptions
{
    PipeOptions = new PipeOptions
    {
        PipeName = "Global\\MyAppPipe",  // ⚠️ 使用 Global\ 前缀
        SecurityDescriptor = "D:(A;;GA;;;SY)(A;;GRGW;;;IU)(A;;GRGW;;;BA)"
    }
};
注意:

使用 Global\ 前缀创建全局命名对象
需要管理员权限或以服务身份运行
确保 ACL 允许所有需要的用户
7. 最佳实践
7.1 错误处理
客户端错误处理
csharp

try
{
    await client.ConnectAsync();
}
catch (ConnectionException ex)
{
    // 连接失败
    _logger.LogError(ex, "Failed to connect to server");
    
    // 显示友好提示
    ShowError("无法连接到服务，请确保服务已启动");
}
catch (TimeoutException ex)
{
    // 连接超时
    _logger.LogWarning(ex, "Connection timeout");
    ShowError("连接超时，请重试");
}

// 请求错误处理
try
{
    var response = await client.SendRequestAsync<MyRequest, MyResponse>(...);
}
catch (RemoteException ex)
{
    // 服务端业务错误
    _logger.LogWarning("Server error: {Code} - {Message}", 
        ex.ErrorCode, ex.Message);
    ShowError($"操作失败: {ex.Message}");
}
catch (TimeoutException ex)
{
    // 请求超时
    ShowError("请求超时，请重试");
}
catch (ConnectionException ex)
{
    // 连接断开
    ShowError("连接已断开");
}
服务端错误处理
csharp

public class MyHandler : MessageHandler<MyRequest, MyResponse>
{
    protected override async Task<MyResponse> HandleAsync(
        MyRequest request,
        ConnectedClient client,
        CancellationToken cancellationToken)
    {
        // 参数验证
        if (string.IsNullOrEmpty(request.Data))
        {
            throw new ArgumentException("Data cannot be empty");
        }

        try
        {
            // 业务逻辑
            var result = await _service.ProcessAsync(request.Data);
            return new MyResponse { Result = result };
        }
        catch (DataNotFoundException ex)
        {
            // 业务异常 - 会转换为 RemoteException
            _logger.LogWarning(ex, "Data not found for request");
            throw;  // MessageHandler 会自动包装
        }
        catch (Exception ex)
        {
            // 未预期的错误
            _logger.LogError(ex, "Unexpected error processing request");
            throw;
        }
    }
}
7.2 生命周期管理
服务端生命周期
csharp

public class MyService : IHostedService
{
    private readonly IpcServer _ipcServer;

    public MyService()
    {
        _ipcServer = new IpcServer(new IpcServerOptions { ... });
        _ipcServer.RegisterHandler(new MyHandler());
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _ipcServer.StartAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _ipcServer.StopAsync();
        _ipcServer.Dispose();
    }
}
客户端生命周期
csharp

public class MyClientService : IDisposable
{
    private readonly IpcClient _client;
    private bool _disposed;

    public MyClientService()
    {
        _client = new IpcClient(new IpcClientOptions { ... });
        _client.StateChanged += OnStateChanged;
    }

    public async Task InitializeAsync()
    {
        await _client.ConnectAsync();
    }

    private void OnStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        if (e.NewState == ConnectionState.Disconnected)
        {
            // 处理断开连接
            OnDisconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _client.DisconnectAsync().GetAwaiter().GetResult();
        _client.Dispose();
    }
}
UWP 生命周期
csharp

public sealed partial class MainPage : Page
{
    private UwpIpcClient _client;

    public MainPage()
    {
        InitializeComponent();
        InitializeIpcClient();
        
        // 订阅应用生命周期
        Application.Current.Suspending += OnSuspending;
        Application.Current.Resuming += OnResuming;
    }

    private void InitializeIpcClient()
    {
        _client = new UwpIpcClient("MyApp");
        _client.StateChanged += OnIpcStateChanged;
    }

    private async void OnSuspending(object sender, SuspendingEventArgs e)
    {
        var deferral = e.SuspendingOperation.GetDeferral();
        try
        {
            // 应用挂起时断开连接
            await _client.DisconnectAsync();
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void OnResuming(object sender, object e)
    {
        // 应用恢复时重新连接
        try
        {
            await _client.ConnectAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to reconnect: {ex.Message}");
        }
    }
}
7.3 性能优化
批量操作
csharp

// ❌ 不好 - 多次往返
foreach (var id in userIds)
{
    var user = await client.SendRequestAsync<GetUserRequest, GetUserResponse>(
        new GetUserRequest { UserId = id }, "GetUser");
    users.Add(user.User);
}

// ✅ 好 - 单次往返
var response = await client.SendRequestAsync<GetUserBatchRequest, GetUserBatchResponse>(
    new GetUserBatchRequest { UserIds = userIds }, "GetUserBatch");
users = response.Users;
复用客户端
csharp

// ❌ 不好 - 每次创建新客户端
public async Task<string> DoSomething()
{
    using var client = new IpcClient(...);
    await client.ConnectAsync();
    return await client.SendEchoStringAsync("test");
}

// ✅ 好 - 复用客户端
private readonly IpcClient _client;

public MyService()
{
    _client = new IpcClient(...);
}

public async Task InitializeAsync()
{
    await _client.ConnectAsync();
}

public Task<string> DoSomething()
{
    return _client.SendEchoStringAsync("test");
}
使用通知而非轮询
csharp

// ❌ 不好 - 轮询
while (true)
{
    var status = await client.GetStatusAsync();
    if (status.IsComplete) break;
    await Task.Delay(1000);
}

// ✅ 好 - 事件通知
client.NotificationReceived += (s, e) =>
{
    if (e.MessageType == "TaskComplete")
    {
        OnTaskCompleted();
    }
};
超时设置
csharp

// 根据操作复杂度设置合理的超时
await client.SendRequestAsync<FastRequest, FastResponse>(
    request, "Fast", timeoutMs: 5000);  // 快速操作 5 秒

await client.SendRequestAsync<SlowRequest, SlowResponse>(
    request, "Slow", timeoutMs: 60000);  // 慢速操作 60 秒
7.4 日志记录
结构化日志
csharp

// 服务端
public class MyHandler : MessageHandler<MyRequest, MyResponse>
{
    private readonly ILogger _logger;

    protected override async Task<MyResponse> HandleAsync(
        MyRequest request,
        ConnectedClient client,
        CancellationToken cancellationToken)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["ClientId"] = client.ClientId,
            ["MessageType"] = MessageType,
            ["RequestId"] = request.Id
        });

        _logger.LogInformation("Processing request {RequestId}", request.Id);

        try
        {
            var result = await ProcessAsync(request);
            _logger.LogInformation("Request {RequestId} completed successfully", request.Id);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Request {RequestId} failed", request.Id);
            throw;
        }
    }
}
客户端日志
csharp

var client = new IpcClient(options, logger);

client.StateChanged += (s, e) =>
{
    _logger.LogInformation("IPC state changed: {OldState} -> {NewState}",
        e.OldState, e.NewState);
};

// 记录请求
_logger.LogDebug("Sending request {MessageType}", messageType);
var response = await client.SendRequestAsync<TReq, TRes>(...);
_logger.LogDebug("Received response for {MessageType}", messageType);
7.5 测试
单元测试
csharp

[Fact]
public async Task Handler_ValidRequest_ReturnsSuccess()
{
    // Arrange
    var handler = new MyHandler(_mockService.Object);
    var request = new MyRequest { Data = "test" };
    var client = new ConnectedClient(Mock.Of<NamedPipeConnection>());

    // Act
    var response = await handler.HandleAsync(request, client, CancellationToken.None);

    // Assert
    Assert.True(response.Success);
}
集成测试
csharp

public class IpcIntegrationTests : IDisposable
{
    private readonly IpcServer _server;
    private readonly IpcClient _client;

    public IpcIntegrationTests()
    {
        _server = new IpcServer(new IpcServerOptions
        {
            PipeOptions = new PipeOptions { PipeName = "Test_" + Guid.NewGuid() }
        });
        _server.RegisterHandler(new MyHandler());

        _client = new IpcClient(new IpcClientOptions
        {
            PipeOptions = new PipeOptions { PipeName = _server.PipeOptions.PipeName }
        });
    }

    [Fact]
    public async Task EndToEnd_RequestResponse_Success()
    {
        // Arrange
        await _server.StartAsync();
        await _client.ConnectAsync();

        // Act
        var response = await _client.SendRequestAsync<MyRequest, MyResponse>(
            new MyRequest { Data = "test" }, "MyMessage");

        // Assert
        Assert.NotNull(response);
        Assert.True(response.Success);
    }

    public void Dispose()
    {
        _client?.DisconnectAsync().Wait();
        _client?.Dispose();
        _server?.StopAsync().Wait();
        _server?.Dispose();
    }
}
8. 故障排除
8.1 常见问题
问题 1: "Access to the path is denied"
现象:

text

System.UnauthorizedAccessException: Access to the path is denied.
原因:

UWP 应用无法访问管道
管道 ACL 不包含 AppContainer 权限
解决:

csharp

// 方案 1: 使用 UWP 兼容模式
SecurityMode = PipeSecurityMode.UwpCompatible

// 方案 2: 使用自定义 SDDL
SecurityDescriptor = "D:(A;;GA;;;WD)(A;;GRGW;;;AC)"

// 方案 3: 添加 Loopback 豁免
CheckNetIsolation.exe LoopbackExempt -a -n="YourApp_xxx"
问题 2: "所有的管道范例都在使用中"
现象:

text

IOException: 所有的管道范例都在使用中
原因:

监听器创建了管道实例但没有及时释放
MaxServerInstances 设置过小
解决:

csharp

// 1. 确保监听器循环正确
private async Task ListenerLoopAsync(...)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        var pipeServer = CreatePipeServer();
        await pipeServer.WaitForConnectionAsync(...);
        
        // 立即继续循环创建新实例
        _ = HandleClientAsync(client, cancellationToken);
    }
}

// 2. 增加最大实例数
MaxConcurrentClients = 50
问题 3: 序列化失败
现象:

text

MessagePackSerializationException: Failed to deserialize...
原因:

消息类缺少 [MessagePackObject] 属性
属性缺少 [Key(n)] 属性
客户端和服务端消息定义不一致
解决:

csharp

// ✅ 正确的消息定义
[MessagePackObject]
public class MyMessage
{
    [Key(0)]
    public string Name { get; set; } = string.Empty;
    
    [Key(1)]
    public int Age { get; set; }
}

// 确保客户端和服务端使用相同的 Contracts 程序集
问题 4: 连接超时
现象:

text

TimeoutException: Connection timeout after 5000ms
原因:

服务端未启动
管道名称不匹配
防火墙阻止
解决:

csharp

// 1. 检查服务端是否运行
sc query YourAppService

// 2. 验证管道名称
// 服务端
PipeName = "MyApp"

// 客户端
PipeName = "MyApp"  // 必须完全一致

// 3. 增加超时时间
ConnectionTimeoutMs = 10000
问题 5: 重连失败
现象:

text

客户端断开后无法重连
原因:

重连策略禁用
超过最大重试次数
服务端未正确清理旧连接
解决:

csharp

// 1. 启用重连
ReconnectionPolicy = ReconnectionPolicy.Default

// 2. 调整重连参数
ReconnectionPolicy = new ReconnectionPolicy
{
    Enabled = true,
    MaxAttempts = 0,  // 无限重试
    InitialDelayMs = 1000,
    MaxDelayMs = 30000
}

// 3. 服务端确保清理
server.ClientDisconnected += (s, e) =>
{
    _logger.LogInformation("Client {Id} cleaned up", e.ClientId);
};
8.2 诊断工具
查看活动管道
PowerShell

# 列出所有命名管道
Get-ChildItem \\.\pipe\

# 查找特定管道
Get-ChildItem \\.\pipe\ | Where-Object { $_.Name -like "*YourApp*" }
监控连接
csharp

// 启用详细日志
var options = new IpcServerOptions
{
    // ... other options
};

var logger = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Trace);  // 最详细的日志
}).CreateLogger<IpcServer>();

var server = new IpcServer(options, logger);
网络隔离诊断
PowerShell

# 查看 UWP 应用的网络隔离状态
CheckNetIsolation.exe LoopbackExempt -s

# 添加诊断
CheckNetIsolation.exe LoopbackExempt -d

# 清除所有豁免
CheckNetIsolation.exe LoopbackExempt -c
8.3 调试技巧
启用详细日志
csharp

// 临时启用 Trace 级别日志
var client = new IpcClient(options, CreateTraceLogger());

ILogger CreateTraceLogger()
{
    return LoggerFactory.Create(b => 
    {
        b.AddConsole();
        b.SetMinimumLevel(LogLevel.Trace);
        b.AddFilter("YourApp.IPC", LogLevel.Trace);
    }).CreateLogger<IpcClient>();
}
消息追踪
csharp

// 在发送前记录
_logger.LogDebug("Sending: {@Message}", request);
await client.SendRequestAsync(...);

// 在接收后记录
var response = await client.SendRequestAsync(...);
_logger.LogDebug("Received: {@Response}", response);
使用事件监控
csharp

// 客户端监控
client.StateChanged += (s, e) =>
{
    Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] State: {e.OldState} -> {e.NewState}");
    if (e.Exception != null)
    {
        Debug.WriteLine($"  Exception: {e.Exception.Message}");
    }
};

// 服务端监控
server.ClientConnected += (s, e) =>
{
    Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Client connected: {e.ClientId}");
};

server.ClientDisconnected += (s, e) =>
{
    Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Client disconnected: {e.ClientId} - {e.Reason}");
};
9. 性能优化
9.1 性能指标
基准测试
text

环境: Windows 10, i7-8700, 16GB RAM, .NET Framework 4.8

简单 Echo (1KB 负载):
  - 延迟: ~0.5ms (P50), ~1ms (P99)
  - 吞吐量: ~20,000 req/s (单连接)

中等负载 (10KB):
  - 延迟: ~2ms (P50), ~5ms (P99)
  - 吞吐量: ~8,000 req/s

大负载 (1MB):
  - 延迟: ~50ms (P50), ~100ms (P99)
  - 吞吐量: ~200 req/s

广播 (100 客户端, 1KB):
  - 延迟: ~10ms (P50), ~20ms (P99)
9.2 优化建议
消息大小
csharp

// ❌ 不好 - 传输大对象
public class MyRequest
{
    public byte[] LargeData { get; set; }  // 10MB
}

// ✅ 好 - 使用引用
public class MyRequest
{
    public string DataId { get; set; }  // 服务端从缓存/数据库获取
}
序列化优化
csharp

// 使用压缩（MessagePack 默认启用 LZ4）
var options = MessagePackSerializerOptions.Standard
    .WithCompression(MessagePackCompression.Lz4BlockArray);

// 对于大对象，考虑分块传输
public async Task<byte[]> DownloadLargeFileAsync(string fileId)
{
    var chunks = new List<byte[]>();
    int chunkIndex = 0;
    
    while (true)
    {
        var response = await client.SendRequestAsync<GetChunkRequest, GetChunkResponse>(
            new GetChunkRequest { FileId = fileId, ChunkIndex = chunkIndex },
            "GetChunk");
        
        if (response.Data.Length == 0) break;
        
        chunks.Add(response.Data);
        chunkIndex++;
    }
    
    return chunks.SelectMany(x => x).ToArray();
}
连接池（高级）
csharp

public class IpcClientPool
{
    private readonly ConcurrentBag<IpcClient> _pool = new ConcurrentBag<IpcClient>();
    private readonly SemaphoreSlim _semaphore;
    private readonly IpcClientOptions _options;

    public IpcClientPool(IpcClientOptions options, int maxSize = 10)
    {
        _options = options;
        _semaphore = new SemaphoreSlim(maxSize, maxSize);
    }

    public async Task<IpcClient> AcquireAsync()
    {
        await _semaphore.WaitAsync();
        
        if (_pool.TryTake(out var client) && client.IsConnected)
        {
            return client;
        }

        client = new IpcClient(_options);
        await client.ConnectAsync();
        return client;
    }

    public void Release(IpcClient client)
    {
        if (client.IsConnected)
        {
            _pool.Add(client);
        }
        else
        {
            client.Dispose();
        }
        
        _semaphore.Release();
    }
}

// 使用
var client = await _pool.AcquireAsync();
try
{
    var result = await client.SendRequestAsync(...);
}
finally
{
    _pool.Release(client);
}
9.3 监控
性能计数器
csharp

public class IpcMetrics
{
    private long _requestCount;
    private long _totalLatencyMs;
    private readonly ConcurrentDictionary<string, int> _activeRequests = new();

    public void RecordRequest(string messageType, long latencyMs)
    {
        Interlocked.Increment(ref _requestCount);
        Interlocked.Add(ref _totalLatencyMs, latencyMs);
    }

    public long RequestsPerSecond => _requestCount / _uptime.TotalSeconds;
    public long AverageLatencyMs => _requestCount > 0 ? _totalLatencyMs / _requestCount : 0;
}
10. FAQ
Q1: 命名管道和 TCP Socket 如何选择？
A:

场景	推荐
本地进程通信	命名管道 ✅
同机器不同用户	命名管道 ✅
UWP 应用通信	命名管道 ✅
跨网络通信	TCP Socket
跨平台	TCP Socket / gRPC
命名管道优势:

更低延迟 (~0.5ms vs ~2ms)
内核级安全
自动清理
支持 ACL
Q2: 支持双向通信吗？
A: 是的，支持三种模式：

请求-响应: SendRequestAsync 返回响应
服务端主动推送: BroadcastNotificationAsync
客户端主动推送: SendNotificationAsync
csharp

// 客户端订阅服务端通知
client.NotificationReceived += (s, e) =>
{
    // 处理服务端推送
};

// 服务端订阅客户端通知
server.NotificationReceived += (s, e) =>
{
    // 处理客户端推送
};
Q3: 如何实现发布/订阅模式？
A:

csharp

// 服务端维护订阅列表
private readonly ConcurrentDictionary<string, HashSet<string>> _subscriptions = new();

public class SubscribeHandler : MessageHandler<SubscribeRequest, SubscribeResponse>
{
    protected override Task<SubscribeResponse> HandleAsync(...)
    {
        var subscribers = _subscriptions.GetOrAdd(request.Topic, _ => new HashSet<string>());
        subscribers.Add(client.ClientId);
        
        return Task.FromResult(new SubscribeResponse { Success = true });
    }
}

// 发布消息
public async Task PublishAsync(string topic, object data)
{
    if (_subscriptions.TryGetValue(topic, out var subscribers))
    {
        await _server.BroadcastNotificationAsync(
            data, 
            "TopicMessage",
            predicate: client => subscribers.Contains(client.ClientId));
    }
}
Q4: 可以传输文件吗？
A: 可以，但建议分块传输：

csharp

// 服务端
public class UploadChunkHandler : MessageHandler<UploadChunkRequest, UploadChunkResponse>
{
    private readonly ConcurrentDictionary<string, MemoryStream> _uploads = new();

    protected override async Task<UploadChunkResponse> HandleAsync(...)
    {
        var stream = _uploads.GetOrAdd(request.UploadId, _ => new MemoryStream());
        
        await stream.WriteAsync(request.ChunkData, 0, request.ChunkData.Length);
        
        if (request.IsLastChunk)
        {
            await SaveFileAsync(request.UploadId, stream);
            _uploads.TryRemove(request.UploadId, out _);
        }
        
        return new UploadChunkResponse { Success = true };
    }
}

// 客户端
public async Task UploadFileAsync(string filePath)
{
    const int chunkSize = 64 * 1024; // 64KB chunks
    var uploadId = Guid.NewGuid().ToString();
    var fileData = await File.ReadAllBytesAsync(filePath);
    
    for (int i = 0; i < fileData.Length; i += chunkSize)
    {
        var chunk = new ArraySegment<byte>(
            fileData, i, Math.Min(chunkSize, fileData.Length - i));
        
        await client.SendRequestAsync<UploadChunkRequest, UploadChunkResponse>(
            new UploadChunkRequest
            {
                UploadId = uploadId,
                ChunkData = chunk.ToArray(),
                IsLastChunk = i + chunkSize >= fileData.Length
            },
            "UploadChunk");
    }
}
Q5: 如何处理网络不稳定？
A: 使用内置的重连机制：

csharp

var client = new IpcClient(new IpcClientOptions
{
    ReconnectionPolicy = new ReconnectionPolicy
    {
        Enabled = true,
        MaxAttempts = 0,  // 无限重试
        InitialDelayMs = 1000,
        MaxDelayMs = 30000,
        BackoffMultiplier = 2.0
    }
});

// 监听重连事件
client.StateChanged += (s, e) =>
{
    if (e.NewState == ConnectionState.Reconnecting)
    {
        ShowReconnectingUI();
    }
    else if (e.NewState == ConnectionState.Connected)
    {
        HideReconnectingUI();
    }
};
Q6: 支持认证和授权吗？
A: 框架本身不内置认证，但可以轻松实现：

csharp

// 1. 定义认证消息
[MessagePackObject]
public class AuthRequest
{
    [Key(0)] public string Token { get; set; }
}

// 2. 实现认证处理器
public class AuthHandler : MessageHandler<AuthRequest, AuthResponse>
{
    protected override async Task<AuthResponse> HandleAsync(...)
    {
        var isValid = await _authService.ValidateTokenAsync(request.Token);
        
        if (isValid)
        {
            // 标记客户端已认证
            client.Metadata.CustomProperties["Authenticated"] = true;
            client.Metadata.CustomProperties["UserId"] = userId;
        }
        
        return new AuthResponse { Success = isValid };
    }
}

// 3. 在其他处理器中检查
public class SecureHandler : MessageHandler<SecureRequest, SecureResponse>
{
    protected override Task<SecureResponse> HandleAsync(...)
    {
        if (!client.Metadata.CustomProperties.TryGetValue("Authenticated", out var auth) 
            || !(bool)auth)
        {
            throw new UnauthorizedAccessException("Not authenticated");
        }
        
        // 处理业务逻辑
    }
}
Q7: 如何升级消息格式？
A: 使用版本控制：

csharp

// V1
[MessagePackObject]
public class MyRequestV1
{
    [Key(0)] public string Name { get; set; }
}

// V2 - 向后兼容
[MessagePackObject]
public class MyRequestV2
{
    [Key(0)] public string Name { get; set; }
    [Key(1)] public string? NewField { get; set; }  // 新增字段，可选
}

// 消息类型使用版本号
public const string MyMessage_V1 = "MyMessage_V1";
public const string MyMessage_V2 = "MyMessage_V2";

// 服务端同时支持两个版本
server.RegisterHandler(new MyHandlerV1());
server.RegisterHandler(new MyHandlerV2());
Q8: 性能瓶颈在哪里？
A:

序列化/反序列化 - 使用 MessagePack 已经很快
大消息传输 - 考虑压缩或分块
锁竞争 - 已使用 SemaphoreSlim 和无锁集合
线程上下文切换 - 使用 ConfigureAwait(false)
性能优化清单:

✅ 复用客户端连接
✅ 批量操作而非循环
✅ 使用通知而非轮询
✅ 合理设置超时时间
✅ 避免传输大对象
Q9: 可以用在 Xamarin/MAUI 吗？
A: 不可以。这是 Windows 专用库（命名管道是 Windows API）。

跨平台需求请使用：

gRPC
SignalR
WebSocket
Q10: 开源吗？
A: 这是你的内部商用库。如果要开源，建议：

添加开源协议（MIT/Apache 2.0）
完善文档和示例
添加 CI/CD
发布到 GitHub
发布到 nuget.org
附录
A. 完整示例
见 samples/ 目录：

SimpleServer - 基础服务端
SimpleClient - 基础客户端
UwpClient - UWP 示例
B. 参考资源
Named Pipes (MSDN)
MessagePack for C#
UWP App Capabilities
C. 版本历史
1.0.0 (2024-01) - 初始版本
核心 IPC 功能
UWP 支持
自动重连
D. 贡献指南
内部项目，联系项目负责人。

E. 支持
问题反馈: 内部 Issue Tracker
技术支持: 发送邮件到 tech-support@yourcompany.com
文档版本: 1.0
最后更新: 2024-01
维护者: Your Team
