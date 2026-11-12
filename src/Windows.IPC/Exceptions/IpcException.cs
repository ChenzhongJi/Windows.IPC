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
        /// <summary>标识错误类型的代码。</summary>
        public string ErrorCode { get; }

        /// <summary>使用错误代码和消息创建 IPC 异常。</summary>
        /// <param name='errorCode'>错误代码。</param>
        /// <param name='message'>错误消息。</param>
        public IpcException(string errorCode, string message)
            : base(message)
        {
            ErrorCode = errorCode;
        }

        /// <summary>使用错误代码、消息和内部异常创建 IPC 异常。</summary>
        /// <param name='errorCode'>错误代码。</param>
        /// <param name='message'>错误消息。</param>
        /// <param name='innerException'>导致当前异常的异常。</param>
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
        /// <summary>使用错误消息创建连接异常。</summary>
        /// <param name='message'>错误消息。</param>
        public ConnectionException(string message)
            : base("CONNECTION_ERROR", message) { }

        /// <summary>使用错误消息和内部异常创建连接异常。</summary>
        /// <param name='message'>错误消息。</param>
        /// <param name='innerException'>导致当前异常的异常。</param>
        public ConnectionException(string message, Exception innerException)
            : base("CONNECTION_ERROR", message, innerException) { }
    }

    /// <summary>
    /// 超时异常
    /// </summary>
    public class TimeoutException : IpcException
    {
        /// <summary>使用错误消息创建超时异常。</summary>
        /// <param name='message'>错误消息。</param>
        public TimeoutException(string message)
            : base("TIMEOUT", message) { }
    }

    /// <summary>
    /// 协议异常
    /// </summary>
    public class ProtocolException : IpcException
    {
        /// <summary>使用错误消息创建协议异常。</summary>
        /// <param name='message'>错误消息。</param>
        public ProtocolException(string message)
            : base("PROTOCOL_ERROR", message) { }

        /// <summary>使用错误消息和内部异常创建协议异常。</summary>
        /// <param name='message'>错误消息。</param>
        /// <param name='innerException'>导致当前异常的异常。</param>
        public ProtocolException(string message, Exception innerException)
            : base("PROTOCOL_ERROR", message, innerException) { }
    }

    /// <summary>
    /// 安全异常
    /// </summary>
    public class SecurityException : IpcException
    {
        /// <summary>使用错误消息创建安全异常。</summary>
        /// <param name='message'>错误消息。</param>
        public SecurityException(string message)
            : base("SECURITY_ERROR", message) { }

        /// <summary>使用错误消息和内部异常创建安全异常。</summary>
        /// <param name='message'>错误消息。</param>
        /// <param name='innerException'>导致当前异常的异常。</param>
        public SecurityException(string message, Exception innerException)
            : base("SECURITY_ERROR", message, innerException) { }
    }

    /// <summary>
    /// 远程调用异常
    /// </summary>
    public class RemoteException : IpcException
    {
        /// <summary>使用远程错误代码和消息创建异常。</summary>
        /// <param name='errorCode'>远程错误代码。</param>
        /// <param name='message'>远程错误消息。</param>
        public RemoteException(string errorCode, string message)
            : base(errorCode, message) { }
    }
}
