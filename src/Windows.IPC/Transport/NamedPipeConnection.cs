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
using Windows.IPC.Security;
using Windows.IPC.Serialization;

namespace Windows.IPC.Transport
{
    /// <summary>
    /// 命名管道连接
    /// 支持安全特性：帧完整性校验（HMAC）、重放防护（序列号）、
    /// 以及通过流包装实现的传输加密（NegotiateStream / AES-CBC-HMAC）
    /// </summary>
    public class NamedPipeConnection : IConnection
    {
        private readonly Stream _stream;              // 可能是 PipeStream 或加密流
        private readonly PipeStream? _pipeStream;     // 原始管道流（用于 IsConnected 检查）
        private readonly ILogger? _logger;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _receiveLock = new SemaphoreSlim(1, 1);
        private readonly ReceiveBuffer _receiveBuffer;
        private readonly FrameSecurity? _frameSecurity; // 帧安全处理器（v2 帧格式）
        private readonly bool _useV2FrameFormat;         // 是否使用 v2 帧格式
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

        public bool IsConnected
        {
            get
            {
                var pipeConnected = _pipeStream?.IsConnected ?? (State == ConnectionState.Connected);
                return pipeConnected && State == ConnectionState.Connected;
            }
        }

        public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

        /// <summary>
        /// 创建命名管道连接（向后兼容，无安全特性）
        /// </summary>
        public NamedPipeConnection(PipeStream pipeStream, ILogger? logger = null)
            : this(pipeStream, pipeStream, null, false, logger)
        {
        }

        /// <summary>
        /// 创建命名管道连接（支持安全特性）
        /// </summary>
        /// <param name="stream">读写流（可能是加密流包装）</param>
        /// <param name="pipeStream">原始管道流（用于 IsConnected 检查，可为 null）</param>
        /// <param name="frameSecurity">帧安全处理器（null 表示 v1 帧格式）</param>
        /// <param name="useV2FrameFormat">是否使用 v2 帧格式</param>
        /// <param name="logger">日志</param>
        public NamedPipeConnection(
            Stream stream,
            PipeStream? pipeStream,
            FrameSecurity? frameSecurity,
            bool useV2FrameFormat,
            ILogger? logger = null)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _pipeStream = pipeStream;
            _logger = logger;
            _frameSecurity = frameSecurity;
            _useV2FrameFormat = useV2FrameFormat;
            _receiveBuffer = new ReceiveBuffer(frameSecurity);
            ConnectionId = Guid.NewGuid().ToString("N");

            _logger?.LogDebug(
                "Connection {ConnectionId} created (V2Frame={UseV2}, FrameSecurity={HasFrameSec})",
                ConnectionId, _useV2FrameFormat, _frameSecurity != null);
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

                // 编码帧（根据协议版本选择帧格式）
                byte[] frameBytes;
                if (_useV2FrameFormat && _frameSecurity != null)
                {
                    frameBytes = _frameSecurity.EncodeSecureFrame(frameType, messageBytes);
                }
                else
                {
                    frameBytes = MessageFrame.Encode(frameType, messageBytes);
                }

                // 发送
                // 当使用加密流时，需要 Flush 以确保加密帧完整发送
                if (_pipeStream != null && ReferenceEquals(_stream, _pipeStream))
                {
                    await _pipeStream.WriteAsync(frameBytes, 0, frameBytes.Length, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _stream.WriteAsync(frameBytes, 0, frameBytes.Length, cancellationToken).ConfigureAwait(false);
                    await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                _logger?.LogTrace(
                    "Sent {FrameType} message {MessageId}, {Size} bytes",
                    frameType, message.MessageId, frameBytes.Length);
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

                        // 在读取前检查连接状态
                        if (_pipeStream != null && !_pipeStream.IsConnected)
                        {
                            State = ConnectionState.Disconnected;
                            return null;
                        }

                        int bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);

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

            // 注意：不释放 _pipeStream 和 _stream，它们由外部管理（IpcServer/IpcClient）
            _frameSecurity?.Dispose();
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
        /// 支持 v1 和 v2 帧格式的自动检测和解码
        /// </summary>
        private class ReceiveBuffer : IDisposable
        {
            private readonly MemoryStream _buffer = new MemoryStream(16384);
            private readonly FrameSecurity? _frameSecurity;

            public ReceiveBuffer(FrameSecurity? frameSecurity)
            {
                _frameSecurity = frameSecurity;
            }

            public void Append(byte[] data, int offset, int count)
            {
                _buffer.Write(data, offset, count);
            }

            public void Clear() { _buffer.SetLength(0); }

            public bool TryDecodeMessage(out IMessage? message)
            {
                message = null;

                if (_buffer.Length == 0)
                {
                    return false;
                }

                var bufferArray = _buffer.ToArray();

                // 检测帧格式版本（v1 或 v2）
                bool isV2 = bufferArray.Length >= 4 && FrameSecurity.IsV2Frame(bufferArray);

                int consumed;
                if (isV2 && _frameSecurity != null)
                {
                    // v2 帧格式：带序列号和 HMAC
                    if (!_frameSecurity.TryDecodeSecureFrame(bufferArray, out var frameType, out var payload, out var sequenceId, out consumed))
                    {
                        return false;
                    }

                    // 反序列化消息
                    message = frameType switch
                    {
                        MessageFrame.FrameType.Request => MessageSerializer.Deserialize<RequestMessage>(payload),
                        MessageFrame.FrameType.Response => MessageSerializer.Deserialize<ResponseMessage>(payload),
                        MessageFrame.FrameType.Notification => MessageSerializer.Deserialize<NotificationMessage>(payload),
                        _ => throw new ProtocolException($"Unknown frame type: {frameType}")
                    };
                }
                else
                {
                    // v1 帧格式：原始格式
                    if (!MessageFrame.TryDecode(bufferArray, out var frameType, out var payload, out consumed))
                    {
                        return false;
                    }

                    // 反序列化消息
                    message = frameType switch
                    {
                        MessageFrame.FrameType.Request => MessageSerializer.Deserialize<RequestMessage>(payload),
                        MessageFrame.FrameType.Response => MessageSerializer.Deserialize<ResponseMessage>(payload),
                        MessageFrame.FrameType.Notification => MessageSerializer.Deserialize<NotificationMessage>(payload),
                        _ => throw new ProtocolException($"Unknown frame type: {frameType}")
                    };
                }

                // 移除已消费的数据
                if (consumed >= bufferArray.Length)
                {
                    _buffer.SetLength(0);
                }
                else
                {
                    var remaining = bufferArray.Length - consumed;
                    _buffer.SetLength(0);
                    _buffer.Write(bufferArray, consumed, remaining);
                }

                return true;
            }

            public void Dispose() { _buffer.Dispose(); }
        }
    }
}
