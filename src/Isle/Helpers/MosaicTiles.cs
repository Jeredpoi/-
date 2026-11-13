using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Isle.Helpers
{
    /// <summary>
    /// Плитка мозаики из кусочков (по 8×8 квадратов). Сетка квадратов привязана к точке, откуда начали тянуть рамку
    /// (а не к её углу), поэтому при растягивании в любую сторону уже посчитанные квадраты не сдвигаются и не «дрожат»,
    /// а пересчитываются и перерисовываются только кусочки у края. Процессорная отрисовка перерисовывает лишь
    /// изменившиеся элементы — большая рамка не тормозит.
    /// </summary>
    public sealed class MosaicTiles : Canvas
    {
        private const int Tile = 8; // квадратов в кусочке по стороне
        private readonly Dictionary<(int, int), (Image img, Int32Rect px)> _tiles = new();
        private int _block = -1, _ax, _ay;

        public MosaicTiles() { IsHitTestVisible = false; }

        /// <summary>Размер квадрата, выбранный при первом обновлении (дальше не меняется — сетка не дрожит).</summary>
        public int Block => _block;
        /// <summary>Сколько кусочков пересчитано при последнем обновлении (для журнала).</summary>
        public int LastRebuilt { get; private set; }
        public int Count => _tiles.Count;

        /// <summary>
        /// Показать мозаику в рамке exact (пиксели снимка w×h). Сетка: от точки (ax, ay) с шагом block.
        /// Пока тянешь — плитка растёт целыми квадратами; final — обрезать ровно по рамке.
        /// </summary>
        public void Update(byte[] px, int w, int h, int ax, int ay, Int32Rect exact, int block, double k, bool final)
        {
            LastRebuilt = 0;
            if (block != _block || ax != _ax || ay != _ay)
            {
                Children.Clear();
                _tiles.Clear();
                _block = block; _ax = ax; _ay = ay;
            }
            // рамка, расширенная до целых квадратов сетки
            int x0 = Math.Max(0, ax + FloorDiv(exact.X - ax, block) * block);
            int y0 = Math.Max(0, ay + FloorDiv(exact.Y - ay, block) * block);
            int x1 = Math.Min(w, ax + CeilDiv(exact.X + exact.Width - ax, block) * block);
            int y1 = Math.Min(h, ay + CeilDiv(exact.Y + exact.Height - ay, block) * block);
            int span = Tile * block;
            int i0 = FloorDiv(x0 - ax, span), i1 = FloorDiv(x1 - 1 - ax, span);
            int j0 = FloorDiv(y0 - ay, span), j1 = FloorDiv(y1 - 1 - ay, span);

            var gone = new List<(int, int)>();
            foreach (var key in _tiles.Keys) if (key.Item1 < i0 || key.Item1 > i1 || key.Item2 < j0 || key.Item2 > j1) gone.Add(key);
            foreach (var key in gone) { Children.Remove(_tiles[key].img); _tiles.Remove(key); }

            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    int tx0 = Math.Max(x0, ax + i * span), tx1 = Math.Min(x1, ax + (i + 1) * span);
                    int ty0 = Math.Max(y0, ay + j * span), ty1 = Math.Min(y1, ay + (j + 1) * span);
                    if (tx1 <= tx0 || ty1 <= ty0) continue;
                    var pr = new Int32Rect(tx0, ty0, tx1 - tx0, ty1 - ty0);
                    if (_tiles.TryGetValue((i, j), out var t) && t.px.Equals(pr)) continue; // не изменился — не трогаем
                    var cells = Mosaic.Cells(px, w, h, pr.X, pr.Y, pr.Width, pr.Height, block, out int cw, out int ch);
                    var src = BitmapSource.Create(cw, ch, 96, 96, PixelFormats.Bgr32, null, cells, cw * 4);
                    src.Freeze();
                    var img = t.img;
                    if (img == null)
                    {
                        img = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
                        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
                        Children.Add(img);
                    }
                    img.Source = src;
                    Canvas.SetLeft(img, pr.X / k); Canvas.SetTop(img, pr.Y / k);
                    img.Width = pr.Width / k; img.Height = pr.Height / k;
                    _tiles[(i, j)] = (img, pr);
                    LastRebuilt++;
                }
            // в конце — ровно по рамке (одна прямоугольная обрезка, а не на каждом движении)
            Clip = final ? new RectangleGeometry(new Rect(exact.X / k, exact.Y / k, exact.Width / k, exact.Height / k)) : null;
        }

        private static int FloorDiv(int a, int b) => (int)Math.Floor(a / (double)b);
        private static int CeilDiv(int a, int b) => (int)Math.Ceiling(a / (double)b);
    }
}
