using MessagePack;
using System;
using Windows.IPC.Abstractions;
using Windows.IPC.Serialization;
using Windows.IPC.UWP.Internal;
using Xunit;

namespace Windows.IPC.Tests
{
    public class SerializationTests
    {
        [Fact]
        public void ByteArrayWrapper_SerializeDeserialize_Success()
        {
            var original = new ByteArrayWrapper { Data = new byte[] { 1, 2, 3, 4 } };
            var bytes = MessageSerializer.Serialize(original);
            var deserialized = MessageSerializer.Deserialize<ByteArrayWrapper>(bytes);

            Assert.Equal(original.Data, deserialized.Data);
        }

        [Fact]
        public void RequestMessage_SerializeDeserialize_Success()
        {
            // Arrange
            var original = new RequestMessage
            {
                MessageId = "test-123",
                MessageType = "TestRequest",
                Payload = new byte[] { 1, 2, 3, 4 },
                TimeoutMs = 5000
            };

            // Act
            var bytes = MessageSerializer.Serialize(original);
            var deserialized = MessageSerializer.Deserialize<RequestMessage>(bytes);

            // Assert
            Assert.Equal(original.MessageId, deserialized.MessageId);
            Assert.Equal(original.MessageType, deserialized.MessageType);
            Assert.Equal(original.Payload, deserialized.Payload);
            Assert.Equal(original.TimeoutMs, deserialized.TimeoutMs);
        }

        [Fact]
        public void ResponseMessage_SerializeDeserialize_Success()
        {
            // Arrange
            var original = new ResponseMessage
            {
                MessageId = "test-456",
                MessageType = "TestResponse",
                IsSuccess = true,
                Payload = new byte[] { 5, 6, 7, 8 }
            };

            // Act
            var bytes = MessageSerializer.Serialize(original);
            var deserialized = MessageSerializer.Deserialize<ResponseMessage>(bytes);

            // Assert
            Assert.Equal(original.MessageId, deserialized.MessageId);
            Assert.Equal(original.MessageType, deserialized.MessageType);
            Assert.Equal(original.IsSuccess, deserialized.IsSuccess);
            Assert.Equal(original.Payload, deserialized.Payload);
        }

        [Fact]
        public void ResponseMessage_WithError_SerializeDeserialize_Success()
        {
            // Arrange
            var original = new ResponseMessage
            {
                MessageId = "test-789",
                MessageType = "TestResponse",
                IsSuccess = false,
                ErrorCode = "ERROR_001",
                ErrorMessage = "Something went wrong"
            };

            // Act
            var bytes = MessageSerializer.Serialize(original);
            var deserialized = MessageSerializer.Deserialize<ResponseMessage>(bytes);

            // Assert
            Assert.Equal(original.MessageId, deserialized.MessageId);
            Assert.Equal(original.IsSuccess, deserialized.IsSuccess);
            Assert.Equal(original.ErrorCode, deserialized.ErrorCode);
            Assert.Equal(original.ErrorMessage, deserialized.ErrorMessage);
        }
    }
}
