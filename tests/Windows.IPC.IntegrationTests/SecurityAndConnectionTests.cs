using System;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.IPC.Abstractions;
using Windows.IPC.Client;
using Windows.IPC.Exceptions;
using Windows.IPC.Security;
using Windows.IPC.Server;
using Windows.IPC.Transport;
using Xunit;

namespace Windows.IPC.IntegrationTests
{
    public class SecurityAndConnectionTests
    {
        [Fact]
        public async Task SharedSecretAuthentication_MatchingKeys_AllowsRequests()
        {
            var pipeName = NewPipeName();
            var secret = Encoding.UTF8.GetBytes("matching-test-secret-32-bytes-long");
            using (var server = CreateServer(pipeName, secret))
            using (var client = CreateClient(pipeName, secret))
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            {
                await server.StartAsync();
                await client.ConnectAsync(timeout.Token);

                var response = await client.SendRequestAsync<TestRequest, TestResponse>(
                    new TestRequest { Data = "authenticated" }, "Test", cancellationToken: timeout.Token);

                Assert.Equal("Processed: authenticated", response.Result);
            }
        }

        [Fact]
        public async Task EncryptedConnection_MatchingKeys_AllowsRequests()
        {
            var pipeName = NewPipeName();
            var secret = Encoding.UTF8.GetBytes("matching-test-secret-32-bytes-long");
            using (var server = CreateServer(pipeName, secret, encrypted: true))
            using (var client = CreateClient(pipeName, secret, encrypted: true))
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            {
                await server.StartAsync();
                await client.ConnectAsync(timeout.Token);

                var response = await client.SendRequestAsync<TestRequest, TestResponse>(
                    new TestRequest { Data = "encrypted" }, "Test", cancellationToken: timeout.Token);

                Assert.Equal("Processed: encrypted", response.Result);
            }
        }

        [Fact]
        public async Task SharedSecretAuthentication_DifferentKeys_RejectsConnection()
        {
            var pipeName = NewPipeName();
            var serverSecret = Encoding.UTF8.GetBytes("server-test-secret-32-bytes-long");
            var clientSecret = Encoding.UTF8.GetBytes("client-test-secret-32-bytes-long");
            using (var server = CreateServer(pipeName, serverSecret))
            using (var client = CreateClient(pipeName, clientSecret))
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            {
                await server.StartAsync();

                var error = await Assert.ThrowsAsync<ConnectionException>(
                    () => client.ConnectAsync(timeout.Token));

                Assert.IsType<Windows.IPC.Exceptions.SecurityException>(error.InnerException);
                Assert.False(client.IsConnected);
                Assert.Equal(0, server.ConnectedClientCount);
            }
        }

        [Fact]
        public async Task SendRequest_OversizedMessage_IsRejectedAndConnectionRemainsUsable()
        {
            var pipeName = NewPipeName();
            using (var server = CreateServer(pipeName))
            using (var client = CreateClient(pipeName, maxMessageSize: 512))
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            {
                await server.StartAsync();
                await client.ConnectAsync(timeout.Token);

                var oversizedData = string.Concat(Enumerable.Range(0, 128).Select(index => Guid.NewGuid().ToString("N")));
                var error = await Assert.ThrowsAsync<ArgumentException>(() =>
                    client.SendRequestAsync<TestRequest, TestResponse>(
                        new TestRequest { Data = oversizedData }, "Test", cancellationToken: timeout.Token));
                Assert.Contains("exceeds maximum", error.Message);

                var response = await client.SendRequestAsync<TestRequest, TestResponse>(
                    new TestRequest { Data = "small" }, "Test", cancellationToken: timeout.Token);
                Assert.Equal("Processed: small", response.Result);
            }
        }

        [Fact]
        public async Task ConcurrentRequests_ResponsesMatchTheirRequests()
        {
            var pipeName = NewPipeName();
            using (var server = CreateServer(pipeName))
            using (var client = CreateClient(pipeName))
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            {
                await server.StartAsync();
                await client.ConnectAsync(timeout.Token);

                var requests = Enumerable.Range(0, 32).Select(index =>
                    client.SendRequestAsync<TestRequest, TestResponse>(
                        new TestRequest { Data = index.ToString() }, "Test", cancellationToken: timeout.Token));
                var responses = await Task.WhenAll(requests);

                for (var index = 0; index < responses.Length; index++)
                {
                    Assert.Equal($"Processed: {index}", responses[index].Result);
                }
            }
        }

        [Fact]
        public async Task DisconnectAndReconnect_AllowsRequestsAgain()
        {
            var pipeName = NewPipeName();
            using (var server = CreateServer(pipeName))
            using (var client = CreateClient(pipeName))
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            {
                await server.StartAsync();
                await client.ConnectAsync(timeout.Token);
                await client.DisconnectAsync();
                await client.ConnectAsync(timeout.Token);

                var response = await client.SendRequestAsync<TestRequest, TestResponse>(
                    new TestRequest { Data = "reconnected" }, "Test", cancellationToken: timeout.Token);
                Assert.Equal("Processed: reconnected", response.Result);
            }
        }

        [Fact]
        public async Task ServerRestart_AutomaticallyReconnectsClient()
        {
            var pipeName = NewPipeName();
            var reconnecting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var server = CreateServer(pipeName))
            using (var client = CreateClient(pipeName, autoReconnect: true))
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            {
                client.StateChanged += (sender, args) =>
                {
                    if (args.NewState == ConnectionState.Reconnecting)
                    {
                        reconnecting.TrySetResult(true);
                    }
                    else if (args.NewState == ConnectionState.Connected && reconnecting.Task.IsCompleted)
                    {
                        reconnected.TrySetResult(true);
                    }
                };

                await server.StartAsync();
                await client.ConnectAsync(timeout.Token);
                await client.SendRequestAsync<TestRequest, TestResponse>(
                    new TestRequest { Data = "before restart" }, "Test", cancellationToken: timeout.Token);
                await server.StopAsync();
                Assert.Same(reconnecting.Task, await Task.WhenAny(reconnecting.Task, Task.Delay(5000, timeout.Token)));

                await server.StartAsync();
                Assert.Same(reconnected.Task, await Task.WhenAny(reconnected.Task, Task.Delay(10000, timeout.Token)));
                var response = await client.SendRequestAsync<TestRequest, TestResponse>(
                    new TestRequest { Data = "server restarted" }, "Test", cancellationToken: timeout.Token);
                Assert.Equal("Processed: server restarted", response.Result);
            }
        }

        [Fact]
        public async Task ServerStops_WhenReconnectionDisabled_ClientBecomesDisconnected()
        {
            var pipeName = NewPipeName();
            var disconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var server = CreateServer(pipeName))
            using (var client = CreateClient(pipeName))
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            {
                client.StateChanged += (sender, args) =>
                {
                    if (args.NewState == ConnectionState.Disconnected)
                    {
                        disconnected.TrySetResult(true);
                    }
                };
                await server.StartAsync();
                await client.ConnectAsync(timeout.Token);
                await client.SendRequestAsync<TestRequest, TestResponse>(
                    new TestRequest { Data = "connected" }, "Test", cancellationToken: timeout.Token);

                await server.StopAsync();

                Assert.Same(disconnected.Task, await Task.WhenAny(disconnected.Task, Task.Delay(5000, timeout.Token)));
                Assert.False(client.IsConnected);
            }
        }

        [Fact]
        public async Task Disconnect_WhenReconnectionEnabled_DoesNotReconnect()
        {
            var pipeName = NewPipeName();
            var reconnecting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var server = CreateServer(pipeName))
            using (var client = CreateClient(pipeName, autoReconnect: true))
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            {
                client.StateChanged += (sender, args) =>
                {
                    if (args.NewState == ConnectionState.Reconnecting)
                    {
                        reconnecting.TrySetResult(true);
                    }
                };
                await server.StartAsync();
                await client.ConnectAsync(timeout.Token);
                await client.SendRequestAsync<TestRequest, TestResponse>(
                    new TestRequest { Data = "connected" }, "Test", cancellationToken: timeout.Token);

                await client.DisconnectAsync();

                await Task.WhenAny(reconnecting.Task, Task.Delay(1000, timeout.Token));
                Assert.False(reconnecting.Task.IsCompleted);
                Assert.Equal(ConnectionState.Disconnected, client.State);
            }
        }

        [Fact]
        public async Task Disconnect_AfterReconnectAttemptFails_StopsFurtherAttempts()
        {
            var pipeName = NewPipeName();
            var reconnecting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var attemptFailed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var server = CreateServer(pipeName))
            using (var client = CreateClient(pipeName, autoReconnect: true))
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                client.StateChanged += (sender, args) =>
                {
                    if (args.NewState == ConnectionState.Reconnecting)
                        reconnecting.TrySetResult(true);
                    else if (reconnecting.Task.IsCompleted && args.NewState == ConnectionState.Disconnected)
                        attemptFailed.TrySetResult(true);
                    else if (reconnecting.Task.IsCompleted && args.NewState == ConnectionState.Connected)
                        reconnected.TrySetResult(true);
                };

                await server.StartAsync();
                await client.ConnectAsync(timeout.Token);
                await client.SendRequestAsync<TestRequest, TestResponse>(
                    new TestRequest { Data = "connected" }, "Test", cancellationToken: timeout.Token);
                await server.StopAsync();
                Assert.Same(attemptFailed.Task, await Task.WhenAny(attemptFailed.Task, Task.Delay(7000, timeout.Token)));

                await client.DisconnectAsync();
                await server.StartAsync();
                Assert.NotSame(reconnected.Task, await Task.WhenAny(reconnected.Task, Task.Delay(2500, timeout.Token)));
                Assert.Equal(ConnectionState.Disconnected, client.State);
            }
        }

        [Fact]
        public async Task Disconnect_FromReconnectingEvent_DoesNotStartReconnection()
        {
            var pipeName = NewPipeName();
            var disconnectStarted = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var server = CreateServer(pipeName))
            using (var client = CreateClient(pipeName, autoReconnect: true))
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                client.StateChanged += (sender, args) =>
                {
                    if (args.NewState == ConnectionState.Reconnecting)
                        disconnectStarted.TrySetResult(client.DisconnectAsync());
                    else if (args.NewState == ConnectionState.Connected && disconnectStarted.Task.IsCompleted)
                        reconnected.TrySetResult(true);
                };

                await server.StartAsync();
                await client.ConnectAsync(timeout.Token);
                await client.SendRequestAsync<TestRequest, TestResponse>(
                    new TestRequest { Data = "connected" }, "Test", cancellationToken: timeout.Token);
                await server.StopAsync();
                Assert.Same(disconnectStarted.Task, await Task.WhenAny(disconnectStarted.Task, Task.Delay(5000, timeout.Token)));
                var disconnectTask = await disconnectStarted.Task;
                Assert.Same(disconnectTask, await Task.WhenAny(disconnectTask, Task.Delay(5000, timeout.Token)));
                await disconnectTask;

                await server.StartAsync();
                Assert.NotSame(reconnected.Task, await Task.WhenAny(reconnected.Task, Task.Delay(2500, timeout.Token)));
                Assert.Equal(ConnectionState.Disconnected, client.State);
            }
        }

        [Fact]
        public async Task Disconnect_FromReconnectAttemptEvent_CompletesWithoutDeadlock()
        {
            var pipeName = NewPipeName();
            var reconnecting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var disconnectStarted = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var server = CreateServer(pipeName))
            using (var client = CreateClient(pipeName, autoReconnect: true))
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            {
                client.StateChanged += (sender, args) =>
                {
                    if (args.NewState == ConnectionState.Reconnecting)
                        reconnecting.TrySetResult(true);
                    else if (args.NewState == ConnectionState.Connecting && reconnecting.Task.IsCompleted)
                        disconnectStarted.TrySetResult(client.DisconnectAsync());
                };

                await server.StartAsync();
                await client.ConnectAsync(timeout.Token);
                await client.SendRequestAsync<TestRequest, TestResponse>(
                    new TestRequest { Data = "connected" }, "Test", cancellationToken: timeout.Token);
                await server.StopAsync();
                Assert.Same(disconnectStarted.Task, await Task.WhenAny(disconnectStarted.Task, Task.Delay(5000, timeout.Token)));
                var disconnectTask = await disconnectStarted.Task;
                Assert.Same(disconnectTask, await Task.WhenAny(disconnectTask, Task.Delay(5000, timeout.Token)));
                await disconnectTask;
                Assert.Equal(ConnectionState.Disconnected, client.State);
            }
        }

        [Fact]
        public async Task ServerStopsAgain_BeforePreviousReconnectionCompletes_ReconnectsAgain()
        {
            var pipeName = NewPipeName();
            var firstReconnect = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondReconnect = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var successBlocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var serverAcceptedAgain = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reconnectedAgain = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reconnectCount = 0;
            var acceptedCount = 0;
            using (var releaseSuccess = new ManualResetEventSlim())
            using (var server = CreateServer(pipeName))
            using (var client = CreateClient(pipeName, autoReconnect: true))
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                server.ClientConnected += (sender, args) =>
                {
                    if (Interlocked.Increment(ref acceptedCount) == 2)
                        serverAcceptedAgain.TrySetResult(true);
                };
                client.StateChanged += (sender, args) =>
                {
                    if (args.NewState == ConnectionState.Reconnecting)
                    {
                        if (Interlocked.Increment(ref reconnectCount) == 1)
                            firstReconnect.TrySetResult(true);
                        else
                            secondReconnect.TrySetResult(true);
                    }
                    else if (args.NewState == ConnectionState.Connected && secondReconnect.Task.IsCompleted)
                    {
                        reconnectedAgain.TrySetResult(true);
                    }
                };

                var reconnector = typeof(IpcClient).GetField("_reconnector", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(client);
                var succeededEvent = reconnector.GetType().GetEvent("ReconnectionSucceeded");
                EventHandler<ReconnectionEventArgs> handler = (sender, args) =>
                {
                    successBlocked.TrySetResult(true);
                    releaseSuccess.Wait(TimeSpan.FromSeconds(20));
                };
                succeededEvent.AddEventHandler(reconnector, handler);

                try
                {
                    await server.StartAsync();
                    await client.ConnectAsync(timeout.Token);
                    await client.SendRequestAsync<TestRequest, TestResponse>(
                        new TestRequest { Data = "connected" }, "Test", cancellationToken: timeout.Token);
                    await server.StopAsync();
                    Assert.Same(firstReconnect.Task, await Task.WhenAny(firstReconnect.Task, Task.Delay(5000, timeout.Token)));

                    await server.StartAsync();
                    Assert.Same(successBlocked.Task, await Task.WhenAny(successBlocked.Task, Task.Delay(10000, timeout.Token)));
                    Assert.Same(serverAcceptedAgain.Task, await Task.WhenAny(serverAcceptedAgain.Task, Task.Delay(5000, timeout.Token)));
                    await server.StopAsync();
                    Assert.Same(secondReconnect.Task, await Task.WhenAny(secondReconnect.Task, Task.Delay(5000, timeout.Token)));
                    await Task.Delay(250, timeout.Token);
                    releaseSuccess.Set();

                    await server.StartAsync();
                    Assert.Same(reconnectedAgain.Task, await Task.WhenAny(reconnectedAgain.Task, Task.Delay(10000, timeout.Token)));
                    var response = await client.SendRequestAsync<TestRequest, TestResponse>(
                        new TestRequest { Data = "reconnected again" }, "Test", cancellationToken: timeout.Token);
                    Assert.Equal("Processed: reconnected again", response.Result);
                }
                finally
                {
                    releaseSuccess.Set();
                    succeededEvent.RemoveEventHandler(reconnector, handler);
                }
            }
        }

        private static string NewPipeName() => "WindowsIpcIntegration_" + Guid.NewGuid().ToString("N");

        private static IpcServer CreateServer(string pipeName, byte[] secret = null, bool encrypted = false)
        {
            var server = new IpcServer(new IpcServerOptions
            {
                PipeOptions = CreatePipeOptions(pipeName, secret, encrypted)
            });
            server.RegisterHandler(new TestHandler());
            return server;
        }

        private static IpcClient CreateClient(string pipeName, byte[] secret = null, int maxMessageSize = 10 * 1024 * 1024, bool autoReconnect = false, bool encrypted = false)
        {
            var pipeOptions = CreatePipeOptions(pipeName, secret, encrypted);
            pipeOptions.MaxMessageSize = maxMessageSize;
            pipeOptions.ConnectionTimeoutMs = 2000;
            return new IpcClient(new IpcClientOptions
            {
                PipeOptions = pipeOptions,
                ReconnectionPolicy = autoReconnect ? ReconnectionPolicy.Fast : ReconnectionPolicy.Disabled,
                HeartbeatIntervalMs = 0
            });
        }

        private static IpcPipeOptions CreatePipeOptions(string pipeName, byte[] secret, bool encrypted)
        {
            var options = new IpcPipeOptions { PipeName = pipeName };
            if (secret != null)
            {
                options.SecurityOptions = new SecurityOptions
                {
                    EnableAuthentication = true,
                    SharedSecret = secret,
                    AuthenticationTimeoutMs = 5000,
                    EncryptionMode = encrypted ? EncryptionMode.AesCbcHmac : EncryptionMode.None,
                    EnableReplayProtection = encrypted
                };
            }
            return options;
        }
    }
}
