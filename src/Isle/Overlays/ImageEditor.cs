using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Isle.Helpers;
using Isle.Services;
using ShapePath = System.Windows.Shapes.Path;

namespace Isle.Overlays
{
    /// <summary>
    /// Пометки на скриншоте: пикселизация (замазать текст), рамка, стрелка, маркер.
    /// «Готово» — снимок с пометками в буфер и в тот же файл.
    /// </summary>
    public class ImageEditor : Window
    {
        private enum Tool { Pixelate, Rect, Arrow, Marker }

        private readonly WriteableBitmap _bmp;
        private readonly string _path;
        private readonly Grid _page;
        private readonly Canvas _overlay;
        private readonly Stack<Action> _undo = new();
        private readonly Dictionary<Tool, Button> _toolButtons = new();
        private Tool _tool = Tool.Pixelate;
        private Point _start;
        private Shape _preview;
        private Polyline _stroke;
        private readonly double _thick;
        private static readonly Color Red = Color.FromRgb(0xFF, 0x4D, 0x4D);

        public ImageEditor(BitmapSource source, string savedPath)
        {
            _path = savedPath;
            var conv = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            _bmp = new WriteableBitmap(conv);
            int w = _bmp.PixelWidth, h = _bmp.PixelHeight;
            _thick = Math.Max(3, Math.Max(w, h) / 380.0);

            Title = "Isle — пометки на снимке";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = true;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            var wa = SystemParameters.WorkArea;
            Width = Math.Min(wa.Width * 0.86, Math.Max(640, w + 40));
            Height = Math.Min(wa.Height * 0.88, Math.Max(420, h + 110));

            _page = new Grid { Width = w, Height = h, ClipToBounds = true, Cursor = Cursors.Cross, Background = Brushes.Transparent };
            _page.Children.Add(new Image { Source = _bmp, Width = w, Height = h, Stretch = Stretch.Fill });
            _overlay = new Canvas { Width = w, Height = h, IsHitTestVisible = false };
            _page.Children.Add(_overlay);
            _page.MouseLeftButtonDown += OnDown;
            _page.MouseMove += OnMove;
            _page.MouseLeftButtonUp += OnUp;

            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            toolbar.Children.Add(ToolButton(Tool.Pixelate, "Scan", "Замазать"));
            toolbar.Children.Add(ToolButton(Tool.Rect, "Crop", "Рамка"));
            toolbar.Children.Add(ToolButton(Tool.Arrow, "Send", "Стрелка"));
            toolbar.Children.Add(ToolButton(Tool.Marker, "Note", "Маркер"));
            toolbar.Children.Add(new Border { Width = 1, Height = 22, Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x30)), Margin = new Thickness(8, 0, 8, 0) });
            toolbar.Children.Add(ActionButton("Restart", "Отменить", Undo, "Отменить последнее (Ctrl+Z)"));
            toolbar.Children.Add(ActionButton("Check", "Готово", Done, "Скопировать снимок с пометками (Enter)", primary: true));
            toolbar.Children.Add(ActionButton("Close", "", Close, "Закрыть без сохранения (Esc)"));

            var root = new DockPanel { LastChildFill = true };
            var top = new Border { Padding = new Thickness(10, 10, 10, 8), Child = toolbar };
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);
            root.Children.Add(new Border { Padding = new Thickness(14, 0, 14, 14), Child = new Viewbox { Child = _page, StretchDirection = StretchDirection.DownOnly } });

            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x0D)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x23, 0x23, 0x2A)), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(18), Child = root
            };
            top.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) Close();
                else if (e.Key == Key.Enter) Done();
                else if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) Undo();
            };
            Loaded += (_, __) => { Activate(); Focus(); };
            SelectTool(Tool.Pixelate);
        }

        private Button ToolButton(Tool tool, string icon, string text)
        {
            var b = ActionButton(icon, text, () => SelectTool(tool), null);
            _toolButtons[tool] = b;
            return b;
        }

        private Button ActionButton(string icon, string text, Action click, string tip, bool primary = false)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new Icon { Kind = icon, Width = 14, Height = 14, Foreground = primary ? Brushes.Black : Brushes.White });
            if (text.Length > 0) content.Children.Add(new TextBlock { Text = text, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
            var b = new Button
            {
                Content = content, Margin = new Thickness(3, 0, 3, 0), Height = 34, Padding = new Thickness(12, 0, 12, 0), ToolTip = tip,
                Style = (Style)Application.Current.FindResource(primary ? "Primary" : "Flat")
            };
            b.Click += (_, __) => click();
            return b;
        }

        private void SelectTool(Tool t)
        {
            _tool = t;
            foreach (var (tool, b) in _toolButtons)
                b.Background = new SolidColorBrush(tool == t ? Color.FromRgb(0x26, 0x26, 0x2D) : Color.FromRgb(0x0E, 0x0E, 0x10));
        }

        private Point Clamp(Point p) => new(Math.Clamp(p.X, 0, _bmp.PixelWidth), Math.Clamp(p.Y, 0, _bmp.PixelHeight));

        private void OnDown(object s, MouseButtonEventArgs e)
        {
            _start = Clamp(e.GetPosition(_page));
            _page.CaptureMouse();
            var brush = new SolidColorBrush(Red);
            switch (_tool)
            {
                case Tool.Pixelate:
                    _preview = new Rectangle { Stroke = Brushes.White, StrokeThickness = Math.Max(1.5, _thick / 2), StrokeDashArray = new DoubleCollection { 4, 3 }, Fill = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)) };
                    break;
                case Tool.Rect:
                    _preview = new Rectangle { Stroke = brush, StrokeThickness = _thick, RadiusX = _thick * 1.5, RadiusY = _thick * 1.5 };
                    break;
                case Tool.Arrow:
                    _preview = new ShapePath { Stroke = brush, StrokeThickness = _thick, Fill = brush, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                    break;
                case Tool.Marker:
                    _stroke = new Polyline
                    {
                        Stroke = new SolidColorBrush(Color.FromArgb(150, 0xFF, 0xE0, 0x4D)), StrokeThickness = _thick * 4,
                        StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
                    };
                    _stroke.Points.Add(_start);
                    _overlay.Children.Add(_stroke);
                    return;
            }
            _overlay.Children.Add(_preview);
            Update(_start);
        }

        private void OnMove(object s, MouseEventArgs e)
        {
            if (!_page.IsMouseCaptured) return;
            var p = Clamp(e.GetPosition(_page));
            if (_stroke != null) { _stroke.Points.Add(p); return; }
            Update(p);
        }

        private void Update(Point p)
        {
            if (_preview is Rectangle r)
            {
                var rect = new Rect(_start, p);
                Canvas.SetLeft(r, rect.X); Canvas.SetTop(r, rect.Y); r.Width = rect.Width; r.Height = rect.Height;
            }
            else if (_preview is ShapePath path) path.Data = ArrowGeometry(_start, p, _thick);
        }

        private void OnUp(object s, MouseButtonEventArgs e)
        {
            if (!_page.IsMouseCaptured) return;
            _page.ReleaseMouseCapture();
            var p = Clamp(e.GetPosition(_page));
            if (_stroke != null)
            {
                var line = _stroke; _stroke = null;
                if (line.Points.Count < 2) { _overlay.Children.Remove(line); return; }
                _undo.Push(() => _overlay.Children.Remove(line));
                return;
            }
            var shape = _preview; _preview = null;
            if (shape == null) return;
            var rect = new Rect(_start, p);
            if (rect.Width < 4 && rect.Height < 4) { _overlay.Children.Remove(shape); return; }
            if (_tool == Tool.Pixelate)
            {
                _overlay.Children.Remove(shape);
                Pixelate(new Int32Rect((int)rect.X, (int)rect.Y, Math.Max(1, (int)rect.Width), Math.Max(1, (int)rect.Height)));
            }
            else _undo.Push(() => _overlay.Children.Remove(shape));
        }

        /// <summary>Крупные «пиксели» вместо выделенного куска — текст под ними не прочитать.</summary>
        private void Pixelate(Int32Rect r)
        {
            r.Width = Math.Min(r.Width, _bmp.PixelWidth - r.X);
            r.Height = Math.Min(r.Height, _bmp.PixelHeight - r.Y);
            if (r.Width <= 0 || r.Height <= 0) return;
            int stride = r.Width * 4;
            var px = new byte[stride * r.Height];
            _bmp.CopyPixels(r, px, stride, 0);
            var backup = (byte[])px.Clone();
            int block = Math.Max(8, (int)(Math.Max(_bmp.PixelWidth, _bmp.PixelHeight) / 90.0));
            for (int by = 0; by < r.Height; by += block)
                for (int bx = 0; bx < r.Width; bx += block)
                {
                    int bw = Math.Min(block, r.Width - bx), bh = Math.Min(block, r.Height - by);
                    long sb = 0, sg = 0, sr = 0; int n = bw * bh;
                    for (int y = 0; y < bh; y++)
                        for (int x = 0; x < bw; x++)
                        {
                            int i = (by + y) * stride + (bx + x) * 4;
                            sb += px[i]; sg += px[i + 1]; sr += px[i + 2];
                        }
                    byte b = (byte)(sb / n), g = (byte)(sg / n), rr = (byte)(sr / n);
                    for (int y = 0; y < bh; y++)
                        for (int x = 0; x < bw; x++)
                        {
                            int i = (by + y) * stride + (bx + x) * 4;
                            px[i] = b; px[i + 1] = g; px[i + 2] = rr; px[i + 3] = 255;
                        }
                }
            _bmp.WritePixels(r, px, stride, 0);
            _undo.Push(() => _bmp.WritePixels(r, backup, stride, 0));
        }

        private static Geometry ArrowGeometry(Point a, Point b, double t)
        {
            var g = new StreamGeometry();
            var v = b - a;
            double len = v.Length;
            if (len < 1) return g;
            v.Normalize();
            var n = new Vector(-v.Y, v.X);
            double head = Math.Max(t * 5, 14);
            var basePt = b - v * head;
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(a, false, false);
                ctx.LineTo(basePt, true, true);
                ctx.BeginFigure(b, true, true);
                ctx.LineTo(basePt + n * head * 0.55, true, true);
                ctx.LineTo(basePt - n * head * 0.55, true, true);
            }
            g.Freeze();
            return g;
        }

        private void Undo()
        {
            if (_undo.Count > 0) _undo.Pop()();
        }

        /// <summary>Снимок с пометками — в буфер и поверх сохранённого файла.</summary>
        private void Done()
        {
            int w = _bmp.PixelWidth, h = _bmp.PixelHeight;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                dc.DrawRectangle(new VisualBrush(_page) { Stretch = Stretch.Fill }, null, new Rect(0, 0, w, h));
            var outBmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            outBmp.Render(dv);
            outBmp.Freeze();
            ScreenTools.CopyImage(outBmp);
            if (!string.IsNullOrEmpty(_path))
            {
                try
                {
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(outBmp));
                    using var fs = File.Create(_path);
                    enc.Save(fs);
                }
                catch { }
            }
            ToastBus.Show(new Toast
            {
                Kind = ToastKind.Success, Title = "Снимок с пометками скопирован",
                Subtitle = _path != null ? "И сохранён поверх: " + System.IO.Path.GetFileName(_path) : "В буфере обмена",
                Image = outBmp, Icon = Icons.Crop, Important = true, Duration = TimeSpan.FromSeconds(3)
            });
            Close();
        }
    }
}
