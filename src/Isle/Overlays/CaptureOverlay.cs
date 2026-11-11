using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Isle.Services;

namespace Isle.Overlays
{
    public enum CaptureMode { Region, Ocr, Qr, Pipette }

    /// <summary>Полноэкранный слой поверх «замороженного» снимка экрана: выделение области или пипетка.</summary>
    public partial class CaptureOverlay : Window
    {
        private readonly BitmapSource _shot;
        private readonly CaptureMode _mode;
        private readonly Int32Rect _vs;
        private readonly Canvas _canvas = new();
        private readonly Rectangle _sel = new() { Stroke = Brushes.White, StrokeThickness = 1.5, Fill = null, Visibility = Visibility.Collapsed }; // без заливки — снимок внутри не высветляется
        private readonly Path _dim = new() { Fill = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)) };
        private readonly Border _hint;
        private readonly TextBlock _hintText;
        private readonly Border _swatch = new() { Width = 22, Height = 22, CornerRadius = new CornerRadius(6), BorderBrush = Brushes.White, BorderThickness = new Thickness(1) };
        private byte[] _px;
        private int _pw, _ph;
        private Point? _start;
        private readonly TaskCompletionSource<object> _tcs = new();
        private readonly System.Windows.Threading.DispatcherTimer _escPoll = new() { Interval = TimeSpan.FromMilliseconds(30) };
        private bool _escWasDown = true, _escArmed;

        private CaptureOverlay(BitmapSource shot, Int32Rect vs, CaptureMode mode)
        {
            _shot = shot; _vs = vs; _mode = mode;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            AllowsTransparency = false;
            Background = Brushes.Black;
            Cursor = Cursors.Cross;
            Left = 0; Top = 0; Width = 100; Height = 100;

            var img = new Image { Source = shot, Stretch = Stretch.Fill };
            var root = new Grid();
            root.Children.Add(img);
            root.Children.Add(_canvas);
            _canvas.Children.Add(_dim);
            _canvas.Children.Add(_sel);

            _hintText = new TextBlock { Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            var hintRow = new StackPanel { Orientation = Orientation.Horizontal };
            if (mode == CaptureMode.Pipette) { hintRow.Children.Add(_swatch); _hintText.Margin = new Thickness(10, 0, 0, 0); }
            hintRow.Children.Add(_hintText);
            _hint = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(8, 8, 10)),
                CornerRadius = new CornerRadius(0, 0, 16, 16),
                Padding = new Thickness(18, 10, 18, 10),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Child = hintRow,
                IsHitTestVisible = false
            };
            root.Children.Add(_hint);
            Content = root;

            _hintText.Text = mode switch
            {
                CaptureMode.Region => "Выдели область — дальше можно скрыть текст и порисовать · Esc — отмена",
                CaptureMode.Ocr => "Выдели текст для распознавания · Esc — отмена",
                CaptureMode.Qr => "Выдели QR-код · Esc — отмена",
                _ => "Наведи и кликни, чтобы скопировать цвет · Esc — отмена"
            };

            Loaded += (_, __) =>
            {
                var h = new WindowInteropHelper(this).Handle;
                SetWindowPos(h, new IntPtr(-1), _vs.X, _vs.Y, _vs.Width, _vs.Height, 0x0040);
                // Windows не даёт фоновой программе забрать фокус обычным Activate() — без фокуса не доходил Esc
                Isle.Native.Win32.ForceForeground(h);
                Activate();
                Focus();
                Keyboard.Focus(this);
                UpdateDim(Rect.Empty);
                _escPoll.Start();
            };
            KeyDown += (_, e) => { if (e.Key == Key.Escape) { OnEscape(); e.Handled = true; } else OnMarkupKey(e); };
            // Подстраховка: Esc ловим напрямую с клавиатуры, даже если окно так и не получило фокус
            _escPoll.Tick += (_, __) =>
            {
                bool down = (Isle.Native.Win32.GetAsyncKeyState(0x1B) & 0x8000) != 0;
                // своё окно в фокусе — Esc уже обработан в KeyDown (там он сначала отменяет набираемый текст)
                if (down && !_escWasDown && _escArmed && !IsKeyboardFocusWithin) OnEscape();
                _escWasDown = down;
                _escArmed = true; // Esc, зажатый ещё до открытия слоя, не считается
            };
            // окно закрыли не через Finish (Alt+F4 и т. п.) — всё равно завершаем, иначе чёлка останется скрытой, а снимки — заблокированы
            Closed += (_, __) => { _escPoll.Stop(); _tcs.TrySetResult(null); };
            MouseLeftButtonDown += OnDown;
            MouseMove += OnMove;
            MouseLeftButtonUp += OnUp;
            MouseRightButtonDown += (_, __) => { if (!_markup) Finish(null); };
            if (mode == CaptureMode.Pipette)
            {
                // пиксели готовим в фоне — слой появляется сразу, без подвисания
                System.Threading.Tasks.Task.Run(() =>
                {
                    var px = ScreenTools.Bgra(shot, out int w, out int h);
                    Dispatcher.InvokeAsync(() => { _pw = w; _ph = h; _px = px; });
                });
            }
        }

        private static CaptureOverlay _open;
        private static int _openCount;

        public static Task<object> Run(CaptureMode mode)
        {
            // прошлый слой почему-то остался на экране — закрываем, иначе он попадёт в новый снимок второй панелью
            if (_open != null) { try { _open.Finish(null); } catch { } _open = null; }
            var vs = ScreenTools.VirtualScreen();
            var shot = ScreenTools.Capture(vs);
            var w = new CaptureOverlay(shot, vs, mode);
            _open = w; _openCount++;
            w.Closed += (_, __) => { _openCount--; if (ReferenceEquals(_open, w)) _open = null; };
            w.Show();
            return w._tcs.Task;
        }

        private void Finish(object result)
        {
            if (_tcs.Task.IsCompleted) return;
            _tcs.TrySetResult(result);
            Close();
        }

        private double Kx => _shot.PixelWidth / Math.Max(1, ActualWidth);
        private double Ky => _shot.PixelHeight / Math.Max(1, ActualHeight);

        private void OnDown(object s, MouseButtonEventArgs e)
        {
            if (_markup) { CommitMarkText(); return; }
            if (_mode == CaptureMode.Pipette)
            {
                if (_px == null) return; // пиксели ещё готовятся (4K, несколько мониторов) — иначе скопировался бы чёрный
                var c = ColorAt(e.GetPosition(this));
                Finish(c);
                return;
            }
            _start = e.GetPosition(_canvas);
            _sel.Visibility = Visibility.Visible;
            CaptureMouse();
        }

        private void OnMove(object s, MouseEventArgs e)
        {
            if (_markup) return;
            var p = e.GetPosition(_canvas);
            if (_mode == CaptureMode.Pipette)
            {
                var c = ColorAt(p);
                _swatch.Background = new SolidColorBrush(c);
                _hintText.Text = Hex(c) + "  ·  rgb(" + c.R + ", " + c.G + ", " + c.B + ")  ·  клик — скопировать";
                return;
            }
            if (_start == null) return;
            var r = new Rect(_start.Value, p);
            Canvas.SetLeft(_sel, r.X); Canvas.SetTop(_sel, r.Y);
            _sel.Width = r.Width; _sel.Height = r.Height;
            UpdateDim(r);
            _hintText.Text = (int)(r.Width * Kx) + " × " + (int)(r.Height * Ky);
        }

        private void OnUp(object s, MouseButtonEventArgs e)
        {
            if (_markup || _mode == CaptureMode.Pipette || _start == null) return;
            ReleaseMouseCapture();
            var r = new Rect(_start.Value, e.GetPosition(_canvas));
            r.Intersect(new Rect(0, 0, ActualWidth, ActualHeight));
            _start = null;
            if (r.IsEmpty) { Finish(null); return; }
            if (r.Width < 4 || r.Height < 4) { Finish(null); return; }
            var crop = new Int32Rect(
                (int)Math.Max(0, r.X * Kx), (int)Math.Max(0, r.Y * Ky),
                (int)Math.Min(_shot.PixelWidth - r.X * Kx, r.Width * Kx),
                (int)Math.Min(_shot.PixelHeight - r.Y * Ky, r.Height * Ky));
            if (crop.Width <= 0 || crop.Height <= 0) { Finish(null); return; }
            var cb = new CroppedBitmap(_shot, crop);
            cb.Freeze();
            // обычный снимок области — сначала пометки прямо на месте (мозаика, стрелки…), «Сохранить» — в чёлку
            if (_mode == CaptureMode.Region) { EnterMarkup(r, cb); return; }
            Finish(cb);
        }

        private void UpdateDim(Rect hole)
        {
            var full = new RectangleGeometry(new Rect(0, 0, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight)));
            _dim.Data = hole.IsEmpty ? full : new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(hole));
        }

        private Color ColorAt(Point p)
        {
            if (_px == null) return Colors.Black;
            int x = Math.Clamp((int)(p.X * Kx), 0, _pw - 1);
            int y = Math.Clamp((int)(p.Y * Ky), 0, _ph - 1);
            int i = (y * _pw + x) * 4;
            return Color.FromRgb(_px[i + 2], _px[i + 1], _px[i]);
        }

        public static string Hex(Color c) => "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }
}
