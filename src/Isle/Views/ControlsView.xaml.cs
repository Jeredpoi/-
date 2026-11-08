using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
        private static readonly Brush Off = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x20));
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

        public async void OnShown()
        {
            SyncVolume();
            FillDevices();
            MicBtn.IsChecked = !AppHost.Audio.MicMuted; // подсвечено = микрофон работает, как у остальных кнопок
            MicBtn.ToolTip = AppHost.Audio.MicMuted ? "Микрофон выключен — нажми, чтобы включить" : "Микрофон включён — нажми, чтобы выключить";

            var net = SystemControls.Network();
            NetName.Text = net.name;
            NetStatus.Text = net.status;
            SetTile(NetDot, null, net.up);

            bool dark = SystemControls.IsDarkTheme;
            ThemeStatus.Text = dark ? "Включена" : "Выключена";
            SetTile(ThemeDot, ThemeIcon, dark);

            var bt = await SystemControls.GetRadio(Radios.RadioKind.Bluetooth);
            BtStatus.Text = bt == null ? "Нет адаптера" : bt.Value ? "Включён" : "Выключен";
            SetTile(BtDot, BtIcon, bt == true);
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

        /// <summary>Включённое — белый кружок с чёрной иконкой, выключенное — тёмный.</summary>
        private static void SetTile(Border dot, Helpers.Icon icon, bool on)
        {
            dot.Background = on ? On : Off;
            var ic = icon ?? (dot.Child as Helpers.Icon);
            if (ic != null) ic.Foreground = on ? Brushes.Black : new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xF1));
        }

        private void OnNetwork(object s, RoutedEventArgs e) => SystemControls.OpenSettings("ms-settings:network");

        /// <summary>Сети Wi-Fi рядом: текущая отмечена, клик — подключиться (к знакомой сети) или открыть настройки Wi-Fi.</summary>
        private async void OnWifiList(object s, RoutedEventArgs e)
        {
            var menu = new ContextMenu { PlacementTarget = WifiListBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, MaxHeight = 360 };
            menu.Items.Add(new MenuItem { Header = "Ищу сети…", IsEnabled = false });
            menu.IsOpen = true;
            var (nets, error) = await SystemControls.WifiNetworksAsync();
            menu.Items.Clear();
            if (error != null) menu.Items.Add(new MenuItem { Header = error, IsEnabled = false });
            else if (nets.Count == 0) menu.Items.Add(new MenuItem { Header = "Сетей рядом не видно", IsEnabled = false });
            var current = SystemControls.Network().status;
            foreach (var n in nets)
            {
                var item = new MenuItem
                {
                    Header = n.ssid + "   ·   " + n.signal + "%" + (n.secured ? "  🔒" : ""),
                    IsChecked = n.ssid == current
                };
                var ssid = n.ssid;
                item.Click += async (_, __) =>
                {
                    NetStatus.Text = "Подключаюсь…";
                    bool ok = await SystemControls.ConnectWifiAsync(ssid);
                    if (!ok) SystemControls.OpenSettings("ms-settings:network-wifi"); // новая сеть с паролем — пароль вводится в Windows
                    await System.Threading.Tasks.Task.Delay(1500);
                    OnShown();
                };
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            var settings = new MenuItem { Header = "Параметры Wi-Fi…" };
            settings.Click += (_, __) => SystemControls.OpenSettings("ms-settings:network-wifi");
            menu.Items.Add(settings);
        }

        private async void OnBluetooth(object s, RoutedEventArgs e)
        {
            BtStatus.Text = "…";
            var r = await SystemControls.ToggleRadio(Radios.RadioKind.Bluetooth);
            if (r == null) SystemControls.OpenSettings("ms-settings:bluetooth");
            OnShown();
        }

        private void OnTheme(object s, RoutedEventArgs e)
        {
            SystemControls.SetDarkTheme(!SystemControls.IsDarkTheme);
            OnShown();
        }

        private void OnTaskMgr(object s, RoutedEventArgs e) => SystemControls.TaskManager();

        // ---------- Звук ----------

        private void SyncVolume()
        {
            _settingVolume = true;
            var v = AppHost.Audio.Volume * 100;
            Volume.Value = v;
            VolText.Text = ((int)Math.Round(v)).ToString();
            VolIcon.Kind = AppHost.Audio.Muted ? "Mute" : "Volume";
            _settingVolume = false;
        }

        private void OnVolume(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (VolText == null) return;
            VolText.Text = ((int)Math.Round(e.NewValue)).ToString();
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
