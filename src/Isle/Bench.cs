using System.Linq;
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

        private static Task _resWait;

        public static async Task RunAsync(MainWindow w, string dir)
        {
            Directory.CreateDirectory(dir);
            var log = Path.Combine(dir, "log.txt");
            void L(string t) => File.AppendAllText(log, t + "\n");
            try
            {
                AppSettings.Current.WelcomeShown = true;
                AppSettings.Current.AutoUpdateCheck = false;
                await Task.Delay(3500);
                { var m0 = System.Diagnostics.Process.GetCurrentProcess(); m0.Refresh(); var c0 = m0.TotalProcessorTime; var t0 = DateTime.Now; await Task.Delay(8000); m0.Refresh();
                  L("до вкладки (чёлка свёрнута, сервер не запущен): ЦП " + ((m0.TotalProcessorTime - c0).TotalMilliseconds / (DateTime.Now - t0).TotalMilliseconds / Environment.ProcessorCount * 100).ToString("0.0") + "%, память " + m0.WorkingSet64 / 1048576 + " МБ / частная " + m0.PrivateMemorySize64 / 1048576 + " МБ"); }
                w.SuppressAutoCollapse = true;
                w.Expand("clip"); await Task.Delay(1500);
                { var m0 = System.Diagnostics.Process.GetCurrentProcess(); m0.Refresh(); var c0 = m0.TotalProcessorTime; var t0 = DateTime.Now; await Task.Delay(8000); m0.Refresh();
                  L("для сравнения — открыта вкладка «Буфер»: ЦП " + ((m0.TotalProcessorTime - c0).TotalMilliseconds / (DateTime.Now - t0).TotalMilliseconds / Environment.ProcessorCount * 100).ToString("0.0") + "%"); }
                w.Expand("phone");
                await Task.Delay(1500);
                var addrs = PhoneLink.Addresses();
                L("сервер: " + PhoneLink.Running + ", порт " + PhoneLink.Port + ", адреса: " + string.Join("; ", addrs.Select(a => a.ip + " " + a.name)));
                var ip = addrs.FirstOrDefault().ip ?? "127.0.0.1";
                var url = PhoneLink.Url(ip);
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                var page = await http.GetAsync(url);
                L("страничка: " + (int)page.StatusCode + ", " + (await page.Content.ReadAsStringAsync()).Length + " символов");
                var bad = await http.GetAsync("http://" + ip + ":" + PhoneLink.Port + "/wrongkey/");
                L("без ключа: " + (int)bad.StatusCode);
                // фото «с телефона»
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(400, 300, 96, 96, PixelFormats.Pbgra32);
                var dv = new DrawingVisual(); using (var dc = dv.RenderOpen()) dc.DrawRectangle(new LinearGradientBrush(Colors.Orange, Colors.DeepPink, 45), null, new Rect(0, 0, 400, 300));
                bmp.Render(dv);
                var ms = new MemoryStream(); var je = new System.Windows.Media.Imaging.JpegBitmapEncoder(); je.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp)); je.Save(ms);
                var upr = await http.PostAsync(url + "up?name=" + Uri.EscapeDataString("IMG_2026 море.jpg"), new System.Net.Http.ByteArrayContent(ms.ToArray()));
                L("загрузка фото: " + (int)upr.StatusCode);
                var tr = await http.PostAsync(url + "text", new System.Net.Http.StringContent("Привет с телефона! https://example.com"));
                L("текст: " + (int)tr.StatusCode);
                await Task.Delay(1500);
                L("получено: " + PhoneLink.Received.Count + " — " + string.Join(", ", PhoneLink.Received.Select(r => r.Text ?? r.Name)));
                L("в буфере сверху: " + string.Join(" | ", AppHost.Clipboard.Items.Take(2).Select(i => i.Kind + ": " + i.Preview + " (" + i.Source + ")")));
                string cbText = null; try { cbText = Clipboard.GetText(); } catch { }
                L("буфер обмена Windows: " + cbText);
                // на телефон
                var tmp = Path.Combine(Path.GetTempPath(), "отчёт для телефона.txt"); File.WriteAllText(tmp, "hello phone");
                PhoneLink.Offer(new[] { tmp });
                var list = await http.GetStringAsync(url + "list");
                L("список для телефона: " + list);
                var id = System.Text.Json.JsonDocument.Parse(list).RootElement.GetProperty("files")[0].GetProperty("id").GetString();
                var dl = await http.GetAsync(url + "f/" + id);
                L("скачивание: " + (int)dl.StatusCode + ", " + await dl.Content.ReadAsStringAsync() + ", имя " + dl.Content.Headers.ContentDisposition);
                await Task.Delay(800);
                var pv = (Isle.Views.PhoneView)typeof(MainWindow).GetMethod("GetView", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public).Invoke(w, new object[] { "phone" });
                var qr = (Image)pv.FindName("Qr");
                var src = (System.Windows.Media.Imaging.BitmapSource)qr.Source;
                var dev = PresentationSource.FromVisual(w).CompositionTarget.TransformToDevice.M11;
                L("QR: клеток " + src.PixelWidth + ", размер " + qr.ActualWidth.ToString("0.###") + "×" + qr.ActualHeight.ToString("0.###") + " DIP, масштаб " + dev + " → " + (qr.ActualWidth * dev / src.PixelWidth).ToString("0.###") + " px на клетку");
                await Task.Delay(4000); // первая анимация (от первого запроса странички) должна закончиться
                var me = System.Diagnostics.Process.GetCurrentProcess();
                void Res(string when, double secs)
                {
                    me.Refresh(); var cpu0 = me.TotalProcessorTime; var t0 = DateTime.Now;
                    System.Threading.Thread.Sleep(0);
                    var tcs = new TaskCompletionSource();
                    var tm = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(secs) };
                    tm.Tick += (_, __) => { tm.Stop(); me.Refresh(); var c = (me.TotalProcessorTime - cpu0).TotalMilliseconds / (DateTime.Now - t0).TotalMilliseconds / Environment.ProcessorCount * 100;
                        L(when + ": ЦП " + c.ToString("0.0") + "% (всех ядер), память " + me.WorkingSet64 / 1048576 + " МБ рабочий набор, " + me.PrivateMemorySize64 / 1048576 + " МБ частная"); tcs.SetResult(); };
                    tm.Start(); _resWait = tcs.Task;
                }
                // телефон «отключился»: подождать, пока статус сбросится (15 с без запросов) — рамка должна задышать
                await Task.Delay(16000);
                Res("вкладка «Телефон» открыта, рамка дышит", 8); await _resWait;
                SnapEl(w.Content as FrameworkElement, dir, "breath-a"); await Task.Delay(700); SnapEl(w.Content as FrameworkElement, dir, "breath-b");
                SnapEl(w.Content as FrameworkElement, dir, "phone-0");
                // телефон открывает страничку → змейка
                var opened = http.GetAsync(url);
                for (int i = 1; i <= 8; i++) { await Task.Delay(150); L("кадр " + i + ": " + DateTime.Now.ToString("ss.fff")); SnapEl(w.Content as FrameworkElement, dir, "phone-snake-" + i); }
                await opened;
                await Task.Delay(2500);
                SnapEl(w.Content as FrameworkElement, dir, "phone-after");
                // уведомления телефона
                PhoneLink.AddNotice(new PhoneNotice { App = "Связь с телефоном", Title = "Мама", Body = "Ты сегодня приедешь на ужин?" });
                await Task.Delay(300);
                PhoneLink.AddNotice(new PhoneNotice { App = "Связь с телефоном", Title = "Банк", Body = "Поступление 1 500 ₽" });
                await Task.Delay(500);
                SnapEl(w.Content as FrameworkElement, dir, "phone-badge");
                var pn = (RadioButton)pv.FindName("PaneNotices"); pn.IsChecked = true;
                pv.GetType().GetMethod("OnPane", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(pv, new object[] { pn, null });
                await Task.Delay(600);
                SnapEl(w.Content as FrameworkElement, dir, "phone-notices");
                PhoneLink.Notices.Clear(); pv.GetType().GetMethod("BuildNotices", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(pv, null);
                await Task.Delay(400);
                SnapEl(w.Content as FrameworkElement, dir, "phone-notices-empty");
                w.Collapse(); await Task.Delay(2000);
                { var me2 = System.Diagnostics.Process.GetCurrentProcess(); me2.Refresh(); var c0 = me2.TotalProcessorTime; var t0 = DateTime.Now; await Task.Delay(8000); me2.Refresh();
                  L("чёлка свёрнута, сервер работает: ЦП " + ((me2.TotalProcessorTime - c0).TotalMilliseconds / (DateTime.Now - t0).TotalMilliseconds / Environment.ProcessorCount * 100).ToString("0.0") + "%, память " + me2.WorkingSet64 / 1048576 + " МБ / частная " + me2.PrivateMemorySize64 / 1048576 + " МБ"); }
                // нагрузка: телефон опрашивает список, как страничка (раз в 2,5 с) — 40 запросов подряд
                { var me3 = System.Diagnostics.Process.GetCurrentProcess(); me3.Refresh(); var c0 = me3.TotalProcessorTime; var sw2 = System.Diagnostics.Stopwatch.StartNew();
                  for (int i = 0; i < 40; i++) await http.GetStringAsync(url + "list");
                  me3.Refresh(); L("40 запросов списка: " + sw2.ElapsedMilliseconds + " мс, ЦП процесса на них " + (me3.TotalProcessorTime - c0).TotalMilliseconds.ToString("0") + " мс"); }
                // проверка распознавания «Связи с телефоном»
                L("PhoneLink распознан: " + NotificationService.IsPhoneLink("Связь с телефоном", "Microsoft.YourPhone_8wekyb3d8bbwe!App") + ", Telegram: " + NotificationService.IsPhoneLink("Telegram", "TelegramDesktop"));
                File.WriteAllText(Path.Combine(dir, "page.html"), await http.GetStringAsync(url));
                var lj = await http.GetStringAsync(url + "list"); L("list: " + lj);
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
