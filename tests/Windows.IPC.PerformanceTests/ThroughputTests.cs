using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Windows.IPC.PerformanceTests
{
    /// <summary>
    /// 吞吐量测试
    /// </summary>
    public class ThroughputTests : PerformanceTestBase
    {
        private readonly ITestOutputHelper _output;

        public ThroughputTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task MaxThroughput_SingleClient()
        {
            await InitializeAsync();

            const int duration = 10; // 秒
            var endTime = DateTime.Now.AddSeconds(duration);
            var count = 0L;

            var sw = Stopwatch.StartNew();

            while (DateTime.Now < endTime)
            {
                await Client!.SendRequestAsync<EmptyRequest, EmptyResponse>(
                    new EmptyRequest(), "Empty");
                count++;
            }

            sw.Stop();

            var throughput = count / sw.Elapsed.TotalSeconds;

            _output.WriteLine($@"
Max Throughput (Single Client):
===============================
Requests:        {count:N0}
Duration:        {sw.Elapsed.TotalSeconds:F2}s
Throughput:      {throughput:N0} req/s
");

            Assert.True(throughput > 5000, $"Throughput {throughput:N0} should be > 5000 req/s");
        }

        [Theory]
        [InlineData(1)]
        [InlineData(5)]
        [InlineData(10)]
        [InlineData(20)]
        [InlineData(50)]
        public async Task ConcurrentClients_Throughput(int clientCount)
        {
            await InitializeAsync();

            const int requestsPerClient = 1000;
            var tasks = new Task[clientCount];

            var sw = Stopwatch.StartNew();

            for (int i = 0; i < clientCount; i++)
            {
                tasks[i] = Task.Run(async () =>
                {
                    // 每个任务复用主客户端（实际场景应该每个创建一个）
                    for (int j = 0; j < requestsPerClient; j++)
                    {
                        await Client!.SendRequestAsync<EmptyRequest, EmptyResponse>(
                            new EmptyRequest(), "Empty");
                    }
                });
            }

            await Task.WhenAll(tasks);
            sw.Stop();

            var totalRequests = clientCount * requestsPerClient;
            var throughput = totalRequests / sw.Elapsed.TotalSeconds;

            _output.WriteLine($@"
Concurrent Clients: {clientCount}
Total Requests:     {totalRequests:N0}
Duration:           {sw.Elapsed.TotalSeconds:F2}s
Throughput:         {throughput:N0} req/s
Avg per Client:     {throughput / clientCount:N0} req/s
");
        }

        [Fact]
        public async Task DataThroughput_LargePayload()
        {
            await InitializeAsync(100);

            const int payloadSize = 1024 * 1024; // 1MB
            const int requestCount = 100;

            var data = new byte[payloadSize];
            new Random().NextBytes(data);

            var sw = Stopwatch.StartNew();

            for (int i = 0; i < requestCount; i++)
            {
                await Client!.SendRequestAsync<LargeDataRequest, LargeDataResponse>(
                    new LargeDataRequest { Data = data }, "LargeData");
            }

            sw.Stop();

            var totalMB = requestCount * payloadSize * 2 / 1024.0 / 1024.0; // *2 for request+response
            var throughputMBps = totalMB / sw.Elapsed.TotalSeconds;

            _output.WriteLine($@"
Large Payload Throughput:
=========================
Payload Size:    {payloadSize / 1024.0 / 1024.0:F2} MB
Requests:        {requestCount}
Total Data:      {totalMB:F2} MB
Duration:        {sw.Elapsed.TotalSeconds:F2}s
Throughput:      {throughputMBps:F2} MB/s
");

            Assert.True(throughputMBps > 50, $"Throughput {throughputMBps:F2} MB/s should be > 50 MB/s");
        }
    }
}
