using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.IO;
using System.IO.Pipes;
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
        /// 使用 MemoryStream 替代 List<byte> 以获得更好的批量写入性能
        /// </summary>
        private class ReceiveBuffer : IDisposable
        {
            private readonly MemoryStream _stream = new MemoryStream(16384);

            public void Append(byte[] data, int offset, int count)
            {
                // MemoryStream.Write 在内部使用 Buffer.BlockCopy 批量拷贝，
                // 远比 List<byte>.Add 的逐字节追加高效
                _stream.Write(data, offset, count);
            }

            public void Clear() { _stream.SetLength(0); }

            public bool TryDecodeMessage(out IMessage? message)
            {
                message = null;

                if (_stream.Length == 0)
                {
                    return false;
                }

                // 仅在缓冲区有足够数据时才获取数组，避免不必要的拷贝
                var bufferArray = _stream.ToArray();

                if (!MessageFrame.TryDecode(bufferArray, out var frameType, out var payload, out var consumed))
                {
                    return false;
                }

                // 反序列化消息
                // payload 已经是独立的 byte[]（由 TryDecode 中的 .ToArray() 创建），
                // 无需再次拷贝

                message = frameType switch
                {
                    MessageFrame.FrameType.Request => MessageSerializer.Deserialize<RequestMessage>(payload),
                    MessageFrame.FrameType.Response => MessageSerializer.Deserialize<ResponseMessage>(payload),
                    MessageFrame.FrameType.Notification => MessageSerializer.Deserialize<NotificationMessage>(payload),
                    _ => throw new ProtocolException($"Unknown frame type: {frameType}")
                };

                // 移除已消费的数据
                if (consumed >= bufferArray.Length)
                {
                    // 全部消费完，重置流
                    _stream.SetLength(0);
                }
                else
                {
                    // 部分消费：保留未消费的数据
                    var remaining = bufferArray.Length - consumed;
                    _stream.SetLength(0);
                    _stream.Write(bufferArray, consumed, remaining);
                }

                return true;
            }

            public void Dispose() { _stream.Dispose(); }
        }
    }
}
