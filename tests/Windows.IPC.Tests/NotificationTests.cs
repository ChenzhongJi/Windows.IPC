using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.IPC.Client;
using Windows.IPC.Contracts;
using Windows.IPC.Contracts.Messages;
using Windows.IPC.Serialization;
using Windows.IPC.Server;
using Windows.IPC.Transport;
using Xunit;

namespace Windows.IPC.Tests
{
    public class NotificationTests : IDisposable
    {
        private readonly IpcServer _server;
        private readonly IpcClient _client;
        private const string TestPipeName = "TestPipe_Notifications";

        public NotificationTests()
        {
            var serverOptions = new IpcServerOptions
            {
                PipeOptions = new IpcPipeOptions { PipeName = TestPipeName },
                AutoHandleHeartbeat = false
            };

            var clientOptions = new IpcClientOptions
            {
                PipeOptions = new IpcPipeOptions { PipeName = TestPipeName },
                ReconnectionPolicy = ReconnectionPolicy.Disabled,
                HeartbeatIntervalMs = 0
            };

            _server = new IpcServer(serverOptions);
            _client = new IpcClient(clientOptions);
        }

        [Fact]
        public async Task Broadcast_ShouldSetMessageType()
        {
            // Arrange
            await _server.StartAsync();
            await _client.ConnectAsync();

            string? receivedMessageType = null;
            BroadcastNotification? receivedData = null;

            _client.NotificationReceived += (s, e) =>
            {
                receivedMessageType = e.MessageType;
                receivedData = MessageSerializer.Deserialize<BroadcastNotification>(e.Payload);
            };

            await Task.Delay(100); // 等待订阅生效

            // Act
            var broadcast = new BroadcastNotification
            {
                Message = "Test Broadcast",
                Timestamp = DateTime.Now
            };

            await _server.BroadcastNotificationAsync(broadcast, MessageTypes.ServerBroadcast);
            await Task.Delay(200); // 等待接收

            // Assert
            Assert.NotNull(receivedMessageType);
            Assert.Equal(MessageTypes.ServerBroadcast, receivedMessageType);
            Assert.NotNull(receivedData);
            Assert.Equal("Test Broadcast", receivedData.Message);
        }

        [Fact]
        public async Task SendNotification_ShouldSetMessageType()
        {
            // Arrange
            await _server.StartAsync();
            await _client.ConnectAsync();

            string? receivedMessageType = null;

            _server.NotificationReceived += (s, e) =>
            {
                receivedMessageType = e.MessageType;
            };

            await Task.Delay(100);

            // Act
            var progress = new ProgressNotification
            {
                Percentage = 50,
                Message = "Half done"
            };

            await _client.SendNotificationAsync(progress, MessageTypes.Progress);
            await Task.Delay(200);

            // Assert
            Assert.NotNull(receivedMessageType);
            Assert.Equal(MessageTypes.Progress, receivedMessageType);
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
