using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Channels;
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
    /// 
    /// 背压机制（基于 System.Threading.Channels）：
    /// 
    /// 发送端背压：
    ///   SendAsync → 有界 Channel&lt;SendItem&gt; → 发送循环 → 写入管道
    ///   当 Channel 满时，SendAsync 自然阻塞（Wait 模式），实现天然背压。
    ///   上层调用者感受到延迟增加而非崩溃，实现优雅降级。
    /// 
    /// 接收端背压：
    ///   管道读取循环 → 有界 Channel&lt;IMessage&gt; → ReceiveAsync 取出
    ///   当 Channel 满时，读取循环在写入 Channel 时阻塞，
    ///   暂停从管道读取，利用传输层背压将压力传导给发送端。
    ///   这种端到端的背压传导确保系统在过载时优雅降级。
    /// </summary>
    public class NamedPipeConnection : IConnection
    {
        private readonly Stream _stream;              // 可能是 PipeStream 或加密流
        private readonly PipeStream? _pipeStream;     // 原始管道流（用于 IsConnected 检查）
        private readonly ILogger? _logger;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);  // 帧序列化锁
        private readonly ReceiveBuffer _receiveBuffer;
        private readonly FrameSecurity? _frameSecurity; // 帧安全处理器（v2 帧格式）
        private readonly bool _useV2FrameFormat;         // 是否使用 v2 帧格式
        private readonly int _maxMessageSize;
        private readonly int _maxReceiveBufferSize;
        private ConnectionState _state = ConnectionState.Connected;
        private bool _disposed;
        private readonly object _stateLock = new object();

        // === 背压相关 ===
        private readonly int _maxSendQueueSize;       // 发送队列容量
        private readonly int _maxReceiveQueueSize;    // 接收队列容量

        // 发送端背压：Channel + 发送循环
        private Channel<SendItem>? _sendChannel;
        private Task? _sendLoopTask;
        private CancellationTokenSource? _sendLoopCts;
        private int _sendQueueCount;  // 用于监控的队列深度

        // 接收端背压：Channel + 读取循环
        private Channel<IMessage>? _receiveChannel;
        private Task? _receiveLoopTask;
        private CancellationTokenSource? _receiveLoopCts;
        private int _receiveQueueCount;  // 用于监控的队列深度

        /// <summary>
        /// 发送队列当前深度（监控用）
        /// </summary>
        public int SendQueueCount => Volatile.Read(ref _sendQueueCount);

        /// <summary>
        /// 接收队列当前深度（监控用）
        /// </summary>
        public int ReceiveQueueCount => Volatile.Read(ref _receiveQueueCount);

        /// <inheritdoc />
        public string ConnectionId { get; }

        /// <inheritdoc />
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

        /// <inheritdoc />
        public bool IsConnected
        {
            get
            {
                var pipeConnected = _pipeStream?.IsConnected ?? (State == ConnectionState.Connected);
                return pipeConnected && State == ConnectionState.Connected;
            }
        }

        /// <inheritdoc />
        public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

        /// <summary>
        /// 创建命名管道连接（向后兼容，无背压机制）
        /// </summary>
        public NamedPipeConnection(PipeStream pipeStream, ILogger? logger = null)
            : this(pipeStream, pipeStream, null, false, logger)
        {
        }

        /// <summary>
        /// 创建命名管道连接（支持安全特性和背压机制）
        /// </summary>
        /// <param name="stream">读写流（可能是加密流包装）</param>
        /// <param name="pipeStream">原始管道流（用于 IsConnected 检查，可为 null）</param>
        /// <param name="frameSecurity">帧安全处理器（null 表示 v1 帧格式）</param>
        /// <param name="useV2FrameFormat">是否使用 v2 帧格式</param>
        /// <param name="logger">日志</param>
        /// <param name="maxMessageSize">最大消息大小（字节），默认 10MB</param>
        /// <param name="maxReceiveBufferSize">接收缓冲区最大容量（字节），默认 16MB</param>
        /// <param name="maxSendQueueSize">发送队列最大容量（背压阈值），默认 1000，设为 0 禁用背压</param>
        /// <param name="maxReceiveQueueSize">接收队列最大容量（背压阈值），默认 256，设为 0 禁用背压</param>
        public NamedPipeConnection(
            Stream stream,
            PipeStream? pipeStream,
            FrameSecurity? frameSecurity,
            bool useV2FrameFormat,
            ILogger? logger = null,
            int maxMessageSize = 10 * 1024 * 1024,
            int maxReceiveBufferSize = 16 * 1024 * 1024,
            int maxSendQueueSize = 1000,
            int maxReceiveQueueSize = 256)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _pipeStream = pipeStream;
            _logger = logger;
            _frameSecurity = frameSecurity;
            _useV2FrameFormat = useV2FrameFormat;
            _maxMessageSize = maxMessageSize;
            _maxReceiveBufferSize = maxReceiveBufferSize;
            _maxSendQueueSize = maxSendQueueSize;
            _maxReceiveQueueSize = maxReceiveQueueSize;
            _receiveBuffer = new ReceiveBuffer(frameSecurity, _maxMessageSize, _maxReceiveBufferSize);
            ConnectionId = Guid.NewGuid().ToString("N");

            // 启动背压 Channel 和工作循环
            StartChannels();

            _logger?.LogDebug(
                "Connection {ConnectionId} created (V2Frame={UseV2}, FrameSecurity={HasFrameSec}, " +
                "SendQueue={SendQueue}, ReceiveQueue={RecvQueue})",
                ConnectionId, _useV2FrameFormat, _frameSecurity != null,
                _maxSendQueueSize, _maxReceiveQueueSize);
        }

        /// <summary>
        /// 启动 Channel 和工作循环
        /// </summary>
        private void StartChannels()
        {
            // 发送端 Channel
            if (_maxSendQueueSize > 0)
            {
                _sendChannel = Channel.CreateBounded<SendItem>(
                    new BoundedChannelOptions(_maxSendQueueSize)
                    {
                        FullMode = BoundedChannelFullMode.Wait,
                        SingleWriter = false,   // 多个调用者可能并发 SendAsync
                        SingleReader = true     // 只有一个发送循环在消费
                    });
            }
            else
            {
                // 无界 Channel（不推荐，但保持向后兼容）
                _sendChannel = Channel.CreateUnbounded<SendItem>(
                    new UnboundedChannelOptions
                    {
                        SingleWriter = false,
                        SingleReader = true
                    });
            }

            // 接收端 Channel
            if (_maxReceiveQueueSize > 0)
            {
                _receiveChannel = Channel.CreateBounded<IMessage>(
                    new BoundedChannelOptions(_maxReceiveQueueSize)
                    {
                        FullMode = BoundedChannelFullMode.Wait,  // 背压核心：满时写入阻塞
                        SingleWriter = true,    // 只有一个读取循环在写入
                        SingleReader = true     // 通常只有一个消费者
                    });
            }
            else
            {
                _receiveChannel = Channel.CreateUnbounded<IMessage>(
                    new UnboundedChannelOptions
                    {
                        SingleWriter = true,
                        SingleReader = true
                    });
            }

            // 启动发送循环
            _sendLoopCts = new CancellationTokenSource();
            _sendLoopTask = SendLoopAsync(_sendLoopCts.Token);

            // 启动接收循环（从管道读取并写入 Channel）
            _receiveLoopCts = new CancellationTokenSource();
            _receiveLoopTask = ReceiveLoopInternalAsync(_receiveLoopCts.Token);
        }

        /// <summary>
        /// 发送消息（背压版）
        /// 
        /// 不再直接写入管道，而是写入有界 Channel。
        /// 当 Channel 满时，此方法自然阻塞（BoundedChannelFullMode.Wait），
        /// 调用者感受到延迟增加而非异常，实现天然背压。
        /// 
        /// 背压传导链：
        /// 调用者 → SendAsync（阻塞等待 Channel 空位）→ 发送循环 → 管道写入
        /// 当管道写入慢（如网络拥塞），Channel 积满，SendAsync 开始阻塞，
        /// 压力传导给调用者，调用者自然会减慢发送速率。
        /// </summary>
        public async Task SendAsync(IMessage message, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            if (!IsConnected)
            {
                throw new ConnectionException("Connection is not established");
            }

            // 序列化消息 + 编码帧（在入队前完成，避免发送循环中重复序列化竞争）
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                byte[] frameBytes = EncodeMessage(message);

                var sendItem = new SendItem
                {
                    FrameBytes = frameBytes,
                    MessageId = message.MessageId,
                    FrameType = GetFrameType(message)
                };

                // 写入 Channel（背压点：Channel 满时 WriteAsync 阻塞）
                await _sendChannel!.Writer.WriteAsync(sendItem, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _sendQueueCount);

                _logger?.LogTrace(
                    "Enqueued {FrameType} message {MessageId} for sending (queue depth: {Depth})",
                    sendItem.FrameType, sendItem.MessageId, Volatile.Read(ref _sendQueueCount));
            }
            catch (ChannelClosedException ex)
            {
                throw new ConnectionException("Send channel is closed (connection may be faulted)", ex);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>
        /// 接收消息（背压版）
        /// 
        /// 从接收 Channel 中取出已解码的消息。
        /// Channel 由独立的读取循环填充，当 Channel 满时，
        /// 读取循环在写入 Channel 时阻塞，暂停从管道读取，
        /// 利用传输层背压将压力传导给发送端。
        /// </summary>
        public async Task<IMessage?> ReceiveAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            try
            {
                // 从 Channel 读取已解码的消息
                if (await _receiveChannel!.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (_receiveChannel.Reader.TryRead(out var message))
                    {
                        Interlocked.Decrement(ref _receiveQueueCount);
                        return message;
                    }
                }

                // Channel 已完成且无更多数据
                return null;
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        #region Send Loop

        /// <summary>
        /// 发送循环：从 Channel 读取帧数据，写入管道
        /// 
        /// 这是唯一的管道写入者，消除了多线程并发写入的竞争。
        /// 发送速度由管道实际吞吐能力决定，Channel 中的积压量
        /// 反映了发送端背压的强度。
        /// </summary>
        private async Task SendLoopAsync(CancellationToken cancellationToken)
        {
            _logger?.LogDebug("Send loop started for connection {ConnectionId}", ConnectionId);

            try
            {
                var reader = _sendChannel!.Reader;

                while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var item))
                    {
                        Interlocked.Decrement(ref _sendQueueCount);

                        try
                        {
                            // 写入管道
                            if (_pipeStream != null && ReferenceEquals(_stream, _pipeStream))
                            {
                                await _pipeStream.WriteAsync(item.FrameBytes, 0, item.FrameBytes.Length, cancellationToken)
                                    .ConfigureAwait(false);
                            }
                            else
                            {
                                await _stream.WriteAsync(item.FrameBytes, 0, item.FrameBytes.Length, cancellationToken)
                                    .ConfigureAwait(false);
                                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                            }

                            _logger?.LogTrace(
                                "Sent {FrameType} message {MessageId}, {Size} bytes (remaining queue: {Depth})",
                                item.FrameType, item.MessageId, item.FrameBytes.Length,
                                Volatile.Read(ref _sendQueueCount));
                        }
                        catch (IOException ex)
                        {
                            _logger?.LogError(ex, "Failed to write message {MessageId} to pipe", item.MessageId);
                            State = ConnectionState.Faulted;
                            _sendChannel.Writer.TryComplete(ex);
                            return;
                        }
                        catch (ObjectDisposedException ex)
                        {
                            _logger?.LogError(ex, "Pipe disposed during send of message {MessageId}", item.MessageId);
                            State = ConnectionState.Faulted;
                            _sendChannel.Writer.TryComplete(ex);
                            return;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger?.LogDebug("Send loop cancelled for connection {ConnectionId}", ConnectionId);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Send loop error for connection {ConnectionId}", ConnectionId);
                State = ConnectionState.Faulted;
                _sendChannel?.Writer.TryComplete(ex);
            }

            _logger?.LogDebug("Send loop stopped for connection {ConnectionId}", ConnectionId);
        }

        #endregion

        #region Receive Loop (Internal - reads from pipe, writes to Channel)

        /// <summary>
        /// 接收循环（内部）：从管道读取原始数据，解码消息，写入接收 Channel
        /// 
        /// 背压传导：
        /// 当接收 Channel 满时（_maxReceiveQueueSize 达到上限），
        /// WriteAsync 会阻塞 → 暂停从管道读取 → 命名管道的内部缓冲区填满 →
        /// 发送端的 WriteAsync 阻塞 → 发送端的 SendAsync（通过发送 Channel 间接）阻塞 →
        /// 发送端应用感受到延迟增加，自然降速。
        /// 
        /// 这是端到端背压传导的关键环节。
        /// </summary>
        private async Task ReceiveLoopInternalAsync(CancellationToken cancellationToken)
        {
            _logger?.LogDebug("Receive loop started for connection {ConnectionId}", ConnectionId);

            var buffer = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                while (!cancellationToken.IsCancellationRequested && IsConnected)
                {
                    int bytesRead;
                    try
                    {
                        bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (IOException ex)
                    {
                        _logger?.LogError(ex, "Pipe I/O error in receive loop");
                        State = ConnectionState.Faulted;
                        break;
                    }
                    catch (ObjectDisposedException ex)
                    {
                        _logger?.LogError(ex, "Pipe disposed during receive");
                        State = ConnectionState.Faulted;
                        break;
                    }

                    if (bytesRead == 0)
                    {
                        _logger?.LogWarning("Connection closed by remote");
                        _receiveBuffer.Clear();
                        State = ConnectionState.Disconnected;
                        break;
                    }

                    // 追加到帧解析缓冲区
                    _receiveBuffer.Append(buffer, 0, bytesRead);

                    // 尝试从缓冲区解码所有完整消息
                    while (_receiveBuffer.TryDecodeMessage(out var message))
                    {
                        if (message != null)
                        {
                            // 写入接收 Channel（背压点：Channel 满时 WriteAsync 阻塞）
                            try
                            {
                                await _receiveChannel!.Writer.WriteAsync(message, cancellationToken)
                                    .ConfigureAwait(false);
                                Interlocked.Increment(ref _receiveQueueCount);
                            }
                            catch (ChannelClosedException)
                            {
                                // Channel 已关闭，停止循环
                                return;
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger?.LogDebug("Receive loop cancelled for connection {ConnectionId}", ConnectionId);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Receive loop error for connection {ConnectionId}", ConnectionId);
                State = ConnectionState.Faulted;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                // 标记 Channel 完成，不再有新消息
                _receiveChannel?.Writer.TryComplete();
            }

            _logger?.LogDebug("Receive loop stopped for connection {ConnectionId}", ConnectionId);
        }

        #endregion

        #region Message Encoding Helpers

        /// <summary>
        /// 编码消息为帧字节
        /// </summary>
        private byte[] EncodeMessage(IMessage message)
        {
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

            // 检查消息大小
            if (messageBytes.Length > _maxMessageSize)
            {
                throw new ArgumentException(
                    $"Message size {messageBytes.Length} bytes exceeds maximum allowed {_maxMessageSize} bytes. " +
                    "Reduce message size or increase MaxMessageSize in IpcPipeOptions.");
            }

            // 编码帧
            if (_useV2FrameFormat && _frameSecurity != null)
            {
                return _frameSecurity.EncodeSecureFrame(frameType, messageBytes);
            }
            else
            {
                return MessageFrame.Encode(frameType, messageBytes);
            }
        }

        /// <summary>
        /// 获取消息对应的帧类型
        /// </summary>
        private static MessageFrame.FrameType GetFrameType(IMessage message)
        {
            return message switch
            {
                RequestMessage _ => MessageFrame.FrameType.Request,
                ResponseMessage _ => MessageFrame.FrameType.Response,
                NotificationMessage _ => MessageFrame.FrameType.Notification,
                _ => MessageFrame.FrameType.Request
            };
        }

        #endregion

        /// <summary>停止连接工作循环并释放相关资源。</summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            State = ConnectionState.Disconnected;

            // 完成 Channel 写入端（通知消费者不再有新数据）
            _sendChannel?.Writer.TryComplete();
            _receiveChannel?.Writer.TryComplete();

            // 取消工作循环
            _sendLoopCts?.Cancel();
            _receiveLoopCts?.Cancel();

            // 等待工作循环完成（限时）
            try
            {
                if (_sendLoopTask != null)
                {
                    Task.Run(() => _sendLoopTask).Wait(TimeSpan.FromSeconds(2));
                }
            }
            catch { /* 释放期间不抛异常 */ }

            try
            {
                if (_receiveLoopTask != null)
                {
                    Task.Run(() => _receiveLoopTask).Wait(TimeSpan.FromSeconds(2));
                }
            }
            catch { /* 释放期间不抛异常 */ }

            // 释放资源
            _frameSecurity?.Dispose();
            _sendLock?.Dispose();
            _receiveBuffer?.Dispose();
            _sendLoopCts?.Dispose();
            _receiveLoopCts?.Dispose();

            // 注意：不释放 _pipeStream 和 _stream，它们由外部管理（IpcServer/IpcClient）

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
        /// 发送队列项
        /// </summary>
        private struct SendItem
        {
            public byte[] FrameBytes;
            public string MessageId;
            public MessageFrame.FrameType FrameType;
        }

        /// <summary>
        /// 接收缓冲区（处理粘包问题）
        /// 支持 v1 和 v2 帧格式的自动检测和解码
        /// </summary>
        private class ReceiveBuffer : IDisposable
        {
            private readonly MemoryStream _buffer = new MemoryStream(16384);
            private readonly FrameSecurity? _frameSecurity;
            private readonly int _maxMessageSize;
            private readonly int _maxReceiveBufferSize;

            public ReceiveBuffer(FrameSecurity? frameSecurity, int maxMessageSize, int maxReceiveBufferSize)
            {
                _frameSecurity = frameSecurity;
                _maxMessageSize = maxMessageSize;
                _maxReceiveBufferSize = maxReceiveBufferSize;
            }

            public void Append(byte[] data, int offset, int count)
            {
                // 检查缓冲区溢出
                if (_buffer.Length + count > _maxReceiveBufferSize)
                {
                    throw new ProtocolException(
                        $"Receive buffer overflow: {_buffer.Length + count} bytes exceeds maximum {_maxReceiveBufferSize} bytes. " +
                        "Possible denial-of-service attack or malformed data.");
                }
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

                // 提前检查声明的消息大小，防止缓冲区无限增长
                // v1/v2: header at offset 4 contains payload length (4 bytes big-endian)
                if (bufferArray.Length >= 8)
                {
                    var declaredLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bufferArray.AsSpan(4, 4));
                    if (declaredLength > _maxMessageSize)
                    {
                        throw new ProtocolException(
                            $"Declared message size {declaredLength} exceeds maximum allowed {_maxMessageSize} bytes. " +
                            "Connection will be closed to prevent denial-of-service attack.");
                    }
                }

                // 检测帧格式版本（v1 或 v2）
                bool isV2 = bufferArray.Length >= 4 && FrameSecurity.IsV2Frame(bufferArray);

                int consumed;
                if (isV2 && _frameSecurity != null)
                {
                    // v2 帧格式：带序列号和 HMAC
                    if (!_frameSecurity.TryDecodeSecureFrame(bufferArray, out var frameType, out var payload, out var sequenceId, out consumed, _maxMessageSize))
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
                    if (!MessageFrame.TryDecode(bufferArray, out var frameType, out var payload, out consumed, _maxMessageSize))
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
