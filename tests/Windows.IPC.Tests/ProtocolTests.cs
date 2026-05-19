using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.IPC.Abstractions;
using Windows.IPC.Protocol;
using Windows.IPC.Serialization;
using Xunit;

namespace Windows.IPC.Tests
{
    /// <summary>
    /// 协议帧编解码测试
    /// 覆盖 MessageFrame 的 Encode/TryDecode 以及未知帧类型处理
    /// </summary>
    public class ProtocolTests
    {
        #region Encode + TryDecode 往返测试

        [Fact]
        public void MessageFrame_EncodeDecode_Request_RoundTrip()
        {
            // Arrange
            var request = new RequestMessage
            {
                MessageId = "req-001",
                MessageType = "TestRequest",
                Payload = new byte[] { 0x01, 0x02, 0x03 },
                TimeoutMs = 5000
            };
            var payload = MessageSerializer.Serialize(request);

            // Act
            var frame = MessageFrame.Encode(MessageFrame.FrameType.Request, payload);
            var decoded = MessageFrame.TryDecode(frame, out var type, out var decodedPayload, out var consumed);

            // Assert
            Assert.True(decoded);
            Assert.Equal(MessageFrame.FrameType.Request, type);
            Assert.Equal(payload.Length, decodedPayload.Length);
            Assert.Equal(payload, decodedPayload.ToArray());
            Assert.Equal(frame.Length, consumed);
        }

        [Fact]
        public void MessageFrame_EncodeDecode_Response_RoundTrip()
        {
            // Arrange
            var response = new ResponseMessage
            {
                MessageId = "res-001",
                MessageType = "TestResponse",
                IsSuccess = true,
                Payload = new byte[] { 0xAA, 0xBB }
            };
            var payload = MessageSerializer.Serialize(response);

            // Act
            var frame = MessageFrame.Encode(MessageFrame.FrameType.Response, payload);
            var decoded = MessageFrame.TryDecode(frame, out var type, out var decodedPayload, out var consumed);

            // Assert
            Assert.True(decoded);
            Assert.Equal(MessageFrame.FrameType.Response, type);
            Assert.Equal(payload, decodedPayload.ToArray());
        }

        [Fact]
        public void MessageFrame_EncodeDecode_Notification_RoundTrip()
        {
            // Arrange
            var notification = new NotificationMessage
            {
                MessageId = "notif-001",
                MessageType = "TestNotification",
                Payload = new byte[] { 0xFF }
            };
            var payload = MessageSerializer.Serialize(notification);

            // Act
            var frame = MessageFrame.Encode(MessageFrame.FrameType.Notification, payload);
            var decoded = MessageFrame.TryDecode(frame, out var type, out var decodedPayload, out var consumed);

            // Assert
            Assert.True(decoded);
            Assert.Equal(MessageFrame.FrameType.Notification, type);
            Assert.Equal(payload, decodedPayload.ToArray());
        }

        [Fact]
        public void MessageFrame_EncodeDecode_EmptyPayload_RoundTrip()
        {
            // Arrange
            var emptyPayload = ReadOnlySpan<byte>.Empty;

            // Act
            var frame = MessageFrame.Encode(MessageFrame.FrameType.Request, emptyPayload);
            var decoded = MessageFrame.TryDecode(frame, out var type, out var decodedPayload, out var consumed);

            // Assert
            Assert.True(decoded);
            Assert.Equal(MessageFrame.FrameType.Request, type);
            Assert.Equal(0, decodedPayload.Length);
            Assert.Equal(10, consumed); // 头部 10 字节，无 payload
        }

        [Fact]
        public void MessageFrame_EncodeDecode_LargePayload_RoundTrip()
        {
            // Arrange — 1MB payload
            var largePayload = new byte[1024 * 1024];
            new Random(42).NextBytes(largePayload);

            // Act
            var frame = MessageFrame.Encode(MessageFrame.FrameType.Request, largePayload);
            var decoded = MessageFrame.TryDecode(frame, out var type, out var decodedPayload, out var consumed);

            // Assert
            Assert.True(decoded);
            Assert.Equal(MessageFrame.FrameType.Request, type);
            Assert.Equal(largePayload.Length, decodedPayload.Length);
            Assert.Equal(largePayload, decodedPayload.ToArray());
        }

        #endregion

        #region TryDecode 边界条件

        [Fact]
        public void MessageFrame_TryDecode_InsufficientData_ReturnsFalse()
        {
            // Arrange — 只有 5 字节，不足头部 10 字节
            var partialData = new byte[5] { 0x49, 0x50, 0x43, 0x58, 0x00 };

            // Act
            var result = MessageFrame.TryDecode(partialData, out var type, out var payload, out var consumed);

            // Assert
            Assert.False(result);
            Assert.Equal(default(MessageFrame.FrameType), type);
            Assert.Equal(default(ReadOnlyMemory<byte>), payload);
            Assert.Equal(0, consumed);
        }

        [Fact]
        public void MessageFrame_TryDecode_PartialPayload_ReturnsFalse()
        {
            // Arrange — 头部完整但 payload 不完整
            var request = new RequestMessage
            {
                MessageId = "partial-test",
                MessageType = "Test",
                Payload = new byte[] { 1, 2, 3, 4, 5 }
            };
            var fullPayload = MessageSerializer.Serialize(request);
            var fullFrame = MessageFrame.Encode(MessageFrame.FrameType.Request, fullPayload);

            // 只截取头部 + 一半 payload
            var partialFrame = fullFrame.AsSpan(0, 10 + fullPayload.Length / 2).ToArray();

            // Act
            var result = MessageFrame.TryDecode(partialFrame, out var type, out var payload, out var consumed);

            // Assert
            Assert.False(result);
        }

        [Fact]
        public void MessageFrame_TryDecode_TwoMessagesInBuffer_DecodesFirst()
        {
            // Arrange — 缓冲区中包含两条完整消息
            var payload1 = MessageSerializer.Serialize(new RequestMessage
            {
                MessageId = "msg-001",
                MessageType = "First",
                Payload = new byte[] { 1 }
            });
            var payload2 = MessageSerializer.Serialize(new ResponseMessage
            {
                MessageId = "msg-002",
                MessageType = "Second",
                IsSuccess = true,
                Payload = new byte[] { 2 }
            });

            var frame1 = MessageFrame.Encode(MessageFrame.FrameType.Request, payload1);
            var frame2 = MessageFrame.Encode(MessageFrame.FrameType.Response, payload2);

            var combined = new byte[frame1.Length + frame2.Length];
            Buffer.BlockCopy(frame1, 0, combined, 0, frame1.Length);
            Buffer.BlockCopy(frame2, 0, combined, frame1.Length, frame2.Length);

            // Act — 解码第一条
            var decoded1 = MessageFrame.TryDecode(combined, out var type1, out var decodedPayload1, out var consumed1);

            // Assert
            Assert.True(decoded1);
            Assert.Equal(MessageFrame.FrameType.Request, type1);
            Assert.Equal(frame1.Length, consumed1);

            // Act — 从 consumed1 位置继续解码第二条
            var remaining = combined.AsSpan(consumed1);
            var decoded2 = MessageFrame.TryDecode(remaining, out var type2, out var decodedPayload2, out var consumed2);

            Assert.True(decoded2);
            Assert.Equal(MessageFrame.FrameType.Response, type2);
            Assert.Equal(frame2.Length, consumed2);
        }

        #endregion

        #region 错误处理

        [Fact]
        public void MessageFrame_TryDecode_InvalidMagic_ThrowsInvalidDataException()
        {
            // Arrange — 错误的 Magic Number
            var buffer = new byte[10];
            buffer[0] = 0xDE; buffer[1] = 0xAD; buffer[2] = 0xBE; buffer[3] = 0xEF; // wrong magic
            buffer[4] = 0; buffer[5] = 0; buffer[6] = 0; buffer[7] = 0; // length = 0
            buffer[8] = 0; buffer[9] = 1; // type = Request

            // Act & Assert
            Assert.Throws<InvalidDataException>(() =>
                MessageFrame.TryDecode(buffer, out _, out _, out _));
        }

        [Fact]
        public void MessageFrame_TryDecode_NegativePayloadLength_ThrowsInvalidDataException()
        {
            // Arrange — 合法 Magic + 负的 payload 长度
            var buffer = new byte[10];
            buffer[0] = 0x49; buffer[1] = 0x50; buffer[2] = 0x43; buffer[3] = 0x58; // "IPCX"
            // Big-endian -1 = 0xFFFFFFFF
            buffer[4] = 0xFF; buffer[5] = 0xFF; buffer[6] = 0xFF; buffer[7] = 0xFF;
            buffer[8] = 0; buffer[9] = 1; // type = Request

            // Act & Assert
            Assert.Throws<InvalidDataException>(() =>
                MessageFrame.TryDecode(buffer, out _, out _, out _));
        }

        [Fact]
        public void MessageFrame_TryDecode_PayloadTooLarge_ThrowsInvalidDataException()
        {
            // Arrange — 合法 Magic + 超大 payload 长度 (>10MB)
            var buffer = new byte[10];
            buffer[0] = 0x49; buffer[1] = 0x50; buffer[2] = 0x43; buffer[3] = 0x58; // "IPCX"
            // Big-endian 11MB = 0x00B71B00
            buffer[4] = 0x00; buffer[5] = 0xB7; buffer[6] = 0x1B; buffer[7] = 0x00;
            buffer[8] = 0; buffer[9] = 1; // type = Request

            // Act & Assert
            Assert.Throws<InvalidDataException>(() =>
                MessageFrame.TryDecode(buffer, out _, out _, out _));
        }

        [Fact]
        public void MessageFrame_Encode_PayloadTooLarge_ThrowsArgumentException()
        {
            // Arrange — 超过 10MB 的 payload
            var oversizedPayload = new byte[10 * 1024 * 1024 + 1];

            // Act & Assert
            Assert.Throws<ArgumentException>(() =>
                MessageFrame.Encode(MessageFrame.FrameType.Request, oversizedPayload));
        }

        [Fact]
        public void MessageFrame_TryDecode_UnknownFrameType_ThrowsProtocolException()
        {
            // Arrange — 合法帧但帧类型为已移除的值 4（原 Heartbeat）
            var payload = new byte[] { 0x01 };
            var buffer = new byte[10 + payload.Length];

            // Magic
            buffer[0] = 0x49; buffer[1] = 0x50; buffer[2] = 0x43; buffer[3] = 0x58;
            // Length = 1 (big-endian)
            buffer[4] = 0x00; buffer[5] = 0x00; buffer[6] = 0x00; buffer[7] = 0x01;
            // Type = 4 (原 Heartbeat，已移除)
            buffer[8] = 0x00; buffer[9] = 0x04;
            // Payload
            buffer[10] = 0x01;

            // Act — TryDecode 本身只做帧解析，不会检查帧类型的合法性
            var decoded = MessageFrame.TryDecode(buffer, out var type, out var decodedPayload, out var consumed);

            // Assert — TryDecode 成功解码帧，但帧类型值不在当前枚举中
            Assert.True(decoded);
            // 强制转换后 type 值为 4，但不在 FrameType 枚举定义中
            Assert.Equal((MessageFrame.FrameType)4, type);
        }

        [Fact]
        public void MessageFrame_TryDecode_RemovedHandshakeFrameType_DecodedAsUnknown()
        {
            // Arrange — 合法帧但帧类型为已移除的值 5（原 Handshake）
            var payload = new byte[] { 0x02 };
            var buffer = new byte[10 + payload.Length];

            // Magic
            buffer[0] = 0x49; buffer[1] = 0x50; buffer[2] = 0x43; buffer[3] = 0x58;
            // Length = 1
            buffer[4] = 0x00; buffer[5] = 0x00; buffer[6] = 0x00; buffer[7] = 0x01;
            // Type = 5 (原 Handshake，已移除)
            buffer[8] = 0x00; buffer[9] = 0x05;
            // Payload
            buffer[10] = 0x02;

            // Act
            var decoded = MessageFrame.TryDecode(buffer, out var type, out var decodedPayload, out var consumed);

            // Assert
            Assert.True(decoded);
            Assert.Equal((MessageFrame.FrameType)5, type);
        }

        #endregion

        #region FrameType 枚举完整性

        [Fact]
        public void FrameType_Enum_ContainsOnlyValidValues()
        {
            // 验证 FrameType 枚举只包含 Request、Response、Notification
            var values = (IEnumerable<MessageFrame.FrameType>)Enum.GetValues(typeof(MessageFrame.FrameType));

            Assert.Equal(3, values.Count());
            Assert.Contains(MessageFrame.FrameType.Request, values);
            Assert.Contains(MessageFrame.FrameType.Response, values);
            Assert.Contains(MessageFrame.FrameType.Notification, values);
        }

        [Fact]
        public void FrameType_Enum_HasCorrectNumericValues()
        {
            Assert.Equal((ushort)1, (ushort)MessageFrame.FrameType.Request);
            Assert.Equal((ushort)2, (ushort)MessageFrame.FrameType.Response);
            Assert.Equal((ushort)3, (ushort)MessageFrame.FrameType.Notification);
        }

        #endregion
    }
}
