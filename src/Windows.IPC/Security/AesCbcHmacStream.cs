using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Windows.IPC.Security
{
    /// <summary>
    /// AES-CBC-HMAC 安全流
    /// 包装底层 PipeStream，提供透明的加密读写能力
    /// 写入时自动加密，读取时自动解密
    /// 
    /// 线上格式（每段数据）：
    /// +----------------+----------------+----------+--------+
    /// | EncLength 4B   | EncData NB     | ...      |        |
    /// +----------------+----------------+----------+--------+
    /// 
    /// 其中 EncData = AesCbcHmacEncryptionProvider.Encrypt(plaintext)
    /// 即 EncData = [IV 16B][Ciphertext NB][HMAC 32B]
    /// </summary>
    internal class AesCbcHmacStream : Stream
    {
        private readonly Stream _innerStream;
        private readonly AesCbcHmacEncryptionProvider _encryptionProvider;
        private readonly ILogger? _logger;
        private bool _disposed;

        /// <summary>
        /// 接收缓冲区 — 缓存从底层流读取但尚未解密的数据
        /// </summary>
        private readonly MemoryStream _receiveBuffer = new MemoryStream(8192);

        public AesCbcHmacStream(Stream innerStream, AesCbcHmacEncryptionProvider encryptionProvider, ILogger? logger = null)
        {
            _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
            _encryptionProvider = encryptionProvider ?? throw new ArgumentNullException(nameof(encryptionProvider));
            _logger = logger;
        }

        public override bool CanRead => _innerStream.CanRead && !_disposed;
        public override bool CanWrite => _innerStream.CanWrite && !_disposed;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <summary>
        /// 写入（加密）数据
        /// 自动将明文加密后写入底层流
        /// 线上格式：[加密数据长度 4B][加密数据 NB]
        /// </summary>
        public override void Write(byte[] buffer, int offset, int count)
        {
            ThrowIfDisposed();

            // 复制需要加密的数据片段（避免在异步上下文中使用 Span）
            var plaintext = new byte[count];
            Buffer.BlockCopy(buffer, offset, plaintext, 0, count);
            var encrypted = _encryptionProvider.Encrypt(plaintext);

            // 写入长度前缀 + 加密数据
            var lengthPrefix = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(lengthPrefix, encrypted.Length);

            _innerStream.Write(lengthPrefix, 0, 4);
            _innerStream.Write(encrypted, 0, encrypted.Length);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();

            // 复制需要加密的数据片段（ReadOnlySpan 不能在 async 方法中使用）
            var plaintext = new byte[count];
            Buffer.BlockCopy(buffer, offset, plaintext, 0, count);
            var encrypted = _encryptionProvider.Encrypt(plaintext);

            var lengthPrefix = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(lengthPrefix, encrypted.Length);

            await _innerStream.WriteAsync(lengthPrefix, 0, 4, cancellationToken).ConfigureAwait(false);
            await _innerStream.WriteAsync(encrypted, 0, encrypted.Length, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 读取（解密）数据
        /// 从底层流读取加密帧，解密后返回明文
        /// </summary>
        public override int Read(byte[] buffer, int offset, int count)
        {
            ThrowIfDisposed();

            // 尝试从缓冲区读取已解密的数据
            if (_receiveBuffer.Length > 0 && _receiveBuffer.Position < _receiveBuffer.Length)
            {
                return ReadFromBuffer(buffer, offset, count);
            }

            // 读取新帧
            var plaintext = ReadAndDecryptFrameSync();
            if (plaintext == null || plaintext.Length == 0)
            {
                return 0;
            }

            // 存入缓冲区
            _receiveBuffer.SetLength(0);
            _receiveBuffer.Position = 0;
            _receiveBuffer.Write(plaintext, 0, plaintext.Length);
            _receiveBuffer.Position = 0;

            return ReadFromBuffer(buffer, offset, count);
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();

            // 尝试从缓冲区读取已解密的数据
            if (_receiveBuffer.Length > 0 && _receiveBuffer.Position < _receiveBuffer.Length)
            {
                return ReadFromBuffer(buffer, offset, count);
            }

            // 读取新帧
            var plaintext = await ReadAndDecryptFrameAsync(cancellationToken).ConfigureAwait(false);
            if (plaintext == null || plaintext.Length == 0)
            {
                return 0;
            }

            // 存入缓冲区
            _receiveBuffer.SetLength(0);
            _receiveBuffer.Position = 0;
            _receiveBuffer.Write(plaintext, 0, plaintext.Length);
            _receiveBuffer.Position = 0;

            return ReadFromBuffer(buffer, offset, count);
        }

        private int ReadFromBuffer(byte[] buffer, int offset, int count)
        {
            int available = (int)(_receiveBuffer.Length - _receiveBuffer.Position);
            int toRead = Math.Min(available, count);
            if (toRead <= 0) return 0;

            _receiveBuffer.Read(buffer, offset, toRead);
            return toRead;
        }

        private byte[]? ReadAndDecryptFrameSync()
        {
            // 读取长度前缀
            var lengthBuffer = new byte[4];
            int totalRead = 0;
            while (totalRead < 4)
            {
                int bytesRead = _innerStream.Read(lengthBuffer, totalRead, 4 - totalRead);
                if (bytesRead == 0) return null;
                totalRead += bytesRead;
            }

            var encryptedLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(lengthBuffer);
            if (encryptedLength <= 0 || encryptedLength > 10 * 1024 * 1024 + 64)
            {
                throw new Exceptions.SecurityException($"Invalid encrypted frame length: {encryptedLength}");
            }

            // 读取加密数据
            var encryptedData = new byte[encryptedLength];
            totalRead = 0;
            while (totalRead < encryptedLength)
            {
                int bytesRead = _innerStream.Read(encryptedData, totalRead, encryptedLength - totalRead);
                if (bytesRead == 0)
                {
                    throw new EndOfStreamException("Unexpected end of stream while reading encrypted frame");
                }
                totalRead += bytesRead;
            }

            // 解密
            return _encryptionProvider.Decrypt(encryptedData);
        }

        private async Task<byte[]?> ReadAndDecryptFrameAsync(CancellationToken cancellationToken)
        {
            // 读取长度前缀
            var lengthBuffer = new byte[4];
            await ReadExactAsync(lengthBuffer, 0, 4, cancellationToken).ConfigureAwait(false);

            var encryptedLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(lengthBuffer);
            if (encryptedLength <= 0 || encryptedLength > 10 * 1024 * 1024 + 64)
            {
                throw new Exceptions.SecurityException($"Invalid encrypted frame length: {encryptedLength}");
            }

            // 读取加密数据
            var encryptedData = new byte[encryptedLength];
            await ReadExactAsync(encryptedData, 0, encryptedLength, cancellationToken).ConfigureAwait(false);

            // 解密
            return _encryptionProvider.Decrypt(encryptedData);
        }

        private async Task ReadExactAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int bytesRead = await _innerStream.ReadAsync(buffer, offset + totalRead, count - totalRead, cancellationToken)
                    .ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    throw new EndOfStreamException($"Unexpected end of stream. Expected {count} bytes, got {totalRead}.");
                }
                totalRead += bytesRead;
            }
        }

        public override void Flush()
        {
            _innerStream.Flush();
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await _innerStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;

            if (disposing)
            {
                _receiveBuffer?.Dispose();
                _encryptionProvider?.Dispose();
                // 注意：不释放 _innerStream，它由外部管理
            }

            base.Dispose(disposing);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(AesCbcHmacStream));
            }
        }
    }
}
