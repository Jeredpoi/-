using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Isle.Helpers;
using Isle.Services;
using ShapePath = System.Windows.Shapes.Path;

namespace Isle.Overlays
{
    /// <summary>
    /// Пометки прямо на месте снимка: выделил область — под ней сразу панель инструментов.
    /// Мозаика видна вживую, пока тянешь рамку. «Сохранить» (Enter) — снимок плавно улетает в чёлку.
    /// </summary>
    public partial class CaptureOverlay
    {
        private enum Mark { Mosaic, Rect, Arrow, Pen, Marker, Text }

        private static readonly (Mark mark, string name, string icon)[] MarkTools =
        {
            (Mark.Mosaic, "Мозаика — скрыть текст (видно сразу, пока тянешь)", "M4 4h16v16H4zM4 9.33h16M4 14.67h16M9.33 4v16M14.67 4v16"),
            (Mark.Rect, "Рамка", "M5 5.5h14a1.5 1.5 0 0 1 1.5 1.5v10a1.5 1.5 0 0 1-1.5 1.5H5A1.5 1.5 0 0 1 3.5 17V7A1.5 1.5 0 0 1 5 5.5z"),
            (Mark.Arrow, "Стрелка", "M5 19L19 5M10 5h9v9"),
            (Mark.Pen, "Карандаш", "M4 20l4.5-1 10-10a2.1 2.1 0 0 0-3-3l-10 10zM14 7l3 3"),
            (Mark.Marker, "Маркер", "M15 4.5l4.5 4.5-9 9H6v-4.5zM4 20.5h16"),
            (Mark.Text, "Текст", "M5 6.5V5h14v1.5M12 5v14M9.5 19h5"),
        };
        private static readonly Color[] MarkColors =
        {
            Color.FromRgb(0xFF, 0x4D, 0x4D), Color.FromRgb(0xFF, 0xE0, 0x4D), Color.FromRgb(0x4A, 0xDE, 0x80),
            Color.FromRgb(0x5A, 0xA9, 0xFF), Color.FromRgb(0xEC, 0xEC, 0xF1)
        };
        private static readonly Brush BarBg = Frz(Color.FromRgb(0x0E, 0x0E, 0x10));
        private static readonly Brush BarSel = Frz(Color.FromRgb(0x2A, 0x2A, 0x31));
        private static readonly Brush BarFg = Frz(Color.FromRgb(0xEC, 0xEC, 0xF1));

        private bool _markup;
        private Rect _area;                // выделенная область в координатах слоя
        private Grid _page;                // снимок + мозаика + пометки, стоит ровно на месте выделения
        private Image _baseImage;
        // Снимок — обычная «замороженная» картинка, как и весь экран под слоем. WriteableBitmap на 4K
        // на части видеокарт рисовался кусками: недорисованный кусок оставался чёрным прямоугольником.
        private BitmapSource _crop;
        private int _cropW, _cropH;
        private byte[] _cropPx;
        private Canvas _patches;           // плитки мозаики поверх снимка (у каждой свои квадраты — по её размеру)
        private Image _livePatch;          // плитка, которая тянется за мышью
        private Int32Rect _livePx;
        private Canvas _ink;
        private Rectangle _outline;
        private Border _bar;
        private readonly Dictionary<Mark, Border> _markButtons = new();
        private readonly List<Border> _colorDots = new();
        private readonly Stack<Action> _markUndo = new();
        private Mark _mark = Mark.Mosaic;
        private Color _markColor = MarkColors[0];
        private Point _from;
        private bool _drawing;
        private Shape _shape;
        private Polyline _line;
        private TextBox _text;
        // Рамка мозаики — 4 стороны отдельными тонкими линиями (каждая — чёрная под белым пунктиром). Слой рисуется
        // процессором и перерисовывает весь прямоугольник изменившегося элемента: рамка одним элементом заставляла
        // перерисовывать всю выделенную область на каждое движение мыши — отсюда лаги на большой мозаике.
        private Canvas _mosaicFrame;
        private readonly Line[] _frameLines = new Line[8];

        private void PlaceFrame(Rect r)
        {
            if (_mosaicFrame == null) return;
            double l = r.X + 0.5, t = r.Y + 0.5, rr = r.X + Math.Max(1, r.Width) - 0.5, b = r.Y + Math.Max(1, r.Height) - 0.5;
            for (int i = 0; i < 2; i++)
            {
                Set(_frameLines[i * 4 + 0], l, t, rr, t);  // верх
                Set(_frameLines[i * 4 + 1], l, b, rr, b);  // низ
                Set(_frameLines[i * 4 + 2], l, t, l, b);   // лево
                Set(_frameLines[i * 4 + 3], rr, t, rr, b); // право
            }
            static void Set(Line ln, double x1, double y1, double x2, double y2) { ln.X1 = x1; ln.Y1 = y1; ln.X2 = x2; ln.Y2 = y2; }
        }

        private static SolidColorBrush Frz(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        private double PxPerDip => _cropW / Math.Max(1, _area.Width);
        private double Ink => 3; // толщина линий (в точках экрана)

        /// <summary>Вместо мгновенного снимка — режим пометок на месте выделения.</summary>
        private void EnterMarkup(Rect area, BitmapSource crop)
        {
            if (_markup) return; // панель пометок — ровно одна
            _markup = true;
            _area = area;
            Cursor = Cursors.Arrow;
            _sel.Visibility = Visibility.Collapsed;

            _crop = crop;
            _cropW = crop.PixelWidth; _cropH = crop.PixelHeight;
            // выделенное место не затемнено — и до, и после появления панели
            UpdateDim(area);

            _page = new Grid { Width = area.Width, Height = area.Height, ClipToBounds = true, Background = Brushes.Transparent, Cursor = Cursors.Cross, RenderTransformOrigin = new Point(0.5, 0.5) };
            _baseImage = new Image { Source = _crop, Stretch = Stretch.Fill };
            _page.Children.Add(_baseImage);
            _patches = new Canvas { IsHitTestVisible = false };
            _page.Children.Add(_patches);
            _ink = new Canvas();
            _page.Children.Add(_ink);
            _outline = new Rectangle { Stroke = Brushes.White, StrokeThickness = 1.5, IsHitTestVisible = false };
            _page.Children.Add(_outline);
            Canvas.SetLeft(_page, area.X); Canvas.SetTop(_page, area.Y);
            _canvas.Children.Add(_page);
            _page.MouseLeftButtonDown += MarkDown;
            _page.MouseMove += MarkMove;
            _page.MouseLeftButtonUp += MarkUp;
            // мышь отпустили за пределами окна или захват потерян (Alt+Tab) — завершаем рисование как обычное отпускание
            _page.LostMouseCapture += (_, e) => { if (_drawing) MarkUp(_page, new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent }); };

            // Полноэкранное непрозрачное окно с GPU-отрисовкой на части видеокарт и сборок Windows (в т. ч. Insider)
            // оставляет чёрные прямоугольники там, где картинка перерисовывается кусками, — а при рисовании так и есть.
            // На время пометок рисуем процессором: меняются только маленькие участки, это быстро и без артефактов.
            try
            {
                if (PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource hs && hs.CompositionTarget != null)
                {
                    hs.CompositionTarget.RenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
                    Journal.Info("capture", "пометки: программная отрисовка (без чёрных прямоугольников GPU)");
                }
            }
            catch (Exception ex) { Journal.Warn("capture", "не удалось включить программную отрисовку: " + ex.Message); }

            BuildBar();
            BuildHandles();
            _hintText.Text = "Рисуй прямо на снимке · уголки растягивают область · Enter — сохранить · Esc — отмена";
        }

        // ---------- панель ----------

        private void BuildBar()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (mark, name, icon) in MarkTools)
            {
                var m = mark;
                var b = BarButton(new Helpers.Icon { Data = icon, Width = 18, Height = 18, Foreground = BarFg }, name, () => SelectMark(m));
                _markButtons[m] = b;
                row.Children.Add(b);
            }
            row.Children.Add(BarDivider());
            for (int i = 0; i < MarkColors.Length; i++)
            {
                int ci = i;
                var dot = new Border
                {
                    Width = 24, Height = 24, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(2), Margin = new Thickness(2, 0, 2, 0),
                    Background = Brushes.Transparent, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
                    Child = new Ellipse { Width = 14, Height = 14, Fill = Frz(MarkColors[i]) }
                };
                dot.MouseLeftButtonUp += (_, e) => { SelectColor(ci); e.Handled = true; };
                _colorDots.Add(dot);
                row.Children.Add(dot);
            }
            row.Children.Add(BarDivider());
            row.Children.Add(BarButton(new Helpers.Icon { Data = "M9 14L4 9l5-5M4 9h10.5a5.5 5.5 0 0 1 0 11H11", Width = 17, Height = 17, Foreground = BarFg }, "Отменить (Ctrl+Z)", MarkUndo));
            row.Children.Add(BarButton(new Helpers.Icon { Data = Icons.Close, Width = 16, Height = 16, Foreground = BarFg }, "Отмена (Esc)", () => Finish(null)));
            var save = new Border
            {
                Height = 34, CornerRadius = new CornerRadius(17), Background = BarFg, Padding = new Thickness(16, 0, 16, 0), Margin = new Thickness(6, 0, 0, 0),
                Cursor = Cursors.Hand, ToolTip = "Сохранить и скопировать (Enter)", VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "Сохранить", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.Black, VerticalAlignment = VerticalAlignment.Center }
            };
            save.MouseLeftButtonUp += (_, e) => { e.Handled = true; SaveMarkup(); };
            row.Children.Add(save);

            _bar = new Border
            {
                Background = BarBg, CornerRadius = new CornerRadius(18), Padding = new Thickness(8, 6, 6, 6),
                BorderBrush = Frz(Color.FromRgb(0x3A, 0x3A, 0x44)), BorderThickness = new Thickness(1),
                Child = row, RenderTransformOrigin = new Point(0.5, 0)
            };
            // без DropShadowEffect: на части видеокарт/масштабов WPF рисует тень жёстким чёрным прямоугольником вокруг панели
            _bar.MouseLeftButtonDown += (_, e) => e.Handled = true; // клик по панели не рисует и не начинает новое выделение
            _canvas.Children.Add(_bar);
            _bar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var sz = _bar.DesiredSize;
            var (x, y, below) = PlaceBar();
            try
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                App.Trace($"пометки: экран {_vs.X},{_vs.Y} {_vs.Width}×{_vs.Height}, окно {ActualWidth:0}×{ActualHeight:0}, масштаб {dpi.DpiScaleX:0.##}, область {_area.X:0},{_area.Y:0} {_area.Width:0}×{_area.Height:0}, панель {sz.Width:0}×{sz.Height:0} в {x:0},{y:0}, слоёв {_openCount}");
            }
            catch { }

            // панель выезжает с лёгкой пружиной
            var st = new ScaleTransform(0.9, 0.9);
            var tt = new TranslateTransform(0, below ? -8 : 8);
            var g = new TransformGroup(); g.Children.Add(st); g.Children.Add(tt);
            _bar.RenderTransform = g;
            var ease = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut };
            var d = TimeSpan.FromMilliseconds(320);
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.9, 1, d) { EasingFunction = ease });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.9, 1, d) { EasingFunction = ease });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(below ? -8 : 8, 0, d) { EasingFunction = ease });
            _bar.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));

            SelectMark(Mark.Mosaic);
            SelectColor(0);
        }

        /// <summary>Панель — под областью, если влезает; иначе над ней; иначе внутри у нижнего края.</summary>
        private (double x, double y, bool below) PlaceBar()
        {
            var sz = _bar.DesiredSize;
            double y = _area.Bottom + 14;
            bool below = y + sz.Height <= ActualHeight - 8;
            if (!below) y = _area.Top - 14 - sz.Height;
            if (y < 8) y = _area.Bottom - sz.Height - 12;
            double x = Math.Clamp(_area.X + (_area.Width - sz.Width) / 2, 8, Math.Max(8, ActualWidth - sz.Width - 8));
            Canvas.SetLeft(_bar, x); Canvas.SetTop(_bar, y);
            return (x, y, below);
        }

        // ---------- уголки: область можно растянуть или сузить и после выделения ----------

        private readonly List<Border> _handles = new();
        private int _dragHandle = -1;
        private Point _dragFrom;
        private Rect _dragArea;
        // 0 ↖, 1 ↑, 2 ↗, 3 →, 4 ↘, 5 ↓, 6 ↙, 7 ←
        private static readonly (double fx, double fy)[] HandleAt = { (0, 0), (0.5, 0), (1, 0), (1, 0.5), (1, 1), (0.5, 1), (0, 1), (0, 0.5) };

        private void BuildHandles()
        {
            Cursor[] cursors = { Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW, Cursors.SizeWE, Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW, Cursors.SizeWE };
            for (int i = 0; i < 8; i++)
            {
                int idx = i;
                var h = new Border
                {
                    Width = 11, Height = 11, CornerRadius = new CornerRadius(3), Background = Brushes.White,
                    BorderBrush = Frz(Color.FromArgb(200, 0, 0, 0)), BorderThickness = new Thickness(1), Cursor = cursors[i]
                };
                h.MouseLeftButtonDown += (_, e) =>
                {
                    e.Handled = true;
                    CommitMarkText();
                    _dragHandle = idx; _dragFrom = e.GetPosition(_canvas); _dragArea = _area;
                    h.CaptureMouse();
                };
                h.MouseMove += (_, e) =>
                {
                    if (_dragHandle != idx || !h.IsMouseCaptured) return;
                    var p = e.GetPosition(_canvas);
                    double dx = p.X - _dragFrom.X, dy = p.Y - _dragFrom.Y;
                    double l = _dragArea.Left, t = _dragArea.Top, r = _dragArea.Right, b = _dragArea.Bottom;
                    var (fx, fy) = HandleAt[idx];
                    if (fx == 0) l += dx; else if (fx == 1) r += dx;
                    if (fy == 0) t += dy; else if (fy == 1) b += dy;
                    l = Math.Clamp(l, 0, ActualWidth); r = Math.Clamp(r, 0, ActualWidth);
                    t = Math.Clamp(t, 0, ActualHeight); b = Math.Clamp(b, 0, ActualHeight);
                    const double min = 16;
                    if (r - l < min) { if (fx == 0) l = r - min; else r = l + min; }
                    if (b - t < min) { if (fy == 0) t = b - min; else b = t + min; }
                    SetArea(new Rect(l, t, r - l, b - t));
                };
                h.MouseLeftButtonUp += (_, e) =>
                {
                    if (_dragHandle != idx) return;
                    _dragHandle = -1;
                    h.ReleaseMouseCapture();
                    e.Handled = true;
                    Journal.Info("capture", "область изменена: " + _area.X.ToString("0") + "," + _area.Y.ToString("0") + " " + _area.Width.ToString("0") + "×" + _area.Height.ToString("0"));
                };
                _handles.Add(h);
                _canvas.Children.Add(h);
            }
            PlaceHandles();
        }

        private void PlaceHandles()
        {
            for (int i = 0; i < _handles.Count; i++)
            {
                var (fx, fy) = HandleAt[i];
                Canvas.SetLeft(_handles[i], _area.X + _area.Width * fx - 5.5);
                Canvas.SetTop(_handles[i], _area.Y + _area.Height * fy - 5.5);
            }
        }

        /// <summary>
        /// Новая область: снимок под ней берётся заново из «замороженного» экрана, пометки и плитки мозаики
        /// остаются на своих местах на экране (сдвигаются внутри страницы на разницу начала).
        /// </summary>
        private void SetArea(Rect a)
        {
            var crop = new Int32Rect(
                (int)Math.Clamp(a.X * Kx, 0, _shot.PixelWidth - 1), (int)Math.Clamp(a.Y * Ky, 0, _shot.PixelHeight - 1), 0, 0);
            crop.Width = Math.Max(1, Math.Min(_shot.PixelWidth - crop.X, (int)Math.Round(a.Width * Kx)));
            crop.Height = Math.Max(1, Math.Min(_shot.PixelHeight - crop.Y, (int)Math.Round(a.Height * Ky)));
            var cb = new CroppedBitmap(_shot, crop);
            cb.Freeze();

            double shiftX = _area.X - a.X, shiftY = _area.Y - a.Y;
            if (shiftX != 0 || shiftY != 0)
                foreach (var host in new Panel[] { _ink, _patches })
                    foreach (UIElement el in host.Children)
                    {
                        double ex = Canvas.GetLeft(el), ey = Canvas.GetTop(el);
                        Canvas.SetLeft(el, (double.IsNaN(ex) ? 0 : ex) + shiftX);
                        Canvas.SetTop(el, (double.IsNaN(ey) ? 0 : ey) + shiftY);
                    }

            _area = a;
            _crop = cb; _cropW = crop.Width; _cropH = crop.Height;
            _cropPx = null;                                    // пиксели для мозаики возьмутся из новой области
            _baseImage.Source = cb;
            _page.Width = a.Width; _page.Height = a.Height;
            Canvas.SetLeft(_page, a.X); Canvas.SetTop(_page, a.Y);
            UpdateDim(a);
            PlaceHandles();
            PlaceBar();
        }

        private static Border BarButton(UIElement content, string tip, Action click)
        {
            var b = new Border
            {
                Width = 36, Height = 34, CornerRadius = new CornerRadius(11), Background = Brushes.Transparent, Cursor = Cursors.Hand,
                ToolTip = tip, Margin = new Thickness(1, 0, 1, 0), Child = content
            };
            b.MouseEnter += (_, __) => { if (b.Tag == null) b.Background = Frz(Color.FromRgb(0x1C, 0x1C, 0x21)); };
            b.MouseLeave += (_, __) => { if (b.Tag == null) b.Background = Brushes.Transparent; };
            b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
            return b;
        }

        private static Border BarDivider() => new() { Width = 1, Height = 20, Background = Frz(Color.FromRgb(0x2A, 0x2A, 0x31)), Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };

        private void SelectMark(Mark m)
        {
            CommitMarkText();
            _mark = m;
            foreach (var (k, b) in _markButtons)
            {
                bool on = k == m;
                b.Tag = on ? "on" : null;
                b.Background = on ? BarSel : Brushes.Transparent;
            }
            if (_page != null) _page.Cursor = m == Mark.Text ? Cursors.IBeam : Cursors.Cross;
        }

        private void SelectColor(int i)
        {
            _markColor = MarkColors[i];
            for (int k = 0; k < _colorDots.Count; k++) _colorDots[k].BorderBrush = k == i ? BarFg : Brushes.Transparent;
            if (_text != null) _text.Foreground = Frz(_markColor);
        }

        // ---------- рисование ----------

        private Point PagePoint(MouseEventArgs e)
        {
            var p = e.GetPosition(_page);
            return new Point(Math.Clamp(p.X, 0, _area.Width), Math.Clamp(p.Y, 0, _area.Height));
        }

        private void MarkDown(object s, MouseButtonEventArgs e)
        {
            e.Handled = true;
            var p = PagePoint(e);
            if (_text != null) { CommitMarkText(); return; }
            if (_mark == Mark.Text) { StartMarkText(p); return; }
            _from = p;
            _drawing = true;
            _page.CaptureMouse();
            var brush = Frz(_markColor);
            switch (_mark)
            {
                case Mark.Mosaic:
                    EnsureMosaic();
                    _livePatch = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
                    RenderOptions.SetBitmapScalingMode(_livePatch, BitmapScalingMode.NearestNeighbor);
                    _patches.Children.Add(_livePatch);
                    _livePx = Int32Rect.Empty;
                    // рамка выделения: тонкий чёрный контур и белый пунктир поверх — видна на любом фоне, ничего не затемняет
                    _mosaicFrame = new Canvas { IsHitTestVisible = false };
                    for (int i = 0; i < 8; i++)
                    {
                        _frameLines[i] = i < 4
                            ? new Line { Stroke = Brushes.Black, StrokeThickness = 1 }
                            : new Line { Stroke = Brushes.White, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 4 } };
                        _mosaicFrame.Children.Add(_frameLines[i]);
                    }
                    _ink.Children.Add(_mosaicFrame);
                    PlaceFrame(new Rect(p, p));
                    break;
                case Mark.Rect:
                    _shape = new Rectangle { Stroke = brush, StrokeThickness = Ink, RadiusX = 4, RadiusY = 4 };
                    _ink.Children.Add(_shape);
                    break;
                case Mark.Arrow:
                    _shape = new ShapePath { Stroke = brush, Fill = brush, StrokeThickness = Ink, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                    _ink.Children.Add(_shape);
                    break;
                case Mark.Pen:
                case Mark.Marker:
                    bool marker = _mark == Mark.Marker;
                    _line = new Polyline
                    {
                        Stroke = marker ? Frz(Color.FromArgb(150, _markColor.R, _markColor.G, _markColor.B)) : brush,
                        StrokeThickness = marker ? 14 : Ink, StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = marker ? PenLineCap.Flat : PenLineCap.Round, StrokeEndLineCap = marker ? PenLineCap.Flat : PenLineCap.Round
                    };
                    _line.Points.Add(p);
                    _ink.Children.Add(_line);
                    break;
            }
        }

        private void MarkMove(object s, MouseEventArgs e)
        {
            if (!_drawing) return;
            var p = PagePoint(e);
            var r = new Rect(_from, p);
            switch (_mark)
            {
                case Mark.Mosaic:
                    // плитка мозаики тянется за мышью — квадраты видны сразу
                    ShowPatch(r);
                    PlaceFrame(r);
                    break;
                case Mark.Rect when _shape != null:
                    Canvas.SetLeft(_shape, r.X); Canvas.SetTop(_shape, r.Y); _shape.Width = r.Width; _shape.Height = r.Height;
                    break;
                case Mark.Arrow when _shape is ShapePath arrow:
                    arrow.Data = Arrow(_from, p, Ink);
                    break;
                case Mark.Pen when _line != null:
                case Mark.Marker when _line != null:
                    _line.Points.Add(p);
                    break;
            }
        }

        private void MarkUp(object s, MouseButtonEventArgs e)
        {
            if (!_drawing) return;
            _drawing = false;
            _page.ReleaseMouseCapture();
            var r = new Rect(_from, PagePoint(e));
            switch (_mark)
            {
                case Mark.Mosaic:
                    if (_mosaicFrame != null) { _ink.Children.Remove(_mosaicFrame); _mosaicFrame = null; }
                    if (_livePatch != null)
                    {
                        var patch = _livePatch; _livePatch = null;
                        ShowPatch(r, patch, force: true);
                        if (r.Width >= 3 && r.Height >= 3 && patch.Source != null)
                            _markUndo.Push(() => _patches.Children.Remove(patch)); // итоговая плитка — та же, что была видна
                        else _patches.Children.Remove(patch);
                    }
                    break;
                case Mark.Rect:
                case Mark.Arrow:
                    var shape = _shape; _shape = null;
                    if (shape == null) break;
                    if (r.Width < 4 && r.Height < 4) { _ink.Children.Remove(shape); break; }
                    _markUndo.Push(() => _ink.Children.Remove(shape));
                    break;
                case Mark.Pen:
                case Mark.Marker:
                    var line = _line; _line = null;
                    if (line == null) break;
                    if (line.Points.Count < 2) { _ink.Children.Remove(line); break; }
                    _markUndo.Push(() => _ink.Children.Remove(line));
                    break;
            }
        }

        /// <summary>Пиксели снимка для мозаики — копируются один раз, при первой мозаике.</summary>
        private void EnsureMosaic()
        {
            _cropPx ??= ScreenTools.Bgra(_crop, out _, out _);
        }

        private long _patchAt; // когда плитка обновлялась последний раз — не чаще кадра

        /// <summary>
        /// Плитка мозаики на месте рамки r (в точках снимка). Размер квадратов — по размеру рамки: мелкое сообщение
        /// закрывается мелкими квадратами, большой блок — крупными. Квадраты считаются прямо для этой рамки
        /// (крошечная картинка «квадрат = пиксель», растянутая без сглаживания), поэтому даже огромная рамка не тормозит.
        /// </summary>
        private void ShowPatch(Rect r, Image patch = null, bool force = false)
        {
            patch ??= _livePatch;
            if (patch == null || _cropPx == null) return;
            long now = Environment.TickCount64;
            if (!force && now - _patchAt < 30) return; // ~30 раз в секунду хватает глазу, процессору — легче
            double k = PxPerDip;
            int x0 = Math.Clamp((int)Math.Floor(r.X * k), 0, _cropW), y0 = Math.Clamp((int)Math.Floor(r.Y * k), 0, _cropH);
            int x1 = Math.Clamp((int)Math.Ceiling(r.Right * k), 0, _cropW), y1 = Math.Clamp((int)Math.Ceiling(r.Bottom * k), 0, _cropH);
            if (x1 - x0 <= 0 || y1 - y0 <= 0) { patch.Source = null; return; }
            int block = Mosaic.BlockForPatch(x1 - x0, y1 - y0);
            if (!force)
            {
                // пока тянешь — плитка растёт целыми квадратами: пересчёт и перерисовка только при переходе на новый квадрат
                x1 = Math.Min(_cropW, x0 + (x1 - x0 + block - 1) / block * block);
                y1 = Math.Min(_cropH, y0 + (y1 - y0 + block - 1) / block * block);
            }
            var ir = new Int32Rect(x0, y0, x1 - x0, y1 - y0);
            if (ir.Equals(_livePx) && patch.Source != null && !force) return;
            _patchAt = now;
            var cells = Mosaic.Cells(_cropPx, _cropW, _cropH, ir.X, ir.Y, ir.Width, ir.Height, block, out int cw, out int ch);
            var src = BitmapSource.Create(cw, ch, 96, 96, PixelFormats.Bgr32, null, cells, cw * 4);
            src.Freeze();
            patch.Source = src;
            _livePx = ir;
            // плитка ровно по рамке, без обрезки (обрезка у процессорной отрисовки дорогая); последний ряд квадратов чуть уже
            Canvas.SetLeft(patch, x0 / k); Canvas.SetTop(patch, y0 / k);
            patch.Width = ir.Width / k; patch.Height = ir.Height / k;
            patch.Clip = null;
        }

        private static Geometry Arrow(Point a, Point b, double t)
        {
            var g = new StreamGeometry();
            var v = b - a;
            if (v.Length < 1) return g;
            v.Normalize();
            var n = new Vector(-v.Y, v.X);
            double head = Math.Max(t * 4.5, 13);
            var basePt = b - v * head;
            using (var c = g.Open())
            {
                c.BeginFigure(a, false, false);
                c.LineTo(basePt, true, true);
                c.BeginFigure(b, true, true);
                c.LineTo(basePt + n * head * 0.55, true, true);
                c.LineTo(basePt - n * head * 0.55, true, true);
            }
            g.Freeze();
            return g;
        }

        // ---------- текст ----------

        private void StartMarkText(Point at)
        {
            _text = new TextBox
            {
                MinWidth = 40, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Frz(_markColor),
                Background = Frz(Color.FromArgb(90, 0, 0, 0)), BorderBrush = Frz(Color.FromArgb(160, 255, 255, 255)), BorderThickness = new Thickness(1),
                CaretBrush = Brushes.White, Padding = new Thickness(3, 0, 3, 0), AcceptsReturn = false
            };
            Canvas.SetLeft(_text, Math.Min(at.X, Math.Max(0, _area.Width - 60)));
            Canvas.SetTop(_text, Math.Max(0, at.Y - 14));
            _ink.Children.Add(_text);
            _text.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitMarkText(); e.Handled = true; } };
            Dispatcher.BeginInvoke(new Action(() => { _text?.Focus(); if (_text != null) Keyboard.Focus(_text); }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void CommitMarkText()
        {
            if (_text == null) return;
            var box = _text; _text = null;
            _ink.Children.Remove(box);
            if (string.IsNullOrWhiteSpace(box.Text)) { Keyboard.Focus(this); return; }
            var tb = new TextBlock
            {
                Text = box.Text, FontSize = box.FontSize, FontWeight = box.FontWeight, Foreground = box.Foreground, Padding = new Thickness(5, 1, 5, 2),
                // полупрозрачная подложка вместо тени — читается на любом фоне и не даёт чёрных прямоугольников
                Background = Frz(Color.FromArgb(140, 0, 0, 0))
            };
            Canvas.SetLeft(tb, Canvas.GetLeft(box)); Canvas.SetTop(tb, Canvas.GetTop(box));
            _ink.Children.Add(tb);
            _markUndo.Push(() => _ink.Children.Remove(tb));
            Keyboard.Focus(this);
        }

        private void MarkUndo()
        {
            if (_text != null) { _ink.Children.Remove(_text); _text = null; Keyboard.Focus(this); return; }
            if (_markUndo.Count > 0) _markUndo.Pop()();
        }

        /// <summary>Esc: сначала отменяет набираемый текст, потом — весь снимок.</summary>
        private void OnEscape()
        {
            if (_markup && _text != null) { MarkUndo(); return; }
            Finish(null);
        }

        private void OnMarkupKey(KeyEventArgs e)
        {
            if (!_markup || _text != null) return;
            if (e.Key == Key.Enter) { SaveMarkup(); e.Handled = true; }
            else if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { MarkUndo(); e.Handled = true; }
        }

        // ---------- сохранение и полёт в чёлку ----------

        private bool _saving;

        private void SaveMarkup()
        {
            if (_saving) return;
            CommitMarkText();
            _saving = true;
            int w = _cropW, h = _cropH;
            _outline.Visibility = Visibility.Hidden;
            _page.UpdateLayout();
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                dc.DrawRectangle(new VisualBrush(_page) { Stretch = Stretch.Fill }, null, new Rect(0, 0, w, h));
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            _outline.Visibility = Visibility.Visible;
            FlyToIsland(() => Finish(rtb), rtb);
        }

        /// <summary>Снимок сжимается и улетает в чёлку (верх основного экрана), затемнение тает.</summary>
        private void FlyToIsland(Action done, BitmapSource flat = null)
        {
            foreach (var h in _handles) h.Visibility = Visibility.Collapsed;
            // Летит одна готовая картинка, а не весь слой с пометками и плитками мозаики — так анимации нечего пересчитывать.
            // Режим отрисовки НЕ переключаем: смена на ходу пересоздаёт поверхность окна — экран мигал белой пеленой.
            if (flat != null)
            {
                _page.Children.Clear();
                var img = new Image { Source = flat, Stretch = Stretch.Fill };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.LowQuality); // в полёте — быстрое масштабирование
                _page.Children.Add(img);
            }
            _escPoll.Stop();
            IsHitTestVisible = false;
            // центр чёлки: верх основного монитора по центру (в координатах слоя)
            double primaryW = Isle.Native.Win32.GetSystemMetrics(0);
            var target = new Point((primaryW / 2 - _vs.X) / Kx, (0 - _vs.Y) / Ky + 20);
            var center = new Point(_area.X + _area.Width / 2, _area.Y + _area.Height / 2);
            double scale = Math.Clamp(44 / Math.Max(_area.Width, _area.Height), 0.02, 1);

            var st = new ScaleTransform(1, 1);
            var tt = new TranslateTransform();
            var g = new TransformGroup(); g.Children.Add(st); g.Children.Add(tt);
            _page.RenderTransform = g;
            _page.Clip = new RectangleGeometry(new Rect(0, 0, _area.Width, _area.Height), 0, 0);

            var dur = TimeSpan.FromMilliseconds(520);
            var move = new CubicEase { EasingMode = EasingMode.EaseInOut };
            // сжимается сразу (большой снимок перерисовывать дорого), а летит плавно
            var shrink = new CubicEase { EasingMode = EasingMode.EaseOut };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, scale, dur) { EasingFunction = shrink });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, scale, dur) { EasingFunction = shrink });
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, target.X - center.X, dur) { EasingFunction = move });
            var fly = new DoubleAnimation(0, target.Y - center.Y, dur) { EasingFunction = move };
            // углы скругляются по мере полёта — к чёлке подлетает «плитка»
            double r = Math.Min(_area.Width, _area.Height) / 2;
            ((RectangleGeometry)_page.Clip).BeginAnimation(RectangleGeometry.RadiusXProperty, new DoubleAnimation(0, Math.Min(r, 14 / scale), dur));
            ((RectangleGeometry)_page.Clip).BeginAnimation(RectangleGeometry.RadiusYProperty, new DoubleAnimation(0, Math.Min(r, 14 / scale), dur));
            _page.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140)) { BeginTime = TimeSpan.FromMilliseconds(400) });
            // панель, подсказка и затемнение — сразу: плавное таяние затемнения на весь 4K-экран процессор рисует рывками
            _bar.Visibility = Visibility.Hidden;
            _hint.Visibility = Visibility.Hidden;
            _dim.Visibility = Visibility.Hidden;
            fly.Completed += (_, __) => done();
            tt.BeginAnimation(TranslateTransform.YProperty, fly);
        }
    }
}
