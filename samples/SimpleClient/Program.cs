using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using Windows.IPC.Client;
using Windows.IPC.Contracts;
using Windows.IPC.Contracts.Messages;
using Windows.IPC.Extensions;

namespace SimpleClient
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var logger = CreateLogger();

            var options = new IpcClientOptions
            {
                PipeOptions = new Windows.IPC.Transport.PipeOptions
                {
                    PipeName = "YourAppPipe",
                    ConnectionTimeoutMs = 5000
                },
                ReconnectionPolicy = ReconnectionPolicy.Default,
                DefaultRequestTimeoutMs = 10000,
                HeartbeatIntervalMs = 0
            };

            using var client = new IpcClient(options, logger);

            client.StateChanged += (sender, e) =>
            {
                Console.WriteLine($"State changed: {e.OldState} -> {e.NewState}");
            };

            client.NotificationReceived += (sender, e) =>
            {
                Console.WriteLine($"\n📨 Received notification:");
                Console.WriteLine($"   MessageType: {e.MessageType}"); // 显示 MessageType

                if (e.MessageType == MessageTypes.ServerBroadcast)
                {
                    try
                    {
                        var data = MessagePack.MessagePackSerializer.Deserialize<BroadcastNotification>(e.Payload);
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine($"   Broadcast: {data.Message}");
                        Console.WriteLine($"   Time: {data.Timestamp:HH:mm:ss}");
                        Console.WriteLine($"   Category: {data.Category}");
                        Console.ResetColor();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"   Failed to deserialize: {ex.Message}");
                    }
                }
                Console.Write("\nEnter message (or 'quit' to exit): ");
            };

            try
            {
                Console.WriteLine("Connecting to server...");
                await client.ConnectAsync();
                Console.WriteLine("Connected!");

                // 测试获取服务器信息
                try
                {
                    var serverInfo = await client.GetServerInfoAsync();
                    Console.WriteLine($"\nServer Info:");
                    Console.WriteLine($"  Version: {serverInfo.Version}");
                    Console.WriteLine($"  Clients: {serverInfo.ConnectedClients}");
                    Console.WriteLine($"  Uptime: {serverInfo.Uptime:hh\\:mm\\:ss}");
                    Console.WriteLine($"  Machine: {serverInfo.MachineName}");
                    Console.WriteLine();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to get server info: {ex.Message}");
                }

                // 主消息循环
                while (true)
                {
                    Console.Write("Enter message (or 'quit' to exit): ");
                    var input = Console.ReadLine();

                    if (string.IsNullOrEmpty(input))
                        continue;

                    if (input.Equals("quit", StringComparison.OrdinalIgnoreCase))
                        break;

                    try
                    {
                        // 使用扩展方法发送 Echo
                        var echo = await client.SendEchoStringAsync(input);
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"  {echo}");
                        Console.ResetColor();
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"  Error: {ex.Message}");
                        Console.ResetColor();
                    }
                }

                await client.DisconnectAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fatal error: {ex.Message}");
            }

            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
        }

        private static ILogger CreateLogger()
        {
            var loggerFactory = new LoggerFactory();
            loggerFactory.AddProvider(new ConsoleLoggerProvider());
            return loggerFactory.CreateLogger<IpcClient>();
        }
    }

    public class ConsoleLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ConsoleLogger(categoryName);
        public void Dispose() { }
    }

    public class ConsoleLogger : ILogger
    {
        private readonly string _categoryName;
        public ConsoleLogger(string categoryName) => _categoryName = categoryName;
        public IDisposable BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            Console.ForegroundColor = GetLogLevelColor(logLevel);
            Console.WriteLine($"[{timestamp}] [{logLevel}] {message}");
            if (exception != null) Console.WriteLine($"  Exception: {exception.Message}");
            Console.ResetColor();
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
