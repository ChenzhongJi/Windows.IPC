using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.IPC.Contracts.Messages;
using Xunit;
using Xunit.Abstractions;

namespace Windows.IPC.PerformanceTests
{
    /// <summary>
    /// 延迟基准测试
    /// </summary>
    public class LatencyTests : PerformanceTestBase
    {
        private readonly ITestOutputHelper _output;

        public LatencyTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task MinimalRequest_Latency()
        {
            await InitializeAsync();

            const int warmupCount = 100;
            const int testCount = 1000000;

            // 预热
            for (int i = 0; i < warmupCount; i++)
            {
                await Client!.SendRequestAsync<EmptyRequest, EmptyResponse>(
                    new EmptyRequest(), "Empty");
            }

            // 测试
            var latencies = new long[testCount];
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < testCount; i++)
            {
                var start = Stopwatch.GetTimestamp();

                await Client!.SendRequestAsync<EmptyRequest, EmptyResponse>(
                    new EmptyRequest(), "Empty");

                var end = Stopwatch.GetTimestamp();
                latencies[i] = (end - start) * 1_000_1000 / Stopwatch.Frequency;
            }

            sw.Stop();

            // 统计
            Array.Sort(latencies);
            var p50 = latencies[testCount / 2];
            var p90 = latencies[(int)(testCount * 0.9)];
            var p99 = latencies[(int)(testCount * 0.99)];
            var avg = latencies.Average();
            var min = latencies.Min();
            var max = latencies.Max();

            _output.WriteLine($@"
Minimal Request Latency Test:
=============================
Requests:        {testCount:N0}
Duration:        {sw.Elapsed.TotalMilliseconds:F2}ms
Throughput:      {testCount / sw.Elapsed.TotalSeconds:N0} req/s

Latency (us):
    Min:           {min}
    Avg:           {avg:F2}
    P50:           {p50}
    P90:           {p90}
    P99:           {p99}
    Max:           {max}
            ");

            // 断言性能目标
            Assert.True(p99 < 5000, $"P99 latency {p99}ms should be < 5000us");
        }

        [Fact]
        public async Task EchoRequest_Latency_1KB()
        {
            await InitializeAsync();

            const int testCount = 5000;
            const int payloadSize = 1024; // 1KB

            var payload = new string('X', payloadSize);
            var latencies = new long[testCount];

            // 预热
            for (int i = 0; i < 100; i++)
            {
                await Client!.SendRequestAsync<EchoRequest, EchoResponse>(
                    new EchoRequest { Message = payload }, "Echo");
            }

            // 测试
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < testCount; i++)
            {
                var start = Stopwatch.GetTimestamp();

                await Client!.SendRequestAsync<EchoRequest, EchoResponse>(
                    new EchoRequest { Message = payload }, "Echo");

                var end = Stopwatch.GetTimestamp();
                latencies[i] = (end - start) * 1_000_000 / Stopwatch.Frequency;
            }

            sw.Stop();

            Array.Sort(latencies);

            _output.WriteLine($@"
Echo Request Latency Test (1KB):
================================
Requests:        {testCount:N0}
Duration:        {sw.Elapsed.TotalMilliseconds:F2}ms
Throughput:      {testCount / sw.Elapsed.TotalSeconds:N0} req/s
Data Rate:       {testCount * payloadSize * 2 / sw.Elapsed.TotalSeconds / 1024 / 1024:F2} MB/s

Latency (us):
  P50:           {latencies[testCount / 2]}
  P90:           {latencies[(int)(testCount * 0.9)]}
  P99:           {latencies[(int)(testCount * 0.99)]}
");
        }

        [Theory]
        [InlineData(1)]           // 1 byte
        [InlineData(1024)]        // 1 KB
        [InlineData(10240)]       // 10 KB
        [InlineData(102400)]      // 100 KB
        [InlineData(1048576)]     // 1 MB
        public async Task Latency_ByPayloadSize(int payloadSize)
        {
            await InitializeAsync();

            const int testCount = 100;
            var data = new byte[payloadSize];
            new Random().NextBytes(data);

            var latencies = new long[testCount];

            for (int i = 0; i < testCount; i++)
            {
                var start = Stopwatch.GetTimestamp();

                await Client!.SendRequestAsync<LargeDataRequest, LargeDataResponse>(
                    new LargeDataRequest { Data = data }, "LargeData");

                var end = Stopwatch.GetTimestamp();
                latencies[i] = (end - start) * 1_000_000 / Stopwatch.Frequency;
            }

            Array.Sort(latencies);

            _output.WriteLine($@"
Payload Size: {payloadSize / 1024.0:F2} KB
  P50: {latencies[testCount / 2]}us
  P90: {latencies[(int)(testCount * 0.9)]}us
  P99: {latencies[(int)(testCount * 0.99)]}us
");
        }
    }
}
