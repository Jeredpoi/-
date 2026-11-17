using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Isle.Helpers;
using Isle.Services;

namespace Isle.Views
{
    /// <summary>
    /// Вкладка «Телефон»: QR-код со ссылкой на страничку, всё, что пришло с телефона, и файлы для телефона.
    /// Сервер (PhoneLink) работает, пока вкладку открывают; без неё выключается через 15 минут простоя.
    /// </summary>
    public partial class PhoneView : UserControl, IIslandView
    {
        private string _ip, _url;
        private int _qrModules;
        private int _seenNotices;
        private bool _wasOnline;
        private readonly DispatcherTimer _status = new() { Interval = TimeSpan.FromSeconds(1) };
        private static bool _hooked;

        public PhoneView()
        {
            InitializeComponent();
            PhoneLink.Changed += () => Dispatcher.InvokeAsync(Refresh);
            _status.Tick += (_, __) => UpdateStatus();
            IsVisibleChanged += (_, __) =>
            {
                PhoneLink.KeepAlive = IsVisible;
                if (!IsVisible) _lastFit = -1;
                if (IsVisible) { PhoneLink.Start(); _status.Start(); Refresh(); } else _status.Stop();
            };
            if (!_hooked) { _hooked = true; PhoneLink.ItemReceived += OnReceived; }
            PhoneLink.PageOpened += () => { if (IsVisible) PlayConnected(); };
            PhoneLink.NoticesChanged += () => Dispatcher.InvokeAsync(BuildNotices);
            QrHost.SizeChanged += (_, __) => FitQr();
            // размер клеток пересчитываем, когда вкладку показали (после анимации раскрытия), а не на каждый проход раскладки
            IsVisibleChanged += (_, __) => { if (IsVisible) { _lastFit = -1; Dispatcher.InvokeAsync(FitQr, DispatcherPriority.Background); DispatcherTimerOnce(500, FitQr); } UpdateBreath(); };
            BuildNotices();
        }

        public void OnShown() { PhoneLink.Start(); Refresh(); }

        /// <summary>С телефона пришло: в «Буфер» (текст — сразу в буфер обмена) и плашка в чёлке.</summary>
        private static void OnReceived(PhoneItem it)
        {
            if (it.Text != null)
            {
                ClipboardService.IgnoreUntil = DateTime.Now.AddMilliseconds(400); // не дублировать: сами положим в историю
                ClipboardService.SetText(it.Text);
                AppHost.Clipboard.AddExternal(new ClipItem { Text = it.Text, Source = "Телефон", Kind = ClipboardService.Classify(it.Text, "") });
                ToastBus.Show(new Toast { Kind = ToastKind.Success, Title = "Текст с телефона — в буфере", Subtitle = it.Text.Length > 60 ? it.Text[..60] + "…" : it.Text, Icon = Icons.Phone, Important = true, Duration = TimeSpan.FromSeconds(3) });
                return;
            }
            AppHost.Clipboard.AddExternal(new ClipItem { Kind = ClipKind.Files, Files = new() { it.Path }, Text = it.Path, Source = "Телефон" });
            BitmapSource thumb = null;
            if (it.IsPhoto) thumb = LoadThumb(it.Path, 120);
            ToastBus.Show(new Toast
            {
                Kind = ToastKind.Success, Title = it.IsPhoto ? "Фото с телефона" : "Файл с телефона", Subtitle = it.Name + " · " + Size(it.Size),
                Icon = Icons.Phone, Image = thumb, FilePath = it.Path, Important = true, Duration = TimeSpan.FromSeconds(3.5),
            });
        }

        private static BitmapSource LoadThumb(string path, int h)
        {
            try
            {
                var b = new BitmapImage();
                b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.DecodePixelHeight = h; b.UriSource = new Uri(path); b.EndInit(); b.Freeze();
                return b;
            }
            catch { return null; }
        }

        private static string Size(long n) => n > 1048576 ? (n / 1048576.0).ToString("0.0") + " МБ" : Math.Max(1, n / 1024) + " КБ";

        // ---------- QR и сеть ----------
        private void Refresh()
        {
            var addrs = PhoneLink.Addresses();
            if (_ip == null || addrs.All(a => a.ip != _ip)) _ip = addrs.FirstOrDefault().ip;
            Networks.Children.Clear();
            if (addrs.Count > 1)
                foreach (var (ip, name) in addrs)
                {
                    var b = new System.Windows.Controls.Primitives.ToggleButton { Style = (Style)FindResource("Chip"), Content = name.Split('·')[0].Trim(), ToolTip = name + " · " + ip, IsChecked = ip == _ip, Margin = new Thickness(0, 0, 6, 6) };
                    var chosen = ip;
                    b.Click += (_, __) => { _ip = chosen; Refresh(); };
                    Networks.Children.Add(b);
                }
            if (_ip == null || !PhoneLink.Running)
            {
                Qr.Source = null; QrNone.Visibility = Visibility.Visible;
                QrNone.Text = _ip == null ? "Нет сети" : "Не запустилось";
                _url = null; CopyLink.ToolTip = null;
            }
            else
            {
                var url = PhoneLink.Url(_ip);
                if (url != _url) { Qr.Source = MakeQr(url, out _qrModules); _lastFit = -1; FitQr(); }
                UpdateBreath();
                QrNone.Visibility = Visibility.Collapsed;
                _url = url; CopyLink.ToolTip = "Скопировать адрес странички\n" + url;
            }
            BuildIn();
            BuildOut();
            UpdateStatus();
        }

        private static readonly Color Green = Color.FromRgb(0x4A, 0xDE, 0x80);

        private void UpdateStatus()
        {
            bool online = PhoneLink.PhoneOnline;
            StatusDot.Fill = new SolidColorBrush(online ? Green : Color.FromRgb(0x6F, 0x6F, 0x79));
            StatusText.Text = !PhoneLink.Running ? "Сервер не запущен" : _ip == null ? "Нет общей сети" : online ? "Телефон на связи" : "Ждёт телефон";
            StatusText.Foreground = online ? new SolidColorBrush(Green) : (Brush)FindResource("Muted");
            StatusPill.Background = new SolidColorBrush(online ? Color.FromRgb(0x0F, 0x24, 0x18) : Color.FromRgb(0x16, 0x16, 0x1A));
            if (online != _wasOnline)
            {
                _wasOnline = online;
                // на связи — точка мягко «дышит» волной
                if (online)
                {
                    var t = TimeSpan.FromSeconds(1.6);
                    var grow = new System.Windows.Media.Animation.DoubleAnimation(1, 2.6, t) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever };
                    PulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                    PulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
                    StatusPulse.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0.6, 0, t) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
                }
                else
                {
                    PulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    PulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    StatusPulse.BeginAnimation(OpacityProperty, null); StatusPulse.Opacity = 0;
                    if (!_snaking) Snake.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(400)));
                }
                UpdateBreath();
            }
        }

        private void DispatcherTimerOnce(int ms, Action a)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += (_, __) => { t.Stop(); a(); };
            t.Start();
        }

        private bool _breathing;

        /// <summary>Оранжевая рамка «дышит», пока вкладка открыта и телефон не подключён. Вкладку закрыли — анимация стоит, ресурсы не тратит.</summary>
        private void UpdateBreath()
        {
            bool want = IsVisible && !_snaking && !PhoneLink.PhoneOnline && Qr.Source != null;
            if (want == _breathing) return;
            _breathing = want;
            if (want)
            {
                var a = new System.Windows.Media.Animation.DoubleAnimation(0.15, 1, TimeSpan.FromSeconds(1.4))
                {
                    AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                    EasingFunction = new System.Windows.Media.Animation.SineEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut },
                };
                System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(a, 24);
                Breath.BeginAnimation(OpacityProperty, a);
            }
            else Breath.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(300)));
        }

        private double _lastFit = -1;

        /// <summary>
        /// QR — ровный квадрат: каждая клетка ровно в целое число пикселей экрана (с учётом масштаба Windows и размера острова).
        /// Иначе при растяжении одни клетки выходят на пиксель толще других и код выглядит «поплывшим».
        /// </summary>
        private void FitQr()
        {
            if (_qrModules <= 0 || Qr.Source == null || !IsVisible) return;
            var src = PresentationSource.FromVisual(this);
            if (src?.RootVisual == null) return;
            double scale;
            try
            {
                var t = TransformToAncestor(src.RootVisual);
                var a = t.Transform(new Point(0, 0)); var b = t.Transform(new Point(100, 0));
                scale = (b.X - a.X) / 100 * src.CompositionTarget.TransformToDevice.M11;
            }
            catch { return; }
            if (scale <= 0 || Math.Abs(scale - _lastFit) < 0.0005) return;
            _lastFit = scale;
            double inner = QrHost.Width - 32; // тёмные поля вокруг кода — чтобы камера уверенно нашла его границы
            int k = Math.Max(1, (int)Math.Floor(inner * scale / _qrModules));
            double side = _qrModules * k / scale;
            Qr.Width = side; Qr.Height = side;
        }

        private bool _snaking;

        /// <summary>Телефон открыл страничку: зелёная змейка обегает код и замыкается в рамку, в центре — галочка.</summary>
        private void PlayConnected()
        {
            if (_snaking) return;
            _snaking = true;
            UpdateBreath();
            Snake.BeginAnimation(OpacityProperty, null);
            Snake.Opacity = 1;
            double w = QrHost.ActualWidth, h = QrHost.ActualHeight, th = Snake.StrokeThickness;
            double perim = 2 * (w + h - 2 * th) / th; // периметр квадрата (по середине линии) в толщинах линии
            var start = DateTime.Now;
            QrTitle.Text = "Телефон подключён";
            QrSub.Text = "Страничка открыта — можно отправлять";
            void Frame(object sender, EventArgs e)
            {
                double t = (DateTime.Now - start).TotalSeconds;
                // 0–1.1 с: хвост длиной в четверть бежит по кругу и растёт; к концу — сплошная рамка
                double p = Math.Min(1, t / 1.1);
                double ease = 1 - Math.Pow(1 - p, 3);
                double len = perim * (0.18 + 0.82 * ease);
                Snake.StrokeDashArray = new DoubleCollection { len, Math.Max(0.001, perim - len) };
                Snake.StrokeDashOffset = -perim * 1.25 * ease;
                SnakeGlow.BlurRadius = 10 + 10 * Math.Sin(Math.Min(1, t / 1.6) * Math.PI);
                // галочка выпрыгивает, когда рамка замкнулась
                double c = Math.Clamp((t - 0.85) / 0.35, 0, 1);
                double back = 1 + 2.2 * Math.Pow(c - 1, 3) + 1.2 * Math.Pow(c - 1, 2);
                double fade = t < 2.4 ? 1 : Math.Max(0, 1 - (t - 2.4) / 0.4);
                QrOk.Opacity = Math.Min(1, c * 1.5) * fade;
                QrOkScale.ScaleX = QrOkScale.ScaleY = 0.5 + 0.5 * back;
                if (t > 3.0)
                {
                    CompositionTarget.Rendering -= Frame;
                    QrOk.Opacity = 0;
                    _snaking = false;
                    Snake.StrokeDashArray = null;
                    UpdateBreath();
                    // рамка остаётся тонким зелёным контуром, пока телефон на связи
                    Snake.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(PhoneLink.PhoneOnline ? 0.45 : 0, TimeSpan.FromMilliseconds(500)));
                    QrTitle.Text = "Наведи камеру телефона";
                    QrSub.Text = "Откроется страничка — без приложений";
                }
            }
            CompositionTarget.Rendering += Frame;
        }

        /// <summary>«Не открывается?» — открыть/закрыть подсказку прямо в карточке.</summary>
        private void OnHelp(object s, RoutedEventArgs e)
        {
            bool show = HelpCard.Visibility != Visibility.Visible;
            if (show)
            {
                HelpCard.Visibility = Visibility.Visible;
                HelpCard.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            }
            else
            {
                var a = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
                a.Completed += (_, __) => { if (HelpCard.Opacity < 0.01) HelpCard.Visibility = Visibility.Collapsed; };
                HelpCard.BeginAnimation(OpacityProperty, a);
            }
        }

        private void OnCopyLink(object s, RoutedEventArgs e)
        {
            if (_url == null) return;
            ClipboardService.SetText(_url);
            ToastBus.Show(new Toast { Kind = ToastKind.Success, Title = "Ссылка скопирована", Subtitle = "Открой её в браузере телефона", Icon = Icons.Link, Important = true, Duration = TimeSpan.FromSeconds(1.6) });
        }

        private void OnPane(object s, RoutedEventArgs e)
        {
            bool notices = PaneNotices.IsChecked == true;
            FilesPane.Visibility = notices ? Visibility.Collapsed : Visibility.Visible;
            NoticesPane.Visibility = notices ? Visibility.Visible : Visibility.Collapsed;
            if (notices) { _seenNotices = PhoneLink.Notices.Count; BuildNotices(); }
            var shown = notices ? NoticesPane : FilesPane;
            shown.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        }

        // ---------- уведомления телефона ----------
        private void BuildNotices()
        {
            int unseen = PaneNotices.IsChecked == true ? 0 : PhoneLink.Notices.Count - _seenNotices;
            if (PaneNotices.IsChecked == true) _seenNotices = PhoneLink.Notices.Count;
            NoticeBadge.Visibility = unseen > 0 ? Visibility.Visible : Visibility.Collapsed;
            NoticeCount.Text = unseen > 99 ? "99+" : unseen.ToString();
            bool fresh = NoticeList.Children.Count > 0 && PhoneLink.Notices.Count > NoticeList.Children.Count;
            NoticeList.Children.Clear();
            NoticesEmpty.Visibility = PhoneLink.Notices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            NoticesHint.Text = AppHost.Notifications?.Available == false ? "Сначала разреши Isle доступ к уведомлениям: Параметры Windows → Конфиденциальность → Уведомления." : "";
            NoticesHint.Visibility = NoticesHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var n in PhoneLink.Notices.Take(60))
            {
                var row = new Border { Background = (Brush)FindResource("Card"), CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 6) };
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.Children.Add(new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x20)), VerticalAlignment = VerticalAlignment.Top,
                    Child = new Icon { Kind = "Phone", Width = 15, Height = 15, Foreground = (Brush)FindResource("Muted") } });
                var info = new StackPanel { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                info.Children.Add(new TextBlock { Text = n.Title, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                if (n.Body.Length > 0) info.Children.Add(new TextBlock { Text = n.Body.Replace('\n', ' '), Style = (Style)FindResource("MutedText"), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 34, TextWrapping = TextWrapping.Wrap });
                Grid.SetColumn(info, 1); g.Children.Add(info);
                var time = new TextBlock { Text = n.Time.ToString("HH:mm"), Style = (Style)FindResource("MutedText"), FontSize = 11, VerticalAlignment = VerticalAlignment.Top };
                Grid.SetColumn(time, 2); g.Children.Add(time);
                row.Child = g;
                NoticeList.Children.Add(row);
            }
            if (fresh && NoticeList.Children.Count > 0) SlideIn((FrameworkElement)NoticeList.Children[0]);
        }

        /// <summary>Новая строка мягко въезжает сверху.</summary>
        private static void SlideIn(FrameworkElement el)
        {
            var tt = new TranslateTransform(0, -14);
            el.RenderTransform = tt;
            var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
            tt.BeginAnimation(TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(-14, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
            el.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
        }

        private void OnOpenPhoneLink(object s, RoutedEventArgs e)
        {
            // «Связь с телефоном» встроена в Windows 10/11; если её нет — раздел «Мобильные устройства» в параметрах
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "shell:AppsFolder\\Microsoft.YourPhone_8wekyb3d8bbwe!App") { UseShellExecute = true }); }
            catch
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:mobile-devices") { UseShellExecute = true }); } catch { }
            }
            AppHost.Window.Collapse();
        }

        private static BitmapSource MakeQr(string text, out int modules)
        {
            modules = 0;
            try
            {
                var hints = new System.Collections.Generic.Dictionary<ZXing.EncodeHintType, object> { [ZXing.EncodeHintType.MARGIN] = 0 };
                var m = new ZXing.QrCode.QRCodeWriter().encode(text, ZXing.BarcodeFormat.QR_CODE, 0, 0, hints);
                int w = m.Width, h = m.Height;
                modules = w;
                var px = new byte[w * h];
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) px[y * w + x] = m[x, y] ? (byte)236 : (byte)0; // светлые клетки на чёрном
                var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Gray8, null, px, w);
                b.Freeze();
                return b;
            }
            catch (Exception ex) { Journal.Warn("phone", "QR не получился: " + ex.Message); return null; }
        }

        // ---------- с телефона ----------
        private void BuildIn()
        {
            bool fresh = InList.Children.Count > 0 && PhoneLink.Received.Count > InList.Children.Count;
            InList.Children.Clear();
            InEmpty.Visibility = PhoneLink.Received.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var it in PhoneLink.Received.Take(50))
            {
                var row = new Border { Background = (Brush)FindResource("Card"), CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 6), Cursor = Cursors.Hand };
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                FrameworkElement icon;
                if (it.IsPhoto && LoadThumb(it.Path, 72) is BitmapSource th)
                    icon = new Border { Width = 56, Height = 40, CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = new Image { Source = th, Stretch = Stretch.UniformToFill } };
                else
                    icon = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x20)),
                        Child = new Icon { Kind = it.Text != null ? "Text" : "File", Width = 15, Height = 15, Foreground = (Brush)FindResource("Muted") } };
                g.Children.Add(icon);
                var info = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                info.Children.Add(new TextBlock { Text = it.Text != null ? it.Text.Replace('\n', ' ') : it.Name, FontSize = 13, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis });
                info.Children.Add(new TextBlock { Text = (it.Text != null ? "текст · в буфере" : Size(it.Size)) + " · " + it.Time.ToString("HH:mm"), Style = (Style)FindResource("MutedText"), FontSize = 11 });
                Grid.SetColumn(info, 1); g.Children.Add(info);
                row.Child = g;
                row.ToolTip = it.Text != null ? "Клик — скопировать снова" : "Клик — открыть · правый клик — показать в папке";
                row.MouseLeftButtonUp += (_, __) =>
                {
                    if (it.Text != null) { ClipboardService.SetText(it.Text); ToastBus.Show(new Toast { Kind = ToastKind.Success, Title = "Скопировано", Icon = Icons.Copy, Important = true, Duration = TimeSpan.FromSeconds(1.2) }); }
                    else if (File.Exists(it.Path)) { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(it.Path) { UseShellExecute = true }); AppHost.Window.Collapse(); } catch { } }
                };
                row.MouseRightButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    if (it.Path != null && File.Exists(it.Path)) { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + it.Path + "\""); AppHost.Window.Collapse(); }
                };
                InList.Children.Add(row);
            }
            if (fresh && InList.Children.Count > 0) SlideIn((FrameworkElement)InList.Children[0]);
        }

        // ---------- на телефон ----------
        private void BuildOut()
        {
            OutList.Children.Clear();
            OutHint.Text = PhoneLink.Outgoing.Count == 0 && PhoneLink.OutgoingText.Length == 0
                ? "Перетащи сюда файлы — на телефоне появится «Скачать»"
                : "Ждут на страничке телефона:";
            if (PhoneLink.OutgoingText.Length > 0) OutList.Children.Add(OutChip("Text", "Текст из буфера", () => PhoneLink.OfferText("")));
            foreach (var f in PhoneLink.Outgoing.ToList()) OutList.Children.Add(OutChip("File", f.Name, () => PhoneLink.Unoffer(f)));
        }

        private FrameworkElement OutChip(string icon, string name, Action remove)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new Icon { Kind = icon, Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("Muted") });
            sp.Children.Add(new TextBlock { Text = name, FontSize = 12, Margin = new Thickness(6, 0, 6, 0), MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var x = new Button { Style = (Style)FindResource("Ghost"), Padding = new Thickness(2), Content = new Icon { Kind = "Close", Width = 11, Height = 11 }, ToolTip = "Убрать" };
            x.Click += (_, __) => remove();
            sp.Children.Add(x);
            return new Border { Child = sp, Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x1A)), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 4, 4, 4), Margin = new Thickness(0, 0, 6, 6) };
        }

        private void OnDragEnter(object s, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) { Drop.BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)); e.Effects = DragDropEffects.Copy; }
            e.Handled = true;
        }

        private void OnDragLeave(object s, DragEventArgs e) => Drop.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x33));

        private void OnDrop(object s, DragEventArgs e)
        {
            OnDragLeave(s, e);
            e.Handled = true;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) { PhoneLink.Start(); PhoneLink.Offer(files); }
        }

        private void OnPickFiles(object s, RoutedEventArgs e)
        {
            AppHost.Window.SuppressAutoCollapse = true;
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Отправить на телефон", Multiselect = true };
                if (dlg.ShowDialog() == true) PhoneLink.Offer(dlg.FileNames);
            }
            finally { AppHost.Window.SuppressAutoCollapse = false; }
        }

        private void OnSendClipboard(object s, RoutedEventArgs e)
        {
            string text = null;
            try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); } catch { }
            if (string.IsNullOrWhiteSpace(text))
            {
                ToastBus.Show(new Toast { Kind = ToastKind.Error, Title = "В буфере нет текста", Icon = Icons.Clipboard, Important = true, Duration = TimeSpan.FromSeconds(2) });
                return;
            }
            PhoneLink.OfferText(text);
        }
    }
}
