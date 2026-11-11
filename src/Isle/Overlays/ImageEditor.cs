using System;
using System.Collections.Generic;
using System.IO;
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
    /// Пометки на скриншоте: 10 инструментов (мозаика, рамка, овал, стрелка, линия, кисть, маркер,
    /// ластик, текст, номер шага), 5 цветов, 3 толщины. «Готово» — снимок с пометками в буфер и в тот же файл.
    /// </summary>
    public class ImageEditor : Window
    {
        private enum Tool { Redact, Mosaic, Rect, Ellipse, Arrow, Line, Brush, Marker, Eraser, Text, Step, Crop }

        private sealed record ToolInfo(Tool Tool, string Name, string Icon, string Hint);

        // линейные иконки 24×24 (штрих 1,75)
        private static readonly ToolInfo[] Tools =
        {
            new(Tool.Mosaic, "Мозаика", "M4 4h16v16H4zM4 9.33h16M4 14.67h16M9.33 4v16M14.67 4v16", "Выдели область — она станет крупными пикселями"),
            new(Tool.Rect, "Рамка", "M5 5.5h14a1.5 1.5 0 0 1 1.5 1.5v10a1.5 1.5 0 0 1-1.5 1.5H5A1.5 1.5 0 0 1 3.5 17V7A1.5 1.5 0 0 1 5 5.5z", "Потяни — нарисуется рамка"),
            new(Tool.Ellipse, "Овал", "M12 5.5c4.7 0 8.5 2.9 8.5 6.5s-3.8 6.5-8.5 6.5S3.5 15.6 3.5 12 7.3 5.5 12 5.5z", "Потяни — нарисуется овал"),
            new(Tool.Arrow, "Стрелка", "M5 19L19 5M10 5h9v9", "Потяни от начала к острию"),
            new(Tool.Line, "Линия", "M5 19L19 5", "Потяни — прямая линия"),
            new(Tool.Brush, "Карандаш", "M4 20l4.5-1 10-10a2.1 2.1 0 0 0-3-3l-10 10zM14 7l3 3", "Рисуй от руки"),
            new(Tool.Marker, "Маркер", "M15 4.5l4.5 4.5-9 9H6v-4.5zM4 20.5h16", "Проведи по тексту — выделится жёлтым"),
            new(Tool.Eraser, "Ластик", "M8.5 19.5l-4-4a1.5 1.5 0 0 1 0-2.1L13.4 4.5a1.5 1.5 0 0 1 2.1 0l4 4a1.5 1.5 0 0 1 0 2.1L11 19.5zM9 9l6 6M11 19.5h9", "Кликни по пометке — она исчезнет"),
            new(Tool.Text, "Текст", "M5 6.5V5h14v1.5M12 5v14M9.5 19h5", "Кликни и печатай, Enter — готово"),
            new(Tool.Step, "Номер шага", "M12 3.5a8.5 8.5 0 1 0 0 17a8.5 8.5 0 1 0 0-17M10.5 9.5L12.5 8v8", "Кликни — поставится следующий номер"),
        };

        private static readonly Color[] Palette =
        {
            Color.FromRgb(0xFF, 0x4D, 0x4D), Color.FromRgb(0xFF, 0xE0, 0x4D), Color.FromRgb(0x4A, 0xDE, 0x80),
            Color.FromRgb(0x5A, 0xA9, 0xFF), Color.FromRgb(0xEC, 0xEC, 0xF1)
        };
        private static readonly int[] Widths = { 2, 4, 6 };
        private static readonly Color MarkerYellow = Color.FromArgb(153, 0xFF, 0xE0, 0x4D); // жёлтый, 60 %
        private static readonly Brush Selected = Frozen(Color.FromRgb(0x26, 0x26, 0x2D));
        private static readonly Brush Muted = Frozen(Color.FromRgb(0x5A, 0x5A, 0x63));
        private static readonly Brush Fg = Frozen(Color.FromRgb(0xEC, 0xEC, 0xF1));

        private readonly WriteableBitmap _bmp;
        private readonly string _path;
        private readonly Grid _page;
        private readonly Canvas _overlay;
        private readonly Stack<Action> _undo = new();
        private readonly Dictionary<Tool, Button> _toolButtons = new();
        private readonly List<Border> _colorRings = new();
        private readonly List<Border> _widthButtons = new();
        private readonly TextBlock _hint;
        private Button _undoButton;
        private Tool _tool = Tool.Arrow;
        private Color _color = Palette[0];
        private int _width = 4;
        private Point _start;
        private Shape _preview;
        private Polyline _stroke;
        private TextBox _textBox;
        private int _step;
        private Rect? _crop;
        private ShapePath _cropMask;
        private readonly double _thick;

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
            Width = Math.Min(wa.Width * 0.9, Math.Max(700, w + 40));
            Height = Math.Min(wa.Height * 0.9, Math.Max(460, h + 150));

            _page = new Grid { Width = w, Height = h, ClipToBounds = true, Cursor = Cursors.Cross, Background = Brushes.Transparent };
            _page.Children.Add(new Image { Source = _bmp, Width = w, Height = h, Stretch = Stretch.Fill });
            _overlay = new Canvas { Width = w, Height = h, Background = null };
            _page.Children.Add(_overlay);
            _page.MouseLeftButtonDown += OnDown;
            _page.MouseMove += OnMove;
            _page.MouseLeftButtonUp += OnUp;

            // 1-я строка: 12 инструментов иконками
            var tools = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            int i = 0;
            foreach (var t in Tools)
            {
                var b = IconButton(t.Icon, t.Name, () => SelectTool(t.Tool));
                b.Width = 38; b.Height = 36; b.Margin = new Thickness(1, 0, 1, 0);
                _toolButtons[t.Tool] = b;
                tools.Children.Add(b);
                Appear(b, i++);
            }

            // 2-я строка: цвета, толщины, «Отменить», «Готово», закрыть
            var row2 = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 8, 0, 0) };
            for (int c = 0; c < Palette.Length; c++)
            {
                int ci = c;
                var ring = new Border
                {
                    Width = 26, Height = 26, CornerRadius = new CornerRadius(13), BorderThickness = new Thickness(2), Margin = new Thickness(0, 0, 4, 0),
                    Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = "Цвет",
                    Child = new Ellipse { Width = 16, Height = 16, Fill = Frozen(Palette[c]) }
                };
                ring.MouseLeftButtonUp += (_, __) => SelectColor(ci);
                _colorRings.Add(ring);
                DockPanel.SetDock(ring, Dock.Left);
                row2.Children.Add(ring);
            }
            row2.Children.Add(Divider());
            foreach (var wd in Widths)
            {
                int width = wd;
                var dotBox = new Border
                {
                    Width = 32, Height = 28, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 2, 0), Cursor = Cursors.Hand,
                    Background = Brushes.Transparent, ToolTip = "Толщина " + wd + " px",
                    Child = new Ellipse { Width = 3 + wd * 1.6, Height = 3 + wd * 1.6, Fill = Fg }
                };
                dotBox.MouseLeftButtonUp += (_, __) => SelectWidth(width);
                _widthButtons.Add(dotBox);
                DockPanel.SetDock(dotBox, Dock.Left);
                row2.Children.Add(dotBox);
            }
            var close = IconButton(Icons.Close, "Закрыть без сохранения (Esc)", Close);
            close.Width = 36; close.Height = 34;
            var done = TextButton("Готово", Done, "Скопировать снимок с пометками (Enter)", primary: true);
            _undoButton = TextButton("Отменить", Undo, "Отменить последнее (Ctrl+Z)", primary: false);
            foreach (var b in new UIElement[] { close, done, _undoButton }) { DockPanel.SetDock(b, Dock.Right); row2.Children.Add(b); }

            var toolbar = new StackPanel();
            toolbar.Children.Add(tools);
            toolbar.Children.Add(row2);

            var root = new DockPanel { LastChildFill = true };
            var top = new Border { Padding = new Thickness(14, 12, 14, 10), Child = toolbar, Background = Brushes.Transparent };
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);
            var footer = new TextBlock
            {
                Text = "Enter — готово · Esc — закрыть · Ctrl+Z — отменить", FontSize = 12, Foreground = Frozen(Color.FromRgb(0x8B, 0x8B, 0x95)),
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 10)
            };
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            // подсказка действия инструмента — в углу снимка
            _hint = new TextBlock { FontSize = 12, Foreground = Fg, Margin = new Thickness(0) };
            var hintBox = new Border
            {
                Background = Frozen(Color.FromArgb(200, 0x0B, 0x0B, 0x0D)), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 5, 10, 5),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10), IsHitTestVisible = false,
                Child = _hint
            };
            var canvasArea = new Grid();
            canvasArea.Children.Add(new Viewbox { Child = _page, StretchDirection = StretchDirection.DownOnly });
            canvasArea.Children.Add(hintBox);
            root.Children.Add(new Border { Padding = new Thickness(14, 0, 14, 8), Child = canvasArea });

            Content = new Border
            {
                Background = Frozen(Color.FromRgb(0x0B, 0x0B, 0x0D)),
                BorderBrush = Frozen(Color.FromRgb(0x23, 0x23, 0x2A)), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(18), Child = root
            };
            top.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource == top) DragMove(); };
            KeyDown += (_, e) =>
            {
                if (_textBox != null) return; // клавиши идут в поле текста
                if (e.Key == Key.Escape) Close();
                else if (e.Key == Key.Enter) Done();
                else if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) Undo();
            };
            Loaded += (_, __) => { Activate(); Focus(); };
            SelectTool(Tool.Arrow);
            SelectColor(0);
            SelectWidth(4);
            UpdateUndo();
        }

        private static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private static Border Divider() =>
            new() { Width = 1, Height = 22, Background = Frozen(Color.FromRgb(0x23, 0x23, 0x2A)), Margin = new Thickness(6, 0, 8, 0) };

        /// <summary>Инструменты появляются с масштабом 0,7 → 1 каскадом по 70 мс.</summary>
        private static void Appear(FrameworkElement el, int index)
        {
            var st = new ScaleTransform(0.7, 0.7);
            el.RenderTransformOrigin = new Point(0.5, 0.5);
            el.RenderTransform = st;
            el.Opacity = 0;
            var begin = TimeSpan.FromMilliseconds(80 + index * 70);
            var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.7, 1, TimeSpan.FromMilliseconds(350)) { BeginTime = begin, EasingFunction = ease });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.7, 1, TimeSpan.FromMilliseconds(350)) { BeginTime = begin, EasingFunction = ease });
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)) { BeginTime = begin });
        }

        private static Button IconButton(string iconData, string tip, Action click)
        {
            var b = new Button
            {
                Content = new Icon { Data = iconData, Width = 18, Height = 18, Foreground = Fg },
                Padding = new Thickness(0), ToolTip = tip, Background = Brushes.Transparent,
                Style = (Style)Application.Current.FindResource("Ghost")
            };
            b.Click += (_, __) => click();
            return b;
        }

        private static Button TextButton(string text, Action click, string tip, bool primary)
        {
            var b = new Button
            {
                Content = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                Margin = new Thickness(4, 0, 0, 0), Height = 34, Padding = new Thickness(16, 0, 16, 0), ToolTip = tip,
                Style = (Style)Application.Current.FindResource(primary ? "Primary" : "Ghost")
            };
            if (primary) { b.Background = Fg; b.Foreground = Frozen(Color.FromRgb(0x0B, 0x0B, 0x0D)); }
            b.Click += (_, __) => click();
            return b;
        }

        private void SelectTool(Tool t)
        {
            CommitText();
            _tool = t;
            foreach (var (tool, b) in _toolButtons) b.Background = tool == t ? Selected : Brushes.Transparent;
            foreach (var info in Tools) if (info.Tool == t) _hint.Text = info.Name + " — " + info.Hint;
            _page.Cursor = t switch { Tool.Text => Cursors.IBeam, Tool.Eraser or Tool.Step => Cursors.Hand, _ => Cursors.Cross };
        }

        private void SelectColor(int index)
        {
            _color = Palette[index];
            for (int i = 0; i < _colorRings.Count; i++) _colorRings[i].BorderBrush = i == index ? Fg : Brushes.Transparent;
            if (_textBox != null) _textBox.Foreground = Frozen(_color);
        }

        private void SelectWidth(int width)
        {
            _width = width;
            for (int i = 0; i < _widthButtons.Count; i++) _widthButtons[i].Background = Widths[i] == width ? Selected : Brushes.Transparent;
        }

        private void UpdateUndo()
        {
            if (_undoButton == null) return;
            bool can = _undo.Count > 0;
            _undoButton.Foreground = can ? Fg : Muted; // серая, пока нечего отменять
            _undoButton.Cursor = can ? Cursors.Hand : Cursors.Arrow;
        }

        private void PushUndo(Action a)
        {
            _undo.Push(a);
            UpdateUndo();
        }

        /// <summary>Толщина линий в пикселях снимка: 4 — обычная, 2 — тонкая, 6 — толстая.</summary>
        private double Thick => _thick * _width / 4.0;

        private Point Clamp(Point p) => new(Math.Clamp(p.X, 0, _bmp.PixelWidth), Math.Clamp(p.Y, 0, _bmp.PixelHeight));

        private void Add(UIElement el)
        {
            // маска обрезки всегда поверх остальных пометок
            int idx = _cropMask != null ? _overlay.Children.IndexOf(_cropMask) : -1;
            if (idx >= 0) _overlay.Children.Insert(idx, el); else _overlay.Children.Add(el);
        }

        private void AddWithUndo(UIElement el)
        {
            Add(el);
            PushUndo(() => _overlay.Children.Remove(el));
        }

        private void OnDown(object s, MouseButtonEventArgs e)
        {
            if (FindParent<TextBox>(e.OriginalSource as DependencyObject) != null) return;
            if (_textBox != null) { CommitText(); if (_tool == Tool.Text) return; }
            _start = Clamp(e.GetPosition(_page));
            var brush = Frozen(_color);
            switch (_tool)
            {
                case Tool.Eraser:
                    EraseAt(e);
                    _page.CaptureMouse();
                    return;
                case Tool.Text:
                    StartText(_start);
                    e.Handled = true;
                    return;
                case Tool.Step:
                    PlaceStep(_start);
                    return;
                case Tool.Brush:
                case Tool.Marker:
                    bool marker = _tool == Tool.Marker;
                    _stroke = new Polyline
                    {
                        Stroke = marker ? Frozen(MarkerYellow) : brush, StrokeThickness = marker ? Thick * 4 : Thick,
                        StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                        IsHitTestVisible = true
                    };
                    _stroke.Points.Add(_start);
                    Add(_stroke);
                    _page.CaptureMouse();
                    return;
                case Tool.Redact:
                    _preview = new Rectangle { Fill = brush };
                    break;
                case Tool.Mosaic:
                    StartLiveMosaic();
                    _preview = new Rectangle { Stroke = Frozen(Color.FromArgb(140, 255, 255, 255)), StrokeThickness = Math.Max(1, _thick / 3), StrokeDashArray = new DoubleCollection { 4, 3 } };
                    break;
                case Tool.Crop:
                    _preview = new Rectangle { Stroke = Brushes.White, StrokeThickness = Math.Max(1.5, _thick / 2), StrokeDashArray = new DoubleCollection { 4, 3 }, Fill = Frozen(Color.FromArgb(50, 255, 255, 255)) };
                    break;
                case Tool.Rect:
                    _preview = new Rectangle { Stroke = brush, StrokeThickness = Thick, RadiusX = Thick * 1.5, RadiusY = Thick * 1.5 };
                    break;
                case Tool.Ellipse:
                    _preview = new Ellipse { Stroke = brush, StrokeThickness = Thick };
                    break;
                case Tool.Arrow:
                    _preview = new ShapePath { Stroke = brush, StrokeThickness = Thick, Fill = brush, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                    break;
                case Tool.Line:
                    _preview = new ShapePath { Stroke = brush, StrokeThickness = Thick, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                    break;
            }
            _page.CaptureMouse();
            Add(_preview);
            Update(_start);
        }

        private void OnMove(object s, MouseEventArgs e)
        {
            if (!_page.IsMouseCaptured) return;
            if (_tool == Tool.Eraser) { EraseAt(e); return; }
            var p = Clamp(e.GetPosition(_page));
            if (_stroke != null) { _stroke.Points.Add(p); return; }
            Update(p);
        }

        private void Update(Point p)
        {
            var rect = new Rect(_start, p);
            switch (_preview)
            {
                case Rectangle r:
                    Canvas.SetLeft(r, rect.X); Canvas.SetTop(r, rect.Y); r.Width = rect.Width; r.Height = rect.Height;
                    if (_tool == Tool.Mosaic && _liveMosaic?.Clip is RectangleGeometry clip) clip.Rect = rect; // мозаика видна сразу, пока тянешь
                    break;
                case Ellipse el:
                    Canvas.SetLeft(el, rect.X); Canvas.SetTop(el, rect.Y); el.Width = rect.Width; el.Height = rect.Height;
                    break;
                case ShapePath path:
                    path.Data = _tool == Tool.Arrow ? ArrowGeometry(_start, p, Thick) : new LineGeometry(_start, p);
                    break;
            }
        }

        private void OnUp(object s, MouseButtonEventArgs e)
        {
            if (!_page.IsMouseCaptured) return;
            _page.ReleaseMouseCapture();
            if (_tool == Tool.Eraser) return;
            var p = Clamp(e.GetPosition(_page));
            if (_stroke != null)
            {
                var line = _stroke; _stroke = null;
                if (line.Points.Count < 2) { _overlay.Children.Remove(line); return; }
                PushUndo(() => _overlay.Children.Remove(line));
                return;
            }
            var shape = _preview; _preview = null;
            if (shape == null) return;
            var rect = new Rect(_start, p);
            if (_liveMosaic != null) _liveMosaic.Visibility = Visibility.Collapsed;
            if (rect.Width < 4 && rect.Height < 4) { _overlay.Children.Remove(shape); return; }
            switch (_tool)
            {
                case Tool.Mosaic:
                    _overlay.Children.Remove(shape);
                    Pixelate(new Int32Rect((int)rect.X, (int)rect.Y, Math.Max(1, (int)rect.Width), Math.Max(1, (int)rect.Height)));
                    break;
                case Tool.Crop:
                    _overlay.Children.Remove(shape);
                    SetCrop(rect, undoable: true);
                    break;
                default:
                    PushUndo(() => _overlay.Children.Remove(shape));
                    break;
            }
        }

        // ---------- ластик ----------

        private void EraseAt(MouseEventArgs e)
        {
            if (_overlay.InputHitTest(e.GetPosition(_overlay)) is not DependencyObject hit) return;
            // ищем прямого ребёнка холста (номер шага — Grid с кружком и цифрой; у текста попадание может быть в Run)
            while (hit != null)
            {
                var parent = hit is Visual ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit);
                if (parent == null || ReferenceEquals(parent, _overlay)) break;
                hit = parent;
            }
            if (hit is not UIElement el || ReferenceEquals(el, _cropMask) || !_overlay.Children.Contains(el)) return;
            int index = _overlay.Children.IndexOf(el);
            _overlay.Children.Remove(el);
            PushUndo(() => _overlay.Children.Insert(Math.Min(index, _overlay.Children.Count), el));
        }

        // ---------- текст ----------

        private void StartText(Point at)
        {
            _textBox = new TextBox
            {
                FontSize = Math.Max(14, Thick * 5), FontWeight = FontWeights.SemiBold, Foreground = Frozen(_color),
                Background = Frozen(Color.FromArgb(90, 0, 0, 0)), BorderBrush = Brushes.White, BorderThickness = new Thickness(1),
                CaretBrush = Brushes.White, MinWidth = 40, Padding = new Thickness(2), AcceptsReturn = false
            };
            Canvas.SetLeft(_textBox, at.X); Canvas.SetTop(_textBox, at.Y);
            _overlay.Children.Add(_textBox);
            _textBox.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { CommitText(); e.Handled = true; }
                else if (e.Key == Key.Escape) { CancelText(); e.Handled = true; }
            };
            _textBox.Loaded += (_, __) => { _textBox?.Focus(); Keyboard.Focus(_textBox); };
        }

        private void CommitText()
        {
            if (_textBox == null) return;
            var box = _textBox;
            _textBox = null;
            _overlay.Children.Remove(box);
            Focus();
            if (string.IsNullOrWhiteSpace(box.Text)) return;
            var tb = new TextBlock { Text = box.Text, FontSize = box.FontSize, FontWeight = FontWeights.SemiBold, Foreground = box.Foreground, Padding = new Thickness(3) };
            Canvas.SetLeft(tb, Canvas.GetLeft(box)); Canvas.SetTop(tb, Canvas.GetTop(box));
            AddWithUndo(tb);
        }

        private void CancelText()
        {
            if (_textBox == null) return;
            _overlay.Children.Remove(_textBox);
            _textBox = null;
            Focus();
        }

        // ---------- номер шага ----------

        private void PlaceStep(Point at)
        {
            _step++;
            double d = Math.Max(22, Thick * 8);
            bool light = _color.R * 0.299 + _color.G * 0.587 + _color.B * 0.114 > 160;
            var g = new Grid { Width = d, Height = d };
            g.Children.Add(new Ellipse { Fill = Frozen(_color) });
            g.Children.Add(new TextBlock
            {
                Text = _step.ToString(), FontSize = d * 0.55, FontWeight = FontWeights.Bold,
                Foreground = light ? Brushes.Black : Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            });
            Canvas.SetLeft(g, at.X - d / 2); Canvas.SetTop(g, at.Y - d / 2);
            Add(g);
            PushUndo(() => { _overlay.Children.Remove(g); _step = Math.Max(0, _step - 1); });
        }

        // ---------- обрезка ----------

        private void SetCrop(Rect? rect, bool undoable)
        {
            var before = _crop;
            _crop = rect;
            if (_cropMask != null) _overlay.Children.Remove(_cropMask);
            _cropMask = null;
            if (rect is Rect r)
            {
                // затемняем всё, что будет отрезано
                var geo = new GeometryGroup { FillRule = FillRule.EvenOdd };
                geo.Children.Add(new RectangleGeometry(new Rect(0, 0, _bmp.PixelWidth, _bmp.PixelHeight)));
                geo.Children.Add(new RectangleGeometry(r));
                _cropMask = new ShapePath
                {
                    Data = geo, Fill = Frozen(Color.FromArgb(160, 0, 0, 0)), Stroke = Brushes.White,
                    StrokeThickness = Math.Max(1.5, _thick / 2), StrokeDashArray = new DoubleCollection { 4, 3 }, IsHitTestVisible = false
                };
                _overlay.Children.Add(_cropMask);
            }
            if (undoable) PushUndo(() => SetCrop(before, undoable: false));
        }

        private Image _liveMosaic;
        private byte[] _mosaicPx;

        /// <summary>Вся картинка заранее превращается в мозаику и показывается только внутри тянущейся рамки — как в живом режиме.</summary>
        private void StartLiveMosaic()
        {
            int w = _bmp.PixelWidth, h = _bmp.PixelHeight;
            var px = new byte[w * h * 4];
            _bmp.CopyPixels(px, w * 4, 0);
            _mosaicPx = Mosaic.Build(px, w, h, Mosaic.BlockFor(w, h));
            var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, _mosaicPx, w * 4);
            src.Freeze();
            if (_liveMosaic == null)
            {
                _liveMosaic = new Image { Width = w, Height = h, Stretch = Stretch.Fill, IsHitTestVisible = false };
                _page.Children.Insert(1, _liveMosaic); // над снимком, под пометками
            }
            _liveMosaic.Source = src;
            _liveMosaic.Clip = new RectangleGeometry(new Rect(_start, _start));
            _liveMosaic.Visibility = Visibility.Visible;
        }

        /// <summary>Крупные «пиксели» вместо выделенного куска — ровно те, что были видны при выделении.</summary>
        private void Pixelate(Int32Rect r)
        {
            int w = _bmp.PixelWidth;
            r.Width = Math.Min(r.Width, w - r.X);
            r.Height = Math.Min(r.Height, _bmp.PixelHeight - r.Y);
            if (r.Width <= 0 || r.Height <= 0 || _mosaicPx == null) return;
            int stride = r.Width * 4;
            var backup = new byte[stride * r.Height];
            _bmp.CopyPixels(r, backup, stride, 0);
            var patch = new byte[stride * r.Height];
            for (int row = 0; row < r.Height; row++)
                Buffer.BlockCopy(_mosaicPx, ((r.Y + row) * w + r.X) * 4, patch, row * stride, stride);
            _bmp.WritePixels(r, patch, stride, 0);
            PushUndo(() => _bmp.WritePixels(r, backup, stride, 0));
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

        private static T FindParent<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null && d is not T) d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            return d as T;
        }

        private void Undo()
        {
            if (_textBox != null) { CancelText(); return; }
            if (_undo.Count > 0) _undo.Pop()();
            UpdateUndo();
        }

        /// <summary>Снимок с пометками (и обрезкой) — в буфер и поверх сохранённого файла.</summary>
        private void Done()
        {
            CommitText();
            int w = _bmp.PixelWidth, h = _bmp.PixelHeight;
            if (_cropMask != null) _cropMask.Visibility = Visibility.Collapsed; // затемнение в снимок не попадает
            _page.UpdateLayout();
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                dc.DrawRectangle(new VisualBrush(_page) { Stretch = Stretch.Fill }, null, new Rect(0, 0, w, h));
            var full = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            full.Render(dv);
            BitmapSource outBmp = full;
            if (_crop is Rect c)
            {
                var ir = new Int32Rect((int)Math.Floor(c.X), (int)Math.Floor(c.Y), 0, 0);
                ir.Width = Math.Max(1, Math.Min(w - ir.X, (int)Math.Ceiling(c.Width)));
                ir.Height = Math.Max(1, Math.Min(h - ir.Y, (int)Math.Ceiling(c.Height)));
                outBmp = new CroppedBitmap(full, ir);
            }
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
