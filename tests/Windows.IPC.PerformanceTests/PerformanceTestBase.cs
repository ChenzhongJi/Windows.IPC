using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Windows.IPC.Client;
using Windows.IPC.Contracts.Messages;
using Windows.IPC.Server;
using Windows.IPC.Transport;

namespace Windows.IPC.PerformanceTests
{
    /// <summary>
    /// 性能测试基类
    /// </summary>
    public abstract class PerformanceTestBase : IDisposable
    {
        protected IpcServer? Server { get; private set; }

        protected IpcClient? Client { get; private set; }

        protected ILogger? Logger { get; set; }

        protected PerformanceMetrics Metrics { get; } = new PerformanceMetrics();

        private const string TestPipeName = "PerfTest_Pipe";

        /// <summary>
        /// 获取服务器的管道名称
        /// </summary>
        protected string? ServerPipeName { get; private set; }

        /// <summary>
        /// 初始化测试环境
        /// </summary>
        protected async Task InitializeAsync(int maxServerInstances = 1, int maxConcurrentClients = 1, bool enableLogging = false)
        {
            if (enableLogging)
            {
                Logger = CreateLogger();
            }

            // 创建服务器
            var serverOptions = new IpcServerOptions
            {
                PipeOptions =
                    new PipeOptions
                    {
                        PipeName = TestPipeName + "_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                        MaxServerInstances = maxServerInstances,
                        BufferSize = 65536
                    },
                MaxConcurrentClients = maxConcurrentClients,
                AutoHandleHeartbeat = false,
                SecurityMode = PipeSecurityMode.Default
            };

            Server = new IpcServer(serverOptions, Logger);

            // 注册处理器
            Server.RegisterHandler(new EchoHandler());
            Server.RegisterHandler(new EmptyHandler());
            Server.RegisterHandler(new LargeDataHandler());

            await Server.StartAsync();

            // 记录服务器管道名称
            ServerPipeName = serverOptions.PipeOptions.PipeName;

            // 创建客户端
            var clientOptions = new IpcClientOptions
            {
                PipeOptions = new PipeOptions { PipeName = serverOptions.PipeOptions.PipeName, BufferSize = 65536 },
                ReconnectionPolicy = ReconnectionPolicy.Disabled,
                HeartbeatIntervalMs = 0,
            };

            Client = new IpcClient(clientOptions, Logger);
            await Client.ConnectAsync();
        }

        /// <summary>
        /// 清理测试环境
        /// </summary>
        public virtual void Dispose()
        {
            Client?.DisconnectAsync().GetAwaiter().GetResult();
            Client?.Dispose();
            Server?.StopAsync().GetAwaiter().GetResult();
            Server?.Dispose();
        }

        private static ILogger CreateLogger()
        {
            var loggerFactory = new LoggerFactory();
            loggerFactory.AddProvider(new ConsoleLoggerProvider());
            return loggerFactory.CreateLogger<IpcClient>();
        }

        public class ConsoleLoggerProvider : ILoggerProvider
        {
            public ILogger CreateLogger(string categoryName) => new ConsoleLogger(categoryName);

            public void Dispose()
            {
            }
        }

        public class ConsoleLogger : ILogger
        {
            private readonly string _categoryName;
            public ConsoleLogger(string categoryName) => _categoryName = categoryName;
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
                Console.ForegroundColor = GetLogLevelColor(logLevel);
                Console.WriteLine($"[{timestamp}] [{logLevel}] {message}");
                if (exception != null)
                    Console.WriteLine($"  Exception: {exception.Message}");
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

        #region Test Handlers
        private class EchoHandler : MessageHandler<EchoRequest, EchoResponse>
        {
            public override string MessageType => "Echo";

            protected override Task<EchoResponse> HandleAsync(
                EchoRequest request,
                ConnectedClient client,
                System.Threading.CancellationToken cancellationToken)
            { return Task.FromResult(new EchoResponse { Echo = request.Message, ServerTime = DateTime.Now }); }
        }

        private class EmptyHandler : MessageHandler<EmptyRequest, EmptyResponse>
        {
            public override string MessageType => "Empty";

            protected override Task<EmptyResponse> HandleAsync(
                EmptyRequest request,
                ConnectedClient client,
                System.Threading.CancellationToken cancellationToken)
            { return Task.FromResult(new EmptyResponse()); }
        }

        private class LargeDataHandler : MessageHandler<LargeDataRequest, LargeDataResponse>
        {
            public override string MessageType => "LargeData";

            protected override Task<LargeDataResponse> HandleAsync(
                LargeDataRequest request,
                ConnectedClient client,
                System.Threading.CancellationToken cancellationToken)
            { return Task.FromResult(new LargeDataResponse { Data = request.Data }); }
        }
        #endregion

        #region Test Message Types
        [MessagePack.MessagePackObject]
        public class EmptyRequest
        {
        }

        [MessagePack.MessagePackObject]
        public class EmptyResponse
        {
        }

        [MessagePack.MessagePackObject]
        public class LargeDataRequest
        {
            [MessagePack.Key(0)]
            public byte[] Data { get; set; } = Array.Empty<byte>();
        }

        [MessagePack.MessagePackObject]
        public class LargeDataResponse
        {
            [MessagePack.Key(0)]
            public byte[] Data { get; set; } = Array.Empty<byte>();
        }
        #endregion
    }

    /// <summary>
    /// 性能指标收集器
    /// </summary>
    public class PerformanceMetrics
    {
        private readonly Stopwatch _stopwatch = new Stopwatch();
        private long _requestCount;
        private long _totalLatencyMs;
        private long _minLatencyMs = long.MaxValue;
        private long _maxLatencyMs = long.MinValue;
        private long _totalBytes;

        public void StartTimer() => _stopwatch.Restart();

        public void StopTimer() => _stopwatch.Stop();

        public void RecordRequest(long latencyMs, long bytes = 0)
        {
            _requestCount++;
            _totalLatencyMs += latencyMs;
            _totalBytes += bytes;

            if (latencyMs < _minLatencyMs)
                _minLatencyMs = latencyMs;
            if (latencyMs > _maxLatencyMs)
                _maxLatencyMs = latencyMs;
        }

        public PerformanceReport GetReport()
        {
            var elapsedSeconds = _stopwatch.Elapsed.TotalSeconds;

            return new PerformanceReport
            {
                TotalRequests = _requestCount,
                ElapsedSeconds = elapsedSeconds,
                RequestsPerSecond = _requestCount / elapsedSeconds,
                AverageLatencyMs = _requestCount > 0 ? (double)_totalLatencyMs / _requestCount : 0,
                MinLatencyMs = _minLatencyMs == long.MaxValue ? 0 : _minLatencyMs,
                MaxLatencyMs = _maxLatencyMs == long.MinValue ? 0 : _maxLatencyMs,
                TotalBytes = _totalBytes,
                ThroughputMBps = _totalBytes / elapsedSeconds / 1024 / 1024
            };
        }

        public void Reset()
        {
            _stopwatch.Reset();
            _requestCount = 0;
            _totalLatencyMs = 0;
            _minLatencyMs = long.MaxValue;
            _maxLatencyMs = long.MinValue;
            _totalBytes = 0;
        }
    }

    public class PerformanceReport
    {
        public long TotalRequests { get; set; }

        public double ElapsedSeconds { get; set; }

        public double RequestsPerSecond { get; set; }

        public double AverageLatencyMs { get; set; }

        public long MinLatencyMs { get; set; }

        public long MaxLatencyMs { get; set; }

        public long TotalBytes { get; set; }

        public double ThroughputMBps { get; set; }

        public override string ToString()
        {
            return $@"
Performance Report:
==================
Total Requests:      {TotalRequests:N0}
Elapsed Time:        {ElapsedSeconds:F2}s
Requests/Second:     {RequestsPerSecond:N0}
Average Latency:     {AverageLatencyMs:F2}ms
Min Latency:         {MinLatencyMs}ms
Max Latency:         {MaxLatencyMs}ms
Total Data:          {TotalBytes / 1024.0 / 1024.0:F2}MB
Throughput:          {ThroughputMBps:F2}MB/s
";
        }
    }
}
