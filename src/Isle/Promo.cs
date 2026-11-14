using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Isle.Services;

namespace Isle
{
    /// <summary>
    /// Съёмка рекламного ролика: Isle.exe --promo &lt;папка&gt;. Демо-данные (трек, громкость, ответ ИИ) и сценарий
    /// настоящей мышью и клавиатурой: плашки, раскрытие по краю, вкладки, громкость, чат с ИИ, заметка, погода,
    /// снимок с мозаикой. Экран пишется своим захватом (видит и прозрачную чёлку) в rec.mp4 30 к/с,
    /// положение курсора — в cursor.txt, время сцен — в promo-phases.txt.
    /// </summary>
    public static class Promo
    {
        // ---------- ввод ----------
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
        [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public KEYBDINPUT ki; public long pad1; }
        [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public IntPtr extra; }

        private static void Click() { mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(40); mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); }
        private static void Key(byte vk) { keybd_event(vk, 0, 0, UIntPtr.Zero); keybd_event(vk, 0, 2, UIntPtr.Zero); }

        private static async Task TypeText(string text, int msPerChar = 55)
        {
            foreach (var ch in text)
            {
                var down = new INPUT { type = 1, ki = new KEYBDINPUT { scan = ch, flags = 0x0004 } };
                var up = new INPUT { type = 1, ki = new KEYBDINPUT { scan = ch, flags = 0x0004 | 0x0002 } };
                SendInput(2, new[] { down, up }, Marshal.SizeOf<INPUT>());
                await Task.Delay(msPerChar + (ch == ' ' ? 30 : 0));
            }
        }

        private static (int x, int y) _pos;
        /// <summary>Плавный перевод курсора (как рукой: разгон и торможение).</summary>
        private static async Task MoveTo(int x, int y, int ms)
        {
            var (x0, y0) = _pos;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                double u = sw.ElapsedMilliseconds / (double)ms;
                u = u < 0.5 ? 4 * u * u * u : 1 - Math.Pow(-2 * u + 2, 3) / 2;
                SetCursorPos((int)(x0 + (x - x0) * u), (int)(y0 + (y - y0) * u));
                await Task.Delay(8);
            }
            SetCursorPos(x, y);
            _pos = (x, y);
        }
        private static async Task DragTo(int x, int y, int ms)
        {
            mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(60);
            await MoveTo(x, y, ms);
            await Task.Delay(60);
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
        }
        private static Point Center(FrameworkElement el, double fx = 0.5, double fy = 0.5) => el.PointToScreen(new Point(el.ActualWidth * fx, el.ActualHeight * fy));
        private static async Task ClickEl(FrameworkElement el, int ms = 600, double fx = 0.5, double fy = 0.5)
        {
            var p = Center(el, fx, fy);
            await MoveTo((int)p.X, (int)p.Y, ms);
            await Task.Delay(120);
            Click();
        }

        // ---------- запись экрана (видит и прозрачные окна) ----------
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr sec, uint off);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO ci);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINTI p);
        [StructLayout(LayoutKind.Sequential)] private struct POINTI { public int x, y; }
        [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int w, int h, uint step, IntPtr brush, uint flags);
        [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
        [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFOHEADER { public int size, width, height; public short planes, bits; public int compression, sizeImage, xppm, yppm, clrUsed, clrImportant; }
        [StructLayout(LayoutKind.Sequential)] private struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public int x, y; }
        [StructLayout(LayoutKind.Sequential)] private struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

        private static volatile bool _recording;
        private static Thread _recThread;
        private static Process _ffmpeg;

        private static void StartRecording(string dir, int W, int H)
        {
            _ffmpeg = Process.Start(new ProcessStartInfo("ffmpeg",
                $"-hide_banner -loglevel error -y -f rawvideo -pix_fmt bgra -s {W}x{H} -framerate 30 -i - -c:v libx264 -preset ultrafast -crf 15 -pix_fmt yuv420p \"{Path.Combine(dir, "rec.mp4")}\"")
            { UseShellExecute = false, RedirectStandardInput = true, CreateNoWindow = true });
            var stdin = _ffmpeg.StandardInput.BaseStream;
            var cursorLog = new StreamWriter(Path.Combine(dir, "cursor.txt"), false, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, "rec-start.txt"), DateTime.UtcNow.ToString("O"));
            _recording = true;
            _recThread = new Thread(() =>
            {
                var screen = GetDC(IntPtr.Zero);
                var mem = CreateCompatibleDC(screen);
                var bi = new BITMAPINFOHEADER { size = Marshal.SizeOf<BITMAPINFOHEADER>(), width = W, height = -H, planes = 1, bits = 32 };
                var bmp = CreateDIBSection(screen, ref bi, 0, out var bits, IntPtr.Zero, 0);
                SelectObject(mem, bmp);
                var buf = new byte[W * H * 4];
                var sw = Stopwatch.StartNew();
                long written = 0;
                while (_recording)
                {
                    BitBlt(mem, 0, 0, W, H, screen, 0, 0, 0x00CC0020 | 0x40000000); // SRCCOPY | CAPTUREBLT — вместе с прозрачными окнами
                    // курсор не рисуем — его дорисует монтаж по cursor.txt (на машине GitHub он в захват не попадает)
                    var ci = new CURSORINFO();
                    if (GetCursorPos(out var cp)) { ci.x = cp.x; ci.y = cp.y; } else { ci.x = _pos.x; ci.y = _pos.y; }
                    Marshal.Copy(bits, buf, 0, buf.Length);
                    for (int i = 3; i < buf.Length; i += 4) buf[i] = 255;
                    // держим ровно 30 к/с: если кадр снимался дольше — дублируем его
                    long due = (long)(sw.Elapsed.TotalSeconds * 30) + 1;
                    while (written < due)
                    {
                        try { stdin.Write(buf, 0, buf.Length); } catch { _recording = false; break; }
                        cursorLog.WriteLine(written + "\t" + ci.x + "\t" + ci.y);
                        written++;
                    }
                    long next = (long)((written) * 1000.0 / 30);
                    int wait = (int)(next - sw.ElapsedMilliseconds);
                    if (wait > 0) Thread.Sleep(wait);
                }
                cursorLog.Dispose();
                DeleteObject(bmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
            }) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "promo rec" };
            _recThread.Start();
        }

        private static void StopRecording()
        {
            _recording = false;
            _recThread?.Join(3000);
            try { _ffmpeg.StandardInput.Close(); _ffmpeg.WaitForExit(60000); } catch { }
        }

        // ---------- сцены ----------
        private static StreamWriter _phases;
        private static void Scene(string name)
        {
            _phases.WriteLine(DateTime.UtcNow.ToString("O") + "\t" + name);
            _phases.Flush();
            Journal.Info("promo", "сцена: " + name);
        }
        private static Task Wait(double s) => Task.Delay(TimeSpan.FromSeconds(s));
        private static void Show(Toast t) => ToastBus.Show(t);

        private static BitmapImage Cover()
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0xFF, 0x6B, 0x9A), Color.FromRgb(0x5B, 0x3B, 0xFF), 45), null, new Rect(0, 0, 300, 300));
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xD1, 0x66)), null, new Point(205, 95), 70, 70);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x60, 0x10, 0x08, 0x30)), null, new Rect(0, 200, 300, 100));
            }
            var rtb = new RenderTargetBitmap(300, 300, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rtb));
            var ms = new MemoryStream(); enc.Save(ms); ms.Position = 0;
            var bi = new BitmapImage(); bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.StreamSource = ms; bi.EndInit(); bi.Freeze();
            return bi;
        }

        public static async Task RunAsync(MainWindow w, string dir)
        {
            Directory.CreateDirectory(dir);
            _phases = new StreamWriter(Path.Combine(dir, "promo-phases.txt"), false, new UTF8Encoding(false));
            try
            {
                var s = AppSettings.Current;
                s.WelcomeShown = true; s.SaveScreenshots = true; s.HideInFullscreen = false; s.ExpandOnHover = true;
                s.ShowInCaptures = true; w.ApplyCaptureVisibility(); // иначе чёлка спрятана от записи экрана
                s.AiKey = "demo"; // строка «нужен API-ключ» в ролике не нужна — отвечает демо-ИИ
                try { typeof(UpdateService).GetField("_notified", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.SetValue(null, true); } catch { }
                AudioService.DemoVolume = 0.42f; AudioService.DemoDevice = "Динамики (Realtek Audio)";
                AiService.Demo = q => q.Contains("таймер", StringComparison.OrdinalIgnoreCase)
                    ? "Готово! Поставил таймер на 5 минут — он уже тикает в чёлке ⏱\n\n[[timer: 5m]]"
                    : "Конечно! Вот что можно сделать:\n• выделить область и замазать лишнее\n• отправить снимок в чат";
                await Wait(2.5);
                var wall = ShowWallpaper();
                // чёлка — непрозрачное окно (см. App): фон — те же обои, ровно под ним
                w.Background = WallBrush(w.Left, w.Top);
                await Wait(2);

                var dev = PresentationSource.FromVisual(w)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
                int W = (int)(SystemParameters.PrimaryScreenWidth * dev.M11), H = (int)(SystemParameters.PrimaryScreenHeight * dev.M22);
                _pos = (W - 160, H - 220); SetCursorPos(_pos.x, _pos.y);
                StartRecording(dir, W, H);
                await Wait(0.6);

                // 1. музыка: в чёлке трек, Клоди танцует
                Scene("music");
                AppHost.Media.Demo(new MediaInfo
                {
                    Title = "Neon Nights", Artist = "Lumen", SourceApp = "Spotify", IsPlaying = true, HasSession = true,
                    Duration = TimeSpan.FromSeconds(214), Position = TimeSpan.FromSeconds(71), PositionStamp = DateTime.Now, Cover = Cover()
                });
                try { w.Buddy.Bpm = 118; w.Buddy.Energy = 0.6; } catch { }
                await Wait(5.5);

                // 2. Caps Lock, громкость
                Scene("caps");
                Show(new Toast { Kind = ToastKind.Caps, Title = "Caps Lock включён", Icon = Icons.Caps, On = true, ReplaceKey = "caps", Duration = TimeSpan.FromSeconds(1.3) });
                await Wait(1.2);
                Show(new Toast { Kind = ToastKind.Caps, Title = "Caps Lock выключен", Icon = Icons.Caps, On = false, ReplaceKey = "caps", Duration = TimeSpan.FromSeconds(1.1) });
                await Wait(1.6);
                Scene("volume");
                for (int v = 42; v <= 74; v += 4) { AppHost.Audio.Volume = v / 100f; await Task.Delay(120); }
                await Wait(1.6);

                // 3. сообщение, загрузка, ошибка
                Scene("message");
                Show(new Toast { Kind = ToastKind.Notification, Title = "Маша  ·  Telegram", Subtitle = "Идём сегодня в кино? 🍿", Icon = Icons.Bell, Duration = TimeSpan.FromSeconds(2.6) });
                await Wait(3.0);
                Scene("download");
                for (int i = 0; i <= 20; i++)
                {
                    double p = i / 20.0;
                    Show(new Toast { Kind = ToastKind.Download, Title = "Cyberpunk 2077", SpeedText = "48 МБ/с", EtaText = i < 20 ? (20 - i) * 3 + " с" : "", Subtitle = "Steam · " + (int)(p * 100) + "%",
                        Icon = Icons.Download, Progress = p, ReplaceKey = "dl-promo", Duration = TimeSpan.FromSeconds(1.5) });
                    await Task.Delay(110);
                }
                Show(new Toast { Kind = ToastKind.Success, Title = "Загрузка завершена", Subtitle = "Cyberpunk 2077 · можно играть", Icon = Icons.Check, Important = true, ReplaceKey = "dl-promo", Duration = TimeSpan.FromSeconds(2.0) });
                await Wait(2.4);
                Scene("error");
                Show(new Toast { Kind = ToastKind.Error, Title = "Нет подключения", Subtitle = "VPN отключился", Icon = Icons.Info, Important = true, Duration = TimeSpan.FromSeconds(1.8) });
                await Wait(2.4);

                // 4. раскрытие: курсор упирается в верх экрана над чёлкой
                Scene("expand");
                await MoveTo(W / 2, 30, 900);
                await MoveTo(W / 2, 0, 250);
                await Wait(0.9);
                if (w.State != IslandState.Expanded) w.Expand("home");
                await Wait(0.8);

                // 5. главная: громкость ползунком
                Scene("home");
                var home = w.TabHost.Content as FrameworkElement;
                if (home?.FindName("Volume") is Slider hv)
                {
                    var a = Center(hv, 0.42, 0.5); var b = Center(hv, 0.8, 0.5);
                    await MoveTo((int)a.X, (int)a.Y, 800);
                    await Task.Delay(150);
                    await DragTo((int)b.X, (int)b.Y, 900);
                }
                await Wait(1.0);

                // 6. чат с ИИ
                Scene("chat");
                await ClickEl(w.TabChat, 700);
                await Wait(0.8);
                if (w.TabHost.Content is FrameworkElement chat && chat.FindName("Input") is TextBox input)
                {
                    await ClickEl(input, 600, 0.3);
                    await Wait(0.3);
                    await TypeText("Поставь таймер на 5 минут");
                    await Wait(0.4);
                    Key(0x0D);
                }
                await Wait(4.2);

                // 7. заметка
                Scene("notes");
                await ClickEl(w.TabNotes, 700);
                await Wait(0.7);
                if (w.TabHost.Content is FrameworkElement notes && notes.FindName("Editor") is FrameworkElement ed)
                {
                    await ClickEl(ed, 600, 0.3, 0.2);
                    await Wait(0.3);
                    await TypeText("Купить попкорн к кино 🍿", 60);
                }
                await Wait(1.2);

                // 8. погода
                Scene("weather");
                await ClickEl(w.TabWeather, 700);
                await Wait(2.4);

                // 9. сворачиваем — в чёлке таймер и музыка
                Scene("collapse");
                await MoveTo(W / 2, (int)(H * 0.72), 900);
                await Wait(0.6);
                if (w.State == IslandState.Expanded) w.Collapse();
                await Wait(3.0);

                // 10. снимок: выделение → мозаика по строке с паролем → полёт в чёлку
                Scene("shot");
                _ = Tools.RegionShot();
                var overlay = await Bench.WaitOverlay();
                if (overlay != null)
                {
                    await Wait(0.5);
                    await MoveTo((int)(W * 0.27), (int)(H * 0.42), 600);
                    await DragTo((int)(W * 0.73), (int)(H * 0.95), 1000);
                    await Wait(0.6);
                    if (await Bench.ClickTool(overlay, "Mosaic"))
                    {
                        _pos = (W / 2, (int)(H * 0.82));
                        await Wait(0.4);
                        Scene("mosaic");
                        await MoveTo((int)(W * 0.474), (int)(H * 0.818), 700);
                        await DragTo((int)(W * 0.648), (int)(H * 0.872), 1300);
                        await Wait(0.6);
                    }
                    Scene("save");
                    Key(0x0D);
                    await Wait(3.2);
                }
                Scene("end");
                await Wait(2.5);
                StopRecording();
                wall.Close();
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(dir, "error.txt"), ex.ToString());
                Scene("error: " + ex.Message);
                try { StopRecording(); } catch { }
            }
            finally
            {
                _phases.Dispose();
                Journal.Flush();
                try { if (File.Exists(Journal.PathNow)) File.Copy(Journal.PathNow, Path.Combine(dir, "isle.log"), true); } catch { }
                File.WriteAllText(Path.Combine(dir, "done.txt"), DateTime.UtcNow.ToString("O"));
                Application.Current.Shutdown();
            }
        }

        /// <summary>«Рабочий стол» за чёлкой: градиент и окно-чат (его потом замажет мозаика). Не во весь экран — иначе чёлка спрячется.</summary>
        /// <summary>Градиент обоев в координатах экрана (точки), сдвинутый под окно с левым верхним углом (ox, oy).</summary>
        private static LinearGradientBrush WallBrush(double ox, double oy)
        {
            double sw = SystemParameters.PrimaryScreenWidth, sh = SystemParameters.PrimaryScreenHeight;
            return new LinearGradientBrush(new GradientStopCollection
            {
                new GradientStop(Color.FromRgb(0x2B, 0x1A, 0x5E), 0), new GradientStop(Color.FromRgb(0x8E, 0x2D, 0x7A), 0.55), new GradientStop(Color.FromRgb(0xF0, 0x8A, 0x4B), 1)
            }, new Point(-ox, -oy), new Point(sw - ox, sh - oy)) { MappingMode = BrushMappingMode.Absolute };
        }

        private static Window ShowWallpaper()
        {
            var bg = WallBrush(0, 0);
            var chat = new StackPanel { Margin = new Thickness(26) };
            string[] lines = { "Маша: идём сегодня в кино?", "Я: давай, во сколько?", "Маша: в 19:40, билеты уже взяла 🎟", "Я: пароль от вайфая — kino2024!", "Маша: ахах, отправила тебе адрес" };
            foreach (var l in lines)
                chat.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1C, 0x1C, 0x22)), CornerRadius = new CornerRadius(14),
                    Padding = new Thickness(16, 10, 16, 10), Margin = new Thickness(0, 0, 0, 10), HorizontalAlignment = l.StartsWith("Я") ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                    Child = new TextBlock { Text = l, Foreground = Brushes.White, FontSize = 20 }
                });
            var card = new Border
            {
                Width = 620, Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x10, 0x10, 0x16)), CornerRadius = new CornerRadius(22),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 50), Child = chat
            };
            double sw = SystemParameters.PrimaryScreenWidth, sh = SystemParameters.PrimaryScreenHeight;
            var win = new Window
            {
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
                Left = 0, Top = 0, Width = sw, Height = sh - 2, Background = bg, Content = new Grid { Children = { card } }
            };
            win.Show();
            return win;
        }
    }
}
