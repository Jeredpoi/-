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
            // 1) графический слой Windows: анимацию ведёт сам DWM — частота монитора, даже если Isle занят
            try { FlyComp(id, hz, image, from, shown, done); return; }
            catch (Exception ex) { Journal.Warn("capture", "полёт на графическом слое не запустился: " + ex); }
            // 2) запасной путь — окно со «стеклом» DWM, 3) — обычное прозрачное окошко
            if (!soft)
            {
                try { FlyGlass(id, hz, image, from, shown, done); return; }
                catch (Exception ex) { Journal.Warn("capture", "полёт через DWM не запустился, обычным окошком: " + ex.Message); }
            }
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
                Journal.Debug("flight", "#" + id + " кадр " + frames + " (окошко): " + Math.Round(now) + " мс, u=" + u.ToString("0.000") + ", окно " + stepW + "×" + stepH);
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
    
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct Margins { public int L, R, T, B; }
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins m);
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

        /// <summary>Куда летит снимок: середина чёлки, высота — 80% чёлки, пропорции снимка (пиксели экрана).</summary>
        private static Rect Target(Rect from)
        {
            try
            {
                var isl = AppHost.Window.IslandScreenRect(out _);
                double h = Math.Max(20, isl.Height * 0.8);
                double w = Math.Min(isl.Width * 0.8, h * from.Width / Math.Max(1, from.Height));
                return new Rect(isl.X + isl.Width / 2 - w / 2, isl.Y + isl.Height / 2 - h / 2, w, h);
            }
            catch
            {
                double cx = Isle.Native.Win32.GetSystemMetrics(0) / 2.0;
                return new Rect(cx - 30, 10, 60, 34);
            }
        }

        /// <summary>
        /// Полёт без «прозрачного» (layered) окна. Такое окно Windows каждый кадр целиком копирует из видеокарты
        /// в память — на большом снимке это десятки мегабайт за кадр, отсюда рывки. Здесь одно обычное окно на всю
        /// траекторию, прозрачность даёт сам DWM (рамка «стекла» на всё окно), а снимок двигается и уменьшается
        /// внутри него трансформацией: окно не двигается и не меняет размер, видеокарта рисует только снимок —
        /// с частотой монитора.
        /// </summary>
        private static void FlyGlass(int id, int hz, BitmapSource image, Rect from, Action shown, Action done)
        {
            Rect to = Target(from);
            var area = Rect.Union(from, to);
            area.Inflate(24, 24);
            int ax = (int)Math.Floor(area.X), ay = (int)Math.Floor(area.Y), aw = (int)Math.Ceiling(area.Width), ah = (int)Math.Ceiling(area.Height);

            var win = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = false, Background = Brushes.Transparent,
                Topmost = true, ShowInTaskbar = false, ShowActivated = false, ResizeMode = ResizeMode.NoResize,
                Left = -32000, Top = -32000, Width = 10, Height = 10, Focusable = false, IsHitTestVisible = false
            };
            var canvas = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
            win.Content = canvas;
            var brush = new ImageBrush(image) { Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.Linear);
            var scale = new ScaleTransform(1, 1);
            var move = new TranslateTransform();
            var tg = new TransformGroup();
            tg.Children.Add(scale); tg.Children.Add(move);
            var card = new Border
            {
                Background = brush, CornerRadius = new CornerRadius(10),
                BorderBrush = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), BorderThickness = new Thickness(1),
                RenderTransformOrigin = new Point(0, 0), RenderTransform = tg
            };
            canvas.Children.Add(card);

            IntPtr hwnd = IntPtr.Zero;
            double dpi = 1;
            win.SourceInitialized += (_, __) =>
            {
                hwnd = new WindowInteropHelper(win).Handle;
                var hs = HwndSource.FromHwnd(hwnd);
                hs.CompositionTarget.BackgroundColor = Colors.Transparent; // фон окна — прозрачный, его «пробивает» стекло DWM
                dpi = hs.CompositionTarget.TransformToDevice.M11;
                // чистое всплывающее окно: без рамки, тени, скругления Windows 11 и без анимации появления
                SetWindowLongPtr(hwnd, -16, new IntPtr(unchecked((int)0x80000000)));                       // WS_POPUP
                long ex = GetWindowLongPtr(hwnd, -20).ToInt64() | 0x80 | 0x08000000 | 0x20;                  // TOOLWINDOW | NOACTIVATE | TRANSPARENT
                SetWindowLongPtr(hwnd, -20, new IntPtr(ex));
                var m = new Margins { L = -1, R = -1, T = -1, B = -1 };
                Marshal(DwmExtendFrameIntoClientArea(hwnd, ref m));
                int one = 1, none = unchecked((int)0xFFFFFFFE);
                DwmSetWindowAttribute(hwnd, 3, ref one, 4);    // DWMWA_TRANSITIONS_FORCEDISABLED
                DwmSetWindowAttribute(hwnd, 33, ref one, 4);   // DWMWA_WINDOW_CORNER_PREFERENCE = DONOTROUND
                DwmSetWindowAttribute(hwnd, 34, ref none, 4);  // DWMWA_BORDER_COLOR = нет рамки
                SetWindowPos(hwnd, new IntPtr(-1), ax, ay, aw, ah, NoActivate | 0x0020 /* FRAMECHANGED */);
            };

            const double Hold = 160, Ms = 900;
            var sw = Stopwatch.StartNew();
            int frames = 0; double last = 0, worst = 0;
            static double Lerp(double a, double b, double t) => a + (b - a) * t;
            void Place(double u, double pop)
            {
                double ks = (1 - Math.Cos(Math.PI * u)) / 2;                                             // размер — плавно на всём пути
                double km = u < 0.5 ? 4 * u * u * u : 1 - Math.Pow(-2 * u + 2, 3) / 2;                  // путь — с разгоном
                double w = Lerp(from.Width, to.Width, ks) * pop, h = Lerp(from.Height, to.Height, ks) * pop;
                double cx = Lerp(from.X + from.Width / 2, to.X + to.Width / 2, km);
                double cy = Lerp(from.Y + from.Height / 2, to.Y + to.Height / 2, km);
                // всё — в точках окна (DIP): пиксели экрана минус угол окна, делённые на масштаб
                scale.ScaleX = w / from.Width; scale.ScaleY = h / from.Height;
                move.X = (cx - w / 2 - ax) / dpi; move.Y = (cy - h / 2 - ay) / dpi;
                card.Opacity = u > 0.82 ? Math.Max(0, 1 - (u - 0.82) / 0.18) : 1;
            }

            bool finished = false, inTick = false, begun = false;
            TimeSpan lastRT = TimeSpan.Zero;
            EventHandler tick = null;
            tick = (_, e) =>
            {
                if (e is RenderingEventArgs re) { if (re.RenderingTime == lastRT) return; lastRT = re.RenderingTime; }
                if (finished || inTick) return;
                inTick = true;
                try
                {
                    double held = sw.Elapsed.TotalMilliseconds;
                    double pop = held < Hold ? 1 - 0.03 * Math.Sin(Math.PI * held / Hold) : 1;
                    double t = Math.Max(0, held - Hold);
                    frames++;
                    double gap = held - last;
                    worst = Math.Max(worst, gap);
                    last = held;
                    double u = Math.Min(1, t / Ms);
                    long p0 = Stopwatch.GetTimestamp();
                    Place(u, pop);
                    Journal.Debug("flight", "#" + id + " кадр " + frames + ": " + Math.Round(held) + " мс, шаг " + Math.Round(gap, 1) + " мс, u=" + u.ToString("0.000")
                        + ", масштаб " + scale.ScaleX.ToString("0.000") + ", расчёт " + ((Stopwatch.GetTimestamp() - p0) * 1000.0 / Stopwatch.Frequency).ToString("0.00") + " мс");
                    if (u < 1) return;
                    finished = true;
                    CompositionTarget.Rendering -= tick;
                    win.Close();
                    try { AppHost.Window?.Gulp(); } catch { }
                    Journal.Info("capture", "полёт #" + id + " в чёлку (DWM): кадров " + frames + " за " + Math.Round(held) + " мс (~"
                        + Math.Round(frames * 1000.0 / Math.Max(1, held)) + " к/с, монитор " + hz + " Гц), худший кадр " + Math.Round(worst) + " мс");
                    done?.Invoke();
                }
                finally { inTick = false; }
            };
            try { win.Show(); }
            catch { try { win.Close(); } catch { } throw; } // запасной путь — обычное окошко (см. Fly)
            card.Width = from.Width / dpi; card.Height = from.Height / dpi;
            Place(0, 1);
            // Слой выделения прячем, только когда окно полёта уже реально на экране: WPF сообщает «нарисовано»
            // раньше, чем DWM покажет кадр, — снимок на один кадр пропадал («мигал»). Ждём 2 кадра и DwmFlush.
            int warm = 0;
            EventHandler warmTick = null;
            warmTick = (_, e) =>
            {
                if (e is RenderingEventArgs re) { if (re.RenderingTime == lastRT) return; lastRT = re.RenderingTime; }
                if (++warm < 3) return;
                CompositionTarget.Rendering -= warmTick;
                try { DwmFlush(); } catch { }
                Journal.Debug("flight", "#" + id + ": окно на экране, прячу слой (окно " + aw + "×" + ah + " px в " + ax + "," + ay + ", масштаб " + dpi + ")");
                try { shown?.Invoke(); } catch (Exception ex) { Journal.Warn("capture", "полёт: " + ex.Message); }
                sw.Restart();
                CompositionTarget.Rendering += tick;
            };
            void Begin()
            {
                if (begun) return;
                begun = true;
                CompositionTarget.Rendering += warmTick;
            }
            win.ContentRendered += (_, __) => Begin();
            if (win.IsLoaded && win.IsVisible) win.Dispatcher.BeginInvoke(new Action(Begin), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmFlush();

        /// <summary>Окно без собственной картинки (WS_EX_NOREDIRECTIONBITMAP): всё, что на нём видно, рисует графический слой.</summary>
        private sealed class CompHost : System.Windows.Forms.NativeWindow
        {
            public CompHost(int x, int y, int w, int h)
            {
                CreateHandle(new System.Windows.Forms.CreateParams
                {
                    Style = unchecked((int)0x80000000),                                  // WS_POPUP
                    ExStyle = 0x00200000 | 0x08 | 0x80 | 0x08000000 | 0x20,              // NOREDIRECTIONBITMAP | TOPMOST | TOOLWINDOW | NOACTIVATE | TRANSPARENT
                    X = x, Y = y, Width = w, Height = h
                });
            }
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);

        /// <summary>
        /// Полёт на графическом слое Windows. Снимок — картинка на видеокарте, путь, уменьшение и затухание —
        /// анимации, которые ведёт DWM (как анимации самой Windows): ни одного кадра не считает Isle, поэтому
        /// плавно на любой частоте монитора, даже если поток интерфейса чем-то занят. Выглядит так же, как раньше:
        /// скругление 10, тонкая светлая рамка, лёгкое «нажатие» на старте, плавное уменьшение и затухание в конце.
        /// </summary>
        private static void FlyComp(int id, int hz, BitmapSource image, Rect from, Action shown, Action done)
        {
            var c = Isle.Helpers.Comp.Compositor;
            Rect to = Target(from);
            var area = Rect.Union(from, to);
            area.Inflate(24, 24);
            int ax = (int)Math.Floor(area.X), ay = (int)Math.Floor(area.Y), aw = (int)Math.Ceiling(area.Width), ah = (int)Math.Ceiling(area.Height);

            var host = new CompHost(ax, ay, aw, ah);
            Windows.UI.Composition.Desktop.DesktopWindowTarget target = null;
            Windows.UI.Composition.CompositionDrawingSurface surface = null;
            try
            {
                target = Isle.Helpers.Comp.Target(host.Handle, true);
                double dpi = Math.Max(1, GetDpiForWindow(host.Handle) / 96.0);
                var root = c.CreateContainerVisual();
                target.Root = root;

                surface = Isle.Helpers.Comp.Surface(image);
                var brush = c.CreateSurfaceBrush(surface);
                brush.Stretch = Windows.UI.Composition.CompositionStretch.Fill;
                brush.BitmapInterpolationMode = Windows.UI.Composition.CompositionBitmapInterpolationMode.Linear;

                float fw = (float)from.Width, fh = (float)from.Height, radius = (float)(10 * dpi);
                var size = new System.Numerics.Vector2(fw, fh);
                var card = c.CreateSpriteVisual();
                card.Brush = brush;
                card.Size = size;
                var clipGeo = c.CreateRoundedRectangleGeometry();
                clipGeo.Size = size; clipGeo.CornerRadius = new System.Numerics.Vector2(radius, radius);
                card.Clip = c.CreateGeometricClip(clipGeo);
                // тонкая светлая рамка, как у окошка раньше
                var frame = c.CreateShapeVisual();
                frame.Size = size;
                var frameGeo = c.CreateRoundedRectangleGeometry();
                frameGeo.Offset = new System.Numerics.Vector2(0.5f, 0.5f);
                frameGeo.Size = size - new System.Numerics.Vector2(1, 1); frameGeo.CornerRadius = new System.Numerics.Vector2(radius, radius);
                var stroke = c.CreateSpriteShape(frameGeo);
                stroke.StrokeBrush = c.CreateColorBrush(Windows.UI.Color.FromArgb(110, 255, 255, 255));
                stroke.StrokeThickness = (float)Math.Max(1, dpi);
                frame.Shapes.Add(stroke);
                card.Children.InsertAtTop(frame);
                card.CenterPoint = new System.Numerics.Vector3(fw / 2, fh / 2, 0);
                var start = new System.Numerics.Vector3((float)(from.X - ax), (float)(from.Y - ay), 0);
                card.Offset = start;
                root.Children.InsertAtTop(card);

                const float Hold = 160, Ms = 900, T = Hold + Ms;
                var end = new System.Numerics.Vector3((float)(to.X + to.Width / 2 - fw / 2 - ax), (float)(to.Y + to.Height / 2 - fh / 2 - ay), 0);
                var easePath = c.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.65f, 0f), new System.Numerics.Vector2(0.35f, 1f));   // разгон и торможение
                var easeSize = c.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.37f, 0f), new System.Numerics.Vector2(0.63f, 1f));   // плавно на всём пути
                var linear = c.CreateLinearEasingFunction();

                var off = c.CreateVector3KeyFrameAnimation();
                off.Duration = TimeSpan.FromMilliseconds(T);
                off.InsertKeyFrame(0, start); off.InsertKeyFrame(Hold / T, start, linear); off.InsertKeyFrame(1, end, easePath);
                var sc = c.CreateVector3KeyFrameAnimation();
                sc.Duration = TimeSpan.FromMilliseconds(T);
                float sx = (float)(to.Width / fw), sy = (float)(to.Height / fh);
                sc.InsertKeyFrame(0, System.Numerics.Vector3.One);
                sc.InsertKeyFrame(Hold / 2 / T, new System.Numerics.Vector3(0.97f, 0.97f, 1), easeSize);   // лёгкое «нажатие»
                sc.InsertKeyFrame(Hold / T, System.Numerics.Vector3.One, easeSize);
                sc.InsertKeyFrame(1, new System.Numerics.Vector3(sx, sy, 1), easeSize);
                var op = c.CreateScalarKeyFrameAnimation();
                op.Duration = TimeSpan.FromMilliseconds(T);
                op.InsertKeyFrame(0, 1); op.InsertKeyFrame((Hold + 0.82f * Ms) / T, 1, linear); op.InsertKeyFrame(1, 0, linear);

                ShowWindow(host.Handle, 4 /* SW_SHOWNOACTIVATE */);
                Journal.Debug("flight", "#" + id + ": графический слой, окно " + aw + "×" + ah + " px в " + ax + "," + ay + ", масштаб " + dpi);

                var sw = Stopwatch.StartNew();
                bool finished = false;
                void Finish(string how)
                {
                    if (finished) return;
                    finished = true;
                    try { target.Root = null; } catch { }
                    try { target.Dispose(); surface.Dispose(); } catch { }
                    try { host.DestroyHandle(); } catch { }
                    try { AppHost.Window?.Gulp(); } catch { }
                    Journal.Info("capture", "полёт #" + id + " в чёлку (графический слой Windows" + how + "): " + sw.ElapsedMilliseconds + " мс, монитор " + hz + " Гц");
                    done?.Invoke();
                }

                // Сначала снимок должен реально появиться на экране — и только потом прячем слой выделения (без мигания)
                int warm = 0;
                TimeSpan lastRT = TimeSpan.Zero;
                EventHandler warmTick = null;
                warmTick = (_, e) =>
                {
                    if (e is RenderingEventArgs re) { if (re.RenderingTime == lastRT) return; lastRT = re.RenderingTime; }
                    if (++warm < 3) return;
                    CompositionTarget.Rendering -= warmTick;
                    try { DwmFlush(); } catch { }
                    try { shown?.Invoke(); } catch (Exception ex) { Journal.Warn("capture", "полёт: " + ex.Message); }
                    sw.Restart();
                    var batch = c.CreateScopedBatch(Windows.UI.Composition.CompositionBatchTypes.Animation);
                    card.StartAnimation("Offset", off);
                    card.StartAnimation("Scale", sc);
                    card.StartAnimation("Opacity", op);
                    batch.End();
                    batch.Completed += (_, __) => Application.Current?.Dispatcher.BeginInvoke(new Action(() => Finish("")));
                    // страховка: если DWM не сообщил о конце анимации
                    var guard = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(T + 600) };
                    guard.Tick += (_, __) => { guard.Stop(); Finish(", по страховке"); };
                    guard.Start();
                };
                CompositionTarget.Rendering += warmTick;
            }
            catch
            {
                try { target?.Dispose(); surface?.Dispose(); } catch { }
                try { host.DestroyHandle(); } catch { }
                throw;
            }
        }

        private static void Marshal(int hr) { if (hr < 0) throw new InvalidOperationException("DWM: 0x" + hr.ToString("X8")); }
}
}
