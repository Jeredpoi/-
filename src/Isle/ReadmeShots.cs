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

                // буфер: записи за несколько дней (картинки и тексты), чтобы видно было разделители по дням
                SeedClipboard();
                await PhoneShotsAsync(w, island, dir);
                await SnowShotAsync(w, dir);
                await SettingsShotAsync(dir);

                foreach (var tab in new[] { "home", "clip", "chat", "notes", "weather", "text", "controls" })
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

        private static void SeedClipboard()
        {
            Directory.CreateDirectory(ClipboardService.ImagesDir);
            string Png(string name, Color a, Color b)
            {
                var bmp = new RenderTargetBitmap(320, 180, 96, 96, PixelFormats.Pbgra32);
                var d = new DrawingVisual(); using (var dc = d.RenderOpen()) dc.DrawRectangle(new LinearGradientBrush(a, b, 35), null, new Rect(0, 0, 320, 180));
                bmp.Render(d);
                WritePng(bmp, Path.Combine(ClipboardService.ImagesDir, name));
                return name;
            }
            var now = DateTime.Now;
            var items = new[]
            {
                new ClipItem { Kind = ClipKind.Text, Text = "Встреча перенесена на 19:30, адрес тот же", Source = "Telegram", Time = now.AddMinutes(-2) },
                new ClipItem { Kind = ClipKind.Image, Text = "Изображение 320×180", ImageFile = Png("r1.png", Color.FromRgb(0xF9, 0x73, 0x16), Color.FromRgb(0xDB, 0x27, 0x77)), Source = "chrome", Time = now.AddMinutes(-15) },
                new ClipItem { Kind = ClipKind.Link, Text = "https://github.com/Jeredpoi/-", Source = "chrome", Time = now.AddMinutes(-48) },
                new ClipItem { Kind = ClipKind.Image, Text = "Изображение 320×180", ImageFile = Png("r2.png", Color.FromRgb(0x38, 0xBD, 0xF8), Color.FromRgb(0x63, 0x66, 0xF1)), Source = "Discord", Time = now.AddDays(-1).AddHours(-2) },
                new ClipItem { Kind = ClipKind.Text, Text = "+7 999 123-45-67", Source = "Telegram", Time = now.AddDays(-1).AddHours(-5) },
                new ClipItem { Kind = ClipKind.Text, Text = "git commit -m \"готово\"", Source = "Terminal", Time = now.AddDays(-3) },
            };
            foreach (var it in items) it.Kind = it.Kind == ClipKind.Text ? ClipboardService.Classify(it.Text, "") : it.Kind;
            foreach (var it in items) AppHost.Clipboard.Items.Add(it);
        }

        /// <summary>Вкладка «Телефон»: подключение (анимация QR), пришедшие фото и текст, уведомления телефона.</summary>
        private static async Task PhoneShotsAsync(MainWindow w, FrameworkElement island, string dir)
        {
            w.Expand("phone"); w.SelectTab("phone");
            await Task.Delay(2000);
            var addrs = PhoneLink.Addresses();
            var url = PhoneLink.Url(addrs.Count > 0 ? addrs[0].ip : "127.0.0.1");
            var view = FindChild<Views.PhoneView>(island);
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            // ждёт телефон: оранжевая рамка дышит — дадим статусу «нет телефона» установиться
            await Task.Delay(1500);
            Save(island, Path.Combine(dir, "phone-wait.png"), 28);
            // телефон открывает страничку — снимаем анимацию подключения по кадрам
            var card = view?.FindName("QrHost") as FrameworkElement;
            var qrDir = Path.Combine(dir, "qr"); Directory.CreateDirectory(qrDir);
            var get = http.GetAsync(url);
            int frame = 0;
            var until = DateTime.Now.AddSeconds(3.6);
            while (DateTime.Now < until && card != null)
            {
                await Task.Delay(40);
                Save(card, Path.Combine(qrDir, "frame_" + (frame++).ToString("0000") + ".png"), 16, 1.5);
            }
            await get;
            // фото и текст «с телефона», файл «на телефон»
            var bmp = new RenderTargetBitmap(640, 480, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual(); using (var dc = dv.RenderOpen()) dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0xFB, 0x92, 0x3C), Color.FromRgb(0xDB, 0x27, 0x77), 40), null, new Rect(0, 0, 640, 480));
            bmp.Render(dv);
            var ms = new MemoryStream(); var je = new JpegBitmapEncoder(); je.Frames.Add(BitmapFrame.Create(bmp)); je.Save(ms);
            await http.PostAsync(url + "up?name=" + Uri.EscapeDataString("IMG_2041.jpg"), new System.Net.Http.ByteArrayContent(ms.ToArray()));
            await http.PostAsync(url + "text", new System.Net.Http.StringContent("Код из СМС: 4821"));
            var tmp = Path.Combine(Path.GetTempPath(), "Презентация.pdf"); File.WriteAllText(tmp, "pdf");
            PhoneLink.Offer(new[] { tmp });
            await Task.Delay(2500);
            w.Expand("phone"); w.SelectTab("phone");
            await Task.Delay(800);
            Save(island, Path.Combine(dir, "phone.png"), 28);
            // уведомления телефона (через «Связь с телефоном»)
            PhoneLink.AddNotice(new PhoneNotice { Title = "Мама", Body = "Ты сегодня приедешь на ужин?", Time = DateTime.Now.AddMinutes(-12) });
            PhoneLink.AddNotice(new PhoneNotice { Title = "Доставка", Body = "Курьер будет через 10 минут", Time = DateTime.Now.AddMinutes(-3) });
            PhoneLink.AddNotice(new PhoneNotice { Title = "Аня", Body = "Скинь фотки с выходных 📸", Time = DateTime.Now });
            if (view?.FindName("PaneNotices") is System.Windows.Controls.Primitives.ToggleButton pn)
            {
                pn.IsChecked = true;
                pn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            }
            await Task.Delay(800);
            Save(island, Path.Combine(dir, "phone-notices.png"), 28);
            if (view?.FindName("PaneFiles") is System.Windows.Controls.Primitives.ToggleButton pf)
            {
                pf.IsChecked = true;
                pf.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            }
            w.Collapse();
            await Task.Delay(1000);
        }

        /// <summary>Снег из-под свёрнутой чёлки.</summary>
        private static async Task SnowShotAsync(MainWindow w, string dir)
        {
            AppSettings.Current.WinterSnow = "on";
            await Task.Delay(4500);
            var root = (FrameworkElement)w.Content;
            var island = (FrameworkElement)w.FindName("Island");
            var p = island.TranslatePoint(new Point(0, 0), root);
            double cx = p.X + island.ActualWidth / 2;
            SaveRegion(root, new Rect(cx - 210, 0, 420, 190), Path.Combine(dir, "snow.png"));
            AppSettings.Current.WinterSnow = "auto";
            await Task.Delay(1500);
        }

        /// <summary>Настройки → Внешний вид: превью чёлки с вкладками, вкладки (галочки и порядок), снег, режим выреза.</summary>
        private static async Task SettingsShotAsync(string dir)
        {
            var sw = new Views.SettingsWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = -4000, ShowActivated = false };
            sw.Show();
            await Task.Delay(1200);
            if (sw.FindName("NavLook") is System.Windows.Controls.RadioButton nav)
            {
                nav.IsChecked = true;
                nav.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            }
            await Task.Delay(900);
            if (sw.Content is FrameworkElement c) Save(c, Path.Combine(dir, "settings-look.png"), 0);
            sw.Close();
            await Task.Delay(300);
        }

        private static T FindChild<T>(DependencyObject d) where T : DependencyObject
        {
            if (d is T t) return t;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
                if (FindChild<T>(VisualTreeHelper.GetChild(d, i)) is T r) return r;
            return null;
        }

        /// <summary>Снимок прямоугольника из окна (2×) на тёмном фоне.</summary>
        private static void SaveRegion(FrameworkElement root, Rect r, string path)
        {
            root.UpdateLayout();
            const double scale = 2;
            var brush = new VisualBrush(root) { Stretch = Stretch.None, Viewbox = r, ViewboxUnits = BrushMappingMode.Absolute, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(Backdrop), null, new Rect(0, 0, r.Width, r.Height), 18, 18);
                dc.DrawRectangle(brush, null, new Rect(0, 0, r.Width, r.Height));
            }
            var bmp = new RenderTargetBitmap((int)(r.Width * scale), (int)(r.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bmp.Render(dv);
            WritePng(bmp, path);
        }

        /// <summary>Снимок элемента в 2× на тёмном фоне с полями.</summary>
        private static void Save(FrameworkElement el, string path, double pad, double scale = 2)
        {
            el.UpdateLayout();
            double w = Math.Ceiling(el.ActualWidth), h = Math.Ceiling(el.ActualHeight);
            if (w < 2 || h < 2) return;
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
            if (win.Content is FrameworkElement card) Save(card, path, 30);
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
