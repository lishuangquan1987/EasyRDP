using System;
using System.Collections.Generic;

namespace EasyRDP.Core.Protocol
{
    public class BitmapEncoder : IFrameEncoder
    {
        public CodecId Codec
        {
            get { return CodecId.Bitmap; }
        }

        public ScreenFrameMessage Encode(int width, int height, byte[] curPixels, byte[] prevPixels, bool forceKey)
        {
            int pixelSize = width * height * 4;

            if (forceKey || prevPixels == null || prevPixels.Length != pixelSize)
            {
                return BuildFullFrame(width, height, curPixels);
            }

            ScreenFrameMessage deltaMsg = BuildDeltaFrame(width, height, curPixels, prevPixels);

            if (deltaMsg.Pixels == null || deltaMsg.Pixels.Length == 0)
            {
                return new ScreenFrameMessage
                {
                    FrameType = FrameType.Full,
                    Compress = CompressType.None,
                    Rects = new[] { new ScreenRect { X = 0, Y = 0, Width = (ushort)width, Height = (ushort)height, Offset = 0 } },
                    Pixels = new byte[0]
                };
            }

            if (deltaMsg.Pixels.Length >= pixelSize)
            {
                return BuildFullFrame(width, height, curPixels);
            }

            return deltaMsg;
        }

        public void Reset()
        {
        }

        private static ScreenFrameMessage BuildFullFrame(int w, int h, byte[] raw)
        {
            int pixelCount = w * h;
            CompressType bestType = CompressType.Zlib;

            if (pixelCount > 10000 && CompressHelper.ShouldUseJPEG(raw, pixelCount))
                bestType = CompressType.JPEG;

            byte[] compressed = CompressHelper.Compress(raw, bestType, w, h);
            bool useCompress = compressed.Length < raw.Length && compressed.Length > 0;
            return new ScreenFrameMessage
            {
                FrameType = FrameType.Full,
                Compress = useCompress ? bestType : CompressType.None,
                Rects = new[] { new ScreenRect { X = 0, Y = 0, Width = (ushort)w, Height = (ushort)h, Offset = 0 } },
                Pixels = useCompress ? compressed : raw
            };
        }

        private static ScreenFrameMessage BuildDeltaFrame(int w, int h, byte[] cur, byte[] prev)
        {
            var rects = DirtyRectDetector.Detect(cur, prev, w, h);
            if (rects.Count == 0)
                return new ScreenFrameMessage { FrameType = FrameType.Full, Compress = CompressType.None,
                    Rects = new[] { new ScreenRect { X = 0, Y = 0, Width = (ushort)w, Height = (ushort)h, Offset = 0 } }, Pixels = new byte[0] };

            int totalBytes = 0;
            for (int i = 0; i < rects.Count; i++) totalBytes += rects[i].Width * rects[i].Height * 4;
            byte[] allPixels = new byte[totalBytes];
            int offset = 0;
            for (int i = 0; i < rects.Count; i++)
            {
                var r = rects[i];
                r.Offset = (uint)offset;
                rects[i] = r;
                int tileBytes = r.Width * r.Height * 4;
                for (int ty = 0; ty < r.Height; ty++)
                    Array.Copy(cur, ((r.Y + ty) * w + r.X) * 4, allPixels, offset + ty * r.Width * 4, r.Width * 4);
                offset += tileBytes;
            }
            byte[] compressed = CompressHelper.Compress(allPixels, CompressType.Zlib);
            bool useZlib = compressed.Length < allPixels.Length;
            return new ScreenFrameMessage { FrameType = FrameType.Delta, Compress = useZlib ? CompressType.Zlib : CompressType.None,
                Rects = rects.ToArray(), Pixels = useZlib ? compressed : allPixels };
        }
    }
}