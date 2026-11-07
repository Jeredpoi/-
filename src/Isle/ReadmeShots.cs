using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Isle.Helpers;
using Isle.Services;

namespace Isle
{
    /// <summary>
    /// Съёмка настоящего интерфейса для README: Isle.exe --readme-shots &lt;папка&gt;.
    /// Программа сама открывает нужные вкладки и сохраняет их снимки (рендер того же окна, что видит пользователь),
    /// плюс кадры танцующего Клоди — из них собирается анимация. Запускается в GitHub Actions на Windows.
    /// </summary>
    public static class ReadmeShots
    {
        private static readonly Color Backdrop = Color.FromRgb(0x0D, 0x11, 0x17); // как тёмная тема GitHub

        public static async Task RunAsync(MainWindow w, string dir)
        {
            Directory.CreateDirectory(dir);
            try
            {
                await Task.Delay(4000); // сервисы, погода, раскладка успевают загрузиться
                var island = (FrameworkElement)w.FindName("Island");

                Save(island, Path.Combine(dir, "collapsed.png"), 40);

                ToastBus.Show(new Toast
                {
                    Kind = ToastKind.Success, Title = "#E07A5F", Subtitle = "rgb(224, 122, 95) · скопирован",
                    Icon = Icons.Pipette, Accent = Color.FromRgb(0xE0, 0x7A, 0x5F), Swatch = Color.FromRgb(0xE0, 0x7A, 0x5F),
                    Important = true, Duration = TimeSpan.FromSeconds(6)
                });
                await Task.Delay(1200);
                Save(island, Path.Combine(dir, "toast.png"), 40);
                await Task.Delay(5500);

                foreach (var tab in new[] { "home", "chat", "notes", "weather", "text", "controls" })
                {
                    w.Expand(tab);
                    w.SelectTab(tab);
                    await Task.Delay(tab == "weather" ? 4000 : 1800);
                    Save(island, Path.Combine(dir, tab + ".png"), 28);
                }
                w.Collapse();
                await Task.Delay(800);

                await ClawdAsync(Path.Combine(dir, "clawd"));
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(dir, "error.txt"), ex.ToString());
            }
            finally
            {
                File.WriteAllText(Path.Combine(dir, "done.txt"), DateTime.Now.ToString("O"));
                Application.Current.Shutdown();
            }
        }

        /// <summary>Снимок элемента в 2× на тёмном фоне с полями.</summary>
        private static void Save(FrameworkElement el, string path, double pad)
        {
            el.UpdateLayout();
            double w = Math.Ceiling(el.ActualWidth), h = Math.Ceiling(el.ActualHeight);
            if (w < 2 || h < 2) return;
            const double scale = 2;
            // через VisualBrush: RenderTargetBitmap.Render(el) учитывает сдвиг элемента в окне и обрезает его
            var brush = new VisualBrush(el) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(Backdrop), null, new Rect(0, 0, w + pad * 2, h + pad * 2), 18, 18);
                dc.DrawRectangle(brush, null, new Rect(pad, pad, w, h));
            }
            var outBmp = new RenderTargetBitmap((int)((w + pad * 2) * scale), (int)((h + pad * 2) * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            outBmp.Render(dv);
            WritePng(outBmp, path);
        }

        private static void WritePng(BitmapSource bmp, string path)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(path);
            enc.Save(fs);
        }

        /// <summary>
        /// Клоди крупно в отдельном окне за краем экрана: танцы по очереди, кадры каждые 50 мс.
        /// Это тот же самый персонаж, что живёт в чёлке, — просто увеличенный.
        /// </summary>
        private static async Task ClawdAsync(string dir)
        {
            Directory.CreateDirectory(dir);
            var buddy = new Character { Mood = Mood.Music, FollowCursor = false, Bpm = 120, Energy = 0.5 };
            var stage = new Border
            {
                Width = 520, Height = 360, Background = new SolidColorBrush(Backdrop),
                Child = new Viewbox { Child = buddy, Margin = new Thickness(150, 95, 150, 75) }
            };
            var win = new Window
            {
                Content = stage, SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None,
                ShowInTaskbar = false, ShowActivated = false, Left = -4000, Top = -4000, Topmost = false
            };
            win.Show();
            var start = DateTime.Now;
            // бит 120 уд/мин — Клоди подпрыгивает в такт
            var beat = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            beat.Tick += (_, __) =>
            {
                double t = (DateTime.Now - start).TotalSeconds * 2; // 2 удара в секунду
                buddy.Beat = (float)Math.Pow(Math.Max(0, Math.Cos((t % 1) * Math.PI)), 6) * 0.5f;
            };
            beat.Start();
            // классика → диско → кружится → брейк-данс
            var plan = new (int dance, double seconds)[] { (0, 2.5), (6, 2.5), (5, 2.5), (8, 6.2) };
            int frame = 0;
            foreach (var (dance, seconds) in plan)
            {
                buddy.ForceDance(dance);
                var until = DateTime.Now.AddSeconds(seconds);
                while (DateTime.Now < until)
                {
                    await Task.Delay(50);
                    stage.UpdateLayout();
                    var bmp = new RenderTargetBitmap(520, 360, 96, 96, PixelFormats.Pbgra32);
                    bmp.Render(stage);
                    WritePng(bmp, Path.Combine(dir, "frame_" + (frame++).ToString("0000") + ".png"));
                }
            }
            beat.Stop();
            win.Close();
        }
    }
}
