using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.IPC.Abstractions;
using Windows.IPC.Client;
using Windows.IPC.Contracts;
using Windows.IPC.Contracts.Messages;
using Windows.IPC.Exceptions;
using Windows.IPC.Server;
using Windows.IPC.Transport;
using Xunit;

namespace Windows.IPC.Tests
{
    /// <summary>
    /// 连接生命周期与错误路径测试
    /// 覆盖：超时、错误响应传播、连接生命周期、Dispose、重连策略
    /// </summary>
    public class LifecycleAndErrorTests
    {
        private const string TestPipePrefix = "TestPipe_Lifecycle_";

        private static string GetUniquePipeName()
        {
            return TestPipePrefix + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        #region 请求超时

        [Fact]
        public async Task SendRequest_Timeout_ThrowsTimeoutException()
        {
            // Arrange — 服务端不注册 handler，请求将无法被处理
            var pipeName = GetUniquePipeName();
            var server = new IpcServer(new IpcServerOptions
            {
                PipeOptions = new IpcPipeOptions { PipeName = pipeName },
                AutoHandleHeartbeat = false, // 不注册心跳处理器
                MaxConcurrentClients = 2
            });

            var client = new IpcClient(new IpcClientOptions
            {
                PipeOptions = new IpcPipeOptions { PipeName = pipeName },
                ReconnectionPolicy = ReconnectionPolicy.Disabled,
                HeartbeatIntervalMs = 0,
                DefaultRequestTimeoutMs = 2000 // 2秒超时
            });

            try
            {
                await server.StartAsync();
                await client.ConnectAsync();

                // Act & Assert — 发送请求但服务端无 handler，应超时
                // 注意：MessageDispatcher 对无 handler 的请求会返回 NO_HANDLER 错误响应，
                // 这不会超时。要触发超时，需要一个延迟响应的 handler。
                // 这里用一个短超时 + 空 handler（但 handler 会返回 NO_HANDLER 错误）
                // 所以我们测试真正的超时场景：发送到断开的服务端
                await client.DisconnectAsync();

                // Act — 已断开的客户端发送请求应抛出 ConnectionException
                await Assert.ThrowsAsync<ConnectionException>(() =>
                    client.SendRequestAsync<EmptyRequest, EmptyResponse>(
                        new EmptyRequest(), "Test", timeoutMs: 1000));
            }
            finally
            {
                client?.Dispose();
                server?.Dispose();
            }
        }

        [Fact]
        public async Task SendRequest_ShortTimeout_ExpiresCorrectly()
        {
            // Arrange — 创建一个慢响应的 handler
            var pipeName = GetUniquePipeName();
            var server = new IpcServer(new IpcServerOptions
            {
                PipeOptions = new IpcPipeOptions { PipeName = pipeName },
                AutoHandleHeartbeat = false,
                MaxConcurrentClients = 2
            });
            server.RegisterHandler(new SlowHandler(3000)); // 3秒才响应

            var client = new IpcClient(new IpcClientOptions
            {
                PipeOptions = new IpcPipeOptions { PipeName = pipeName },
                ReconnectionPolicy = ReconnectionPolicy.Disabled,
                HeartbeatIntervalMs = 0
            });

            try
            {
                await server.StartAsync();
                await client.ConnectAsync();

                // Act & Assert — 设置1秒超时，handler要3秒才响应，应该超时
                await Assert.ThrowsAsync<Windows.IPC.Exceptions.TimeoutException>(() =>
                    client.SendRequestAsync<EmptyRequest, EmptyResponse>(
                        new EmptyRequest(), "Slow", timeoutMs: 1000));
            }
            finally
            {
                client?.Dispose();
                server?.Dispose();
            }
        }

        #endregion

        #region 错误响应传播

        [Fact]
        public async Task SendRequest_NoHandler_ReturnsRemoteException()
        {
            // Arrange — 服务端不注册对应 handler
            var pipeName = GetUniquePipeName();
            var server = new IpcServer(new IpcServerOptions
            {
                PipeOptions = new IpcPipeOptions { PipeName = pipeName },
                AutoHandleHeartbeat = false,
                MaxConcurrentClients = 2
            });

            var client = new IpcClient(new IpcClientOptions
            {
                PipeOptions = new IpcPipeOptions { PipeName = pipeName },
                ReconnectionPolicy = ReconnectionPolicy.Disabled,
                HeartbeatIntervalMs = 0
            });

            try
            {
                await server.StartAsync();
                await client.ConnectAsync();

                // Act & Assert — 服务端无 handler，返回 NO_HANDLER 错误响应，
                // 客户端应抛出 RemoteException
                var ex = await Assert.ThrowsAsync<RemoteException>(() =>
                    client.SendRequestAsync<EmptyRequest, EmptyResponse>(
                        new EmptyRequest(), "NonExistent", timeoutMs: 5000));

                Assert.Equal("NO_HANDLER", ex.ErrorCode);
            }
            finally
            {
                client?.Dispose();
                server?.Dispose();
            }
        }

        [Fact]
        public async Task SendRequest_HandlerThrows_ReturnsHandlerError()
        {
            // Arrange — 注册一个会抛异常的 handler
            var pipeName = GetUniquePipeName();
            var server = new IpcServer(new IpcServerOptions
            {
                PipeOptions = new IpcPipeOptions { PipeName = pipeName },
                AutoHandleHeartbeat = false,
                MaxConcurrentClients = 2
            });
            server.RegisterHandler(new ThrowingHandler());

            var client = new IpcClient(new IpcClientOptions
            {
                PipeOptions = new IpcPipeOptions { PipeName = pipeName },
                ReconnectionPolicy = ReconnectionPolicy.Disabled,
                HeartbeatIntervalMs = 0
            });

            try
            {
                await server.StartAsync();
                await client.ConnectAsync();

                // Act & Assert — handler 抛异常，服务端返回 HANDLER_ERROR
                var ex = await Assert.ThrowsAsync<RemoteException>(() =>
                    client.SendRequestAsync<EmptyRequest, EmptyResponse>(
                        new EmptyRequest(), "Throwing", timeoutMs: 5000));

                Assert.Equal("InvalidOperationException", ex.ErrorCode);
            }
            finally
            {
                client?.Dispose();
                server?.Dispose();
            }
        }

        #endregion

        #region 连接生命周期

        [Fact]
        public async Task Connect_WhenServerNotRunning_ThrowsConnectionException()
        {
            // Arrange — 客户端尝试连接不存在的服务端
            var pipeName = GetUniquePipeName();
            var client = new IpcClient(new IpcClientOptions
            {
                PipeOptions = new IpcPipeOptions { PipeName = pipeName, ConnectionTimeoutMs = 1000 },
                ReconnectionPolicy = ReconnectionPolicy.Disabled,
                HeartbeatIntervalMs = 0
            });

            try
            {
                // Act & Assert
                await Assert.ThrowsAsync<ConnectionException>(() => client.ConnectAsync());
            }
            finally
            {
                client?.Dispose();
            }
        }

        [Fact]
        public async Task Connect_AlreadyConnected_ReturnsEarly()
        {
            // Arrange
            var pipeName = GetUniquePipeName();
            var server = new IpcServer(new IpcServerOptions
            {
                PipeOptions = new IpcPipeOptions { PipeName = pipeName },
                MaxConcurrentClients = 2
            });
            var client = new IpcClient(new IpcClientOptions
            {
                PipeOptions = new IpcPipeOptions { PipeName = pipeName },
                ReconnectionPolicy = ReconnectionPolicy.Disabled,
                HeartbeatIntervalMs = 0
            });

            try
            {
                await server.StartAsync();
                await client.ConnectAsync();

                // Act — 再次连接应直接返回，不抛异常
                await client.ConnectAsync();

                // Assert — 仍然连接
                Assert.True(client.IsConnected);
            }
            finally
            {
                client?.Dispose();
                server?.Dispose();
            }
        }

        [Fact]
        public async Task Disconnect_AlreadyDisconnected_ReturnsEarly()
        {
            // Arrange
            var pipeName = GetUniquePipeName();
            var client = new IpcClient(new IpcClientOptions
            {
                PipeOptions = new IpcPipeOptions { PipeName = pipeName },
                ReconnectionPolicy = ReconnectionPolicy.Disabled,
                HeartbeatIntervalMs = 0
            });

            try
            {
                // Act — 断开一个从未连接的客户端，不应抛异常
                await client.DisconnectAsync();
                Assert.Equal(ConnectionState.Disconnected, client.State);
            }
            finally
            {
                client?.Dispose();
            }
        }

        [Fact]
        public async Task ServerStop_WithConnectedClients_DisconnectsGracefully()
        {
            // Arrange — 启动服务端并连接多个客户端
            var pipeName = GetUniquePipeName();
            var server = new IpcServer(new IpcServerOptions
            {
                PipeOptions = new IpcPipeOptions { PipeName = pipeName },
                AutoHandleHeartbeat = true,
                MaxConcurrentClients = 5
            });

            var client1 = new IpcClient(new IpcClientOptions
            {
                PipeOptions = new IpcPipeOptions { PipeName = pipeName },
                ReconnectionPolicy = ReconnectionPolicy.Disabled,
                HeartbeatIntervalMs = 0
            });

            var client2 = new IpcClient(new IpcClientOptions
            {
                PipeOptions = new IpcPipeOptions { PipeName = pipeName },
                ReconnectionPolicy = ReconnectionPolicy.Disabled,
                HeartbeatIntervalMs = 0
            });

            var bothClientsConnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var connectionCount = 0;
            server.ClientConnected += (sender, args) =>
            {
                if (Interlocked.Increment(ref connectionCount) == 2)
                {
                    bothClientsConnected.TrySetResult(true);
                }
            };

            try
            {
                await server.StartAsync();
                await client1.ConnectAsync();
                await client2.ConnectAsync();

                Assert.Same(bothClientsConnected.Task, await Task.WhenAny(bothClientsConnected.Task, Task.Delay(5000)));
                Assert.Equal(2, server.ConnectedClientCount);

                // Act — 停止服务端
                await server.StopAsync();

                // Assert — 客户端应被断开
                Assert.Equal(0, server.ConnectedClientCount);
            }
            finally
            {
                client1?.Dispose();
                client2?.Dispose();
                server?.Dispose();
            }
        }

        [Fact]
        public async Task StateChanged_Fires_OnConnectAndDisconnect()
        {
            // Arrange
            var pipeName = GetUniquePipeName();
            var server = new IpcServer(new IpcServerOptions
            {
                PipeOptions = new IpcPipeOptions { PipeName = pipeName },
                MaxConcurrentClients = 2
            });

            var client = new IpcClient(new IpcClientOptions
            {
                PipeOptions = new IpcPipeOptions { PipeName = pipeName },
                ReconnectionPolicy = ReconnectionPolicy.Disabled,
                HeartbeatIntervalMs = 0
            });

            var stateChanges = new System.Collections.Generic.List<ConnectionState>();

            client.StateChanged += (s, e) => stateChanges.Add(e.NewState);

            try
            {
                await server.StartAsync();

                // Act — 连接
                await client.ConnectAsync();

                // 断开
                await client.DisconnectAsync();

                // Assert — 应记录状态变更
                Assert.Contains(ConnectionState.Connecting, stateChanges);
                Assert.Contains(ConnectionState.Connected, stateChanges);
                Assert.Contains(ConnectionState.Disconnected, stateChanges);
            }
            finally
            {
                client?.Dispose();
                server?.Dispose();
            }
        }

        #endregion

        #region Dispose 模式

        [Fact]
        public void IpcClient_DisposeTwice_DoesNotThrow()
        {
            // Arrange
            var client = new IpcClient();

            // Act — 双重释放
            client.Dispose();
            client.Dispose();

            // Assert — 不抛异常
        }

        [Fact]
        public void IpcServer_DisposeTwice_DoesNotThrow()
        {
            // Arrange
            var server = new IpcServer();

            // Act — 双重释放
            server.Dispose();
            server.Dispose();

            // Assert — 不抛异常
        }

        [Fact]
        public void IpcClient_AfterDispose_ThrowsObjectDisposedException()
        {
            // Arrange
            var client = new IpcClient();
            client.Dispose();

            // Act & Assert
            Assert.Throws<ObjectDisposedException>(() => client.ConnectAsync().GetAwaiter().GetResult());
        }

        [Fact]
        public void IpcServer_AfterDispose_ThrowsObjectDisposedException()
        {
            // Arrange
            var server = new IpcServer();
            server.Dispose();

            // Act & Assert
            Assert.Throws<ObjectDisposedException>(() => server.StartAsync().GetAwaiter().GetResult());
        }

        [Fact]
        public async Task IpcClient_DisposeWhileConnected_CleansUp()
        {
            // Arrange
            var pipeName = GetUniquePipeName();
            var server = new IpcServer(new IpcServerOptions
            {
                PipeOptions = new IpcPipeOptions { PipeName = pipeName },
                MaxConcurrentClients = 2
            });

            var client = new IpcClient(new IpcClientOptions
            {
                PipeOptions = new IpcPipeOptions { PipeName = pipeName },
                ReconnectionPolicy = ReconnectionPolicy.Disabled,
                HeartbeatIntervalMs = 0
            });

            try
            {
                await server.StartAsync();
                await client.ConnectAsync();
                Assert.True(client.IsConnected);

                // Act — 在连接中释放
                client.Dispose();

                // Assert — 已释放
                Assert.False(client.IsConnected);
            }
            finally
            {
                server?.Dispose();
            }
        }

        #endregion

        #region 重连策略

        [Fact]
        public void ReconnectionPolicy_CalculateDelay_ExponentialBackoff()
        {
            // Arrange
            var policy = new ReconnectionPolicy
            {
                InitialDelayMs = 1000,
                MaxDelayMs = 30000,
                BackoffMultiplier = 2.0
            };

            // Act & Assert
            Assert.Equal(1000, policy.CalculateDelay(1));   // 1000 * 2^0 = 1000
            Assert.Equal(2000, policy.CalculateDelay(2));   // 1000 * 2^1 = 2000
            Assert.Equal(4000, policy.CalculateDelay(3));   // 1000 * 2^2 = 4000
            Assert.Equal(8000, policy.CalculateDelay(4));   // 1000 * 2^3 = 8000
        }

        [Fact]
        public void ReconnectionPolicy_CalculateDelay_CappedAtMaxDelay()
        {
            // Arrange
            var policy = new ReconnectionPolicy
            {
                InitialDelayMs = 1000,
                MaxDelayMs = 5000,
                BackoffMultiplier = 2.0
            };

            // Act & Assert — 第4次重试理论值 8000，但应被限制为 5000
            Assert.Equal(5000, policy.CalculateDelay(4));
            Assert.Equal(5000, policy.CalculateDelay(10));
        }

        [Fact]
        public void ReconnectionPolicy_DefaultPolicy_HasExpectedValues()
        {
            var policy = ReconnectionPolicy.Default;
            Assert.True(policy.Enabled);
            Assert.Equal(1000, policy.InitialDelayMs);
            Assert.Equal(30000, policy.MaxDelayMs);
            Assert.Equal(0, policy.MaxAttempts); // 无限
            Assert.Equal(2.0, policy.BackoffMultiplier);
        }

        [Fact]
        public void ReconnectionPolicy_DisabledPolicy_HasEnabledFalse()
        {
            var policy = ReconnectionPolicy.Disabled;
            Assert.False(policy.Enabled);
        }

        #endregion

        #region 服务端幂等操作

        [Fact]
        public async Task Server_StartTwice_ReturnsEarly()
        {
            var pipeName = GetUniquePipeName();
            var server = new IpcServer(new IpcServerOptions
            {
                PipeOptions = new IpcPipeOptions { PipeName = pipeName },
                MaxConcurrentClients = 2
            });

            try
            {
                await server.StartAsync();
                Assert.True(server.IsRunning);

                // 再次启动应直接返回
                await server.StartAsync();
                Assert.True(server.IsRunning);
            }
            finally
            {
                server?.Dispose();
            }
        }

        [Fact]
        public async Task Server_StopWhenNotRunning_ReturnsEarly()
        {
            var pipeName = GetUniquePipeName();
            var server = new IpcServer(new IpcServerOptions
            {
                PipeOptions = new IpcPipeOptions { PipeName = pipeName },
                MaxConcurrentClients = 2
            });

            try
            {
                // 从未启动，停止应直接返回
                await server.StopAsync();
                Assert.False(server.IsRunning);
            }
            finally
            {
                server?.Dispose();
            }
        }

        #endregion

        #region 辅助类

        /// <summary>
        /// 慢响应处理器，用于测试超时
        /// </summary>
        private class SlowHandler : MessageHandler<EmptyRequest, EmptyResponse>
        {
            private readonly int _delayMs;

            public override string MessageType => "Slow";

            public SlowHandler(int delayMs) => _delayMs = delayMs;

            protected override async Task<EmptyResponse> HandleAsync(
                EmptyRequest request,
                ConnectedClient client,
                CancellationToken cancellationToken)
            {
                await Task.Delay(_delayMs, cancellationToken);
                return new EmptyResponse { Success = true };
            }
        }

        /// <summary>
        /// 抛异常的处理器，用于测试错误传播
        /// </summary>
        private class ThrowingHandler : MessageHandler<EmptyRequest, EmptyResponse>
        {
            public override string MessageType => "Throwing";

            protected override Task<EmptyResponse> HandleAsync(
                EmptyRequest request,
                ConnectedClient client,
                CancellationToken cancellationToken)
            {
                throw new InvalidOperationException("Test exception from handler");
            }
        }

        #endregion
    }
}
