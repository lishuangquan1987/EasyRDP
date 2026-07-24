namespace EasyRDP.Core.Rendering
{
    /// <summary>
    /// 读帧引用。借用期间有效，ReleaseReadFrame 后不得再访问 Pixels。
    /// </summary>
    public struct ReadFrameRef
    {
        public byte[] Pixels;
        public int Width;
        public int Height;
        public long Sequence;
    }
}
