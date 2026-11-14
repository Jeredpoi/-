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
        private const uint NoActivate = 0x0010, NoSize = 0x0001;

        /// <param name="shown">окошко уже на экране поверх слоя — теперь слой можно спрятать (без «провала» между ними)</param>
        private static int _flights;

        public static void Fly(BitmapSource image, Rect from, Action shown, Action done)
        {
            int id = ++_flights;
            int hz = Isle.Helpers.Display.RefreshHz((int)(from.X + from.Width / 2), (int)(from.Y + from.Height / 2));
            Journal.Info("capture", "полёт #" + id + ": старт, снимок " + image.PixelWidth + "×" + image.PixelHeight + ", окно " + Math.Round(from.Width) + "×" + Math.Round(from.Height) + " px");
            // Снимок начинает с настоящего размера и плавно уменьшается по пути — без «прыжка» в маленькое окошко.
            // Рисует видеокарта: окно каждый кадр перерисовывается целиком, частичных обновлений (и чёрных
            // прямоугольников) нет. Только когда видеокарта занята игрой (чёлка сама ушла на процессор) — рисует
            // процессор, и тогда окошко сразу поменьше: большое прозрачное окно процессор не тянет.
            bool soft = AppHost.Window?.SoftRender ?? false;
            if (soft)
            {
                double k0 = Math.Min(1, 640 / Math.Max(1, Math.Max(from.Width, from.Height)));
                from = new Rect(from.X + from.Width / 2 - from.Width * k0 / 2, from.Y + from.Height / 2 - from.Height * k0 / 2, from.Width * k0, from.Height * k0);
            }
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
            // картинку заранее уменьшаем до размера окошка: каждый кадр не нужно пересчитывать 900 px в 480
            BitmapSource pic = image;
            double kp = Math.Min(1, Math.Max(from.Width / Math.Max(1, image.PixelWidth), from.Height / Math.Max(1, image.PixelHeight)));
            if (soft && kp < 0.95)
            {
                try
                {
                    var small = new CachedBitmap(new TransformedBitmap(image, new ScaleTransform(kp, kp)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    small.Freeze();
                    pic = small;
                }
                catch { }
            }
            var brush = new ImageBrush(pic) { Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.LowQuality);
            // Размер окна — ступеньками, снимок плавно уменьшается внутри него (масштабом).
            var scale = new ScaleTransform(1, 1);
            win.Content = new Border
            {
                Background = brush, CornerRadius = new CornerRadius(10),
                BorderBrush = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), BorderThickness = new Thickness(1),
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = scale
            };
            win.Show();
            var hwnd = new WindowInteropHelper(win).Handle;
            if (soft)
                try { if (PresentationSource.FromVisual(win) is HwndSource hs && hs.CompositionTarget != null) hs.CompositionTarget.RenderMode = RenderMode.SoftwareOnly; } catch { }
            double stepW = 0, stepH = 0;
            int resizes = 0;

            const double Hold = 160;   // сначала снимок «щёлкает» на месте — глаз успевает понять, что сейчас будет
            const double Ms = 900;     // сам полёт — не торопясь
            var sw = Stopwatch.StartNew();
            int frames = 0; double last = 0, worst = 0;
            static double Lerp(double a, double b, double t) => a + (b - a) * t;
            double pop = 1; // «вдох» при появлении
            void Place(double u)
            {
                // размер уменьшается плавно и заметно на всём пути (раньше почти весь — в первые кадры, выглядело как «прыжок»)
                double ks = (1 - Math.Cos(Math.PI * u)) / 2;
                double km = u < 0.5 ? 4 * u * u * u : 1 - Math.Pow(-2 * u + 2, 3) / 2;                // путь: плавно, с разгоном
                double w = Lerp(from.Width, to.Width, ks), h = Lerp(from.Height, to.Height, ks);
                double cx = Lerp(from.X + from.Width / 2, to.X + to.Width / 2, km);
                double cy = Lerp(from.Y + from.Height / 2, to.Y + to.Height / 2, km);
                // Размер окна меняется ступеньками (раз в ~15%), между ними снимок плавно уменьшается масштабом:
                // смена размера прозрачного окна дорогая, а так большой снимок быстро становится лёгким окошком
                bool resize = stepW <= 0 || w < stepW * 0.85 || h < stepH * 0.85;
                if (resize) { stepW = Math.Max(1, Math.Ceiling(w)); stepH = Math.Max(1, Math.Ceiling(h)); resizes++; }
                scale.ScaleX = w / stepW * pop; scale.ScaleY = h / stepH * pop;
                SetWindowPos(hwnd, new IntPtr(-1), (int)Math.Round(cx - stepW / 2), (int)Math.Round(cy - stepH / 2),
                    (int)stepW, (int)stepH, NoActivate | (resize ? 0u : NoSize));
                win.Opacity = u > 0.82 ? Math.Max(0, 1 - (u - 0.82) / 0.18) : 1;
            }
            Place(0);
            bool finished = false;

            EventHandler tick = null;
            bool inTick = false;
            TimeSpan lastRT = TimeSpan.Zero;
            tick = (_, e) =>
            {
                if (e is RenderingEventArgs re) { if (re.RenderingTime == lastRT) return; lastRT = re.RenderingTime; }
                if (finished || inTick) return; // окно закрывается / кадр вызван изнутри предыдущего — пропускаем
                inTick = true;
                try { Frame(); } finally { inTick = false; }
            };
            void Frame()
            {
                double held = sw.Elapsed.TotalMilliseconds;
                // лёгкое «нажатие» на месте (до 97% и обратно) — глаз замечает старт, но скачка размера нет
                pop = held < Hold ? 1 - 0.03 * Math.Sin(Math.PI * held / Hold) : 1;
                double t = Math.Max(0, held - Hold);
                frames++;
                double now = sw.Elapsed.TotalMilliseconds;
                worst = Math.Max(worst, now - last);
                last = now;
                double u = Math.Min(1, t / Ms);
                Place(u);
                // SetWindowPos внутри Place отдаёт управление Windows, и та может успеть вызвать следующий кадр
                // прямо отсюда — он и завершил бы полёт, а потом ещё раз этот (в журнале было два «полёт #1»)
                if (finished || u < 1) return;
                finished = true;
                CompositionTarget.Rendering -= tick;
                win.Close();
                try { AppHost.Window?.Gulp(); } catch { } // чёлка «проглатывает» снимок
                Journal.Info("capture", "полёт #" + id + " в чёлку (окошком): кадров " + frames + " за " + Math.Round(t) + " мс (~"
                    + Math.Round(frames * 1000.0 / Math.Max(1, t + Hold)) + " к/с, монитор " + hz + " Гц), худший кадр " + Math.Round(worst) + " мс, смен размера " + resizes);
                done?.Invoke();
            }
            // окошко нарисовано поверх слоя — только теперь прячем слой и начинаем отсчёт (без «провала» между ними)
            bool begun = false;
            void Begin()
            {
                if (begun) return;
                begun = true;
                // ошибка в «показан» (слой уже закрыт страховкой) не должна оставить окошко висеть на экране
                try { shown?.Invoke(); } catch (Exception ex) { Journal.Warn("capture", "полёт: " + ex.Message); }
                sw.Restart();
                CompositionTarget.Rendering += tick;
            }
            win.ContentRendered += (_, __) => Begin();
            if (win.IsLoaded && win.IsVisible) win.Dispatcher.BeginInvoke(new Action(Begin), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }
}
