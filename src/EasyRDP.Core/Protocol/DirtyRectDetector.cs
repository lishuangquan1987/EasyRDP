using System;
using System.Collections.Generic;

namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 脏矩形检测器——32x32 分块比较 + 相邻合并。
    /// 兼容 .NET 4.0 / C# 5.0。
    /// </summary>
    public static class DirtyRectDetector
    {
        /// <summary>分块大小（像素）</summary>
        public const int TileSize = 32;

        /// <summary>
        /// 检测脏矩形。
        /// 将画面划分为 TileSize×TileSize 的块，逐块比较，
        /// 然后将相邻的脏块合并为尽量大的矩形。
        /// </summary>
        /// <param name="cur">当前帧像素 BGRA32</param>
        /// <param name="prev">上一帧像素 BGRA32</param>
        /// <param name="width">帧宽度</param>
        /// <param name="height">帧高度</param>
        /// <returns>脏矩形列表；无变化返回空列表</returns>
        public static List<ScreenRect> Detect(byte[] cur, byte[] prev, int width, int height)
        {
            if (cur == null || prev == null)
                return new List<ScreenRect>();

            int tilesX = (width + TileSize - 1) / TileSize;
            int tilesY = (height + TileSize - 1) / TileSize;
            int stride = width * 4;

            // Step 1: 找出所有发生变化的 Tile
            bool[,] dirtyTiles = new bool[tilesX, tilesY];
            bool anyDirty = false;

            for (int ty = 0; ty < tilesY; ty++)
            {
                for (int tx = 0; tx < tilesX; tx++)
                {
                    if (IsTileDirty(cur, prev, tx, ty, width, height, stride))
                    {
                        dirtyTiles[tx, ty] = true;
                        anyDirty = true;
                    }
                }
            }

            if (!anyDirty)
                return new List<ScreenRect>();

            // Step 2: 将相邻脏 Tile 合并为矩形
            return MergeTilesToRects(dirtyTiles, tilesX, tilesY, width, height);
        }

        /// <summary>
        /// 比较一个 32×32 Tile 中是否有像素变化（按 int32 比较，每像素一次）。
        /// </summary>
        private static bool IsTileDirty(
            byte[] cur, byte[] prev,
            int tx, int ty, int width, int height, int stride)
        {
            int startX = tx * TileSize;
            int startY = ty * TileSize;
            int endX = Math.Min(startX + TileSize, width);
            int endY = Math.Min(startY + TileSize, height);

            for (int y = startY; y < endY; y++)
            {
                int rowBase = y * stride;
                for (int x = startX; x < endX; x++)
                {
                    int offset = rowBase + x * 4;
                    // 按 int32 一次性比较 BGRA 四个字节
                    if (BitConverter.ToInt32(cur, offset) != BitConverter.ToInt32(prev, offset))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 将脏 Tile 二维矩阵合并为尽量大的矩形。
        /// 逐行扫描，找到未消费的脏 Tile 后向右下扩张。
        /// </summary>
        private static List<ScreenRect> MergeTilesToRects(
            bool[,] dirty, int tilesX, int tilesY,
            int width, int height)
        {
            var rects = new List<ScreenRect>();
            bool[,] consumed = new bool[tilesX, tilesY];

            for (int ty = 0; ty < tilesY; ty++)
            {
                for (int tx = 0; tx < tilesX; tx++)
                {
                    if (!dirty[tx, ty] || consumed[tx, ty])
                        continue;

                    // 向右找到连续脏 Tile 的终点
                    int endTx = tx;
                    while (endTx + 1 < tilesX && dirty[endTx + 1, ty] && !consumed[endTx + 1, ty])
                        endTx++;

                    // 向下扩张：检查下一行在 [tx, endTx] 范围内是否全部为脏
                    int endTy = ty;
                    while (endTy + 1 < tilesY)
                    {
                        bool allDirty = true;
                        for (int x = tx; x <= endTx; x++)
                        {
                            if (!dirty[x, endTy + 1] || consumed[x, endTy + 1])
                            {
                                allDirty = false;
                                break;
                            }
                        }
                        if (allDirty)
                            endTy++;
                        else
                            break;
                    }

                    // 标记此矩形内的所有 Tile 为已消费
                    for (int y = ty; y <= endTy; y++)
                        for (int x = tx; x <= endTx; x++)
                            consumed[x, y] = true;

                    // 计算矩形对应的像素坐标（取 Tile 边界）
                    int x1 = tx * TileSize;
                    int y1 = ty * TileSize;
                    int x2 = Math.Min((endTx + 1) * TileSize, width) - 1;
                    int y2 = Math.Min((endTy + 1) * TileSize, height) - 1;
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
    }
}
