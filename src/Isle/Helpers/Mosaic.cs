using System;

namespace Isle.Helpers
{
    /// <summary>Пикселизация (мозаика) — крупные квадраты со средним цветом: текст под ними не прочитать.</summary>
    public static class Mosaic
    {
        /// <summary>Размер квадрата для картинки w×h: чем больше снимок, тем крупнее, но не меньше 8 px.</summary>
        public static int BlockFor(int w, int h) => Math.Max(8, (int)(Math.Max(w, h) / 70.0));

        /// <summary>
        /// Размер квадрата для рамки w×h (в пикселях снимка): маленькое сообщение — мелкие квадраты,
        /// большой блок — крупнее, но не больше 24 px: в рамке почти всегда обычный текст, и огромные квадраты на нём
        /// выглядят грубо. Примерно 10 квадратов по короткой стороне, от 6 до 24 px.
        /// </summary>
        public static int BlockForPatch(int w, int h)
        {
            int b = (int)Math.Round(Math.Min(w, h) / 10.0);
            b = Math.Clamp(b, 6, 24);
            return b <= 12 ? b : b - b % 4; // крупные — ступеньками, чтобы при растягивании рамки сетка не дрожала
        }

        /// <summary>
        /// Квадраты для куска r (в пикселях BGRA-буфера w×h): по одному пикселю на квадрат — средний цвет из сетки
        /// до 4×4 точек внутри квадрата. Получается крошечная картинка cw×ch, её растягивают «без сглаживания».
        /// Работает за время, пропорциональное числу квадратов, а не площади, — даже огромная рамка не лагает.
        /// </summary>
        public static byte[] Cells(byte[] px, int w, int h, int rx, int ry, int rw, int rh, int block, out int cw, out int ch)
        {
            cw = Math.Max(1, (rw + block - 1) / block);
            ch = Math.Max(1, (rh + block - 1) / block);
            var dst = new byte[cw * ch * 4];
            int s = Math.Min(block, 4);
            for (int cy = 0; cy < ch; cy++)
                for (int cx = 0; cx < cw; cx++)
                {
                    int x0 = rx + cx * block, y0 = ry + cy * block;
                    int bw = Math.Max(1, Math.Min(block, rx + rw - x0)), bh = Math.Max(1, Math.Min(block, ry + rh - y0));
                    long sb = 0, sg = 0, sr = 0; int n = 0;
                    for (int j = 0; j < s; j++)
                        for (int i = 0; i < s; i++)
                        {
                            int x = Math.Clamp(x0 + (2 * i + 1) * bw / (2 * s), 0, w - 1);
                            int y = Math.Clamp(y0 + (2 * j + 1) * bh / (2 * s), 0, h - 1);
                            int k = (y * w + x) * 4;
                            sb += px[k]; sg += px[k + 1]; sr += px[k + 2]; n++;
                        }
                    int d = (cy * cw + cx) * 4;
                    dst[d] = (byte)(sb / n); dst[d + 1] = (byte)(sg / n); dst[d + 2] = (byte)(sr / n); dst[d + 3] = 255;
                }
            return dst;
        }

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

namespace Isle.Helpers
{
    /// <summary>
    /// Живая мозаика прямо в пикселях картинки: пока тянешь рамку, внутри неё — квадраты, снаружи — исходные пиксели
    /// (восстанавливаются из снимка, сделанного в начале перетаскивания). Никаких слоёв поверх — ни следов, ни затемнений.
    /// </summary>
    public sealed class LiveMosaic
    {
        private readonly System.Windows.Media.Imaging.WriteableBitmap _bmp;
        private readonly byte[] _mosaic;   // вся картинка в квадратах
        private readonly byte[] _before;   // пиксели до этого перетаскивания
        private System.Windows.Int32Rect _cur = System.Windows.Int32Rect.Empty;
        private readonly int _w, _h;

        public LiveMosaic(System.Windows.Media.Imaging.WriteableBitmap bmp)
        {
            _bmp = bmp; _w = bmp.PixelWidth; _h = bmp.PixelHeight;
            _before = new byte[_w * _h * 4];
            bmp.CopyPixels(_before, _w * 4, 0);
            _mosaic = Mosaic.Build(_before, _w, _h, Mosaic.BlockFor(_w, _h));
        }

        /// <summary>Рамка в пикселях картинки (обрезается по краям).</summary>
        public System.Windows.Int32Rect Clamp(double x, double y, double width, double height)
        {
            int x0 = Math.Clamp((int)Math.Floor(x), 0, _w), y0 = Math.Clamp((int)Math.Floor(y), 0, _h);
            int x1 = Math.Clamp((int)Math.Ceiling(x + width), 0, _w), y1 = Math.Clamp((int)Math.Ceiling(y + height), 0, _h);
            return new System.Windows.Int32Rect(x0, y0, x1 - x0, y1 - y0);
        }

        /// <summary>Показать мозаику в рамке r: старую рамку вернуть к исходным пикселям, новую — заполнить квадратами.</summary>
        public void Show(System.Windows.Int32Rect r)
        {
            if (!_cur.IsEmpty && _cur.Width > 0 && _cur.Height > 0) Write(_cur, _before);
            _cur = r;
            if (r.Width > 0 && r.Height > 0) Write(r, _mosaic);
        }

        /// <summary>Отменить показ (рамка слишком мала) — вернуть исходные пиксели.</summary>
        public void Cancel() => Show(System.Windows.Int32Rect.Empty);

        /// <summary>Действие отмены для итоговой рамки: вернуть то, что было до перетаскивания.</summary>
        public Action UndoFor(System.Windows.Int32Rect r)
        {
            var patch = Slice(_before, r);
            var bmp = _bmp;
            return () => { if (r.Width > 0 && r.Height > 0) bmp.WritePixels(r, patch, r.Width * 4, 0); };
        }

        private void Write(System.Windows.Int32Rect r, byte[] src) => _bmp.WritePixels(r, Slice(src, r), r.Width * 4, 0);

        private byte[] Slice(byte[] src, System.Windows.Int32Rect r)
        {
            var patch = new byte[Math.Max(0, r.Width * r.Height * 4)];
            for (int row = 0; row < r.Height; row++)
                Buffer.BlockCopy(src, ((r.Y + row) * _w + r.X) * 4, patch, row * r.Width * 4, r.Width * 4);
            return patch;
        }
    }
}
