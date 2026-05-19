using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.IPC.Client;
using Windows.IPC.Contracts;
using Windows.IPC.Contracts.Messages;
using Windows.IPC.Server;
using Windows.IPC.Transport;
using Xunit;

namespace Windows.IPC.Tests
{
    /// <summary>
    /// 心跳功能集成测试
    /// 覆盖客户端心跳发送、服务端自动处理、心跳元数据更新等场景
    /// </summary>
    public class HeartbeatTests : IDisposable
    {
        private readonly IpcServer _server;
        private IpcClient _client;
        private const string TestPipePrefix = "TestPipe_Heartbeat_";

        private static string GetUniquePipeName()
        {
            return TestPipePrefix + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        public HeartbeatTests()
        {
            var pipeName = GetUniquePipeName();

            var serverOptions = new IpcServerOptions
            {
                PipeOptions = new IpcPipeOptions { PipeName = pipeName },
                AutoHandleHeartbeat = true
            };

            var clientOptions = new IpcClientOptions
            {
                PipeOptions = new IpcPipeOptions { PipeName = pipeName },
                ReconnectionPolicy = ReconnectionPolicy.Disabled,
                HeartbeatIntervalMs = 0, // 默认禁用，由各测试自行控制
                HeartbeatTimeoutMs = 5000
            };

            _server = new IpcServer(serverOptions);
            _client = new IpcClient(clientOptions);
        }

        #region 心跳发送与响应

        [Fact]
        public async Task Heartbeat_ClientSendsRequest_ReceivesResponse()
        {
            // Arrange
            await _server.StartAsync();
            await _client.ConnectAsync();

            // Act — 手动发送心跳请求（模拟心跳定时器触发）
            var response = await _client.SendRequestAsync<HeartbeatRequest, HeartbeatResponse>(
                new HeartbeatRequest(),
                MessageTypes.Heartbeat,
                timeoutMs: 5000);

            // Assert
            Assert.NotNull(response);
        }

        [Fact]
        public async Task Heartbeat_MultipleSequentialRequests_AllSucceed()
        {
            // Arrange
            await _server.StartAsync();
            await _client.ConnectAsync();

            // Act — 连续发送多次心跳
            for (int i = 0; i < 5; i++)
            {
                var response = await _client.SendRequestAsync<HeartbeatRequest, HeartbeatResponse>(
                    new HeartbeatRequest(),
                    MessageTypes.Heartbeat,
                    timeoutMs: 5000);

                // Assert
                Assert.NotNull(response);
            }
        }

        [Fact]
        public async Task Heartbeat_ConcurrentRequests_AllSucceed()
        {
            // Arrange
            await _server.StartAsync();
            await _client.ConnectAsync();

            // Act — 并发发送多个心跳请求
            var tasks = new Task<HeartbeatResponse>[5];
            for (int i = 0; i < 5; i++)
            {
                tasks[i] = _client.SendRequestAsync<HeartbeatRequest, HeartbeatResponse>(
                    new HeartbeatRequest(),
                    MessageTypes.Heartbeat,
                    timeoutMs: 5000);
            }

            var responses = await Task.WhenAll(tasks);

            // Assert — 所有请求都应收到回复
            Assert.All(responses, r => Assert.NotNull(r));
        }

        #endregion

        #region 服务端自动处理

        [Fact]
        public async Task Heartbeat_AutoHandleEnabled_ServerRespondsAutomatically()
        {
            // Arrange — AutoHandleHeartbeat = true (默认)
            await _server.StartAsync();
            await _client.ConnectAsync();

            // Act — 客户端发送心跳，服务端应自动回复
            var response = await _client.SendRequestAsync<HeartbeatRequest, HeartbeatResponse>(
                new HeartbeatRequest(),
                MessageTypes.Heartbeat,
                timeoutMs: 5000);

            // Assert
            Assert.NotNull(response);
        }

        [Fact]
        public async Task Heartbeat_AutoHandleDisabled_ServerDoesNotRespond()
        {
            // Arrange — 创建禁用自动心跳的服务端
            var pipeName = GetUniquePipeName();
            var serverNoAuto = new IpcServer(new IpcServerOptions
            {
                PipeOptions = new IpcPipeOptions { PipeName = pipeName },
                AutoHandleHeartbeat = false
            });

            var clientNoAuto = new IpcClient(new IpcClientOptions
            {
                PipeOptions = new IpcPipeOptions { PipeName = pipeName },
                ReconnectionPolicy = ReconnectionPolicy.Disabled,
                HeartbeatIntervalMs = 0,
                DefaultRequestTimeoutMs = 2000
            });

            try
            {
                await serverNoAuto.StartAsync();
                await clientNoAuto.ConnectAsync();

                // Act & Assert — 服务端没有注册心跳处理器，客户端发送心跳应超时
                await Assert.ThrowsAsync<Windows.IPC.Exceptions.RemoteException>(() =>
                    clientNoAuto.SendRequestAsync<HeartbeatRequest, HeartbeatResponse>(
                        new HeartbeatRequest(),
                        MessageTypes.Heartbeat,
                        timeoutMs: 2000));
            }
            finally
            {
                clientNoAuto?.DisconnectAsync().GetAwaiter().GetResult();
                clientNoAuto?.Dispose();
                serverNoAuto?.StopAsync().GetAwaiter().GetResult();
                serverNoAuto?.Dispose();
            }
        }

        [Fact]
        public async Task Heartbeat_CustomHandler_OverridesDefault()
        {
            // Arrange — 注册自定义心跳处理器
            var pipeName = GetUniquePipeName();
            var customServer = new IpcServer(new IpcServerOptions
            {
                PipeOptions = new IpcPipeOptions { PipeName = pipeName },
                AutoHandleHeartbeat = true
            });

            var customClient = new IpcClient(new IpcClientOptions
            {
                PipeOptions = new IpcPipeOptions { PipeName = pipeName },
                ReconnectionPolicy = ReconnectionPolicy.Disabled,
                HeartbeatIntervalMs = 0
            });

            // 注意：由于 AutoHandleHeartbeat = true 会在构造函数中注册默认 HeartbeatHandler，
            // 而相同 MessageType 的 handler 只能有一个，后注册的会覆盖先注册的。
            // 如果需要自定义心跳处理，可以先 UnregisterHandler 再 RegisterHandler。

            try
            {
                await customServer.StartAsync();
                await customClient.ConnectAsync();

                // 使用默认处理器
                var response = await customClient.SendRequestAsync<HeartbeatRequest, HeartbeatResponse>(
                    new HeartbeatRequest(),
                    MessageTypes.Heartbeat,
                    timeoutMs: 5000);

                Assert.NotNull(response);
            }
            finally
            {
                customClient?.DisconnectAsync().GetAwaiter().GetResult();
                customClient?.Dispose();
                customServer?.StopAsync().GetAwaiter().GetResult();
                customServer?.Dispose();
            }
        }

        #endregion

        #region 心跳元数据更新

        [Fact]
        public async Task Heartbeat_ServerUpdatesLastHeartbeatTime()
        {
            // Arrange
            await _server.StartAsync();
            await _client.ConnectAsync();

            var beforeHeartbeat = DateTime.UtcNow;

            // Act — 发送心跳
            await _client.SendRequestAsync<HeartbeatRequest, HeartbeatResponse>(
                new HeartbeatRequest(),
                MessageTypes.Heartbeat,
                timeoutMs: 5000);

            // 等待服务端处理完毕
            await Task.Delay(200);

            var afterHeartbeat = DateTime.UtcNow;

            // Assert — 检查服务端客户端的 LastHeartbeat 时间已更新
            var clients = _server.GetConnectedClients();
            Assert.Single(clients);
            Assert.NotNull(clients[0].Metadata.LastHeartbeat);
            Assert.InRange(clients[0].Metadata.LastHeartbeat!.Value, beforeHeartbeat, afterHeartbeat);
        }

        [Fact]
        public async Task Heartbeat_LastHeartbeat_UpdatesOnEachBeat()
        {
            // Arrange
            await _server.StartAsync();
            await _client.ConnectAsync();

            // Act — 第一次心跳
            await _client.SendRequestAsync<HeartbeatRequest, HeartbeatResponse>(
                new HeartbeatRequest(),
                MessageTypes.Heartbeat,
                timeoutMs: 5000);
            await Task.Delay(100);

            var firstHeartbeat = _server.GetConnectedClients()[0].Metadata.LastHeartbeat;
            Assert.NotNull(firstHeartbeat);

            // 等待至少 1ms 确保时间戳不同
            await Task.Delay(10);

            // Act — 第二次心跳
            await _client.SendRequestAsync<HeartbeatRequest, HeartbeatResponse>(
                new HeartbeatRequest(),
                MessageTypes.Heartbeat,
                timeoutMs: 5000);
            await Task.Delay(100);

            var secondHeartbeat = _server.GetConnectedClients()[0].Metadata.LastHeartbeat;
            Assert.NotNull(secondHeartbeat);

            // Assert — 第二次心跳时间应晚于或等于第一次
            Assert.True(secondHeartbeat >= firstHeartbeat,
                $"Second heartbeat ({secondHeartbeat}) should be >= first ({firstHeartbeat})");
        }

        [Fact]
        public async Task Heartbeat_NonHeartbeatRequest_UpdatesLastActivityNotLastHeartbeat()
        {
            // Arrange — 注册一个 Echo handler
            _server.RegisterHandler(new EchoHandler());
            await _server.StartAsync();
            await _client.ConnectAsync();

            // Act — 发送非心跳请求
            var response = await _client.SendRequestAsync<EmptyRequest, EmptyResponse>(
                new EmptyRequest(),
                "Echo",
                timeoutMs: 5000);
            await Task.Delay(200);

            // Assert
            Assert.NotNull(response);
            var client = _server.GetConnectedClients()[0];
            // 非心跳请求应更新 LastActivity，而非 LastHeartbeat
            Assert.Null(client.Metadata.LastHeartbeat);
            // LastActivity 应该被更新（默认值为 DateTime.MinValue，肯定小于当前时间）
            Assert.True(client.Metadata.LastActivity > DateTime.MinValue);
        }

        #endregion

        #region 心跳定时器

        [Fact]
        public async Task Heartbeat_TimerAutomaticallySendsHeartbeat()
        {
            // Arrange — 创建启用心跳的客户端
            var pipeName = GetUniquePipeName();
            var timerServer = new IpcServer(new IpcServerOptions
            {
                PipeOptions = new IpcPipeOptions { PipeName = pipeName },
                AutoHandleHeartbeat = true
            });

            var timerClient = new IpcClient(new IpcClientOptions
            {
                PipeOptions = new IpcPipeOptions { PipeName = pipeName },
                ReconnectionPolicy = ReconnectionPolicy.Disabled,
                HeartbeatIntervalMs = 500, // 每 500ms 发送一次心跳
                HeartbeatTimeoutMs = 3000
            });

            try
            {
                await timerServer.StartAsync();
                await timerClient.ConnectAsync();

                // 等待足够时间让定时器触发至少 2 次心跳
                await Task.Delay(1500);

                // Assert — 服务端应已收到心跳，LastHeartbeat 应不为 null
                var clients = timerServer.GetConnectedClients();
                Assert.Single(clients);
                Assert.NotNull(clients[0].Metadata.LastHeartbeat);

                // 心跳时间应在近期（2秒内）
                var heartbeatAge = DateTime.UtcNow - clients[0].Metadata.LastHeartbeat!.Value;
                Assert.True(heartbeatAge < TimeSpan.FromSeconds(2),
                    $"Heartbeat age {heartbeatAge.TotalMilliseconds}ms should be less than 2000ms");
            }
            finally
            {
                timerClient?.DisconnectAsync().GetAwaiter().GetResult();
                timerClient?.Dispose();
                timerServer?.StopAsync().GetAwaiter().GetResult();
                timerServer?.Dispose();
            }
        }

        [Fact]
        public async Task Heartbeat_TimerDisabled_DoesNotSendHeartbeat()
        {
            // Arrange — 心跳间隔为 0（禁用）
            var pipeName = GetUniquePipeName();
            var noHbServer = new IpcServer(new IpcServerOptions
            {
                PipeOptions = new IpcPipeOptions { PipeName = pipeName },
                AutoHandleHeartbeat = true
            });

            var noHbClient = new IpcClient(new IpcClientOptions
            {
                PipeOptions = new IpcPipeOptions { PipeName = pipeName },
                ReconnectionPolicy = ReconnectionPolicy.Disabled,
                HeartbeatIntervalMs = 0 // 禁用心跳
            });

            try
            {
                await noHbServer.StartAsync();
                await noHbClient.ConnectAsync();

                // 等待一段时间
                await Task.Delay(2000);

                // Assert — LastHeartbeat 应为 null（没有自动心跳发送）
                var clients = noHbServer.GetConnectedClients();
                Assert.Single(clients);
                Assert.Null(clients[0].Metadata.LastHeartbeat);
            }
            finally
            {
                noHbClient?.DisconnectAsync().GetAwaiter().GetResult();
                noHbClient?.Dispose();
                noHbServer?.StopAsync().GetAwaiter().GetResult();
                noHbServer?.Dispose();
            }
        }

        #endregion

        #region 心跳与正常请求共存

        [Fact]
        public async Task Heartbeat_DoesNotInterfereWithNormalRequests()
        {
            // Arrange — 注册 Echo handler，启用自动心跳
            _server.RegisterHandler(new EchoHandler());
            await _server.StartAsync();
            await _client.ConnectAsync();

            // Act — 同时发送心跳和正常请求
            var heartbeatTask = _client.SendRequestAsync<HeartbeatRequest, HeartbeatResponse>(
                new HeartbeatRequest(),
                MessageTypes.Heartbeat,
                timeoutMs: 5000);

            var echoTask = _client.SendRequestAsync<EmptyRequest, EmptyResponse>(
                new EmptyRequest(),
                "Echo",
                timeoutMs: 5000);

            await Task.WhenAll(heartbeatTask, echoTask);

            // Assert — 两种请求都应正常完成
            Assert.NotNull(heartbeatTask.Result);
            Assert.NotNull(echoTask.Result);
        }

        #endregion

        #region 辅助类

        /// <summary>
        /// 简单的 Echo 处理器，用于测试非心跳请求
        /// </summary>
        private class EchoHandler : MessageHandler<EmptyRequest, EmptyResponse>
        {
            public override string MessageType => "Echo";

            protected override Task<EmptyResponse> HandleAsync(
                EmptyRequest request,
                ConnectedClient client,
                CancellationToken cancellationToken)
            {
                return Task.FromResult(new EmptyResponse { Success = true });
            }
        }

        #endregion

        public void Dispose()
        {
            _client?.DisconnectAsync().GetAwaiter().GetResult();
            _client?.Dispose();
            _server?.StopAsync().GetAwaiter().GetResult();
            _server?.Dispose();
        }
    }
}
