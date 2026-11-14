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
        private ShapeVisual _cSelVis, _cFrameVis;
        private CompositionRectangleGeometry _cSelGeo, _cFrameGeo;

        private void BuildCompShapes(Compositor c)
        {
            float k = (float)Math.Max(1, Kx > 0 ? Kx : 1);
            _cSelGeo = c.CreateRectangleGeometry();
            var sel = c.CreateSpriteShape(_cSelGeo);
            sel.StrokeBrush = c.CreateColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));
            sel.StrokeThickness = 1.5f * k;                      // как у рамки WPF: 1,5 точки
            _cSelVis = c.CreateShapeVisual();
            _cSelVis.Size = new Vector2(_vs.Width, _vs.Height);
            _cSelVis.Shapes.Add(sel);
            _cSelVis.IsVisible = false;
            _cRoot.Children.InsertAtTop(_cSelVis);

            _cFrameGeo = c.CreateRectangleGeometry();
            var under = c.CreateSpriteShape(_cFrameGeo);         // чёрная линия под белым пунктиром — видна на любом фоне
            under.StrokeBrush = c.CreateColorBrush(Windows.UI.Color.FromArgb(255, 0, 0, 0));
            under.StrokeThickness = k;
            var dash = c.CreateSpriteShape(_cFrameGeo);
            dash.StrokeBrush = c.CreateColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));
            dash.StrokeThickness = k;
            dash.StrokeDashArray.Add(4); dash.StrokeDashArray.Add(4); // штрихи в толщинах линии — как у WPF (4, 4)
            _cFrameVis = c.CreateShapeVisual();
            _cFrameVis.Size = new Vector2(_vs.Width, _vs.Height);
            _cFrameVis.Shapes.Add(under);
            _cFrameVis.Shapes.Add(dash);
            _cFrameVis.IsVisible = false;
            _cRoot.Children.InsertAtTop(_cFrameVis);
        }

        private static void PlaceRect(CompositionRectangleGeometry g, float l, float t, float r, float b)
        {
            g.Offset = new Vector2(l, t);
            g.Size = new Vector2(Math.Max(1, r - l), Math.Max(1, b - t));
        }

        /// <summary>Рамка выделения (в точках слоя); null — спрятать.</summary>
        private void CompSel(Rect? r)
        {
            if (!_comp || _cSelVis == null) return;
            if (r is not Rect x || x.IsEmpty) { _cSelVis.IsVisible = false; return; }
            PlaceRect(_cSelGeo, (float)(x.X * Kx), (float)(x.Y * Ky), (float)(x.Right * Kx), (float)(x.Bottom * Ky));
            _cSelVis.IsVisible = true;
        }

        /// <summary>Пунктирная рамка мозаики (в точках страницы); null — спрятать.</summary>
        private void CompFrame(Rect? r)
        {
            if (!_comp || _cFrameVis == null) return;
            if (r is not Rect x) { _cFrameVis.IsVisible = false; return; }
            double ox = _area.X, oy = _area.Y;
            float l = (float)Math.Round((ox + x.X) * Kx) + 0.5f, t = (float)Math.Round((oy + x.Y) * Ky) + 0.5f;
            float rr = (float)Math.Round((ox + x.X + Math.Max(1, x.Width)) * Kx) - 0.5f, b = (float)Math.Round((oy + x.Y + Math.Max(1, x.Height)) * Ky) - 0.5f;
            PlaceRect(_cFrameGeo, l, t, rr, b);
            _cFrameVis.IsVisible = true;
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
