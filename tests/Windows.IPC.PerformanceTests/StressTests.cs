using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.IPC.Client;
using Windows.IPC.Transport;
using Xunit;
using Xunit.Abstractions;

namespace Windows.IPC.PerformanceTests
{
    /// <summary>
    /// 压力测试
    /// </summary>
    public class StressTests : PerformanceTestBase
    {
        private readonly ITestOutputHelper _output;

        public StressTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Theory]
        [InlineData(10)]
        [InlineData(50)]
        [InlineData(100)]
        public async Task MultipleClients_ConcurrentConnections(int clientCount)
        {
            await InitializeAsync(clientCount + 1, clientCount + 1);

            var clients = new List<IpcClient>();
            var connectTasks = new List<Task>();

            _output.WriteLine($"Creating {clientCount} clients...");

            var sw = Stopwatch.StartNew();

            try
            {
                // 创建并连接所有客户端
                for (int i = 0; i < clientCount; i++)
                {
                    var client = new IpcClient(new IpcClientOptions
                    {
                        PipeOptions = new PipeOptions
                        {
                            PipeName = ServerPipeName ?? "Unknown"
                        },
                        ReconnectionPolicy = ReconnectionPolicy.Disabled,
                        HeartbeatIntervalMs = 0
                    });

                    clients.Add(client);
                    connectTasks.Add(client.ConnectAsync());
                }

                // Task.WhenAll(connectTasks)只是保证所有客户端发起连接请求完成，不保证服务器端处理完成
                await Task.WhenAll(connectTasks);

                sw.Stop();

                _output.WriteLine($"✓ All {clientCount} clients connected in {sw.ElapsedMilliseconds}ms");
                _output.WriteLine($"Server reports {Server!.ConnectedClientCount} connected clients");

                // 添加重试逻辑，等待所有连接事件被处理
                int actualConnectedClients = 0;
                int maxRetries = 10;
                int retryCount = 0;

                while (retryCount < maxRetries)
                {
                    actualConnectedClients = Server.ConnectedClientCount;
                    if (actualConnectedClients == clientCount + 1) // +1 for base client
                    {
                        break;
                    }

                    // 等待一小段时间让连接事件被处理
                    await Task.Delay(100);
                    retryCount++;
                }

                _output.WriteLine($"Retry #{retryCount}: Server reports {actualConnectedClients} connected clients");

                Assert.Equal(clientCount + 1, Server.ConnectedClientCount); // +1 for base client

                // 每个客户端发送请求
                _output.WriteLine("Sending requests from all clients...");

                sw.Restart();
                var requestTasks = clients.Select(async c =>
                {
                    return await c.SendRequestAsync<EmptyRequest, EmptyResponse>(
                        new EmptyRequest(), "Empty");
                }).ToList();

                await Task.WhenAll(requestTasks);
                sw.Stop();

                _output.WriteLine($"✓ All requests completed in {sw.ElapsedMilliseconds}ms");
                _output.WriteLine($"Average: {sw.ElapsedMilliseconds / (double)clientCount:F2}ms per request");
            }
            finally
            {
                // 清理
                foreach (var client in clients)
                {
                    await client.DisconnectAsync();
                    client.Dispose();
                }
            }
        }

        [Fact]
        public async Task LongRunning_MemoryStability()
        {
            await InitializeAsync();

            const int duration = 60; // 秒
            const int interval = 10; // 每10ms一个请求

            var startMemory = GC.GetTotalMemory(true);
            var samples = new List<long>();

            _output.WriteLine($"Starting long-running test for {duration}s...");
            _output.WriteLine($"Initial memory: {startMemory / 1024.0 / 1024.0:F2} MB");

            var endTime = DateTime.Now.AddSeconds(duration);
            var requestCount = 0L;

            while (DateTime.Now < endTime)
            {
                await Client!.SendRequestAsync<EmptyRequest, EmptyResponse>(
                    new EmptyRequest(), "Empty");

                requestCount++;

                if (requestCount % 1000 == 0)
                {
                    var currentMemory = GC.GetTotalMemory(false);
                    samples.Add(currentMemory);

                    _output.WriteLine($"  {requestCount:N0} requests, Memory: {currentMemory / 1024.0 / 1024.0:F2} MB");
                }

                await Task.Delay(interval);
            }

            var endMemory = GC.GetTotalMemory(true);
            var memoryGrowth = endMemory - startMemory;

            _output.WriteLine($@"
Long Running Test Results:
=========================
Duration:        {duration}s
Requests:        {requestCount:N0}
Start Memory:    {startMemory / 1024.0 / 1024.0:F2} MB
End Memory:      {endMemory / 1024.0 / 1024.0:F2} MB
Memory Growth:   {memoryGrowth / 1024.0 / 1024.0:F2} MB
");

            // 内存增长不应超过 50MB
            Assert.True(memoryGrowth < 50 * 1024 * 1024,
                $"Memory growth {memoryGrowth / 1024.0 / 1024.0:F2} MB should be < 50 MB");
        }

        [Fact]
        public async Task RapidConnect_Disconnect()
        {
            await InitializeAsync();

            const int cycles = 100;

            _output.WriteLine($"Testing {cycles} rapid connect/disconnect cycles...");

            var sw = Stopwatch.StartNew();

            for (int i = 0; i < cycles; i++)
            {
                var client = new IpcClient(new IpcClientOptions
                {
                    PipeOptions = new PipeOptions
                    {
                        PipeName = ServerPipeName ?? "Unknown"
                    },
                    ReconnectionPolicy = ReconnectionPolicy.Disabled
                });

                await client.ConnectAsync();

                // 发送一个请求
                await client.SendRequestAsync<EmptyRequest, EmptyResponse>(
                    new EmptyRequest(), "Empty");

                await client.DisconnectAsync();
                client.Dispose();

                if ((i + 1) % 20 == 0)
                {
                    _output.WriteLine($"  Completed {i + 1} cycles");
                }
            }

            sw.Stop();

            _output.WriteLine($@"
Rapid Connect/Disconnect Test:
==============================
Cycles:          {cycles}
Duration:        {sw.Elapsed.TotalSeconds:F2}s
Avg per Cycle:   {sw.ElapsedMilliseconds / (double)cycles:F2}ms
Server Clients:  {Server!.ConnectedClientCount}
");

            // 确保没有泄漏连接
            Assert.True(Server.ConnectedClientCount <= 2, "Should have no leaked connections");
        }

        [Fact(Skip = "Long running test")]
        public async Task Endurance_24Hours()
        {
            // 24小时耐久测试
            await InitializeAsync();

            const int durationHours = 24;
            var endTime = DateTime.Now.AddHours(durationHours);
            var requestCount = 0L;
            var errorCount = 0L;
            var lastReport = DateTime.Now;

            _output.WriteLine($"Starting {durationHours}h endurance test...");

            while (DateTime.Now < endTime)
            {
                try
                {
                    await Client!.SendRequestAsync<EmptyRequest, EmptyResponse>(
                        new EmptyRequest(), "Empty", timeoutMs: 5000);

                    requestCount++;
                }
                catch
                {
                    errorCount++;
                }

                // 每小时报告一次
                if (DateTime.Now - lastReport > TimeSpan.FromHours(1))
                {
                    var memory = GC.GetTotalMemory(false);
                    _output.WriteLine($"[{DateTime.Now:HH:mm}] Requests: {requestCount:N0}, Errors: {errorCount}, Memory: {memory / 1024.0 / 1024.0:F2} MB");
                    lastReport = DateTime.Now;
                }

                await Task.Delay(100);
            }

            var successRate = (requestCount - errorCount) / (double)requestCount * 100;

            _output.WriteLine($@"
Endurance Test Complete:
========================
Duration:        {durationHours}h
Total Requests:  {requestCount:N0}
Errors:          {errorCount}
Success Rate:    {successRate:F2}%
");

            Assert.True(successRate > 99.9, $"Success rate {successRate:F2}% should be > 99.9%");
        }
    }
}
