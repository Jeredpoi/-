using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Isle.Helpers;
using Isle.Native;
using Isle.Services;

namespace Isle.Overlays
{
    /// <summary>
    /// Выбор области для записи видео: экран чуть темнеет (но остаётся живым), тянешь рамку мышью,
    /// рядом — «Звук ПК», «Микрофон» и «Начать». Enter — начать, Esc — отмена. Монитор — тот, где курсор.
    /// </summary>
    public class RecordPicker : Window
    {
        private readonly Canvas _canvas = new() { Background = Brushes.Transparent };
        private readonly Path _dim = new() { Fill = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)), IsHitTestVisible = false };
        private readonly Rectangle _frame = new() { Stroke = Brushes.White, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 3 }, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        private readonly Border _size = new() { CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 2, 7, 2), Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x10, 0x10, 0x14)), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        private readonly TextBlock _sizeText = new() { Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold };
        private readonly Border _bar = new() { CornerRadius = new CornerRadius(14), Padding = new Thickness(6), Visibility = Visibility.Collapsed };
        private readonly Border _hint;
        private bool _micOn = AppSettings.Current.RecordMic;
        private bool _sysOn = AppSettings.Current.RecordSystemAudio;
        private Border _sysBtn;
        private Icon _sysIcon;
        private Border _micBtn;
        private Icon _micIcon;
        private readonly (int x, int y, int w, int h)? _preset;
        private Point? _start;
        private Rect _sel;
        private bool _done;
        private readonly Win32.RECT _mon;

        /// <summary>Область уже выбрана (из снимка нажали «камеру») — сразу кнопки «Начать» и микрофон.</summary>
        public RecordPicker(int x, int y, int w, int h) : this() { _preset = (x, y, w, h); }

        public RecordPicker()
        {
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
            Topmost = true; ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize; Cursor = Cursors.Cross;
            // монитор под курсором
            Win32.GetCursorPos(out var cur);
            var mon = MonitorFromPoint(cur, 2);
            var mi = new Win32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
            Win32.GetMonitorInfo(mon, ref mi);
            _mon = mi.rcMonitor;

            _size.Child = _sizeText;
            _canvas.Children.Add(_dim);
            _canvas.Children.Add(_frame);
            _canvas.Children.Add(_size);

            // подсказка по центру, пока ничего не выделено
            _hint = new Border
            {
                CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 10, 16, 10), IsHitTestVisible = false,
                Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x12, 0x12, 0x16)),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)), VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = "Выдели область для записи видео · Esc — отмена", Foreground = Brushes.White, FontSize = 13, Margin = new Thickness(10, 0, 0, 0) },
                    }
                }
            };
            _canvas.Children.Add(_hint);

            // панель над/под областью
            _bar.Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x12, 0x12, 0x16));
            _bar.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x32)); _bar.BorderThickness = new Thickness(1);
            // микрофон: круглая кнопка, зелёная — пишется, красная — выключен
            // звук компьютера — так же: зелёный — пишется, красный — заглушён
            _sysIcon = new Icon { Kind = "Volume", Width = 15, Height = 15, StrokeWidth = 2, Foreground = Brushes.White };
            _sysBtn = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Cursor = Cursors.Hand, Child = _sysIcon, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            _sysBtn.MouseLeftButtonUp += (_, e) => { e.Handled = true; _sysOn = !_sysOn; PaintMic(); };
            _micIcon = new Icon { Kind = "Mic", Width = 15, Height = 15, StrokeWidth = 2, Foreground = Brushes.White };
            _micBtn = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Cursor = Cursors.Hand, Child = _micIcon, VerticalAlignment = VerticalAlignment.Center };
            _micBtn.MouseLeftButtonUp += (_, e) => { e.Handled = true; _micOn = !_micOn; PaintMic(); };
            PaintMic();
            var go = new Button { Style = (Style)Application.Current.FindResource("Flat"), Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(6, 0, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)), Foreground = Brushes.White, Cursor = Cursors.Hand,
                Content = new StackPanel { Orientation = Orientation.Horizontal, Children = {
                    new Ellipse { Width = 9, Height = 9, Fill = Brushes.White, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = "Начать", FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center } } } };
            go.Click += (_, __) => Finish(true);
            var cancel = new Button { Style = (Style)Application.Current.FindResource("Ghost"), Width = 32, Height = 32, Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0), Cursor = Cursors.Hand,
                ToolTip = "Отмена (Esc)", Content = new Icon { Kind = "Close", Width = 13, Height = 13, Foreground = Brushes.White } };
            cancel.Click += (_, __) => Finish(false);
            _bar.Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _sysBtn, _micBtn, go, cancel } };
            _canvas.Children.Add(_bar);
            Content = _canvas;

            SourceInitialized += (_, __) =>
            {
                // окно ровно на монитор, в пикселях (на разных масштабах мониторов WPF-координаты врут)
                var h = new WindowInteropHelper(this).Handle;
                SetWindowPos(h, new IntPtr(-1), _mon.Left, _mon.Top, _mon.Right - _mon.Left, _mon.Bottom - _mon.Top, 0x0040);
            };
            Loaded += (_, __) =>
            {
                if (_preset is { } p)
                {
                    // область пришла из снимка: переводим пиксели экрана в координаты окна и сразу показываем кнопки
                    var m = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
                    _sel = new Rect((p.x - _mon.Left) / m.M11, (p.y - _mon.Top) / m.M22, p.w / m.M11, p.h / m.M22);
                    _hint.Visibility = Visibility.Collapsed;
                    UpdateDim(); PlaceBar();
                }
                else { UpdateDim(); PlaceHint(); }
                Activate(); Focus();
            };
            MouseLeftButtonDown += OnDown;
            MouseMove += OnMove;
            MouseLeftButtonUp += OnUp;
            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) Finish(false);
                else if (e.Key == Key.Enter && _sel.Width > 8) Finish(true);
            };
            Deactivated += (_, __) => { if (!_done && _sel.Width < 8) Finish(false); };
        }

        /// <summary>Результат: область в пикселях экрана и звук; null — отменили.</summary>
        public (int x, int y, int w, int h, bool sys, bool mic)? Result { get; private set; }

        private void PaintMic()
        {
            _micBtn.Background = new SolidColorBrush(_micOn ? Color.FromRgb(0x22, 0xC5, 0x5E) : Color.FromRgb(0xEF, 0x44, 0x44));
            _micIcon.Kind = _micOn ? "Mic" : "MicOff";
            _micBtn.ToolTip = _micOn ? "Микрофон включён · клик — выключить" : "Микрофон выключен · клик — включить";
            if (_sysBtn == null) return;
            _sysBtn.Background = new SolidColorBrush(_sysOn ? Color.FromRgb(0x22, 0xC5, 0x5E) : Color.FromRgb(0xEF, 0x44, 0x44));
            _sysIcon.Kind = _sysOn ? "Volume" : "Mute";
            _sysBtn.ToolTip = _sysOn ? "Звук компьютера пишется · клик — заглушить" : "Звук компьютера заглушён · клик — включить";
        }

        private void OnDown(object s, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && IsInside(d, _bar)) return;
            _start = e.GetPosition(_canvas);
            _sel = new Rect(_start.Value, new Size(0, 0));
            _bar.Visibility = Visibility.Collapsed;
            _hint.Visibility = Visibility.Collapsed;
            CaptureMouse();
        }

        private void OnMove(object s, MouseEventArgs e)
        {
            if (_start == null || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(_canvas);
            _sel = new Rect(_start.Value, p);
            UpdateDim();
        }

        private void OnUp(object s, MouseButtonEventArgs e)
        {
            if (_start == null) return;
            ReleaseMouseCapture();
            _start = null;
            if (_sel.Width < 8 || _sel.Height < 8) { _sel = default; UpdateDim(); _hint.Visibility = Visibility.Visible; return; }
            PlaceBar();
        }

        private void UpdateDim()
        {
            var all = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
            Geometry g = all;
            if (_sel.Width > 0 && _sel.Height > 0)
            {
                g = new CombinedGeometry(GeometryCombineMode.Exclude, all, new RectangleGeometry(_sel));
                _frame.Visibility = Visibility.Visible;
                Canvas.SetLeft(_frame, _sel.X); Canvas.SetTop(_frame, _sel.Y);
                _frame.Width = _sel.Width; _frame.Height = _sel.Height;
                var px = ToPixels(_sel);
                _sizeText.Text = (px.w & ~1) + " × " + (px.h & ~1);
                _size.Visibility = Visibility.Visible;
                Canvas.SetLeft(_size, _sel.X); Canvas.SetTop(_size, Math.Max(0, _sel.Y - 24));
            }
            else { _frame.Visibility = Visibility.Collapsed; _size.Visibility = Visibility.Collapsed; }
            _dim.Data = g;
        }

        private void PlaceHint()
        {
            _hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(_hint, (ActualWidth - _hint.DesiredSize.Width) / 2);
            Canvas.SetTop(_hint, ActualHeight * 0.42);
        }

        private void PlaceBar()
        {
            _bar.Visibility = Visibility.Visible;
            _bar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var bw = _bar.DesiredSize.Width; var bh = _bar.DesiredSize.Height;
            double x = Math.Clamp(_sel.Right - bw, 8, ActualWidth - bw - 8);
            double y = _sel.Bottom + 10 + bh < ActualHeight ? _sel.Bottom + 10 : Math.Max(8, _sel.Top - bh - 10);
            if (_sel.Height > ActualHeight - 2 * bh - 30) y = _sel.Bottom - bh - 12; // область почти на весь экран — панель внутри
            Canvas.SetLeft(_bar, x); Canvas.SetTop(_bar, y);
        }

        private (int x, int y, int w, int h) ToPixels(Rect r)
        {
            var src = PresentationSource.FromVisual(this);
            var m = src?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
            int x = (int)Math.Round(r.X * m.M11) + _mon.Left, y = (int)Math.Round(r.Y * m.M22) + _mon.Top;
            int w = (int)Math.Round(r.Width * m.M11), h = (int)Math.Round(r.Height * m.M22);
            return (x, y, w, h);
        }

        private void Finish(bool ok)
        {
            if (_done) return;
            _done = true;
            if (ok && _sel.Width >= 8 && _sel.Height >= 8)
            {
                var px = ToPixels(_sel);
                AppSettings.Current.RecordMic = _micOn;
                AppSettings.Current.RecordSystemAudio = _sysOn;
                AppSettings.Current.Save();
                Result = (px.x, px.y, px.w, px.h, _sysOn, _micOn);
            }
            Close();
        }

        private static bool IsInside(DependencyObject d, DependencyObject parent)
        {
            for (; d != null; d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (d == parent) return true;
            return false;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Win32.POINT pt, uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }

    /// <summary>
    /// Рамка вокруг записываемой области: тонкая красная, клики проходят насквозь, в само видео не попадает
    /// (WDA_EXCLUDEFROMCAPTURE). Стоит чуть снаружи области.
    /// </summary>
    public class RecordFrame : Window
    {
        private readonly int _x, _y, _w, _h;
        public RecordFrame(int x, int y, int w, int h)
        {
            _x = x - 3; _y = y - 3; _w = w + 6; _h = h + 6;
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
            Topmost = true; ShowInTaskbar = false; ShowActivated = false; ResizeMode = ResizeMode.NoResize; Focusable = false;
            Content = new Border { BorderThickness = new Thickness(2), BorderBrush = new SolidColorBrush(Color.FromArgb(0xE0, 0xEF, 0x44, 0x44)), CornerRadius = new CornerRadius(3) };
            SourceInitialized += (_, __) =>
            {
                var h = new WindowInteropHelper(this).Handle;
                // клики насквозь, без активации, вне снимков и записи
                int ex = Win32.GetWindowLong(h, -20);
                Win32.SetWindowLong(h, -20, ex | 0x20 /*TRANSPARENT*/ | 0x80 /*TOOLWINDOW*/ | 0x08000000 /*NOACTIVATE*/ | 0x80000 /*LAYERED*/);
                Win32.SetWindowDisplayAffinity(h, 0x11);
                SetWindowPos(h, new IntPtr(-1), _x, _y, _w, _h, 0x0010 | 0x0040);
            };
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }
}
