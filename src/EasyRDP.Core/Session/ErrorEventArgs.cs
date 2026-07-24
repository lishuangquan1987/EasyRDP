namespace EasyRDP.Core.Session
{
    using System;
    /// <summary>致命错误事件参数。</summary>
    public class ErrorEventArgs : EventArgs
    {
        public string Message;
        public Exception Exception;

        public ErrorEventArgs(string message, Exception exception = null)
        {
            Message = message;
            Exception = exception;
        }
    }
}
