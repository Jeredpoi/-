using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Isle.Helpers
{
    /// <summary>
    /// Плитка мозаики из кусочков (по 16×16 квадратов). Пока тянешь рамку, сетка квадратов привязана к её углу,
    /// поэтому уже посчитанные кусочки внутри не меняются — пересчитываются и перерисовываются только кусочки у края.
    /// Процессорная отрисовка перерисовывает лишь изменившиеся элементы, и большая рамка больше не тормозит.
    /// </summary>
    public sealed class MosaicTiles : Canvas
    {
        private const int Tile = 16; // квадратов в кусочке по стороне
        private readonly Dictionary<(int, int), (Image img, int w, int h)> _tiles = new();
        private int _block, _x0, _y0;

        public MosaicTiles() { IsHitTestVisible = false; }

        /// <summary>Сколько кусочков пересчитано при последнем обновлении (для журнала).</summary>
        public int LastRebuilt { get; private set; }
        public int Count => _tiles.Count;

        /// <summary>
        /// Показать мозаику в рамке r (пиксели снимка px шириной w и высотой h); k — пикселей снимка на точку слоя.
        /// </summary>
        public void Update(byte[] px, int w, int h, Int32Rect r, int block, double k)
        {
            LastRebuilt = 0;
            if (block != _block || r.X != _x0 || r.Y != _y0)
            {
                // другая сетка — всё заново
                Children.Clear();
                _tiles.Clear();
                _block = block; _x0 = r.X; _y0 = r.Y;
            }
            Canvas.SetLeft(this, r.X / k);
            Canvas.SetTop(this, r.Y / k);
            int span = Tile * block;
            int tilesX = (r.Width + span - 1) / span, tilesY = (r.Height + span - 1) / span;

            // лишние кусочки (рамку уменьшили) — убрать
            var gone = new List<(int, int)>();
            foreach (var key in _tiles.Keys) if (key.Item1 >= tilesX || key.Item2 >= tilesY) gone.Add(key);
            foreach (var key in gone) { Children.Remove(_tiles[key].img); _tiles.Remove(key); }

            for (int ty = 0; ty < tilesY; ty++)
                for (int tx = 0; tx < tilesX; tx++)
                {
                    int ox = tx * span, oy = ty * span;
                    int tw = Math.Min(span, r.Width - ox), th = Math.Min(span, r.Height - oy);
                    if (tw <= 0 || th <= 0) continue;
                    if (_tiles.TryGetValue((tx, ty), out var t) && t.w == tw && t.h == th) continue; // не изменился
                    var cells = Mosaic.Cells(px, w, h, r.X + ox, r.Y + oy, tw, th, block, out int cw, out int ch);
                    var src = BitmapSource.Create(cw, ch, 96, 96, PixelFormats.Bgr32, null, cells, cw * 4);
                    src.Freeze();
                    Image img;
                    if (t.img != null) img = t.img;
                    else
                    {
                        img = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
                        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
                        Children.Add(img);
                    }
                    img.Source = src;
                    Canvas.SetLeft(img, ox / k); Canvas.SetTop(img, oy / k);
                    img.Width = tw / k; img.Height = th / k;
                    _tiles[(tx, ty)] = (img, tw, th);
                    LastRebuilt++;
                }
        }
    }
}
