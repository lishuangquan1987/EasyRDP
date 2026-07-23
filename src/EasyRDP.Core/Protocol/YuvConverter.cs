namespace EasyRDP.Core.Protocol
{
#if NET8_0_OR_GREATER
    public static class YuvConverter
    {
        public static void Bgra32ToI420(byte[] bgra, int width, int height, byte[] y, byte[] u, byte[] v)
        {
            int ySize = width * height;
            int uvSize = (width / 2) * (height / 2);

            if (y.Length < ySize || u.Length < uvSize || v.Length < uvSize)
                throw new System.ArgumentException("YUV buffer size insufficient");

            int yIdx = 0;
            int uIdx = 0;
            int vIdx = 0;

            for (int row = 0; row < height; row++)
            {
                for (int col = 0; col < width; col++)
                {
                    int bgraIdx = (row * width + col) * 4;
                    byte blue = bgra[bgraIdx];
                    byte green = bgra[bgraIdx + 1];
                    byte red = bgra[bgraIdx + 2];

                    y[yIdx++] = (byte)((0.299 * red + 0.587 * green + 0.114 * blue) + 0.5);

                    if (row % 2 == 0 && col % 2 == 0)
                    {
                        u[uIdx++] = (byte)((-0.1687 * red - 0.3313 * green + 0.5 * blue + 128) + 0.5);
                        v[vIdx++] = (byte)((0.5 * red - 0.4187 * green - 0.0813 * blue + 128) + 0.5);
                    }
                }
            }
        }

        public static byte[] Bgra32ToI420(byte[] bgra, int width, int height)
        {
            int ySize = width * height;
            int uvSize = (width / 2) * (height / 2);
            byte[] result = new byte[ySize + uvSize * 2];

            int yIdx = 0;
            int uIdx = ySize;
            int vIdx = ySize + uvSize;

            for (int row = 0; row < height; row++)
            {
                for (int col = 0; col < width; col++)
                {
                    int bgraIdx = (row * width + col) * 4;
                    byte blue = bgra[bgraIdx];
                    byte green = bgra[bgraIdx + 1];
                    byte red = bgra[bgraIdx + 2];

                    result[yIdx++] = (byte)((0.299 * red + 0.587 * green + 0.114 * blue) + 0.5);

                    if (row % 2 == 0 && col % 2 == 0)
                    {
                        result[uIdx++] = (byte)((-0.1687 * red - 0.3313 * green + 0.5 * blue + 128) + 0.5);
                        result[vIdx++] = (byte)((0.5 * red - 0.4187 * green - 0.0813 * blue + 128) + 0.5);
                    }
                }
            }

            return result;
        }

        public static void I420ToBgra32(byte[] y, byte[] u, byte[] v, int width, int height, byte[] bgra)
        {
            int ySize = width * height;
            int uvSize = (width / 2) * (height / 2);

            if (y.Length < ySize || u.Length < uvSize || v.Length < uvSize || bgra.Length < ySize * 4)
                throw new System.ArgumentException("Buffer size insufficient");

            int uvWidth = width / 2;

            for (int row = 0; row < height; row++)
            {
                for (int col = 0; col < width; col++)
                {
                    int yIdx = row * width + col;
                    int uvRow = row / 2;
                    int uvCol = col / 2;
                    int uvIdx = uvRow * uvWidth + uvCol;

                    double yVal = y[yIdx];
                    double uVal = u[uvIdx] - 128;
                    double vVal = v[uvIdx] - 128;

                    double r = yVal + 1.402 * vVal;
                    double g = yVal - 0.3441 * uVal - 0.7141 * vVal;
                    double b = yVal + 1.772 * uVal;

                    int bgraIdx = yIdx * 4;
                    bgra[bgraIdx] = Clamp(b);
                    bgra[bgraIdx + 1] = Clamp(g);
                    bgra[bgraIdx + 2] = Clamp(r);
                    bgra[bgraIdx + 3] = 255;
                }
            }
        }

        private static byte Clamp(double value)
        {
            if (value < 0) return 0;
            if (value > 255) return 255;
            return (byte)(value + 0.5);
        }
    }
#endif
}