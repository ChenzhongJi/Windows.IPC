using MessagePack;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.IPC.Abstractions;
using Windows.IPC.Exceptions;
using Windows.IPC.Protocol;
using Windows.IPC.Serialization;

namespace Windows.IPC.Transport
{
    /// <summary>
    /// 命名管道连接
    /// </summary>
    public class NamedPipeConnection : IConnection
    {
        private readonly PipeStream _pipeStream;
        private readonly ILogger? _logger;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _receiveLock = new SemaphoreSlim(1, 1);
        private readonly ReceiveBuffer _receiveBuffer = new ReceiveBuffer();
        private ConnectionState _state = ConnectionState.Connected;
        private bool _disposed;
        private readonly object _stateLock = new object();
        public string ConnectionId { get; }

        public ConnectionState State
        {
            get
            {
                lock (_stateLock)
                {
                    return _state;
                }
            }
            private set
            {
                lock (_stateLock)
                {
                    if (_state != value)
                    {
                        var oldState = _state;
                        _state = value;
                        StateChanged?.Invoke(
                        this,
                        new ConnectionStateChangedEventArgs { OldState = oldState, NewState = value });
                    }
                }
            }

        }

        public bool IsConnected => _pipeStream?.IsConnected == true && State == ConnectionState.Connected;

        public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

        public NamedPipeConnection(PipeStream pipeStream, ILogger? logger = null)
        {
            _pipeStream = pipeStream ?? throw new ArgumentNullException(nameof(pipeStream));
            _logger = logger;
            ConnectionId = Guid.NewGuid().ToString("N");

            _logger?.LogDebug("Connection {ConnectionId} created", ConnectionId);
        }

        /// <summary>
        /// 发送消息
        /// </summary>
        public async Task SendAsync(IMessage message, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            if (!IsConnected)
            {
                throw new ConnectionException("Connection is not established");
            }

            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // 序列化消息
                byte[] messageBytes;
                MessageFrame.FrameType frameType;

                if (message is RequestMessage request)
                {
                    messageBytes = MessageSerializer.Serialize(request);
                    frameType = MessageFrame.FrameType.Request;
                }
                else if (message is ResponseMessage response)
                {
                    messageBytes = MessageSerializer.Serialize(response);
                    frameType = MessageFrame.FrameType.Response;
                }
                else if (message is NotificationMessage notification)
                {
                    messageBytes = MessageSerializer.Serialize(notification);
                    frameType = MessageFrame.FrameType.Notification;
                }
                else
                {
                    throw new ArgumentException($"Unknown message type: {message.GetType()}");
                }

                // 编码帧
                var frameBytes = MessageFrame.Encode(frameType, messageBytes);

                // 发送
                await _pipeStream.WriteAsync(frameBytes, 0, frameBytes.Length, cancellationToken).ConfigureAwait(false);
                //await _pipeStream.FlushAsync(cancellationToken).ConfigureAwait(false);

                _logger?.LogTrace(
                "Sent {FrameType} message {MessageId}, {Size} bytes",
                frameType,
                message.MessageId,
                frameBytes.Length);
            }
            catch (IOException ex)
            {
                _logger?.LogError(ex, "Failed to send message");
                State = ConnectionState.Faulted;
                throw new ConnectionException("Failed to send message", ex);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>
        /// 接收消息
        /// </summary>
        public async Task<IMessage?> ReceiveAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            await _receiveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var buffer = ArrayPool<byte>.Shared.Rent(8192);
                try
                {
                    while (true)
                    {
                        // 先尝试从缓冲区解码
                        if (_receiveBuffer.TryDecodeMessage(out var message))
                        {
                            return message;
                        }

                        // 可选：在读取前检查连接状态，避免不必要的异常
                        if (!_pipeStream.IsConnected)
                        {
                            State = ConnectionState.Disconnected;
                            return null;
                        }

                        // 使用 Task.WhenAny 来确保取消令牌能够及时响应
                        var readTask = _pipeStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                        var cancelTask = Task.Delay(Timeout.Infinite, cancellationToken);

                        var completedTask = await Task.WhenAny(readTask, cancelTask).ConfigureAwait(false);

                        if (completedTask == cancelTask)
                        {
                            // 取消令牌被触发，这将引发 OperationCanceledException
                            await cancelTask;
                        }

                        int bytesRead = await readTask.ConfigureAwait(false);

                        if (bytesRead == 0)
                        {
                            _logger?.LogWarning("Connection closed by remote");
                            _receiveBuffer.Clear();
                            State = ConnectionState.Disconnected;
                            return null;
                        }

                        // 追加到缓冲区
                        _receiveBuffer.Append(buffer, 0, bytesRead);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            catch (OperationCanceledException)
            {
                // 取消不算故障，直接抛出让调用方处理
                throw;
            }
            catch (IOException ex)
            {
                _logger?.LogError(ex, "Pipe I/O error");
                State = ConnectionState.Faulted;
                throw new ConnectionException("Failed to receive message", ex);
            }
            catch (ObjectDisposedException ex)
            {
                _logger?.LogError(ex, "Pipe disposed during receive");
                State = ConnectionState.Faulted;
                throw new ConnectionException("Connection has been disposed", ex);
            }
            finally
            {
                _receiveLock.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            State = ConnectionState.Disconnected;

            _pipeStream?.Dispose();
            _sendLock?.Dispose();
            _receiveLock?.Dispose();
            _receiveBuffer?.Dispose();

            _logger?.LogDebug("Connection {ConnectionId} disposed", ConnectionId);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(NamedPipeConnection));
            }
        }

        /// <summary>
        /// 接收缓冲区（处理粘包问题）
        /// </summary>
        private class ReceiveBuffer : IDisposable
        {
            private readonly List<byte> _buffer = new List<byte>(16384);

            public void Append(byte[] data, int offset, int count)
            {
                for (int i = 0; i < count; i++)
                {
                    _buffer.Add(data[offset + i]);
                }
            }

            public void Clear() { _buffer.Clear(); }

            public bool TryDecodeMessage(out IMessage? message)
            {
                message = null;

                if (_buffer.Count == 0)
                {
                    return false;
                }

                var bufferArray = _buffer.ToArray();

                if (!MessageFrame.TryDecode(bufferArray, out var frameType, out var payload, out var consumed))
                {
                    return false;
                }

                // 反序列化消息
                var payloadBytes = payload.ToArray();

                message = frameType switch
                {
                    MessageFrame.FrameType.Request => MessageSerializer.Deserialize<RequestMessage>(payloadBytes),
                    MessageFrame.FrameType.Response => MessageSerializer.Deserialize<ResponseMessage>(payloadBytes),
                    MessageFrame.FrameType.Notification => MessageSerializer.Deserialize<NotificationMessage>(payloadBytes),
                    _ => throw new ProtocolException($"Unknown frame type: {frameType}")
                };

                // 移除已消费的数据
                _buffer.RemoveRange(0, consumed);

                return true;
            }

            public void Dispose() { _buffer.Clear(); }
        }
    }
}
