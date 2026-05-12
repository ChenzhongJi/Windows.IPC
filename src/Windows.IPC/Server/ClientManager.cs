using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.IPC.Abstractions;
using Windows.IPC.Server;

namespace Windows.IPC.Server
{
    /// <summary>
    /// 客户端连接管理器
    /// </summary>
    internal class ClientManager : IDisposable
    {
        private readonly ConcurrentDictionary<string, ConnectedClient> _clients;
        private readonly ILogger? _logger;
        private bool _disposed;

        public int ClientCount => _clients.Count;

        public event EventHandler<ClientConnectedEventArgs>? ClientConnected;
        public event EventHandler<ClientDisconnectedEventArgs>? ClientDisconnected;

        public ClientManager(ILogger? logger = null)
        {
            _clients = new ConcurrentDictionary<string, ConnectedClient>();
            _logger = logger;
        }

        /// <summary>
        /// 添加客户端
        /// </summary>
        public bool AddClient(ConnectedClient client)
        {
            if (client == null)
                throw new ArgumentNullException(nameof(client));

            if (_clients.TryAdd(client.ClientId, client))
            {
                client.StateChanged += OnClientStateChanged;

                _logger?.LogInformation("Client {ClientId} connected. Total clients: {Count}",
                    client.ClientId, _clients.Count);

                OnClientConnected(client);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 移除客户端
        /// </summary>
        public bool RemoveClient(string clientId, string? reason = null)
        {
            if (_clients.TryRemove(clientId, out var client))
            {
                client.StateChanged -= OnClientStateChanged;

                _logger?.LogInformation("Client {ClientId} disconnected. Reason: {Reason}. Remaining clients: {Count}",
                    clientId, reason ?? "Unknown", _clients.Count);

                OnClientDisconnected(client, reason);

                client.Dispose();
                return true;
            }

            return false;
        }

        /// <summary>
        /// 获取客户端
        /// </summary>
        public ConnectedClient? GetClient(string clientId)
        {
            _clients.TryGetValue(clientId, out var client);
            return client;
        }

        /// <summary>
        /// 获取所有客户端
        /// </summary>
        public IReadOnlyList<ConnectedClient> GetAllClients()
        {
            return _clients.Values.ToList();
        }

        /// <summary>
        /// 向指定客户端发送消息
        /// </summary>
        public async Task<bool> SendToClientAsync(
            string clientId,
            IMessage message,
            CancellationToken cancellationToken = default)
        {
            var client = GetClient(clientId);
            if (client == null || !client.IsConnected)
            {
                return false;
            }

            try
            {
                await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to send message to client {ClientId}", clientId);
                return false;
            }
        }

        /// <summary>
        /// 广播消息给所有客户端
        /// </summary>
        public async Task<int> BroadcastAsync(
            IMessage message,
            Func<ConnectedClient, bool>? predicate = null,
            CancellationToken cancellationToken = default)
        {
            var clients = predicate != null
                ? _clients.Values.Where(predicate).ToList()
                : _clients.Values.ToList();

            var successCount = 0;
            var tasks = new List<Task>();

            foreach (var client in clients)
            {
                if (!client.IsConnected)
                    continue;

                var task = Task.Run(async () =>
                {
                    try
                    {
                        await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
                        Interlocked.Increment(ref successCount);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Failed to broadcast to client {ClientId}", client.ClientId);
                    }
                }, cancellationToken);

                tasks.Add(task);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);

            _logger?.LogDebug("Broadcast completed. Sent to {SuccessCount}/{TotalCount} clients",
                successCount, clients.Count);

            return successCount;
        }

        /// <summary>
        /// 断开所有客户端
        /// </summary>
        public void DisconnectAll(string? reason = null)
        {
            var clientIds = _clients.Keys.ToList();
            foreach (var clientId in clientIds)
            {
                RemoveClient(clientId, reason);
            }
        }

        private void OnClientStateChanged(object? sender, ConnectionStateChangedEventArgs e)
        {
            if (sender is ConnectedClient client)
            {
                if (e.NewState == ConnectionState.Disconnected ||
                    e.NewState == ConnectionState.Faulted)
                {
                    RemoveClient(client.ClientId, $"State changed to {e.NewState}");
                }
            }
        }

        private void OnClientConnected(ConnectedClient client)
        {
            ClientConnected?.Invoke(this, new ClientConnectedEventArgs
            {
                ClientId = client.ClientId,
                ConnectedAt = client.ConnectedAt
            });
        }

        private void OnClientDisconnected(ConnectedClient client, string? reason)
        {
            ClientDisconnected?.Invoke(this, new ClientDisconnectedEventArgs
            {
                ClientId = client.ClientId,
                DisconnectedAt = DateTime.UtcNow,
                Reason = reason
            });
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            DisconnectAll("Server shutting down");
        }
    }

    /// <summary>
    /// 客户端连接事件参数
    /// </summary>
    public class ClientConnectedEventArgs : EventArgs
    {
        public string ClientId { get; set; } = string.Empty;
        public DateTime ConnectedAt { get; set; }
    }

    /// <summary>
    /// 客户端断开事件参数
    /// </summary>
    public class ClientDisconnectedEventArgs : EventArgs
    {
        public string ClientId { get; set; } = string.Empty;
        public DateTime DisconnectedAt { get; set; }
        public string? Reason { get; set; }
    }
}
