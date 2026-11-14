using System;
using System.Collections.Generic;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using Isle.Services;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using ContainerVisual = Windows.UI.Composition.ContainerVisual;

namespace Isle.Overlays
{
    /// <summary>
    /// Тяжёлая часть слоя снимка — на графическом слое Windows (видеокарта, частота монитора):
    /// замороженный снимок экрана, затемнение вокруг выделения и квадраты мозаики. Всё выглядит как раньше:
    /// снимок пиксель в пиксель, то же затемнение, те же квадраты (маленькая картинка, растянутая без сглаживания).
    /// WPF поверх рисует только лёгкое: панель инструментов, рамку и уголки, перо, стрелки, текст.
    /// Невидимые WPF-копии снимка и мозаики остаются — из них, как и раньше, склеивается сохраняемая картинка.
    /// Если графический слой недоступен — всё рисует WPF, как было.
    /// </summary>
    public partial class CaptureOverlay
    {
        private bool _comp;                       // слой включён
        private DesktopWindowTarget _cTarget;
        private ContainerVisual _cRoot, _cPatchLayer;
        private SpriteVisual _cShot;
        private readonly SpriteVisual[] _cDim = new SpriteVisual[4];
        private CompositionDrawingSurface _cShotSurface;
        private readonly List<IDisposable> _cTrash = new();

        /// <summary>Окно графического слоя (без своей картинки) — лежит прямо под окном WPF.</summary>
        private sealed class CompHost : System.Windows.Forms.NativeWindow
        {
            public CompHost(int x, int y, int w, int h)
            {
                CreateHandle(new System.Windows.Forms.CreateParams
                {
                    Style = unchecked((int)0x80000000),                       // WS_POPUP
                    ExStyle = 0x00200000 | 0x08 | 0x80 | 0x08000000,          // NOREDIRECTIONBITMAP | TOPMOST | TOOLWINDOW | NOACTIVATE
                    X = x, Y = y, Width = w, Height = h
                });
            }
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
        private CompHost _cHost;

        /// <summary>
        /// Включить графический слой (до показа окна). Снимок, затемнение и мозаика — в отдельном окне под этим;
        /// это окно WPF становится прозрачным и рисует только лёгкое (панель, рамку, пометки). false — всё на WPF.
        /// </summary>
        private bool TryEnableComp(UIElement wpfShot)
        {
            if (!AppSettings.Current.CaptureComposition) return false;
            try
            {
                var t0 = System.Diagnostics.Stopwatch.StartNew();
                var c = Helpers.Comp.Compositor;
                _cHost = new CompHost(_vs.X, _vs.Y, _vs.Width, _vs.Height);
                _cTarget = Helpers.Comp.Target(_cHost.Handle, true);
                _cRoot = c.CreateContainerVisual();
                _cTarget.Root = _cRoot;

                _cShotSurface = Helpers.Comp.Surface(_shot);
                var brush = c.CreateSurfaceBrush(_cShotSurface);
                brush.Stretch = CompositionStretch.Fill;
                brush.BitmapInterpolationMode = CompositionBitmapInterpolationMode.NearestNeighbor;
                _cShot = c.CreateSpriteVisual();
                _cShot.Brush = brush;
                _cShot.Size = new Vector2(_vs.Width, _vs.Height);
                _cRoot.Children.InsertAtTop(_cShot);

                _cPatchLayer = c.CreateContainerVisual();
                _cPatchLayer.Size = new Vector2(_vs.Width, _vs.Height);
                _cRoot.Children.InsertAtTop(_cPatchLayer);

                var dimBrush = c.CreateColorBrush(Windows.UI.Color.FromArgb(110, 0, 0, 0)); // то же затемнение, что было
                for (int i = 0; i < 4; i++)
                {
                    _cDim[i] = c.CreateSpriteVisual();
                    _cDim[i].Brush = dimBrush;
                    _cRoot.Children.InsertAtTop(_cDim[i]);
                }
                BuildCompShapes(c);
                ShowWindow(_cHost.Handle, 4 /* SW_SHOWNOACTIVATE */);

                // окно WPF — прозрачное; почти невидимый фон, чтобы клики не проходили сквозь него
                AllowsTransparency = true;
                Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
                wpfShot.Visibility = Visibility.Hidden;
                _dim.Visibility = Visibility.Hidden;
                _comp = true;
                IsVisibleChanged += (_, e) => { if (_cHost != null) ShowWindow(_cHost.Handle, (bool)e.NewValue ? 4 : 0); }; // слой прячется вместе с окном
                Closed += (_, __) => DisposeComp();
                Journal.Info("capture", "слой: снимок, затемнение и мозаика — на графическом слое Windows (" + t0.ElapsedMilliseconds + " мс)");
                return true;
            }
            catch (Exception ex)
            {
                Journal.Warn("capture", "графический слой не включился, рисует WPF: " + ex);
                DisposeComp();
                return false;
            }
        }

        // Рамка выделения и пунктир мозаики — тоже на графическом слое: при перетаскивании WPF вообще не
        // перерисовывается, рамка идёт с частотой монитора (240 Гц), а не ~60, как у прозрачного окна WPF.
        // Только простые прямоугольники (как затемнение и мозаика): «фигуры» слоя на части видеокарт оставляли следы.
        private readonly SpriteVisual[] _cSel = new SpriteVisual[4];       // белая рамка выделения
        private readonly SpriteVisual[] _cFrameUnder = new SpriteVisual[4]; // чёрная линия рамки мозаики
        private readonly SpriteVisual[] _cFrameDash = new SpriteVisual[4];  // белый пунктир поверх
        private float _selW = 2, _lineW = 1;

        private void BuildCompShapes(Compositor c)
        {
            float k = (float)Math.Max(1, _vs.Width / Math.Max(1.0, SystemParameters.VirtualScreenWidth));
            _selW = Math.Max(1, (float)Math.Round(1.5 * k)); // как у WPF: 1,5 точки
            _lineW = Math.Max(1, (float)Math.Round(k));
            var white = c.CreateColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));
            var black = c.CreateColorBrush(Windows.UI.Color.FromArgb(255, 0, 0, 0));
            for (int i = 0; i < 4; i++)
            {
                _cSel[i] = Sprite(white);
                _cFrameUnder[i] = Sprite(black);
            }
            // пунктир 4 на 4 точки — длинная полоска-картинка, видна её начальная часть нужной длины
            int dash = Math.Max(1, (int)Math.Round(4 * k)), len = Math.Max(_vs.Width, _vs.Height) + dash * 2;
            var hBrush = DashBrush(c, len, 1, dash, true);
            var vBrush = DashBrush(c, 1, len, dash, false);
            for (int i = 0; i < 4; i++) _cFrameDash[i] = Sprite(i < 2 ? hBrush : vBrush);
            SpriteVisual Sprite(CompositionBrush brush)
            {
                var v = c.CreateSpriteVisual();
                v.Brush = brush;
                v.IsVisible = false;
                _cRoot.Children.InsertAtTop(v);
                return v;
            }
        }

        private static CompositionSurfaceBrush DashBrush(Compositor c, int w, int h, int dash, bool horizontal)
        {
            var px = new byte[w * h * 4];
            int n = horizontal ? w : h;
            for (int i = 0; i < n; i++)
                if ((i / dash) % 2 == 0) { int o = i * 4; px[o] = px[o + 1] = px[o + 2] = px[o + 3] = 255; }
            var surf = Helpers.Comp.Device.CreateDrawingSurface(new Windows.Foundation.Size(w, h),
                Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, Windows.Graphics.DirectX.DirectXAlphaMode.Premultiplied);
            Helpers.Comp.Draw(surf, px, w, h, w * 4);
            var b = c.CreateSurfaceBrush(surf);
            b.Stretch = CompositionStretch.None;          // без растягивания: видна начальная часть полоски
            b.HorizontalAlignmentRatio = 0; b.VerticalAlignmentRatio = 0;
            b.BitmapInterpolationMode = CompositionBitmapInterpolationMode.NearestNeighbor;
            return b;
        }

        /// <summary>Четыре стороны рамки толщиной t (пиксели слоя).</summary>
        private static void Box(SpriteVisual[] v, float l, float t, float r, float b, float th, bool show)
        {
            if (!show) { foreach (var x in v) x.IsVisible = false; return; }
            float w = Math.Max(th, r - l), h = Math.Max(th, b - t);
            Set(v[0], l, t, w, th);            // верх
            Set(v[1], l, t + h - th, w, th);   // низ
            Set(v[2], l, t, th, h);            // лево
            Set(v[3], l + w - th, t, th, h);   // право
            static void Set(SpriteVisual s, float x, float y, float w, float h)
            {
                s.Offset = new Vector3(x, y, 0);
                s.Size = new Vector2(w, h);
                s.IsVisible = true;
            }
        }

        /// <summary>Рамка выделения (в точках слоя); null — спрятать.</summary>
        private void CompSel(Rect? r)
        {
            if (!_comp || _cSel[0] == null) return;
            if (r is not Rect x || x.IsEmpty) { Box(_cSel, 0, 0, 0, 0, 0, false); return; }
            float l = (float)Math.Round(x.X * Kx), t = (float)Math.Round(x.Y * Ky), rr = (float)Math.Round(x.Right * Kx), b = (float)Math.Round(x.Bottom * Ky);
            Box(_cSel, l - _selW / 2, t - _selW / 2, rr + _selW / 2, b + _selW / 2, _selW, true);
        }

        /// <summary>Пунктирная рамка мозаики (в точках страницы); null — спрятать.</summary>
        private void CompFrame(Rect? r)
        {
            if (!_comp || _cFrameUnder[0] == null) return;
            if (r is not Rect x) { Box(_cFrameUnder, 0, 0, 0, 0, 0, false); Box(_cFrameDash, 0, 0, 0, 0, 0, false); return; }
            float l = (float)Math.Round((_area.X + x.X) * Kx), t = (float)Math.Round((_area.Y + x.Y) * Ky);
            float rr = (float)Math.Round((_area.X + x.X + Math.Max(1, x.Width)) * Kx), b = (float)Math.Round((_area.Y + x.Y + Math.Max(1, x.Height)) * Ky);
            Box(_cFrameUnder, l, t, rr, b, _lineW, true);
            Box(_cFrameDash, l, t, rr, b, _lineW, true);
        }

        private void DisposeComp()
        {
            _comp = false;
            try { if (_cTarget != null) _cTarget.Root = null; } catch { }
            foreach (var d in _cTrash) try { d.Dispose(); } catch { }
            _cTrash.Clear();
            try { _cShotSurface?.Dispose(); } catch { }
            try { _cTarget?.Dispose(); } catch { }
            try { _cHost?.DestroyHandle(); } catch { }
            _cTarget = null; _cShotSurface = null; _cHost = null;
        }

        /// <summary>Затемнение вокруг «окна» hole (в точках слоя) — четыре полосы.</summary>
        private void CompDim(Rect hole)
        {
            if (!_comp) return;
            float W = _vs.Width, H = _vs.Height;
            if (hole.IsEmpty)
            {
                Set(_cDim[0], 0, 0, W, H);
                for (int i = 1; i < 4; i++) Set(_cDim[i], 0, 0, 0, 0);
                return;
            }
            float l = (float)Math.Round(hole.X * Kx), t = (float)Math.Round(hole.Y * Ky);
            float r = (float)Math.Round(hole.Right * Kx), b = (float)Math.Round(hole.Bottom * Ky);
            l = Math.Clamp(l, 0, W); r = Math.Clamp(r, 0, W); t = Math.Clamp(t, 0, H); b = Math.Clamp(b, 0, H);
            Set(_cDim[0], 0, 0, W, t);            // сверху
            Set(_cDim[1], 0, b, W, H - b);        // снизу
            Set(_cDim[2], 0, t, l, b - t);        // слева
            Set(_cDim[3], r, t, W - r, b - t);    // справа
            static void Set(SpriteVisual v, float x, float y, float w, float h)
            {
                v.Offset = new Vector3(x, y, 0);
                v.Size = new Vector2(Math.Max(0, w), Math.Max(0, h));
            }
        }

        /// <summary>Мозаика видна только внутри области (как у WPF-страницы с обрезкой по краю).</summary>
        private void CompClipPatches(Rect area)
        {
            if (!_comp) return;
            float l = (float)Math.Round(area.X * Kx), t = (float)Math.Round(area.Y * Ky);
            float r = (float)Math.Round(area.Right * Kx), b = (float)Math.Round(area.Bottom * Ky);
            _cPatchLayer.Clip = _cPatchLayer.Compositor.CreateInsetClip(l, t, Math.Max(0, _vs.Width - r), Math.Max(0, _vs.Height - b));
        }

        /// <summary>Повторить WPF-плитку мозаики на графическом слое (кусочки — по событиям плитки).</summary>
        private void CompMirror(Helpers.MosaicTiles patch, int cropX, int cropY)
        {
            if (!_comp) return;
            var c = _cPatchLayer.Compositor;
            var holder = c.CreateContainerVisual();
            holder.Offset = new Vector3(cropX, cropY, 0);
            holder.Size = new Vector2(_cropW, _cropH);
            _cPatchLayer.Children.InsertAtTop(holder);
            var tiles = new Dictionary<(int, int), (SpriteVisual vis, CompositionDrawingSurface surf, int w, int h)>();
            void Drop((int, int) key)
            {
                if (!tiles.TryGetValue(key, out var t)) return;
                holder.Children.Remove(t.vis);
                t.vis.Dispose(); t.surf.Dispose();
                tiles.Remove(key);
            }
            patch.TileSet += (key, pr, cells, cw, ch) =>
            {
                if (!_comp) return;
                // у квадратов мозаики «лишний» байт — не прозрачность: делаем непрозрачными
                var px = (byte[])cells.Clone();
                for (int i = 3; i < px.Length; i += 4) px[i] = 255;
                if (tiles.TryGetValue(key, out var t) && (t.w != cw || t.h != ch)) { Drop(key); t = default; }
                if (t.vis == null)
                {
                    var surf = Helpers.Comp.Device.CreateDrawingSurface(new Windows.Foundation.Size(cw, ch),
                        Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, Windows.Graphics.DirectX.DirectXAlphaMode.Premultiplied);
                    var brush = c.CreateSurfaceBrush(surf);
                    brush.Stretch = CompositionStretch.Fill;
                    brush.BitmapInterpolationMode = CompositionBitmapInterpolationMode.NearestNeighbor; // квадраты чёткие, как в WPF
                    var vis = c.CreateSpriteVisual();
                    vis.Brush = brush;
                    holder.Children.InsertAtTop(vis);
                    t = (vis, surf, cw, ch);
                    tiles[key] = t;
                }
                Helpers.Comp.Draw(t.surf, px, cw, ch, cw * 4);
                t.vis.Offset = new Vector3(pr.X, pr.Y, 0);
                t.vis.Size = new Vector2(pr.Width, pr.Height);
            };
            patch.TileGone += key => Drop(key);
            patch.Cleared += () => { foreach (var k in new List<(int, int)>(tiles.Keys)) Drop(k); };
            patch.Clipped += exact =>
            {
                holder.Clip = exact is Int32Rect e
                    ? c.CreateInsetClip(e.X, e.Y, Math.Max(0, _cropW - e.X - e.Width), Math.Max(0, _cropH - e.Y - e.Height))
                    : null;
            };
            _compPatches[patch] = holder;
        }

        private readonly Dictionary<Helpers.MosaicTiles, ContainerVisual> _compPatches = new();

        /// <summary>Убрать плитку мозаики (отмена) — и с WPF, и с графического слоя.</summary>
        private void RemovePatch(Helpers.MosaicTiles patch)
        {
            _patches.Children.Remove(patch);
            if (_compPatches.TryGetValue(patch, out var holder))
            {
                try { _cPatchLayer.Children.Remove(holder); holder.Dispose(); } catch { }
                _compPatches.Remove(patch);
            }
        }
    }
}
