using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Isle.Services;

namespace Isle
{
    /// <summary>
    /// Съёмка рекламного ролика: Isle.exe --promo &lt;папка&gt;. Показывает фишки по сценарию (плашки, загрузка,
    /// панель, Клоди, снимок с мозаикой и полётом), а workflow пишет экран. Время каждой сцены — в promo-phases.txt,
    /// по нему ролик потом монтируется.
    /// </summary>
    public static class Promo
    {
        [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        private static StreamWriter _phases;

        private static void Scene(string name)
        {
            _phases.WriteLine(DateTime.UtcNow.ToString("O") + "\t" + name);
            _phases.Flush();
            Journal.Info("promo", "сцена: " + name);
        }

        private static Task Wait(double s) => Task.Delay(TimeSpan.FromSeconds(s));

        // ---------- запись самой чёлки ----------
        // Чёлка — прозрачное окно, обычная запись экрана его не видит. Поэтому Isle сам рисует своё окно в картинку
        // 30 раз в секунду (вдвое крупнее, с прозрачным фоном); из кадров workflow собирает видео для монтажа.
        private static System.Windows.Threading.DispatcherTimer _rec;
        private static int _frame;
        private static readonly System.Diagnostics.Stopwatch _recClock = new();
        private static StreamWriter _frameLog;

        private static void StartIslandRec(Window w, string dir)
        {
            var fdir = Path.Combine(dir, "island");
            Directory.CreateDirectory(fdir);
            _frameLog = new StreamWriter(Path.Combine(dir, "island-frames.txt"), false, new UTF8Encoding(false));
            var root = (FrameworkElement)w.Content;
            // только область вокруг чёлки (по центру, сверху) — так кадр рисуется быстро и запись идёт ~30 к/с
            double rw = Math.Min(root.ActualWidth, 640), rh = Math.Min(root.ActualHeight, 520);
            var region = new Rect((root.ActualWidth - rw) / 2, 0, rw, rh);
            const double K = 1.6;
            int pw = (int)Math.Ceiling(rw * K), ph = (int)Math.Ceiling(rh * K);
            var brush = new VisualBrush(root) { Viewbox = region, ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
            File.WriteAllText(Path.Combine(dir, "island-region.txt"), region + " ×" + K + " → " + pw + "×" + ph);
            _recClock.Restart();
            _rec = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1000.0 / 30) };
            _rec.Tick += (_, __) =>
            {
                try
                {
                    var dv = new DrawingVisual();
                    using (var dc = dv.RenderOpen()) dc.DrawRectangle(brush, null, new Rect(0, 0, pw, ph));
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(dv);
                    rtb.Freeze();
                    int n = _frame++;
                    _frameLog.WriteLine(n + "\t" + _recClock.ElapsedMilliseconds + "\t" + DateTime.UtcNow.ToString("O"));
                    var file = Path.Combine(fdir, "f" + n.ToString("00000") + ".png");
                    Task.Run(() =>
                    {
                        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                        using var fs = File.Create(file);
                        enc.Save(fs);
                    });
                }
                catch (Exception ex) { Journal.Warn("promo", "кадр чёлки: " + ex.Message); }
            };
            _rec.Start();
        }

        private static void StopIslandRec()
        {
            _rec?.Stop();
            try { _frameLog?.Flush(); _frameLog?.Dispose(); } catch { }
            System.Threading.Thread.Sleep(1500); // дописываются последние кадры
        }

        public static async Task RunAsync(MainWindow w, string dir)
        {
            Directory.CreateDirectory(dir);
            _phases = new StreamWriter(Path.Combine(dir, "promo-phases.txt"), false, new UTF8Encoding(false));
            try
            {
                AppSettings.Current.WelcomeShown = true;
                AppSettings.Current.SaveScreenshots = true;
                AppSettings.Current.HideInFullscreen = false;
                // «доступна новая версия» в ролике не нужна
                try { typeof(UpdateService).GetField("_notified", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.SetValue(null, true); } catch { }
                await Wait(3);
                var wall = ShowWallpaper();
                await Wait(2.5);

                int sw = (int)SystemParameters.PrimaryScreenWidth, sh = (int)SystemParameters.PrimaryScreenHeight;
                var dev = PresentationSource.FromVisual(w)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
                int W = (int)(sw * dev.M11), H = (int)(sh * dev.M22);
                Journal.Info("promo", "экран " + W + "×" + H);

                StartIslandRec(w, dir);
                // 1. Клоди танцует в свёрнутой чёлке
                Scene("idle");
                try { w.Buddy.Mood = Helpers.Mood.Music; w.Buddy.Bpm = 120; w.Buddy.Energy = 0.6; w.Buddy.ForceDance(19); } catch { }
                await Wait(3.2);
                try { w.Buddy.ForceDance(6); } catch { }
                await Wait(2.2);

                // 2. Caps Lock
                Scene("caps");
                Show(new Toast { Kind = ToastKind.Caps, Title = "Caps Lock включён", Icon = Icons.Caps, On = true, ReplaceKey = "caps", Duration = TimeSpan.FromSeconds(1.4) });
                await Wait(1.1);
                Show(new Toast { Kind = ToastKind.Caps, Title = "Caps Lock выключен", Icon = Icons.Caps, On = false, ReplaceKey = "caps", Duration = TimeSpan.FromSeconds(1.2) });
                await Wait(1.6);

                // 3. Громкость
                Scene("volume");
                for (int v = 30; v <= 80; v += 5)
                {
                    Show(new Toast { Kind = ToastKind.Volume, Title = "Громкость", SpeedText = v.ToString(), Icon = Icons.Volume, Progress = v / 100.0, ReplaceKey = "volume", Duration = TimeSpan.FromSeconds(1.2) });
                    await Task.Delay(110);
                }
                await Wait(1.5);

                // 4. Уведомление
                Scene("message");
                Show(new Toast { Kind = ToastKind.Notification, Title = "Маша  ·  Telegram", Subtitle = "Идём сегодня в кино? 🍿", Icon = Icons.Bell, Duration = TimeSpan.FromSeconds(2.6) });
                await Wait(3.0);

                // 5. Загрузка
                Scene("download");
                for (int i = 0; i <= 20; i++)
                {
                    double p = i / 20.0;
                    Show(new Toast { Kind = ToastKind.Download, Title = "Cyberpunk 2077", SpeedText = "48 МБ/с", EtaText = i < 20 ? (20 - i) * 3 + " с" : "", Subtitle = "Steam · " + (int)(p * 100) + "%",
                        Icon = Icons.Download, Progress = p, ReplaceKey = "dl-promo", Duration = TimeSpan.FromSeconds(1.5) });
                    await Task.Delay(120);
                }
                Show(new Toast { Kind = ToastKind.Success, Title = "Загрузка завершена", Subtitle = "Cyberpunk 2077 · можно играть", Icon = Icons.Check, Important = true, ReplaceKey = "dl-promo", Duration = TimeSpan.FromSeconds(2.2) });
                await Wait(2.6);

                // 6. Ошибка — красная
                Scene("error");
                Show(new Toast { Kind = ToastKind.Error, Title = "Нет подключения", Subtitle = "VPN отключился", Icon = Icons.Info, Important = true, Duration = TimeSpan.FromSeconds(2.0) });
                await Wait(2.6);

                // 7. Панель
                Scene("panel");
                w.Expand("home");
                await Wait(3.6);
                w.Collapse();
                await Wait(1.4);

                // 8. Снимок: выделение → мозаика → полёт в чёлку (его снимает обычная запись экрана)
                Scene("shot");
                _ = Tools.RegionShot();
                var overlay = await Bench.WaitOverlay();
                if (overlay != null)
                {
                    await Wait(0.6);
                    await Bench.Drag((int)(W * 0.18), (int)(H * 0.22), (int)(W * 0.82), (int)(H * 0.78), 900);
                    await Wait(0.7);
                    if (await Bench.ClickTool(overlay, "Mosaic"))
                    {
                        await Wait(0.4);
                        Scene("mosaic");
                        await Bench.Drag((int)(W * 0.26), (int)(H * 0.34), (int)(W * 0.62), (int)(H * 0.46), 1300);
                        await Wait(0.6);
                    }
                    Scene("save");
                    keybd_event(0x0D, 0, 0, UIntPtr.Zero);
                    keybd_event(0x0D, 0, 2, UIntPtr.Zero);
                    await Wait(3.2);
                }

                Scene("end");
                try { w.Buddy.ForceDance(20); } catch { }
                await Wait(3);
                StopIslandRec();
                wall.Close();
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(dir, "error.txt"), ex.ToString());
                Scene("error: " + ex.Message);
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

        private static void Show(Toast t) => ToastBus.Show(t);

        /// <summary>Красивый «рабочий стол» за чёлкой: градиент и окно-чат (его скроет мозаика). Не во весь экран — иначе чёлка спрячется.</summary>
        private static Window ShowWallpaper()
        {
            var bg = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromRgb(0x2B, 0x1A, 0x5E), 0), new GradientStop(Color.FromRgb(0x8E, 0x2D, 0x7A), 0.55), new GradientStop(Color.FromRgb(0xF0, 0x8A, 0x4B), 1)
                }, new Point(0, 0), new Point(1, 1));
            var chat = new StackPanel { Margin = new Thickness(26) };
            string[] lines = { "Маша: идём сегодня в кино?", "Я: давай, во сколько?", "Маша: в 19:40, билеты уже взяла 🎟", "Я: пароль от вайфая — kino2024!", "Маша: ахах, отправила тебе адрес", "Я: ок, буду" };
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
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = chat
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
