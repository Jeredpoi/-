using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Isle.Services;
using Radios = Windows.Devices.Radios;

namespace Isle.Views
{
    public partial class ControlsView : UserControl, IIslandView
    {
        private static readonly TimeSpan ConfirmTime = TimeSpan.FromSeconds(3);
        private static readonly Brush On = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xF1));
        private static readonly Brush Off = Brushes.Black;
        private static readonly Brush OnBorder = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x52));
        private static readonly Brush DangerBg = new SolidColorBrush(Color.FromRgb(0x3A, 0x12, 0x14));
        private static readonly Brush DangerFg = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));

        private string _armed;
        private readonly DispatcherTimer _disarm = new() { Interval = ConfirmTime };
        private bool _fillingDevices, _settingVolume;

        // загрузка ЦП и памяти в плитке «Диспетчер» — обновляется, пока вкладка открыта
        private readonly DispatcherTimer _load = new() { Interval = TimeSpan.FromSeconds(2) };

        public ControlsView()
        {
            InitializeComponent();
            _load.Tick += (_, __) => ShowLoad();
            IsVisibleChanged += (_, __) => { if (IsVisible) { ShowLoad(); _load.Start(); } else _load.Stop(); };
            _disarm.Tick += (_, __) => Disarm();
            AppHost.Audio.VolumeChanged += (_, __) => SyncVolume();
            AppHost.Audio.DeviceChanged += () => { SyncVolume(); FillDevices(); };
        }

        public void OnShown()
        {
            WifiPanel.BeginAnimation(OpacityProperty, null);
            WifiPanel.Visibility = Visibility.Collapsed; // вкладка всегда открывается с плитками
            Cascade();
            Refresh();
        }

        /// <summary>Обновить состояния плиток (после переключения — без повторной анимации появления).</summary>
        private async void Refresh()
        {
            SyncVolume();
            FillDevices();
            MicBtn.IsChecked = !AppHost.Audio.MicMuted; // подсвечено = микрофон работает, как у остальных кнопок
            MicBtn.ToolTip = AppHost.Audio.MicMuted ? "Микрофон выключен — нажми, чтобы включить" : "Микрофон включён — нажми, чтобы выключить";

            var net = SystemControls.Network();
            NetName.Text = net.name;
            NetStatus.Text = net.status;
            SetTile(NetBtn, NetDot, null, net.up);
            SetBadge(NetBadgeText, net.up ? "Вкл" : "Нет сети");

            bool dark = SystemControls.IsDarkTheme;
            ThemeStatus.Text = dark ? "Включена" : "Выключена";
            SetTile(ThemeTile, ThemeDot, ThemeIcon, dark);
            SetBadge(ThemeBadgeText, dark ? "Вкл" : "Выкл");
            SetBadge(TaskBadgeText, "Задачи");

            var bt = await SystemControls.GetRadio(Radios.RadioKind.Bluetooth);
            BtStatus.Text = bt == null ? "Нет адаптера" : bt.Value ? "Включён" : "Выключен";
            SetTile(BtTile, BtDot, BtIcon, bt == true);
            SetBadge(BtBadgeText, bt == null ? "Нет" : bt.Value ? "Вкл" : "Выкл");
            if (bt == true)
            {
                // подключённые устройства: одно — по имени, несколько — числом (имена во всплывающей подсказке)
                var devices = await SystemControls.ConnectedBluetoothAsync();
                if (devices.Count == 1) BtStatus.Text = devices[0];
                else if (devices.Count > 1) BtStatus.Text = devices.Count + " " + Plural(devices.Count, "устройство", "устройства", "устройств");
                BtStatus.ToolTip = devices.Count > 0 ? string.Join("\n", devices) : null;
            }
            ShowLoad();
        }

        private void ShowLoad()
        {
            var (cpu, ram) = SystemControls.Load();
            LoadText.Text = "ЦП " + cpu + "% · ОЗУ " + ram + "%";
        }

        private static string Plural(int n, string one, string few, string many)
        {
            int m10 = n % 10, m100 = n % 100;
            return m10 == 1 && m100 != 11 ? one : m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14) ? few : many;
        }

        /// <summary>Включённое — белая плитка-иконка с чёрным глифом и светлой обводкой карточки, выключенное — чёрная с белым глифом.</summary>
        private static void SetTile(Button tile, Border dot, Helpers.Icon icon, bool on)
        {
            if (on) tile.BorderBrush = OnBorder; else tile.ClearValue(BorderBrushProperty);
            dot.Background = on ? On : Off;
            var ic = icon ?? (dot.Child as Helpers.Icon);
            if (ic != null) ic.Foreground = on ? Brushes.Black : new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xF1));
        }

        private static void SetBadge(TextBlock text, string value)
        {
            text.Text = value;
            ((Border)text.Parent).Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>Плитки, строка громкости и кнопки питания появляются каскадом: сдвиг 12 px снизу, шаг 70 мс.</summary>
        private void Cascade()
        {
            var items = new FrameworkElement[] { WifiTile, BtTile, ThemeTile, TaskTile, VolumeCard, PowerRow };
            var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
            for (int i = 0; i < items.Length; i++)
            {
                var el = items[i];
                var begin = TimeSpan.FromMilliseconds(i * 70);
                var tt = el.RenderTransform as TranslateTransform;
                if (tt == null) el.RenderTransform = tt = new TranslateTransform();
                // до своей очереди элемент скрыт и сдвинут (базовые значения), анимация доводит до места и держит
                tt.Y = 12;
                el.Opacity = 0;
                tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(450)) { BeginTime = begin, EasingFunction = ease });
                el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(350)) { BeginTime = begin });
            }
        }

        private void OnNetwork(object s, RoutedEventArgs e) => SystemControls.OpenSettings("ms-settings:network");

        // ===== Сети Wi-Fi прямо в чёлке =====
        private bool _wifiScanning;
        private string _wifiOpenSsid; // у какой сети сейчас раскрыто поле пароля

        private void OnWifiList(object s, RoutedEventArgs e) => ShowWifiPanel(true);
        private void OnWifiBack(object s, RoutedEventArgs e) => ShowWifiPanel(false);
        private void OnWifiRescan(object s, RoutedEventArgs e) => ScanWifi();

        /// <summary>Панель сетей раскрывается из шеврона плитки Wi-Fi (масштаб + прозрачность) и закрывается обратно.</summary>
        private void ShowWifiPanel(bool show)
        {
            var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(show ? 320 : 200);
            if (show)
            {
                WifiPanel.Visibility = Visibility.Visible;
                WifiPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur));
                WifiPanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1, dur) { EasingFunction = ease });
                WifiPanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1, dur) { EasingFunction = ease });
                ScanWifi();
            }
            else
            {
                var fade = new DoubleAnimation(1, 0, dur);
                fade.Completed += (_, __) => { if (WifiPanel.Opacity < 0.05) WifiPanel.Visibility = Visibility.Collapsed; };
                WifiPanel.BeginAnimation(OpacityProperty, fade);
                WifiPanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.95, dur) { EasingFunction = ease });
                WifiPanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.95, dur) { EasingFunction = ease });
                _wifiOpenSsid = null;
                Refresh();
            }
        }

        private async void ScanWifi()
        {
            if (_wifiScanning) return;
            _wifiScanning = true;
            WifiHint.Text = "ищу сети…";
            var rot = (RotateTransform)WifiRescanIcon.RenderTransform;
            rot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(800)) { RepeatBehavior = RepeatBehavior.Forever });
            try
            {
                var (nets, error) = await SystemControls.WifiNetworksAsync();
                var current = SystemControls.Network().status;
                // подключённая — первой
                nets.Sort((a, b) => a.Ssid == current ? -1 : b.Ssid == current ? 1 : b.Signal.CompareTo(a.Signal));
                WifiList.Children.Clear();
                WifiHint.Text = error == null ? (nets.Count == 0 ? "сетей рядом не видно" : nets.Count + " " + Plural(nets.Count, "сеть", "сети", "сетей")) : "";
                if (error != null)
                    WifiList.Children.Add(new TextBlock { Text = error, TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(4, 6, 4, 0) });
                int i = 0;
                foreach (var n in nets)
                {
                    var row = WifiRow(n, n.Ssid == current);
                    WifiList.Children.Add(row);
                    // строки въезжают каскадом, как плитки
                    var tt = new TranslateTransform(0, 8);
                    row.RenderTransform = tt;
                    row.Opacity = 0;
                    var begin = TimeSpan.FromMilliseconds(Math.Min(i++, 10) * 35);
                    tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(320)) { BeginTime = begin, EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } });
                    row.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { BeginTime = begin });
                }
            }
            finally
            {
                rot.BeginAnimation(RotateTransform.AngleProperty, null);
                _wifiScanning = false;
            }
        }

        /// <summary>
        /// Строка сети: значок с силой сигнала, имя, «Подключено»/«Защищённая»/«Открытая» и кнопка «Подключить».
        /// Для новой защищённой сети по кнопке под строкой раскрывается поле пароля и «Войти».
        /// </summary>
        private FrameworkElement WifiRow(SystemControls.WifiNet n, bool connected)
        {
            var fg = (Brush)FindResource("Fg");
            var muted = (Brush)FindResource("Muted");
            var root = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            var card = new Border { CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x1A)), Padding = new Thickness(10, 8, 8, 8) };
            root.Children.Add(card);
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            card.Child = g;

            var dot = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(11), Background = connected ? On : Off, Margin = new Thickness(0, 0, 10, 0) };
            dot.Child = SignalGlyph(n.Signal, connected ? Brushes.Black : fg);
            g.Children.Add(dot);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(text, 1);
            var title = new StackPanel { Orientation = Orientation.Horizontal };
            title.Children.Add(new TextBlock { Text = n.Ssid, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = fg, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260 });
            if (n.Secured) title.Children.Add(new Helpers.Icon { Data = Icons.Lock, Width = 12, Height = 12, Foreground = muted, Margin = new Thickness(6, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            text.Children.Add(title);
            var sub = new TextBlock { FontSize = 12, Foreground = muted, Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
                Text = connected ? "Подключено" : (n.Secured ? "Защищённая" : "Открытая") + " · " + (n.Signal >= 70 ? "сильный сигнал" : n.Signal >= 40 ? "средний сигнал" : "слабый сигнал") };
            text.Children.Add(sub);
            g.Children.Add(text);

            if (connected) return root;

            var connect = PillButton("Подключить", false);
            Grid.SetColumn(connect, 2);
            g.Children.Add(connect);

            // поле пароля (раскрывается под строкой)
            var pwdRow = new Grid { Margin = new Thickness(44, 6, 0, 0), Visibility = Visibility.Collapsed };
            pwdRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pwdRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var pwd = new PasswordBox { Height = 34, FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Пароль сети" };
            pwdRow.Children.Add(pwd);
            var login = PillButton("Войти", true);
            login.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(login, 1);
            pwdRow.Children.Add(login);
            var status = new TextBlock { FontSize = 12, Margin = new Thickness(46, 4, 0, 0), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
            root.Children.Add(pwdRow);
            root.Children.Add(status);

            void Status(string msg, bool error)
            {
                status.Text = msg ?? "";
                status.Foreground = error ? DangerFg : muted;
                status.Visibility = string.IsNullOrEmpty(msg) ? Visibility.Collapsed : Visibility.Visible;
            }
            void OpenPassword()
            {
                _wifiOpenSsid = n.Ssid;
                // у остальных строк поле пароля сворачиваем — открыто только одно
                foreach (var other in WifiList.Children.OfType<StackPanel>())
                    if (other != root && other.Children.Count > 1) other.Children[1].Visibility = Visibility.Collapsed;
                pwdRow.Visibility = Visibility.Visible;
                pwdRow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
                Dispatcher.BeginInvoke(new Action(() => { pwd.Focus(); Keyboard.Focus(pwd); }), DispatcherPriority.Input);
            }
            async void Login()
            {
                if (pwd.Password.Length < 8) { Status("Пароль Wi-Fi — от 8 символов", true); Shake(pwdRow); return; }
                login.IsEnabled = pwd.IsEnabled = connect.IsEnabled = false;
                Status("Подключаюсь…", false);
                var err = await SystemControls.ConnectWifiWithPasswordAsync(n, pwd.Password);
                login.IsEnabled = pwd.IsEnabled = connect.IsEnabled = true;
                if (err == null) { pwd.Clear(); Connected(n); return; }
                Status(err, true);
                Shake(pwdRow);
                pwd.SelectAll(); pwd.Focus();
            }

            connect.Click += async (_, __) =>
            {
                if (!n.PasswordSupported) { SystemControls.OpenSettings("ms-settings:network-wifi"); Status("Корпоративная сеть — вход через Параметры Windows", false); return; }
                connect.IsEnabled = false;
                Status("Подключаюсь…", false);
                // знакомая Windows сеть подключается без пароля
                bool ok = await SystemControls.ConnectWifiAsync(n.Ssid);
                if (!ok && !n.Secured) ok = await SystemControls.ConnectWifiWithPasswordAsync(n, null) == null;
                connect.IsEnabled = true;
                if (ok) { Connected(n); return; }
                if (n.Secured) { Status(null, false); OpenPassword(); }
                else Status("Не удалось подключиться", true);
            };
            login.Click += (_, __) => Login();
            pwd.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Login(); e.Handled = true; } else if (e.Key == Key.Escape) { pwdRow.Visibility = Visibility.Collapsed; e.Handled = true; } };
            if (_wifiOpenSsid == n.Ssid) OpenPassword();
            return root;
        }

        private void Connected(SystemControls.WifiNet n)
        {
            _wifiOpenSsid = null;
            ToastBus.Show(new Toast
            {
                Kind = ToastKind.Success, Title = n.Ssid,
                Subtitle = "Подключено · " + (n.Signal >= 70 ? "сильный сигнал" : n.Signal >= 40 ? "средний сигнал" : "слабый сигнал"),
                Icon = Icons.Wifi, ReplaceKey = "wifi", Important = true, Duration = TimeSpan.FromSeconds(3)
            });
            Refresh();
            ScanWifi();
        }

        /// <summary>Кнопка-пилюля: светлая (главное действие) или тёмная.</summary>
        private Button PillButton(string text, bool primary)
        {
            var b = new Button { Style = (Style)FindResource("Bare"), VerticalAlignment = VerticalAlignment.Center };
            b.Content = new Border
            {
                Height = 30, CornerRadius = new CornerRadius(15), Padding = new Thickness(13, 0, 13, 0),
                Background = primary ? On : new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x33)),
                Child = new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = primary ? Brushes.Black : (Brush)FindResource("Fg") }
            };
            return b;
        }

        /// <summary>Значок Wi-Fi: дуги, которых не хватает до силы сигнала, — тусклые.</summary>
        private static FrameworkElement SignalGlyph(int signal, Brush fg)
        {
            int bars = signal >= 70 ? 3 : signal >= 40 ? 2 : signal > 0 ? 1 : 0;
            var grid = new Grid { Width = 18, Height = 18 };
            string[] arcs = { "M9.5 16a3 3 0 0 1 5 0", "M6.5 12.8a7.5 7.5 0 0 1 11 0", "M3.5 9.5a12 12 0 0 1 17 0" };
            for (int i = 0; i < arcs.Length; i++)
                grid.Children.Add(new Helpers.Icon { Data = arcs[i], Width = 18, Height = 18, Foreground = fg, StrokeWidth = 2, Opacity = i < bars ? 1 : 0.28 });
            grid.Children.Add(new Helpers.Icon { Data = "M12 19.5h.01", Width = 18, Height = 18, Foreground = fg, StrokeWidth = 2.6 });
            return grid;
        }

        private static void Shake(UIElement el)
        {
            var tt = new TranslateTransform();
            el.RenderTransform = tt;
            var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(360) };
            foreach (var (t, x) in new[] { (0, 0.0), (60, -7.0), (120, 6.0), (180, -4.0), (240, 3.0), (300, -1.0), (360, 0.0) })
                a.KeyFrames.Add(new EasingDoubleKeyFrame(x, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t))));
            tt.BeginAnimation(TranslateTransform.XProperty, a);
        }

        private async void OnBluetooth(object s, RoutedEventArgs e)
        {
            BtStatus.Text = "…";
            var r = await SystemControls.ToggleRadio(Radios.RadioKind.Bluetooth);
            if (r == null) SystemControls.OpenSettings("ms-settings:bluetooth");
            Refresh();
        }

        private void OnTheme(object s, RoutedEventArgs e)
        {
            SystemControls.SetDarkTheme(!SystemControls.IsDarkTheme);
            Refresh();
        }

        private void OnTaskMgr(object s, RoutedEventArgs e) => SystemControls.TaskManager();

        // ---------- Звук ----------

        private void SyncVolume()
        {
            _settingVolume = true;
            var v = AppHost.Audio.Volume * 100;
            Volume.Value = v;
            VolText.Text = ((int)Math.Round(v)).ToString();
            VolIcon.Kind = AppHost.Audio.Muted || v < 0.5 ? "Mute" : "Volume";
            _settingVolume = false;
        }

        private void OnVolume(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (VolText == null) return;
            VolText.Text = ((int)Math.Round(e.NewValue)).ToString();
            VolIcon.Kind = AppHost.Audio.Muted || e.NewValue < 0.5 ? "Mute" : "Volume"; // на нуле — перечёркнутый динамик
            if (_settingVolume) return;
            AppHost.Audio.Volume = (float)(e.NewValue / 100.0);
        }

        private void OnMute(object s, RoutedEventArgs e)
        {
            AppHost.Audio.Muted = !AppHost.Audio.Muted;
            VolIcon.Kind = AppHost.Audio.Muted ? "Mute" : "Volume";
        }

        private void OnMic(object s, RoutedEventArgs e)
        {
            AppHost.Audio.MicMuted = MicBtn.IsChecked != true;
            MicBtn.ToolTip = AppHost.Audio.MicMuted ? "Микрофон выключен — нажми, чтобы включить" : "Микрофон включён — нажми, чтобы выключить";
            ToastBus.Show(new Toast { Title = AppHost.Audio.MicMuted ? "Микрофон выключен" : "Микрофон включён", Icon = Icons.Mic, On = !AppHost.Audio.MicMuted, ReplaceKey = "mic", Important = true });
        }

        private void FillDevices()
        {
            _fillingDevices = true;
            var list = AppHost.Audio.OutputDevices();
            Devices.ItemsSource = list;
            Devices.SelectedItem = list.FirstOrDefault(d => d.Id == AppHost.Audio.DeviceId);
            _fillingDevices = false;
        }

        private void OnDevicesOpen(object s, EventArgs e) { AppHost.Window.SuppressAutoCollapse = true; FillDevices(); }
        private void OnDevicesClosed(object s, EventArgs e) => AppHost.Window.SuppressAutoCollapse = false;

        private void OnDevice(object s, SelectionChangedEventArgs e)
        {
            if (_fillingDevices || Devices.SelectedItem is not AudioDevice d) return;
            if (d.Id != AppHost.Audio.DeviceId) AppHost.Audio.SetDefault(d.Id);
        }

        // ---------- Питание: подтверждение вторым нажатием ----------

        private void OnPower(object sender, RoutedEventArgs e)
        {
            var key = (string)((Button)sender).Tag;
            if (_armed != key)
            {
                Arm(key);
                return;
            }
            Disarm();
            switch (key)
            {
                case "lock": AppHost.Window.Collapse(); SystemControls.Lock(); break;
                case "sleep": AppHost.Window.Collapse(); SystemControls.Sleep(); break;
                case "restart": SystemControls.Restart(); break;
                case "shutdown": SystemControls.Shutdown(); break;
            }
        }

        private (Button btn, TextBlock text, Border bar, string label) Parts(string key) => key switch
        {
            "lock" => (LockBtn, LockText, LockBar, "Блокировка"),
            "sleep" => (SleepBtn, SleepText, SleepBar, "Сон"),
            "restart" => (RestartBtn, RestartText, RestartBar, "Перезагрузка"),
            _ => (ShutdownBtn, ShutdownText, ShutdownBar, "Выключение")
        };

        private void Arm(string key)
        {
            Disarm();
            _armed = key;
            var (btn, text, bar, _) = Parts(key);
            btn.Background = DangerBg;
            btn.BorderBrush = DangerFg;
            btn.Foreground = DangerFg;
            text.Text = "Нажми ещё раз";
            bar.Visibility = Visibility.Visible;
            var scale = (ScaleTransform)bar.RenderTransform;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0, ConfirmTime));
            _disarm.Stop(); _disarm.Start();
        }

        private void Disarm()
        {
            _disarm.Stop();
            foreach (var k in new[] { "lock", "sleep", "restart", "shutdown" })
            {
                var (btn, text, bar, label) = Parts(k);
                btn.ClearValue(BackgroundProperty);
                btn.ClearValue(BorderBrushProperty);
                btn.ClearValue(ForegroundProperty);
                text.Text = label;
                bar.Visibility = Visibility.Hidden;
                ((ScaleTransform)bar.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, null);
            }
            _armed = null;
        }
    }
}
