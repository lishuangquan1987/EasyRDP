namespace EasyRDP.Core.Rendering
{
    /// <summary>
    /// 读帧引用。借用期间有效，ReleaseReadFrame 后不得再访问 Pixels。
    /// </summary>
    public struct ReadFrameRef
    {
        /// <summary>Pointer to the borrowed pixel buffer (BGRA32). Invalid after ReleaseReadFrame.</summary>
        public byte[] Pixels;
        /// <summary>Width of the frame in pixels.</summary>
        public int Width;
        /// <summary>Height of the frame in pixels.</summary>
        public int Height;
        /// <summary>Sequence number of this frame.</summary>
        public long Sequence;
    }
}
