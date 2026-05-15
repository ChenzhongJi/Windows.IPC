using Microsoft.Extensions.Logging;
using System;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Windows.IPC.Contracts;
using Windows.IPC.Contracts.Messages;
using Windows.IPC.Server;

namespace SimpleServer
{
    // Echo 处理器
    public class EchoHandler : MessageHandler<EchoRequest, EchoResponse>
    {
        public override string MessageType => MessageTypes.Echo;

        protected override Task<EchoResponse> HandleAsync(
            EchoRequest request,
            ConnectedClient client,
            CancellationToken cancellationToken)
        {
            Console.WriteLine($"[{client.ClientId.Substring(0, 8)}...] Echo request: {request.Message}");

            var response = new EchoResponse
            {
                Echo = $"Server echo: {request.Message}",
                ServerTime = DateTime.Now
            };

            return Task.FromResult(response);
        }
    }

    // 服务器信息处理器
    public class ServerInfoHandler : MessageHandler<ServerInfoRequest, ServerInfoResponse>
    {
        private static readonly DateTime _startTime = DateTime.Now;

        public override string MessageType => MessageTypes.GetServerInfo;

        protected override Task<ServerInfoResponse> HandleAsync(
            ServerInfoRequest request,
            ConnectedClient client,
            CancellationToken cancellationToken)
        {
            var info = new ServerInfoResponse
            {
                Version = "1.0.0",
                ConnectedClients = Program.Server?.ConnectedClientCount ?? 0,
                ServerStartTime = _startTime,
                Uptime = DateTime.Now - _startTime,
                MachineName = Environment.MachineName,
                ProcessId = System.Diagnostics.Process.GetCurrentProcess().Id
            };

            return Task.FromResult(info);
        }
    }

    class Program
    {
        public static IpcServer? Server { get; private set; }

        static async Task Main(string[] args)
        {
            Console.Title = "IPC Server";

            // 创建日志
            var logger = CreateLogger();

            // 配置服务端
            var options = new IpcServerOptions
            {
                PipeOptions = new Windows.IPC.Transport.IpcPipeOptions
                {
                    PipeName = "YourAppPipe",
                    MaxServerInstances = 10
                },
                MaxConcurrentClients = 10,
                AutoHandleHeartbeat = true,

                // 选择安全模式：
                // - Default: 系统默认（最稳定）
                // - Relaxed: Everyone 可访问（开发测试用）
                // - Restricted: 仅管理员和当前用户
                // - UwpCompatible: 支持 UWP AppContainer
                SecurityMode = PipeSecurityMode.UwpCompatible,  // 先使用 Default

                // 只允许使用这些证书签名的客户端
                //ClientAuthenticator = new SignatureAuthenticator(
                //    allowedThumbprints: new[]
                //    {
                //        "866DCB97167E43D24ADAF8600C9CF73D6A547AA1",  // 你的证书指纹
                //        "ABCDEF1234567890ABCDEF1234567890ABCDEF12"   // 另一个证书
                //    },
                //    requireSigned: true,  // 必须签名
                //    logger: logger)
            };

            // 创建服务端
            using var server = new IpcServer(options, logger);

            // 注册消息处理器
            server.RegisterHandler(new EchoHandler());
            server.RegisterHandler(new ServerInfoHandler());

            // 保存 server 引用供 ServerInfoHandler 使用
            Server = server;

            // 订阅事件
            server.ClientConnected += (sender, e) =>
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"✓ Client connected: {e.ClientId.Substring(0, 8)}... at {e.ConnectedAt:HH:mm:ss}");
                Console.WriteLine($"  Total clients: {server.ConnectedClientCount}");
                Console.ResetColor();
            };

            server.ClientDisconnected += (sender, e) =>
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"✗ Client disconnected: {e.ClientId.Substring(0, 8)}... - {e.Reason}");
                Console.ResetColor();
            };

            server.NotificationReceived += (sender, e) =>
            {
                if (e.MessageType != "Heartbeat")
                {
                    Console.WriteLine($"→ Notification from {e.ClientId.Substring(0, 8)}...: {e.MessageType}");
                }
            };

            // 启动服务端
            Console.WriteLine("=====================================");
            Console.WriteLine("       IPC Server Starting...        ");
            Console.WriteLine("=====================================");

            await server.StartAsync();

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"\n✓ Server is running on pipe: {options.PipeOptions.PipeName}");
            Console.WriteLine($"✓ Max concurrent clients: {options.MaxConcurrentClients}");
            Console.ResetColor();

            Console.WriteLine("\nCommands:");
            Console.WriteLine("  [Q] Quit");
            Console.WriteLine("  [B] Broadcast message");
            Console.WriteLine("  [L] List connected clients");
            Console.WriteLine("  [S] Show statistics");
            Console.WriteLine();

            // 主循环
            var cts = new CancellationTokenSource();
            var inputTask = Task.Run(() => HandleUserInput(server, cts.Token));

            await inputTask;

            // 停止服务端
            Console.WriteLine("\n=====================================");
            Console.WriteLine("       Stopping Server...            ");
            Console.WriteLine("=====================================");

            await server.StopAsync();

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("✓ Server stopped successfully");
            Console.ResetColor();
        }

        static async Task HandleUserInput(IpcServer server, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var key = Console.ReadKey(true);

                switch (char.ToUpper(key.KeyChar))
                {
                    case 'Q':
                        Console.WriteLine("\nShutting down...");
                        return;

                    case 'B':
                        await BroadcastMessage(server);
                        break;

                    case 'L':
                        ListClients(server);
                        break;

                    case 'S':
                        ShowStatistics(server);
                        break;

                    default:
                        Console.WriteLine($"\nUnknown command: {key.KeyChar}");
                        break;
                }
            }
        }

        static async Task BroadcastMessage(IpcServer server)
        {
            Console.Write("\nEnter broadcast message: ");
            var message = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(message))
            {
                Console.WriteLine("Message cannot be empty");
                return;
            }

            var broadcast = new BroadcastNotification
            {
                Message = message,
                Timestamp = DateTime.Now,
                Category = "Manual"
            };

            Console.WriteLine($"Broadcasting with MessageType: {MessageTypes.ServerBroadcast}");

            var count = await server.BroadcastNotificationAsync(
                broadcast,
                MessageTypes.ServerBroadcast);

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"✓ Broadcast sent to {count} client(s)\n");
            Console.ResetColor();
        }

        static void ListClients(IpcServer server)
        {
            var clients = server.GetConnectedClients();

            Console.WriteLine("\n=====================================");
            Console.WriteLine($"    Connected Clients ({clients.Count})");
            Console.WriteLine("=====================================");

            if (clients.Count == 0)
            {
                Console.WriteLine("  (No clients connected)");
            }
            else
            {
                foreach (var client in clients)
                {
                    var duration = DateTime.UtcNow - client.ConnectedAt;
                    Console.WriteLine($"  • {client.ClientId.Substring(0, 8)}...");
                    Console.WriteLine($"    Connected: {client.ConnectedAt:yyyy-MM-dd HH:mm:ss}");
                    Console.WriteLine($"    Duration: {duration.TotalMinutes:F1} minutes");
                    Console.WriteLine($"    State: {client.State}");
                    Console.WriteLine();
                }
            }

            Console.WriteLine("=====================================\n");
        }

        static void ShowStatistics(IpcServer server)
        {
            var clients = server.GetConnectedClients();
            var totalDuration = TimeSpan.Zero;

            foreach (var client in clients)
            {
                totalDuration += DateTime.UtcNow - client.ConnectedAt;
            }

            var avgDuration = clients.Count > 0
                ? totalDuration.TotalMinutes / clients.Count
                : 0;

            Console.WriteLine("\n=====================================");
            Console.WriteLine("         Server Statistics           ");
            Console.WriteLine("=====================================");
            Console.WriteLine($"  Total clients: {clients.Count}");
            Console.WriteLine($"  Average connection time: {avgDuration:F1} minutes");
            Console.WriteLine($"  Machine: {Environment.MachineName}");
            Console.WriteLine($"  Process ID: {System.Diagnostics.Process.GetCurrentProcess().Id}");
            Console.WriteLine("=====================================\n");
        }

        private static ILogger CreateLogger()
        {
            var loggerFactory = new LoggerFactory();
            loggerFactory.AddProvider(new ConsoleLoggerProvider());
            return loggerFactory.CreateLogger<IpcServer>();
        }
    }

    // 简单的控制台日志提供者（与客户端相同）
    public class ConsoleLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName)
        {
            return new ConsoleLogger(categoryName);
        }

        public void Dispose() { }
    }

    public class ConsoleLogger : ILogger
    {
        private readonly string _categoryName;

        public ConsoleLogger(string categoryName)
        {
            _categoryName = categoryName;
        }

        public IDisposable BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception exception,
            Func<TState, Exception, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var message = formatter(state, exception);
            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");

            var originalColor = Console.ForegroundColor;
            Console.ForegroundColor = GetLogLevelColor(logLevel);
            Console.WriteLine($"[{timestamp}] [{logLevel}] {message}");

            if (exception != null)
            {
                Console.WriteLine($"  Exception: {exception.Message}");
            }

            Console.ForegroundColor = originalColor;
        }

        private ConsoleColor GetLogLevelColor(LogLevel logLevel)
        {
            return logLevel switch
            {
                LogLevel.Trace => ConsoleColor.Gray,
                LogLevel.Debug => ConsoleColor.Gray,
                LogLevel.Information => ConsoleColor.White,
                LogLevel.Warning => ConsoleColor.Yellow,
                LogLevel.Error => ConsoleColor.Red,
                LogLevel.Critical => ConsoleColor.DarkRed,
                _ => ConsoleColor.White
            };
        }
    }
}
