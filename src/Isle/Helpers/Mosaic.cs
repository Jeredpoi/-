using System;

namespace Isle.Helpers
{
    /// <summary>Пикселизация (мозаика) — крупные квадраты со средним цветом: текст под ними не прочитать.</summary>
    public static class Mosaic
    {
        /// <summary>Размер квадрата для картинки w×h: чем больше снимок, тем крупнее, но не меньше 8 px.</summary>
        public static int BlockFor(int w, int h) => Math.Max(8, (int)(Math.Max(w, h) / 70.0));

        /// <summary>Пикселизованная копия всего BGRA-буфера (сетка квадратов от левого верхнего угла).</summary>
        public static byte[] Build(byte[] px, int w, int h, int block)
        {
            var dst = new byte[px.Length];
            int stride = w * 4;
            for (int by = 0; by < h; by += block)
                for (int bx = 0; bx < w; bx += block)
                {
                    int bw = Math.Min(block, w - bx), bh = Math.Min(block, h - by);
                    long sb = 0, sg = 0, sr = 0; int n = bw * bh;
                    for (int y = 0; y < bh; y++)
                    {
                        int row = (by + y) * stride + bx * 4;
                        for (int x = 0; x < bw; x++) { int i = row + x * 4; sb += px[i]; sg += px[i + 1]; sr += px[i + 2]; }
                    }
                    byte b = (byte)(sb / n), g = (byte)(sg / n), r = (byte)(sr / n);
                    for (int y = 0; y < bh; y++)
                    {
                        int row = (by + y) * stride + bx * 4;
                        for (int x = 0; x < bw; x++) { int i = row + x * 4; dst[i] = b; dst[i + 1] = g; dst[i + 2] = r; dst[i + 3] = 255; }
                    }
                }
            return dst;
        }
    }
}
