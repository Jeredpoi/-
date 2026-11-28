using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
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
            Hook();
            PhoneLink.PairedNow += () => Dispatcher.InvokeAsync(() => DispatcherTimerOnce(2600, () => ShowPaired(true)));
            PhoneLink.PageOpened += () =>
            {
                if (IsVisible) PlayConnected();
                // показывали код привязанному телефону (потерял закладку) — открыл: снова прячем
                if (_qrForced && PhoneLink.Paired) { _qrForced = false; DispatcherTimerOnce(2600, () => ShowPaired(true)); }
            };
            PhoneLink.NoticesChanged += () => Dispatcher.InvokeAsync(BuildNotices);
            QrHost.SizeChanged += (_, __) => FitQr();
            // размер клеток пересчитываем, когда вкладку показали (после анимации раскрытия), а не на каждый проход раскладки
            IsVisibleChanged += (_, __) => { if (IsVisible) { _lastFit = -1; Dispatcher.InvokeAsync(FitQr, DispatcherPriority.Background); DispatcherTimerOnce(500, FitQr); } UpdateBreath(); };
            BuildNotices();
        }

        public void OnShown() { PhoneLink.Start(); Refresh(); }

        /// <summary>Пришедшее с телефона — в буфер и плашку, даже если вкладку ещё не открывали (сервер стартует с Isle).</summary>
        public static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            PhoneLink.ItemReceived += OnReceived;
        }

        /// <summary>С телефона пришло: в «Буфер» (текст — сразу в буфер обмена) и плашка в чёлке.</summary>
        private static void OnReceived(PhoneItem it)
        {
            if (it.Text != null)
            {
                ClipboardService.IgnoreUntil = DateTime.Now.AddMilliseconds(400); // не дублировать: сами положим в историю
                ClipboardService.SetText(it.Text);
                AppHost.Clipboard.AddExternal(new ClipItem { Text = it.Text, Source = "Телефон", Kind = ClipboardService.Classify(it.Text, "") });
                ToastBus.Show(new Toast { Kind = ToastKind.Success, Title = "Текст с телефона — в буфере", Private = true, Subtitle = it.Text.Length > 60 ? it.Text[..60] + "…" : it.Text, Icon = Icons.Phone, Important = true, Duration = TimeSpan.FromSeconds(3) });
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
            ShowPaired(false);
            UpdateStatus();
            UpdateHttpsHelp();
        }

        private static readonly Color Green = Color.FromRgb(0x4A, 0xDE, 0x80);

        private void UpdateStatus()
        {
            bool online = PhoneLink.PhoneOnline;
            StatusDot.Fill = new SolidColorBrush(online ? Green : Color.FromRgb(0x6F, 0x6F, 0x79));
            StatusText.Text = !PhoneLink.Running ? "Сервер не запущен" : _ip == null ? "Нет общей сети" : online ? "Телефон на связи" : "Ждёт телефон";
            StatusText.Foreground = online ? new SolidColorBrush(Green) : (Brush)FindResource("Muted");
            if (online) StatusPill.Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x24, 0x18));
            else StatusPill.SetResourceReference(Border.BackgroundProperty, "Plate");
            if (PhoneLink.Paired)
            {
                PairedName.Text = PhoneLink.PairedName;
                PairedState.Text = online ? "на связи" : "открой страничку из закладок";
                PairedDot.Fill = new SolidColorBrush(online ? Green : Color.FromRgb(0x6F, 0x6F, 0x79));
            }
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

        // ---------- привязка ----------
        private bool _qrForced;     // «Показать QR» у привязанного телефона
        private bool _pairAnim;

        /// <summary>
        /// Привязан — QR-карточка уезжает влево и прячется, справа сверху имя телефона и «Привязать заново».
        /// Не привязан — карточка с кодом на месте.
        /// </summary>
        private void ShowPaired(bool animate)
        {
            bool paired = PhoneLink.Paired;
            PairedBox.Visibility = paired ? Visibility.Visible : Visibility.Collapsed;
            bool hideQr = paired && !_qrForced;
            if (_pairAnim) return;
            if (!hideQr)
            {
                PairCardShift.BeginAnimation(TranslateTransform.XProperty, null); PairCardShift.X = 0;
                PairCard.BeginAnimation(OpacityProperty, null); PairCard.Opacity = 1;
                PairCard.Visibility = Visibility.Visible;
                QrCol.Width = new GridLength(268);
                return;
            }
            if (PairCard.Visibility != Visibility.Visible) return;
            if (!animate || !IsVisible) { PairCard.Visibility = Visibility.Collapsed; QrCol.Width = new GridLength(0); return; }
            _pairAnim = true;
            var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn };
            var slide = new System.Windows.Media.Animation.DoubleAnimation(0, -300, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease };
            slide.Completed += (_, __) =>
            {
                _pairAnim = false;
                PairCard.Visibility = Visibility.Collapsed;
                QrCol.Width = new GridLength(0);
                // правая часть плавно занимает освободившееся место
                if (QrCol.Width.Value == 0 && Content is Grid root && root.Children.Count > 1 && root.Children[1] is FrameworkElement right)
                {
                    var tt = new TranslateTransform(282, 0);
                    right.RenderTransform = tt;
                    tt.BeginAnimation(TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(380))
                        { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } });
                }
            };
            PairCardShift.BeginAnimation(TranslateTransform.XProperty, slide);
            PairCard.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(380)));
        }

        private void OnShowQr(object s, RoutedEventArgs e)
        {
            _qrForced = !_qrForced;
            ShowPaired(true);
        }

        private void OnRepair(object s, RoutedEventArgs e)
        {
            _qrForced = false;
            PhoneLink.Unpair(); // новый ключ → Changed → Refresh: карточка с новым QR-кодом возвращается
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

        private void UpdateHttpsHelp()
        {
            bool tls = PhoneTls.Ready;
            HttpsToggle.Visibility = tls ? Visibility.Visible : Visibility.Collapsed;
            HttpsNote.Text = !tls
                ? "5. Ссылка по http: сертификат для https не скачался (нет интернета?). «Небезопасно» в браузере не страшно — данные не выходят из твоей сети."
                : PhoneLink.Https
                    ? "5. Ссылка по https (…local-ip.sh) — телефону нужен интернет, чтобы найти адрес. Нет интернета — перейди на http."
                    : "5. Ссылка по http — браузер напишет «Небезопасно». Есть интернет на телефоне — верни https.";
            HttpsToggleText.Text = PhoneLink.Https ? "Перейти на http" : "Вернуть https";
        }

        private void OnHttpsToggle(object s, RoutedEventArgs e) => PhoneLink.SetHttps(!AppSettings.Current.PhoneHttps);

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
                g.Children.Add(new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(10), Background = (Brush)FindResource("Plate"), VerticalAlignment = VerticalAlignment.Top,
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

        private const string PhoneLinkFamily = "Microsoft.YourPhone_8wekyb3d8bbwe";

        /// <summary>Установлена ли «Связь с телефоном» (Phone Link) у этого пользователя.</summary>
        private static bool PhoneLinkInstalled()
        {
            try { return new Windows.Management.Deployment.PackageManager().FindPackagesForUser("", PhoneLinkFamily).Any(); }
            catch (Exception ex) { Journal.Debug("phone", "не удалось проверить «Связь с телефоном»: " + ex.Message); return true; }
        }

        private void OnOpenPhoneLink(object s, RoutedEventArgs e)
        {
            // «Связь с телефоном» — приложение Windows. Нет её — открываем страницу в Microsoft Store, а если и он не открылся — «Мобильные устройства» в параметрах
            bool installed = PhoneLinkInstalled();
            Journal.Info("phone", "«Подключить телефон»: «Связь с телефоном» " + (installed ? "установлена — открываю" : "не установлена — открываю Microsoft Store"));
            bool Run(string target)
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); return true; }
                catch (Exception ex) { Journal.Warn("phone", "не открылось " + target + ": " + ex.Message); return false; }
            }
            bool ok = installed
                ? Run("shell:AppsFolder\\" + PhoneLinkFamily + "!App") || Run("ms-phone:")
                : Run("ms-windows-store://pdp/?productid=9NMPJ99VJBWV");
            if (!ok) ok = Run("ms-settings:mobile-devices");
            if (!ok) ToastBus.Show(new Toast { Kind = ToastKind.Error, Title = "Не открылась «Связь с телефоном»", Subtitle = "Найди её в меню «Пуск»", Icon = Icons.Phone, Important = true, Duration = TimeSpan.FromSeconds(3) });
            AppHost.Window.Collapse();
        }

        internal static BitmapSource MakeQr(string text, out int modules)
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
                var row = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 6), Cursor = Cursors.Hand };
                row.SetResourceReference(Border.BackgroundProperty, "Card");
                row.MouseEnter += (_, __) => row.SetResourceReference(Border.BackgroundProperty, "CardHover");
                row.MouseLeave += (_, __) => row.SetResourceReference(Border.BackgroundProperty, "Card");
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                FrameworkElement icon;
                if (it.IsPhoto && LoadThumb(it.Path, 72) is BitmapSource th)
                {
                    var tb = new Border { Width = 56, Height = 40, CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = new Image { Source = th, Stretch = Stretch.UniformToFill },
                        ToolTip = "Клик — увеличить" };
                    // клик по миниатюре — фото вырастает на всю вкладку (как в буфере обмена); по остальной строке — копирует
                    tb.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (File.Exists(it.Path)) OpenPhoto(it, tb, th); };
                    icon = tb;
                }
                else
                    icon = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(8), Background = (Brush)FindResource("Plate"),
                        Child = new Icon { Kind = it.Text != null ? "Text" : "File", Width = 15, Height = 15, Foreground = (Brush)FindResource("Muted") } };
                g.Children.Add(icon);
                var info = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                info.Children.Add(new TextBlock { Text = it.Text != null ? it.Text.Replace('\n', ' ') : it.Name, FontSize = 13, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis });
                info.Children.Add(new TextBlock { Text = (it.Text != null ? "текст · в буфере" : Size(it.Size)) + " · " + it.Time.ToString("HH:mm"), Style = (Style)FindResource("MutedText"), FontSize = 11 });
                Grid.SetColumn(info, 1); g.Children.Add(info);
                // справа — кнопки «Открыть» и «Показать в папке»; клик по самой записи только копирует
                if (it.Path != null)
                {
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
                    btns.Children.Add(RowButton("External", "Открыть", () => OpenFile(it.Path)));
                    btns.Children.Add(RowButton("Folder", "Показать в папке", () => ShowInFolder(it.Path)));
                    Grid.SetColumn(btns, 2); g.Children.Add(btns);
                }
                row.Child = g;
                row.ToolTip = it.Text != null ? "Клик — скопировать снова" : "Клик — скопировать" + (it.IsPhoto ? " (вставится и как картинка, и как файл) · клик по фото — увеличить" : "");
                row.MouseLeftButtonUp += (_, __) =>
                {
                    if (it.Text != null) { ClipboardService.SetText(it.Text); Copied(); }
                    else if (File.Exists(it.Path)) CopyFile(it);
                    else ToastBus.Show(new Toast { Kind = ToastKind.Error, Title = "Файла больше нет", Subtitle = it.Name, Icon = Icons.File, Important = true, Duration = TimeSpan.FromSeconds(2) });
                };
                InList.Children.Add(row);
            }
            if (fresh && InList.Children.Count > 0) SlideIn((FrameworkElement)InList.Children[0]);
        }

        private Button RowButton(string icon, string tip, Action act)
        {
            var b = new Button { Style = (Style)FindResource("Ghost"), Width = 30, Height = 30, Padding = new Thickness(0), ToolTip = tip,
                Content = new Icon { Kind = icon, Width = 14, Height = 14 } };
            b.Click += (_, e) => { e.Handled = true; act(); };
            return b;
        }

        private static void Copied(string what = "Скопировано") =>
            ToastBus.Show(new Toast { Kind = ToastKind.Success, Title = what, Icon = Icons.Copy, Important = true, Duration = TimeSpan.FromSeconds(1.2) });

        /// <summary>Файл — в буфер как файл (вставится в папку, чат, письмо); фото — ещё и как картинка (вставится в редактор).</summary>
        private static void CopyFile(PhoneItem it)
        {
            var path = it.Path;
            BitmapSource img = null;
            if (it.IsPhoto)
            {
                try
                {
                    var b = new BitmapImage();
                    b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.UriSource = new Uri(path); b.EndInit(); b.Freeze();
                    img = b;
                }
                catch { } // формат, который Windows не умеет показывать (например, HEIC без расширения) — копируем только файл
            }
            ClipboardService.IgnoreUntil = DateTime.Now.AddMilliseconds(600); // в историю буфера он уже попал, когда пришёл
            bool ok = ClipboardWrite.SetOrLater(() =>
            {
                var d = new DataObject();
                d.SetFileDropList(new System.Collections.Specialized.StringCollection { path });
                if (img != null) d.SetImage(img);
                return d;
            }, it.IsPhoto ? "фото с телефона" : "файл с телефона");
            if (ok) Copied(it.IsPhoto ? "Фото скопировано" : "Файл скопирован");
        }

        private static void OpenFile(string path)
        {
            if (!File.Exists(path)) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); AppHost.Window.Collapse(); } catch { }
        }

        private static void ShowInFolder(string path)
        {
            if (!File.Exists(path)) return;
            try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\""); AppHost.Window.Collapse(); } catch { }
        }

        // ---------- на телефон ----------
        private void BuildOut()
        {
            OutList.Children.Clear();
            OutHint.Text = PhoneLink.Outgoing.Count == 0 && PhoneLink.OutgoingText.Length == 0
                ? "Перетащи сюда файлы — на телефоне появится «Скачать»"
                : "Ждут на страничке телефона:";
            if (PhoneLink.OutgoingText.Length > 0) OutList.Children.Add(OutChip("Text", "Текст из буфера", () => PhoneLink.OfferText("")));
            foreach (var f in PhoneLink.OutgoingSnapshot()) OutList.Children.Add(OutChip("File", f.Name, () => PhoneLink.Unoffer(f)));
        }

        private FrameworkElement OutChip(string icon, string name, Action remove)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new Icon { Kind = icon, Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("Muted") });
            sp.Children.Add(new TextBlock { Text = name, FontSize = 12, Margin = new Thickness(6, 0, 6, 0), MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var x = new Button { Style = (Style)FindResource("Ghost"), Padding = new Thickness(2), Content = new Icon { Kind = "Close", Width = 11, Height = 11 }, ToolTip = "Убрать" };
            x.Click += (_, __) => remove();
            sp.Children.Add(x);
            return new Border { Child = sp, Background = (Brush)FindResource("ChipBg"), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 4, 4, 4), Margin = new Thickness(0, 0, 6, 6) };
        }

        private void OnDragEnter(object s, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) { Drop.BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)); e.Effects = DragDropEffects.Copy; }
            e.Handled = true;
        }

        private void OnDragLeave(object s, DragEventArgs e) => Drop.SetResourceReference(Border.BorderBrushProperty, "CardBorder");

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

        // ---------- просмотр фото ----------
        private PhoneItem _photo;
        private FrameworkElement _photoThumb;
        private Size _photoPx;
        private Point? _photoPan;
        private bool _photoPanned;
        private int _photoVersion;

        private void OpenPhoto(PhoneItem it, FrameworkElement thumb, BitmapSource preview)
        {
            _photo = it; _photoThumb = thumb;
            int version = ++_photoVersion;
            PhotoViewer.Visibility = Visibility.Visible;
            PhotoViewer.UpdateLayout();
            PhotoImage.Source = preview;
            _photoPx = new Size(preview.PixelWidth, preview.PixelHeight);
            PhotoZoom.Matrix = Matrix.Identity;
            AnimatePhoto(ThumbRectOf(thumb), FitPhoto(), true);
            Journal.Info("phone", "просмотр фото");
            AppHost.Window.FocusIsland();
            PhotoViewer.Focus();
            var path = it.Path;
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    // уменьшаем только большие: по большей стороне до 2400 (длинный скриншот 1080×10000 раньше
                    // растягивался до 2400×22222 — сотни мегабайт)
                    int pw = 0, ph = 0;
                    try
                    {
                        using var fs = File.OpenRead(path);
                        var fr = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                        pw = fr.PixelWidth; ph = fr.PixelHeight;
                    }
                    catch { }
                    var b = new BitmapImage();
                    b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.UriSource = new Uri(path);
                    if (pw >= ph && pw > 2400) b.DecodePixelWidth = 2400;
                    else if (ph > pw && ph > 2400) b.DecodePixelHeight = 2400;
                    b.EndInit(); b.Freeze();
                    Dispatcher.InvokeAsync(() =>
                    {
                        if (version != _photoVersion || PhotoViewer.Visibility != Visibility.Visible) return;
                        PhotoImage.Source = b;
                    });
                }
                catch (Exception ex) { Journal.Debug("phone", "фото не открылось: " + ex.Message); }
            });
        }

        private Rect ThumbRectOf(FrameworkElement thumb)
        {
            try
            {
                var p = thumb.TranslatePoint(new Point(0, 0), PhotoCanvas);
                return new Rect(p, new Size(thumb.ActualWidth, thumb.ActualHeight));
            }
            catch { return new Rect(PhotoCanvas.ActualWidth / 2 - 20, PhotoCanvas.ActualHeight / 2 - 15, 40, 30); }
        }

        private Rect FitPhoto()
        {
            double W = PhotoCanvas.ActualWidth - 24, H = PhotoCanvas.ActualHeight - 60; // снизу — кнопки
            double k = Math.Min(W / Math.Max(1, _photoPx.Width), H / Math.Max(1, _photoPx.Height));
            double w = _photoPx.Width * k, h = _photoPx.Height * k;
            return new Rect((PhotoCanvas.ActualWidth - w) / 2, 12 + (H - h) / 2, w, h);
        }

        private void AnimatePhoto(Rect from, Rect to, bool opening)
        {
            var dur = TimeSpan.FromMilliseconds(opening ? 320 : 240);
            IEasingFunction ease = opening ? new QuarticEase { EasingMode = EasingMode.EaseOut } : new CubicEase { EasingMode = EasingMode.EaseInOut };
            void Go(DependencyProperty dp, double a, double b, EventHandler done = null)
            {
                var an = new DoubleAnimation(a, b, dur) { EasingFunction = ease };
                if (done != null) an.Completed += done;
                PhotoFrame.BeginAnimation(dp, an);
            }
            Go(Canvas.LeftProperty, from.X, to.X);
            Go(Canvas.TopProperty, from.Y, to.Y);
            Go(WidthProperty, from.Width, to.Width);
            Go(HeightProperty, from.Height, to.Height, opening ? null : (_, __) =>
            {
                PhotoViewer.Visibility = Visibility.Collapsed;
                PhotoImage.Source = null; // полное фото не держим в памяти
            });
            PhotoImage.Stretch = Stretch.Uniform;
            var fade = new DoubleAnimation(opening ? 1 : 0, dur);
            PhotoBack.BeginAnimation(OpacityProperty, fade);
            PhotoBar.BeginAnimation(OpacityProperty, fade);
        }

        private void ClosePhoto()
        {
            if (PhotoViewer.Visibility != Visibility.Visible) return;
            _photoVersion++;
            PhotoZoom.Matrix = Matrix.Identity;
            var cur = new Rect(Canvas.GetLeft(PhotoFrame), Canvas.GetTop(PhotoFrame), PhotoFrame.ActualWidth, PhotoFrame.ActualHeight);
            var to = _photoThumb != null && _photoThumb.IsVisible ? ThumbRectOf(_photoThumb) : new Rect(cur.X + cur.Width / 2 - 10, cur.Y + cur.Height / 2 - 10, 20, 20);
            AnimatePhoto(cur, to, false);
        }

        private void OnPhotoBackClick(object s, MouseButtonEventArgs e) { e.Handled = true; if (!_photoPanned) ClosePhoto(); _photoPanned = false; }
        private void OnPhotoClose(object s, RoutedEventArgs e) => ClosePhoto();
        private void OnPhotoKey(object s, KeyEventArgs e) { if (e.Key == Key.Escape) { e.Handled = true; ClosePhoto(); } }

        private void OnPhotoImageClick(object s, MouseButtonEventArgs e)
        {
            // клик по самому фото не закрывает; перетаскивание — сдвиг увеличенного
            e.Handled = true;
            _photoPan = null;
            _photoPanned = false; // иначе следующий клик по фону после перетаскивания не закрывал просмотр
            PhotoImage.ReleaseMouseCapture();
        }

        private void OnPhotoWheel(object s, MouseWheelEventArgs e)
        {
            e.Handled = true;
            var p = e.GetPosition(PhotoImage);
            var m = PhotoZoom.Matrix;
            double f = e.Delta > 0 ? 1.2 : 1 / 1.2;
            double next = Math.Clamp(m.M11 * f, 1, 8);
            f = next / m.M11;
            m.ScaleAtPrepend(f, f, p.X, p.Y);
            if (next <= 1.001) m = Matrix.Identity;
            PhotoZoom.Matrix = m;
        }

        protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseLeftButtonDown(e);
            if (PhotoViewer.Visibility == Visibility.Visible && PhotoZoom.Matrix.M11 > 1.001 && e.OriginalSource == PhotoImage)
            {
                _photoPan = e.GetPosition(PhotoFrame);
                PhotoImage.CaptureMouse();
            }
        }

        protected override void OnPreviewMouseMove(MouseEventArgs e)
        {
            base.OnPreviewMouseMove(e);
            if (_photoPan is Point from && e.LeftButton == MouseButtonState.Pressed)
            {
                var p = e.GetPosition(PhotoFrame);
                var m = PhotoZoom.Matrix; m.Translate(p.X - from.X, p.Y - from.Y);
                PhotoZoom.Matrix = m;
                _photoPan = p; _photoPanned = true;
            }
        }

        private void OnPhotoCopy(object s, RoutedEventArgs e) { if (_photo != null && File.Exists(_photo.Path)) CopyFile(_photo); }
        private void OnPhotoOpen(object s, RoutedEventArgs e) { if (_photo != null) OpenFile(_photo.Path); }
        private void OnPhotoFolder(object s, RoutedEventArgs e) { if (_photo != null) ShowInFolder(_photo.Path); }
    }
}
