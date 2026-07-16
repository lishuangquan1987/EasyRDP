using System;
using System.Collections.Generic;

namespace EasyRDP.Client.Common
{
    /// <summary>
    /// 消息路由器。
    /// 将收到的消息按类型分派到注册的处理器。
    /// 处理器按消息 Body 的实际 CLR 类型匹配。
    /// </summary>
    public class MessageDispatcher
    {
        private readonly Dictionary<Type, Action<object>> _handlers = new Dictionary<Type, Action<object>>();

        /// <summary>当日志回调（可选），用于记录未处理消息。</summary>
        public Action<string> OnLog { get; set; }

        /// <summary>
        /// 注册消息处理器。
        /// </summary>
        /// <typeparam name="T">消息 Body 类型</typeparam>
        /// <param name="handler">处理器</param>
        public void Register<T>(Action<T> handler) where T : class
        {
            if (handler == null)
                throw new ArgumentNullException("handler");

            _handlers[typeof(T)] = new Action<object>(obj => handler((T)obj));
        }

        /// <summary>
        /// 取消注册。
        /// </summary>
        /// <typeparam name="T">消息 Body 类型</typeparam>
        public void Unregister<T>() where T : class
        {
            _handlers.Remove(typeof(T));
        }

        /// <summary>
        /// 分发消息。根据 message.Body 的实际类型查找注册的处理器并调用。
        /// 未注册的类型静默忽略（触发 OnLog 回调）。
        /// </summary>
        public void Dispatch(object messageBody)
        {
            if (messageBody == null)
                return;

            Type msgType = messageBody.GetType();
            Action<object> handler;
            if (_handlers.TryGetValue(msgType, out handler))
            {
                handler(messageBody);
            }
            else
            {
                var log = OnLog;
                if (log != null)
                    log(string.Format("No handler registered for message type: {0}", msgType.Name));
            }
        }

        /// <summary>
        /// 清除所有注册的处理器。
        /// </summary>
        public void Clear()
        {
            _handlers.Clear();
        }
    }
}
