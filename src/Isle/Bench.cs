using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Isle.Overlays;
using Isle.Services;

namespace Isle
{
    /// <summary>
    /// Стенд для проверки снимков: Isle.exe --bench &lt;папка&gt;. Запускается в GitHub Actions на Windows.
    /// Настоящей мышью и клавиатурой проходит сценарий «выделить → мозаика (маленькая, средняя, на весь экран) →
    /// перо → Enter (сохранение и полёт в чёлку)», отмечает время каждого этапа (bench-phases.txt) и выходит.
    /// Плавность считает workflow по записи экрана: сколько реально разных кадров в секунду было на каждом этапе.
    /// </summary>
    public static class Bench
    {
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
        [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        private const uint LeftDown = 0x0002, LeftUp = 0x0004, KeyUp = 0x0002;

        private static StreamWriter _phases;
        private static void Phase(string name)
        {
            // абсолютное время (UTC, мс) — workflow сопоставит его с началом записи экрана
            _phases.WriteLine(DateTime.UtcNow.ToString("O") + "\t" + name);
            _phases.Flush();
            Journal.Info("bench", "этап: " + name);
        }

        [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr h);
        [DllImport("user32.dll")] private static extern bool CloseClipboard();

        private static void SnapEl(FrameworkElement v, string dir, string name)
        {
            v.UpdateLayout();
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)v.ActualWidth, (int)v.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) { dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(40, 30, 70)), null, new Rect(0, 0, v.ActualWidth, v.ActualHeight)); dc.DrawRectangle(new VisualBrush(v), null, new Rect(0, 0, v.ActualWidth, v.ActualHeight)); }
            rtb.Render(dv);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder(); enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = File.Create(Path.Combine(dir, name + ".png")); enc.Save(fs);
        }

        public static async Task RunAsync(MainWindow w, string dir)
        {
            Directory.CreateDirectory(dir);
            var log = Path.Combine(dir, "log.txt");
            void L(string t) => File.AppendAllText(log, t + "\n");
            try
            {
                AppSettings.Current.WelcomeShown = true;
                AppSettings.Current.AutoUpdateCheck = false;
                await Task.Delay(3000);
                // 150 записей: 50 снимков 3840×2160 за несколько дней
                Directory.CreateDirectory(ClipboardService.ImagesDir);
                var rnd = new Random(1);
                var sw0 = System.Diagnostics.Stopwatch.StartNew();
                var big = new System.Windows.Media.Imaging.RenderTargetBitmap(3840, 2160, 96, 96, PixelFormats.Pbgra32);
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen()) { for (int k = 0; k < 400; k++) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256))), null, new Rect(rnd.Next(3800), rnd.Next(2100), rnd.Next(40, 600), rnd.Next(40, 400))); }
                big.Render(dv);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder(); enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(big));
                var ms = new MemoryStream(); enc.Save(ms); var bytes = ms.ToArray();
                var now = DateTime.Now;
                for (int i = 0; i < 150; i++)
                {
                    var t = now.AddHours(-i * 1.5);
                    if (i % 3 == 0)
                    {
                        var name = "b" + i + ".png"; File.WriteAllBytes(Path.Combine(ClipboardService.ImagesDir, name), bytes);
                        AppHost.Clipboard.Items.Add(new ClipItem { Kind = ClipKind.Image, Text = "Изображение 3840×2160", ImageFile = name, Source = "chrome", Time = t });
                    }
                    else AppHost.Clipboard.Items.Add(new ClipItem { Kind = ClipKind.Text, Text = "запись " + i, Source = "notepad", Time = t });
                }
                L("подготовка: " + sw0.ElapsedMilliseconds + " мс, записей " + AppHost.Clipboard.Items.Count + ", PNG " + bytes.Length / 1024 + " КБ");
                w.SuppressAutoCollapse = true;
                w.Expand("home"); await Task.Delay(1500);
                for (int round = 1; round <= 2; round++)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    w.Expand("clip");
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    L("вкладка «Буфер», открытие " + round + ": интерфейс свободен через " + sw.ElapsedMilliseconds + " мс");
                    await Task.Delay(2500);
                    SnapEl((FrameworkElement)w.Content, dir, "clip-" + round);
                    w.Expand("home"); await Task.Delay(1200);
                }
                var thumbs = Path.Combine(ClipboardService.ImagesDir, "thumbs");
                L("миниатюр в кэше: " + (Directory.Exists(thumbs) ? Directory.GetFiles(thumbs).Length : 0));
                // буфер обмена: обычная запись и чтение
                var s1 = System.Diagnostics.Stopwatch.StartNew();
                bool okT = ClipboardService.SetText("проверка 123");
                L("запись текста: " + okT + " за " + s1.ElapsedMilliseconds + " мс, прочитано «" + Clipboard.GetText() + "»");
                s1.Restart();
                var small = new System.Windows.Media.Imaging.RenderTargetBitmap(200, 100, 96, 96, PixelFormats.Pbgra32); small.Render(dv);
                bool okI = ScreenTools.CopyImage(small);
                L("запись картинки: " + okI + " за " + s1.ElapsedMilliseconds + " мс, в буфере картинка: " + Clipboard.ContainsImage());
                // буфер занят другим потоком (как другой программой) на 3 с
                var held = new System.Threading.ManualResetEventSlim();
                var th = new System.Threading.Thread(() => { OpenClipboard(IntPtr.Zero); held.Set(); System.Threading.Thread.Sleep(3000); CloseClipboard(); });
                th.Start(); held.Wait();
                s1.Restart();
                bool okBusy = ClipboardService.SetText("занято");
                L("буфер занят: запись " + okBusy + ", интерфейс ждал " + s1.ElapsedMilliseconds + " мс");
                th.Join();
                L("после освобождения: " + ClipboardService.SetText("снова") + ", прочитано «" + Clipboard.GetText() + "»");
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(dir, "error.txt"), ex.ToString()); }
            Application.Current.Shutdown();
        }

        public static async Task RunAsyncOld(MainWindow w, string dir)
        {
            Directory.CreateDirectory(dir);
            _phases = new StreamWriter(Path.Combine(dir, "bench-phases.txt"), false, new UTF8Encoding(false));
            try
            {
                AppSettings.Current.WelcomeShown = true;
                AppSettings.Current.SaveScreenshots = true;
                await Task.Delay(3500);
                var backdrop = ShowBackdrop();
                await Task.Delay(1500);

                int sw = (int)SystemParameters.PrimaryScreenWidth, sh = (int)SystemParameters.PrimaryScreenHeight;
                var dev = PresentationSource.FromVisual(w)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
                int W = (int)(sw * dev.M11), H = (int)(sh * dev.M22); // пиксели экрана
                Journal.Info("bench", "экран " + W + "×" + H + " px, масштаб " + dev.M11);

                Phase("open");
                _ = Tools.RegionShot();
                var overlay = await WaitOverlay();
                Phase("idle");
                if (overlay == null) { Phase("error: слой не открылся"); return; }
                await Task.Delay(700);

                Phase("select");
                await Drag((int)(W * 0.04), (int)(H * 0.05), (int)(W * 0.96), (int)(H * 0.88), 900);
                Phase("idle");
                await Task.Delay(1000);

                if (!await ClickTool(overlay, "Mosaic")) { Phase("error: нет кнопки мозаики"); return; }
                await Task.Delay(500);

                Phase("mosaic-small");
                await Drag((int)(W * 0.10), (int)(H * 0.10), (int)(W * 0.10) + 260, (int)(H * 0.10) + 60, 1500);
                Phase("idle");
                await Task.Delay(700);
                Phase("mosaic-medium");
                await Drag((int)(W * 0.15), (int)(H * 0.25), (int)(W * 0.55), (int)(H * 0.55), 1800);
                Phase("idle");
                await Task.Delay(700);
                Phase("mosaic-big");
                await Drag((int)(W * 0.06), (int)(H * 0.08), (int)(W * 0.94), (int)(H * 0.84), 2400);
                Phase("idle");
                await Task.Delay(700);

                if (await ClickTool(overlay, "Pen"))
                {
                    await Task.Delay(400);
                    Phase("pen");
                    await Squiggle((int)(W * 0.3), (int)(H * 0.6), (int)(W * 0.4), 1500);
                    Phase("idle");
                    await Task.Delay(600);
                }

                Phase("save");
                keybd_event(0x0D, 0, 0, UIntPtr.Zero);
                keybd_event(0x0D, 0, KeyUp, UIntPtr.Zero);
                await Task.Delay(1600); // полёт ~1.1 с
                Phase("idle");
                await Task.Delay(1900);
                backdrop.Close(); // фон на весь экран прячет чёлку (полноэкранный режим) — плашки снимаем без него
                await Task.Delay(1500);
                Phase("toast-ok");
                ToastBus.Show(new Toast { Kind = ToastKind.Success, Title = "Снимок скопирован", Subtitle = "Сохранён в Изображения\\Isle", Icon = Icons.Check, Important = true, Duration = TimeSpan.FromSeconds(1.6) });
                await Task.Delay(1800);
                Phase("toast-err");
                ToastBus.Show(new Toast { Kind = ToastKind.Error, Title = "Не удалось сохранить", Subtitle = "Нет доступа к папке", Icon = Icons.Info, Important = true, Duration = TimeSpan.FromSeconds(1.6) });
                await Task.Delay(1800);
                Phase("idle");
                await DanceSheets(dir);
                Journal.Info("bench", "память перед уборкой: " + (Environment.WorkingSet >> 20) + " МБ, управляемая " + (GC.GetTotalMemory(false) >> 20) + " МБ");
                Helpers.MemoryTrim.Now("стенд");
                await Task.Delay(500);
                Phase("done");
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(dir, "error.txt"), ex.ToString());
                Phase("error: " + ex.Message);
            }
            finally
            {
                _phases.Dispose();
                Journal.Flush();
                try
                {
                    // весь журнал — рядом с результатами (на стенде в нём нет ничего личного)
                    if (File.Exists(Journal.PathNow)) File.Copy(Journal.PathNow, Path.Combine(dir, "isle.log"), true);
                }
                catch { }
                File.WriteAllText(Path.Combine(dir, "done.txt"), DateTime.UtcNow.ToString("O"));
                Application.Current.Shutdown();
            }
        }

        /// <summary>Каждый танец Клоди — полоска из 10 кадров (по 120 мс) в dance-NN.png: проверить, как он выглядит.</summary>
        private static async Task DanceSheets(string dir)
        {
            try
            {
                var buddy = new Isle.Helpers.Character { Mood = Isle.Helpers.Mood.Music, FollowCursor = false, Bpm = 120, Energy = 0.5 };
                var stage = new Border { Width = 160, Height = 110, Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x18)),
                    Child = new Viewbox { Child = buddy, Margin = new Thickness(40, 22, 40, 18) } };
                var win = new Window { Content = stage, SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Left = -4000, Top = -4000 };
                win.Show();
                for (int d = 0; d < Isle.Helpers.Character.DanceCount; d++)
                {
                    buddy.ForceDance(d);
                    var sheet = new System.Windows.Media.Imaging.RenderTargetBitmap(160 * 10, 110, 96, 96, PixelFormats.Pbgra32);
                    var dv = new DrawingVisual();
                    using (var dc = dv.RenderOpen())
                        for (int f = 0; f < 10; f++)
                        {
                            await Task.Delay(120);
                            stage.UpdateLayout();
                            var one = new System.Windows.Media.Imaging.RenderTargetBitmap(160, 110, 96, 96, PixelFormats.Pbgra32);
                            one.Render(stage);
                            dc.DrawImage(one, new Rect(f * 160, 0, 160, 110));
                        }
                    sheet.Render(dv);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(sheet));
                    using var fs = File.Create(Path.Combine(dir, "dance-" + d.ToString("00") + ".png"));
                    enc.Save(fs);
                }
                win.Close();
            }
            catch (Exception ex) { Journal.Warn("bench", "кадры танцев: " + ex.Message); }
        }

        /// <summary>Фон на весь экран: пёстрые строки «текста», чтобы мозаике было что закрывать.</summary>
        private static Window ShowBackdrop()
        {
            var rnd = new Random(7);
            var panel = new WrapPanel { Margin = new Thickness(20) };
            Color[] cols = { Colors.White, Colors.Gold, Colors.LightSkyBlue, Colors.LightGreen, Colors.Salmon, Colors.Plum };
            for (int i = 0; i < 900; i++)
                panel.Children.Add(new TextBlock
                {
                    Text = new string((char)('А' + rnd.Next(32)), 2 + rnd.Next(9)), FontSize = 12 + rnd.Next(20),
                    Foreground = new SolidColorBrush(cols[rnd.Next(cols.Length)]), Margin = new Thickness(6, 3, 6, 3)
                });
            var win = new Window
            {
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, WindowState = WindowState.Maximized,
                Background = new LinearGradientBrush(Color.FromRgb(0x14, 0x1E, 0x30), Color.FromRgb(0x30, 0x14, 0x24), 45),
                Content = panel, ShowInTaskbar = false
            };
            win.Show();
            return win;
        }

        private static async Task<object> WaitOverlay()
        {
            var f = typeof(CaptureOverlay).GetField("_open", BindingFlags.NonPublic | BindingFlags.Static);
            for (int i = 0; i < 60; i++)
            {
                var o = f?.GetValue(null);
                if (o is Window win && win.IsLoaded) return o;
                await Task.Delay(100);
            }
            return null;
        }

        /// <summary>Нажать кнопку инструмента на панели пометок настоящим кликом.</summary>
        private static async Task<bool> ClickTool(object overlay, string mark)
        {
            var f = typeof(CaptureOverlay).GetField("_markButtons", BindingFlags.NonPublic | BindingFlags.Instance);
            if (f?.GetValue(overlay) is not IDictionary dict) return false;
            foreach (DictionaryEntry e in dict)
            {
                if (e.Key.ToString() != mark || e.Value is not FrameworkElement b) continue;
                var c = b.PointToScreen(new Point(b.ActualWidth / 2, b.ActualHeight / 2));
                SetCursorPos((int)c.X, (int)c.Y);
                await Task.Delay(120);
                mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
                await Task.Delay(40);
                mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
                return true;
            }
            return false;
        }

        /// <summary>Перетаскивание настоящей мышью: ~125 движений в секунду, как у обычной мыши.</summary>
        private static async Task Drag(int x0, int y0, int x1, int y1, int ms)
        {
            SetCursorPos(x0, y0);
            await Task.Delay(80);
            mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
            var t = System.Diagnostics.Stopwatch.StartNew();
            while (t.ElapsedMilliseconds < ms)
            {
                double u = t.ElapsedMilliseconds / (double)ms;
                u = u < 0.5 ? 2 * u * u : 1 - Math.Pow(-2 * u + 2, 2) / 2;
                SetCursorPos((int)(x0 + (x1 - x0) * u), (int)(y0 + (y1 - y0) * u));
                await Task.Delay(8);
            }
            SetCursorPos(x1, y1);
            await Task.Delay(60);
            mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
        }

        private static async Task Squiggle(int x0, int y0, int width, int ms)
        {
            SetCursorPos(x0, y0);
            await Task.Delay(60);
            mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
            var t = System.Diagnostics.Stopwatch.StartNew();
            while (t.ElapsedMilliseconds < ms)
            {
                double u = t.ElapsedMilliseconds / (double)ms;
                SetCursorPos(x0 + (int)(width * u), y0 + (int)(Math.Sin(u * 18) * 60));
                await Task.Delay(8);
            }
            mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
        }
    }
}
