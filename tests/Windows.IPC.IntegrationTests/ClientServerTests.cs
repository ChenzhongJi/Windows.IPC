using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.IPC.Client;
using Windows.IPC.Server;
using Windows.IPC.Transport;
using Xunit;

namespace Windows.IPC.IntegrationTests
{
    [MessagePack.MessagePackObject]
    public class TestRequest
    {
        [MessagePack.Key(0)]
        public string Data { get; set; } = string.Empty;
    }

    [MessagePack.MessagePackObject]
    public class TestResponse
    {
        [MessagePack.Key(0)]
        public string Result { get; set; } = string.Empty;
    }

    public class TestHandler : MessageHandler<TestRequest, TestResponse>
    {
        public override string MessageType => "Test";

        protected override Task<TestResponse> HandleAsync(
            TestRequest request,
            ConnectedClient client,
            System.Threading.CancellationToken cancellationToken)
        {
            return Task.FromResult(new TestResponse
            {
                Result = $"Processed: {request.Data}"
            });
        }
    }

    public class ClientServerTests : IDisposable
    {
        private readonly IpcServer _server;
        private readonly IpcClient _client;
        private const string TestPipeName = "TestPipe_" + nameof(ClientServerTests);

        public ClientServerTests()
        {
            var serverOptions = new IpcServerOptions
            {
                PipeOptions = new IpcPipeOptions { PipeName = TestPipeName }
            };

            var clientOptions = new IpcClientOptions
            {
                PipeOptions = new IpcPipeOptions { PipeName = TestPipeName },
                ReconnectionPolicy = ReconnectionPolicy.Disabled
            };

            _server = new IpcServer(serverOptions);
            _server.RegisterHandler(new TestHandler());

            _client = new IpcClient(clientOptions);
        }

        [Fact]
        public async Task RequestResponse_Success()
        {
            // Arrange
            await _server.StartAsync();
            await _client.ConnectAsync();

            // Act
            var response = await _client.SendRequestAsync<TestRequest, TestResponse>(
                new TestRequest { Data = "Hello" },
                "Test");

            // Assert
            Assert.NotNull(response);
            Assert.Equal("Processed: Hello", response.Result);
        }

        [Fact]
        public async Task MultipleRequests_Success()
        {
            // Arrange
            await _server.StartAsync();
            await _client.ConnectAsync();

            // Act & Assert
            for (int i = 0; i < 10; i++)
            {
                var response = await _client.SendRequestAsync<TestRequest, TestResponse>(
                    new TestRequest { Data = $"Message {i}" },
                    "Test");

                Assert.Equal($"Processed: Message {i}", response.Result);
            }
        }

        [Fact]
        public async Task ServerBroadcast_ClientReceives()
        {
            // Arrange
            await _server.StartAsync();
            await _client.ConnectAsync();

            var receivedNotification = false;
            _client.NotificationReceived += (sender, e) =>
            {
                receivedNotification = true;
            };

            // Act
            await Task.Delay(100); // 确保客户端准备好接收
            await _server.BroadcastNotificationAsync(new { }, "TestNotification");
            await Task.Delay(100); // 等待接收

            // Assert
            Assert.True(receivedNotification);
        }

        public void Dispose()
        {
            _client?.DisconnectAsync().GetAwaiter().GetResult();
            _client?.Dispose();
            _server?.StopAsync().GetAwaiter().GetResult();
            _server?.Dispose();
        }
    }
}
