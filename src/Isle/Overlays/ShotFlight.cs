using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Isle.Services;

namespace Isle.Overlays
{
    /// <summary>
    /// Снимок «улетает» в чёлку отдельным маленьким окошком. Слой пометок рисуется процессором и на 4K не успевает
    /// показать ни одного кадра анимации, а это окошко небольшое, видеокарта перерисовывает его целиком каждый кадр
    /// (без частичной перерисовки — без чёрных прямоугольников) и оно быстро уменьшается.
    /// Координаты — в пикселях экрана.
    /// </summary>
    public static class ShotFlight
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        private const uint NoActivate = 0x0010;

        public static void Fly(BitmapSource image, Rect from, Action done)
        {
            // куда: середина чёлки; снимок уменьшается до её высоты, сохраняя пропорции
            Rect to;
            try
            {
                var isl = AppHost.Window.IslandScreenRect(out _);
                double h = Math.Max(20, isl.Height * 0.8);
                double w = Math.Min(isl.Width * 0.8, h * from.Width / Math.Max(1, from.Height));
                to = new Rect(isl.X + isl.Width / 2 - w / 2, isl.Y + isl.Height / 2 - h / 2, w, h);
            }
            catch
            {
                double cx = Isle.Native.Win32.GetSystemMetrics(0) / 2.0;
                to = new Rect(cx - 30, 10, 60, 34);
            }

            var win = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                Topmost = true, ShowInTaskbar = false, ShowActivated = false, ResizeMode = ResizeMode.NoResize,
                Left = -32000, Top = -32000, Width = 1, Height = 1
            };
            var brush = new ImageBrush(image) { Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.LowQuality);
            win.Content = new Border
            {
                Background = brush, CornerRadius = new CornerRadius(10),
                BorderBrush = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), BorderThickness = new Thickness(1)
            };
            win.Show();
            var hwnd = new WindowInteropHelper(win).Handle;

            const double Ms = 560;
            var sw = Stopwatch.StartNew();
            int frames = 0; double last = 0, worst = 0;
            static double Lerp(double a, double b, double t) => a + (b - a) * t;
            void Place(double u)
            {
                double ks = 1 - Math.Pow(1 - u, 3);                                                   // размер: сразу сжимается
                double km = u < 0.5 ? 4 * u * u * u : 1 - Math.Pow(-2 * u + 2, 3) / 2;                // путь: плавно
                double w = Lerp(from.Width, to.Width, ks), h = Lerp(from.Height, to.Height, ks);
                double cx = Lerp(from.X + from.Width / 2, to.X + to.Width / 2, km);
                double cy = Lerp(from.Y + from.Height / 2, to.Y + to.Height / 2, km);
                SetWindowPos(hwnd, new IntPtr(-1), (int)Math.Round(cx - w / 2), (int)Math.Round(cy - h / 2),
                    Math.Max(1, (int)Math.Round(w)), Math.Max(1, (int)Math.Round(h)), NoActivate);
                win.Opacity = u > 0.82 ? Math.Max(0, 1 - (u - 0.82) / 0.18) : 1;
            }
            Place(0);

            EventHandler tick = null;
            tick = (_, __) =>
            {
                double t = sw.Elapsed.TotalMilliseconds;
                frames++;
                worst = Math.Max(worst, t - last);
                last = t;
                double u = Math.Min(1, t / Ms);
                Place(u);
                if (u < 1) return;
                CompositionTarget.Rendering -= tick;
                win.Close();
                Journal.Info("capture", "полёт в чёлку (окошком): кадров " + frames + " за " + Math.Round(t) + " мс, худший кадр " + Math.Round(worst) + " мс");
                done?.Invoke();
            };
            CompositionTarget.Rendering += tick;
        }
    }
}
