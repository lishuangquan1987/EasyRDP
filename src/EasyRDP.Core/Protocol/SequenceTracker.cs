namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 通道内消息序号跟踪器——线程安全。
    /// </summary>
    public class SequenceTracker
    {
        private uint _next;

        public SequenceTracker()
        {
            _next = 1;
        }

        /// <summary>
        /// 获取下一个序号并递增。
        /// </summary>
        public uint Next()
        {
            uint current = _next;
            _next = _next + 1;
            return current;
        }

        /// <summary>
        /// 当前序号（不递增）。
        /// </summary>
        public uint Current
        {
            get { return _next; }
        }

        /// <summary>
        /// 重置序号计数器。
        /// </summary>
        public void Reset()
        {
            _next = 1;
        }
    }
}
