using System;
using System.Collections.Generic;

namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 脏矩形检测器——行级比较 + 相邻合并。
    /// 对比当前帧与上一帧，输出变化区域的最小包围矩形列表。
    /// 兼容 .NET 4.0 / C# 5.0。
    /// </summary>
    public static class DirtyRectDetector
    {
        private const int MergeGap = 2; // 合并时允许的最大间隙（像素）

        /// <summary>
        /// 检测脏矩形。返回按 Offset 递增的列表（Offset 由调用方在合并像素后填入）。
        /// </summary>
        /// <param name="cur">当前帧像素 BGRA32</param>
        /// <param name="prev">上一帧像素 BGRA32</param>
        /// <param name="width">帧宽度</param>
        /// <param name="height">帧高度</param>
        /// <returns>脏矩形列表；无变化返回空列表</returns>
        public static List<ScreenRect> Detect(byte[] cur, byte[] prev, int width, int height)
        {
            // Step 1: 逐行扫描，找到每行的脏水平段
            var rowSegments = new List<DirtySegment>[height];
            bool anyDirty = false;
            for (int y = 0; y < height; y++)
            {
                rowSegments[y] = FindDirtySegments(cur, prev, width, y);
                if (rowSegments[y].Count > 0)
                    anyDirty = true;
            }

            if (!anyDirty)
                return new List<ScreenRect>();

            // Step 2: 垂直合并相邻行的重叠段
            return MergeToRectangles(rowSegments, width, height);
        }

        #region Row-level dirty detection

        private class DirtySegment
        {
            public int StartX, EndX; // inclusive range
            public bool Consumed;
        }

        private static List<DirtySegment> FindDirtySegments(byte[] cur, byte[] prev, int width, int y)
        {
            var segments = new List<DirtySegment>();
            int rowBase = y * width * 4;
            int x = 0;

            while (x < width)
            {
                // Skip clean pixels — fast int32 comparison
                while (x < width && PixelsEqual(cur, prev, rowBase, x))
                    x++;

                if (x >= width) break;

                int startX = x;
                while (x < width && !PixelsEqual(cur, prev, rowBase, x))
                    x++;
                int endX = x - 1;

                // Merge tiny adjacent gaps
                if (segments.Count > 0 && startX - segments[segments.Count - 1].EndX <= MergeGap)
                    segments[segments.Count - 1].EndX = endX;
                else
                    segments.Add(new DirtySegment { StartX = startX, EndX = endX });
            }

            return segments;
        }

        /// <summary>
        /// 比较单个 BGRA32 像素（作为 int32 比较，4x 快于逐字节）。
        /// </summary>
        private static bool PixelsEqual(byte[] a, byte[] b, int rowBase, int x)
        {
            int offset = rowBase + x * 4;
            // Compare as 32-bit int — BGRA pixel fits in one int
            return a[offset] == b[offset]
                && a[offset + 1] == b[offset + 1]
                && a[offset + 2] == b[offset + 2]
                && a[offset + 3] == b[offset + 3];
        }

        #endregion

        #region Rectangle merging

        /// <summary>
        /// 将逐行脏段垂直合并为尽可能大的矩形。
        /// 策略：对每行每个未消费段，向下扩张直到无法匹配。
        /// </summary>
        private static List<ScreenRect> MergeToRectangles(
            List<DirtySegment>[] rowSegments, int width, int height)
        {
            var rects = new List<ScreenRect>();

            for (int y = 0; y < height; y++)
            {
                var row = rowSegments[y];
                for (int s = 0; s < row.Count; s++)
                {
                    var seg = row[s];
                    if (seg.Consumed) continue;

                    int x1 = seg.StartX;
                    int x2 = seg.EndX;
                    int y1 = y;
                    int y2 = y;
                    seg.Consumed = true;

                    // Expand downward
                    for (int ny = y + 1; ny < height; ny++)
                    {
                        var nextRow = rowSegments[ny];
                        DirtySegment match = FindOverlapping(nextRow, x1, x2);
                        if (match != null)
                        {
                            match.Consumed = true;
                            x1 = Math.Min(x1, match.StartX);
                            x2 = Math.Max(x2, match.EndX);
                            y2 = ny;
                        }
                        else break;
                    }

                    // Only add rectangles with meaningful size
                    int rw = x2 - x1 + 1;
                    int rh = y2 - y1 + 1;
                    if (rw > 0 && rh > 0)
                    {
                        rects.Add(new ScreenRect
                        {
                            X = (ushort)x1,
                            Y = (ushort)y1,
                            Width = (ushort)rw,
                            Height = (ushort)rh,
                            Offset = 0
                        });
                    }
                }
            }

            return rects;
        }

        /// <summary>
        /// 在给定行中查找与 [x1, x2] 有水平重叠且未消费的段。
        /// 返回重叠的最宽匹配，或 null。
        /// </summary>
        private static DirtySegment FindOverlapping(List<DirtySegment> row, int x1, int x2)
        {
            DirtySegment best = null;
            int bestOverlap = 0;
            foreach (var seg in row)
            {
                if (seg.Consumed) continue;
                // Check overlap
                int overlapStart = Math.Max(x1, seg.StartX);
                int overlapEnd = Math.Min(x2, seg.EndX);
                if (overlapStart <= overlapEnd)
                {
                    int overlap = overlapEnd - overlapStart;
                    if (overlap > bestOverlap)
                    {
                        bestOverlap = overlap;
                        best = seg;
                    }
                }
            }
            return best;
        }

        #endregion
    }
}
