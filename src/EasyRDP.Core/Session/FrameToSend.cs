namespace EasyRDP.Core.Session
{
    /// <summary>
    /// 发送队列元素（两级队列第二级）。编码线程产出，发送线程消费。
    /// </summary>
    public struct FrameToSend
    {
        public byte[] Data;
        public bool IsKeyframe;
        public long SequenceNumber;
        public long CaptureTimestamp;
    }
}
