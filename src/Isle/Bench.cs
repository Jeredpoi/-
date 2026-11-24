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
            const double k = 2;
            double W = v.ActualWidth, H = v.ActualHeight;
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(W * k), (int)(H * k), 96 * k, 96 * k, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) { dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(40, 44, 52)), null, new Rect(0, 0, W, H)); dc.DrawRectangle(new VisualBrush(v), null, new Rect(0, 0, W, H)); }
            rtb.Render(dv);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder(); enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = File.Create(Path.Combine(dir, name + ".png")); enc.Save(fs);
        }

        public static async Task RunAsync(MainWindow w, string dir)
        {
            Directory.CreateDirectory(dir);
            var log = new StringBuilder();
            try
            {
                AppSettings.Current.WelcomeShown = true;
                AppSettings.Current.AutoUpdateCheck = false;
                AppSettings.Current.WinterSnow = "off";
                await Task.Delay(3000);
                // движущаяся картинка: цветной квадрат ездит по окну 640×360
                var box = new Border { Width = 80, Height = 80, Background = Brushes.OrangeRed, CornerRadius = new CornerRadius(12) };
                var cv = new Canvas { Background = new SolidColorBrush(Color.FromRgb(30, 60, 110)), Width = 640, Height = 360 };
                var label = new TextBlock { Foreground = Brushes.White, FontSize = 28, Text = "Isle запись" };
                Canvas.SetLeft(label, 20); Canvas.SetTop(label, 300);
                cv.Children.Add(box); cv.Children.Add(label);
                var win = new Window { Content = cv, SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None, Left = 100, Top = 150, Topmost = true, ShowActivated = false };
                win.Show();
                var t0 = DateTime.Now;
                var anim = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                anim.Tick += (_, __) => { double t = (DateTime.Now - t0).TotalSeconds; Canvas.SetLeft(box, 280 + 240 * Math.Sin(t * 2)); Canvas.SetTop(box, 140 + 100 * Math.Cos(t * 3)); label.Text = "Isle запись " + t.ToString("0.0"); };
                anim.Start();
                await Task.Delay(800);
                var dev = PresentationSource.FromVisual(w)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
                // панель записи над уже выбранной областью (как после «камеры» в снимке)
                {
                    var d0 = PresentationSource.FromVisual(w)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
                    var pk = new Isle.Overlays.RecordPicker((int)(100 * d0.M11), (int)(150 * d0.M22), (int)(640 * d0.M11), (int)(360 * d0.M22));
                    pk.Show(); await Task.Delay(900);
                    SnapEl((FrameworkElement)pk.Content, dir, "picker");
                    pk.Close(); await Task.Delay(300);
                }
                int px(double v) => (int)Math.Round(v * dev.M11);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await RegionRecorder.StartAsync(px(100), px(150), px(640), px(360), true, false);
                log.AppendLine("старт за " + sw.ElapsedMilliseconds + " мс");
                await Task.Delay(1500);
                SnapEl((FrameworkElement)w.Content, dir, "island-rec");
                log.AppendLine("в чёлке: " + w.RecText.Text + " видно=" + w.RecChip.Visibility);
                await Task.Delay(1500);
                async Task ClickAt(FrameworkElement el, double x, double y, string what)
                {
                    var q = el.PointToScreen(new Point(x, y));
                    SetCursorPos((int)q.X, (int)q.Y); await Task.Delay(200);
                    mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero); await Task.Delay(60); mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
                    await Task.Delay(500);
                    var r = RegionRecorder.Current;
                    log.AppendLine(what + ": звук ПК=" + r?.SysOn + " мик=" + r?.MicOn + " остров " + w.State + " запись " + (r != null ? "идёт" : "стоп"));
                }
                log.AppendLine("кнопки: стоп " + w.RecStop.ActualWidth + "×" + w.RecStop.ActualHeight + ", звук " + w.RecSys.ActualWidth + "×" + w.RecSys.ActualHeight + ", чёлка " + w.Island.ActualWidth + "×" + w.Island.ActualHeight);
                log.AppendLine("до: звук ПК=" + RegionRecorder.Current?.SysOn + " мик=" + RegionRecorder.Current?.MicOn);
                await ClickAt(w.RecSys, w.RecSys.ActualWidth + 2, w.RecSys.ActualHeight / 2, "промах между динамиком и микрофоном (ближе к динамику)");
                await ClickAt(w.RecSys, w.RecSys.ActualWidth / 2, w.RecSys.ActualHeight + 5, "промах ниже динамика");
                await ClickAt(w.RecMic, w.RecMic.ActualWidth + 8, w.RecMic.ActualHeight / 2, "промах правее микрофона");
                await ClickAt(w.RecMic, w.RecMic.ActualWidth / 2, w.RecMic.ActualHeight / 2, "точно в микрофон");
                {
                    var q = w.RecMic.PointToScreen(new Point(w.RecMic.ActualWidth / 2, 0));
                    SetCursorPos((int)q.X, 0); await Task.Delay(900);
                    log.AppendLine("курсор упёрся в верх над кнопками 0,9 с: остров " + w.State);
                }
                SnapEl((FrameworkElement)w.Content, dir, "island-rec2");
                await ClickAt(w.RecText, w.RecText.ActualWidth / 2, w.RecText.ActualHeight / 2, "клик по таймеру");
                for (int k = 0; k < 40 && RegionRecorder.Current != null; k++) await Task.Delay(100);
                log.AppendLine("после клика: запись " + (RegionRecorder.Current == null ? "остановлена" : "ИДЁТ") + ", остров " + w.State);
                string file = null;
                for (int k = 0; k < 40 && RegionRecorder.Current != null; k++) await Task.Delay(100);
                log.AppendLine("после клика: запись " + (RegionRecorder.Current == null ? "остановлена" : "ИДЁТ") + ", остров " + w.State);
                var dirV = RegionRecorder.Folder;
                if (Directory.Exists(dirV)) { var fs = new DirectoryInfo(dirV).GetFiles("*.mp4"); if (fs.Length > 0) file = fs.OrderBy(f => f.LastWriteTime).Last().FullName; }
                sw.Restart();
                log.AppendLine("стоп и сохранение за " + sw.ElapsedMilliseconds + " мс: " + file + " " + (file != null ? new FileInfo(file).Length / 1024 + " КБ" : ""));
                anim.Stop(); win.Close();
                if (file != null) File.Copy(file, Path.Combine(dir, "rec-region.mp4"), true);
                // сведение со звуком: на стенде нет звуковой карты — проверяем на сгенерированном тоне 440 Гц
                if (file != null)
                {
                    var wav = Path.Combine(Path.GetTempPath(), "tone.wav");
                    var gen = new NAudio.Wave.SampleProviders.SignalGenerator(48000, 2) { Frequency = 440, Gain = 0.2 };
                    NAudio.Wave.WaveFileWriter.CreateWaveFile16(wav, new NAudio.Wave.SampleProviders.OffsetSampleProvider(gen) { TakeSamples = 48000 * 2 * 7 });
                    var muxed = Path.Combine(dir, "rec-region-audio.mp4");
                    sw.Restart();
                    bool ok = await RegionRecorder.MuxAsync(file, new[] { wav }, TimeSpan.FromMilliseconds(300), muxed, RegionRecorder.Profile(640, 360, true));
                    log.AppendLine("сведение со звуком: " + ok + " за " + sw.ElapsedMilliseconds + " мс, " + (File.Exists(muxed) ? new FileInfo(muxed).Length / 1024 + " КБ" : "нет файла"));
                }
                log.AppendLine("процессор/память: " + Environment.WorkingSet / 1048576 + " МБ");
            }
            catch (Exception ex) { File.AppendAllText(Path.Combine(dir, "error.txt"), ex.ToString()); }
            File.WriteAllText(Path.Combine(dir, "log.txt"), log.ToString());
            Journal.Flush();
            try { File.Copy(Journal.PathNow, Path.Combine(dir, "isle.log"), true); } catch { }
            Application.Current.Shutdown();
        }

        public static async Task RunAsyncOld2(MainWindow w, string dir) => await Task.CompletedTask;

        public static async Task RunAsyncOld9(MainWindow w, string dir)
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
