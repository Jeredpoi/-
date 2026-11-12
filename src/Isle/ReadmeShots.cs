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
                AppSettings.Current.WelcomeShown = true; // приветствие первого запуска снимаем отдельно, а не поверх всего
                await Task.Delay(4000); // сервисы, погода, раскладка успевают загрузиться
                var island = (FrameworkElement)w.FindName("Island");

                Save(island, Path.Combine(dir, "collapsed.png"), 40);

                // всплывашки — те же, что показывает Isle (тексты и иконки как в настоящих событиях)
                async Task ToastShot(Toast t, string name, int wait = 1200)
                {
                    ToastBus.Show(t);
                    await Task.Delay(wait);
                    Save(island, Path.Combine(dir, name), 40);
                    await Task.Delay(Math.Max(300, (int)t.Duration.TotalMilliseconds - wait + 600));
                }
                await ToastShot(new Toast { Kind = ToastKind.Caps, Title = "Caps Lock включён", Icon = Icons.Caps, ReplaceKey = "caps", Duration = TimeSpan.FromSeconds(2) }, "caps.png");
                await ToastShot(new Toast { Kind = ToastKind.Layout, Title = "RU  ·  Русский", Icon = Icons.Keyboard, ReplaceKey = "layout", Duration = TimeSpan.FromSeconds(2) }, "layout.png");
                await ToastShot(new Toast { Kind = ToastKind.Volume, Title = "Громкость 64", Icon = Icons.Volume, Progress = 0.64, ReplaceKey = "volume", Duration = TimeSpan.FromSeconds(2) }, "volume.png");
                await ToastShot(new Toast { Kind = ToastKind.Success, Title = "Загрузка завершена", Subtitle = "Counter-Strike 2 · Steam", Icon = Icons.Check, Important = true, Duration = TimeSpan.FromSeconds(2.4) }, "success.png", 1500);

                // загрузка игры в Steam: обложка игры (если workflow её скачал) с кольцом прогресса
                ImageSource art = null;
                var artPath = Path.Combine(dir, "game.jpg");
                if (File.Exists(artPath))
                {
                    var bi = new BitmapImage();
                    bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.UriSource = new Uri(artPath); bi.EndInit(); bi.Freeze();
                    art = bi;
                }
                w.ShowDemoDownload("Counter-Strike 2", "Steam", 0.68, "18,4 МБ/с", "25 с", art);
                await Task.Delay(2500);
                Save(island, Path.Combine(dir, "download.png"), 40);
                w.ShowDemoDownload(null, null, null, null, null, null);
                await Task.Delay(800);

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
                await WelcomeAsync(Path.Combine(dir, "welcome.png"));
                await ClawdLifeAsync(Path.Combine(dir, "life"));
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

        /// <summary>Окно приветствия первого запуска — за краем экрана, после анимации появления.</summary>
        private static async Task WelcomeAsync(string path)
        {
            var win = new Views.WelcomeWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = -4000, ShowActivated = false };
            win.Show();
            await Task.Delay(2600);
            if (win.Content is FrameworkElement card)
            {
                Save(card, path, 30);
                // каждый шаг знакомства — отдельным снимком (welcome-1.png …)
                for (int i = 1; i < win.ShotSteps - 1; i++)
                {
                    win.ShotGo(i);
                    await Task.Delay(i == 1 ? 3400 : 1800);
                    Save(card, Path.Combine(Path.GetDirectoryName(path), "welcome-" + i + ".png"), 30);
                }
            }
            win.Close();
            await Task.Delay(300);
        }

        /// <summary>
        /// «Жизнь Клоди»: занятия в покое по очереди и разная одежда, в конце засыпает в кровати. Кадры каждые 70 мс.
        /// </summary>
        private static async Task ClawdLifeAsync(string dir)
        {
            Directory.CreateDirectory(dir);
            var buddy = new Character { Mood = Mood.Idle, FollowCursor = false };
            var stage = new Border
            {
                Width = 460, Height = 320, Background = new SolidColorBrush(Backdrop),
                Child = new Viewbox { Child = buddy, Margin = new Thickness(130, 90, 130, 60) }
            };
            var win = new Window
            {
                Content = stage, SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None,
                ShowInTaskbar = false, ShowActivated = false, Left = -4000, Top = -4000, Topmost = false
            };
            win.Show();
            // жонглирует → змей → гитара → фото → гантели → поливает цветок → мороженое → кубик
            var plan = new (int activity, Outfit outfit)[]
            {
                (18, Outfit.None), (21, Outfit.Viking), (14, Outfit.Cowboy), (23, Outfit.Detective),
                (30, Outfit.Ninja), (26, Outfit.Flowers), (28, Outfit.Party), (29, Outfit.Graduate),
            };
            int frame = 0;
            async Task Capture(double seconds)
            {
                var until = DateTime.Now.AddSeconds(seconds);
                while (DateTime.Now < until)
                {
                    await Task.Delay(70);
                    stage.UpdateLayout();
                    var bmp = new RenderTargetBitmap(460, 320, 96, 96, PixelFormats.Pbgra32);
                    bmp.Render(stage);
                    WritePng(bmp, Path.Combine(dir, "frame_" + (frame++).ToString("0000") + ".png"));
                }
            }
            foreach (var (activity, outfit) in plan)
            {
                buddy.Outfit = outfit;
                buddy.ForceActivity(activity, 3);
                await Capture(2.6);
            }
            // устал — засыпает в кровати
            buddy.Outfit = Outfit.None;
            buddy.Mood = Mood.Sleep;
            await Capture(4.5);
            win.Close();
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
