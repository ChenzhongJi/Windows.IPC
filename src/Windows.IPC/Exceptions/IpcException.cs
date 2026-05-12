using System;
using System.Collections.Generic;
using System.Text;

namespace Windows.IPC.Exceptions
{
    /// <summary>
    /// IPC 基础异常
    /// </summary>
    public class IpcException : Exception
    {
        public string ErrorCode { get; }

        public IpcException(string errorCode, string message)
            : base(message)
        {
            ErrorCode = errorCode;
        }

        public IpcException(string errorCode, string message, Exception innerException)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }
    }

    /// <summary>
    /// 连接异常
    /// </summary>
    public class ConnectionException : IpcException
    {
        public ConnectionException(string message)
            : base("CONNECTION_ERROR", message) { }

        public ConnectionException(string message, Exception innerException)
            : base("CONNECTION_ERROR", message, innerException) { }
    }

    /// <summary>
    /// 超时异常
    /// </summary>
    public class TimeoutException : IpcException
    {
        public TimeoutException(string message)
            : base("TIMEOUT", message) { }
    }

    /// <summary>
    /// 协议异常
    /// </summary>
    public class ProtocolException : IpcException
    {
        public ProtocolException(string message)
            : base("PROTOCOL_ERROR", message) { }

        public ProtocolException(string message, Exception innerException)
            : base("PROTOCOL_ERROR", message, innerException) { }
    }

    /// <summary>
    /// 安全异常
    /// </summary>
    public class SecurityException : IpcException
    {
        public SecurityException(string message)
            : base("SECURITY_ERROR", message) { }
    }

    /// <summary>
    /// 远程调用异常
    /// </summary>
    public class RemoteException : IpcException
    {
        public RemoteException(string errorCode, string message)
            : base(errorCode, message) { }
    }
}
