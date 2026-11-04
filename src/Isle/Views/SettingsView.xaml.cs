using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Isle.Services;

namespace Isle.Views
{
    public partial class SettingsView : UserControl, IIslandView
    {
        private bool _loading;
        private AppSettings S => AppSettings.Current;

        public SettingsView()
        {
            _loading = true; // слайдеры шлют ValueChanged уже при создании — не сохраняем их
            InitializeComponent();
            Load();
        }

        public void OnShown() => Load();

        private void Load()
        {
            _loading = true;
            Oled.IsChecked = S.OledMode;
            OledSec.Value = Math.Clamp(S.OledRevealSeconds, 3, 60);
            OledSecText.Text = (int)OledSec.Value + " с";
            ScaleSlider.Value = Math.Round(S.Scale * 100);
            ScaleText.Text = (int)ScaleSlider.Value + "%";
            Hover.IsChecked = S.ExpandOnHover;
            Fullscreen.IsChecked = S.HideInFullscreen;
            Clock24.IsChecked = S.Use24h;
            Autostart.IsChecked = SystemControls.Autostart;
            Media.IsChecked = S.MediaEnabled;
            Lyrics.IsChecked = S.LyricsEnabled;
            Volume.IsChecked = S.VolumeToasts;
            Layout.IsChecked = S.LayoutToasts;
            Caps.IsChecked = S.CapsToasts;
            Battery.IsChecked = S.BatteryToasts;
            Notif.IsChecked = S.NotificationToasts;
            NotifStatus.Text = "Статус: " + (AppHost.Notifications?.Status ?? "—");
            ClipHist.IsChecked = S.ClipboardHistory;
            ClipImg.IsChecked = S.ClipboardImages;
            Vpn.IsChecked = S.VpnStatus;
            Captures.IsChecked = S.ShowInCaptures;
            CityBox.Text = S.City;
            Provider.SelectedItem = Provider.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == S.AiProvider) ?? Provider.Items[0];
            Key.Password = S.AiKey;
            Model.Text = S.AiModel;
            Endpoint.Text = S.AiEndpoint;
            EndpointPanel.Visibility = S.AiProvider == "openai" ? Visibility.Visible : Visibility.Collapsed;
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            About.Text = "Isle " + v.Major + "." + v.Minor + "." + v.Build + " · данные хранятся локально в " + Storage.Dir;
            _loading = false;
        }

        private void OnToggle(object s, RoutedEventArgs e)
        {
            if (_loading) return;
            S.ExpandOnHover = Hover.IsChecked == true;
            S.HideInFullscreen = Fullscreen.IsChecked == true;
            S.Use24h = Clock24.IsChecked == true;
            S.MediaEnabled = Media.IsChecked == true;
            S.LyricsEnabled = Lyrics.IsChecked == true;
            S.VolumeToasts = Volume.IsChecked == true;
            S.LayoutToasts = Layout.IsChecked == true;
            S.CapsToasts = Caps.IsChecked == true;
            S.BatteryToasts = Battery.IsChecked == true;
            S.NotificationToasts = Notif.IsChecked == true;
            S.ClipboardHistory = ClipHist.IsChecked == true;
            S.ClipboardImages = ClipImg.IsChecked == true;
            S.VpnStatus = Vpn.IsChecked == true;
            S.Save();
        }

        private void OnCaptures(object s, RoutedEventArgs e)
        {
            if (_loading) return;
            S.ShowInCaptures = Captures.IsChecked == true;
            S.Save();
            AppHost.Window.ApplyCaptureVisibility();
        }

        private void OnOled(object s, RoutedEventArgs e)
        {
            if (_loading) return;
            S.OledMode = Oled.IsChecked == true;
            S.Save();
            AppHost.Window.ApplyOledMode();
        }

        private void OnOledSec(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (OledSecText == null) return;
            OledSecText.Text = (int)e.NewValue + " с";
            if (_loading) return;
            S.OledRevealSeconds = (int)e.NewValue;
            S.Save();
        }

        private void OnScale(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ScaleText == null) return;
            ScaleText.Text = (int)e.NewValue + "%";
            if (_loading) return;
            S.Scale = e.NewValue / 100.0;
            S.Save();
            AppHost.Window.ApplyScale();
        }

        private void OnAutostart(object s, RoutedEventArgs e)
        {
            if (_loading) return;
            try { SystemControls.Autostart = Autostart.IsChecked == true; } catch { }
        }

        private async void OnCity(object s, RoutedEventArgs e)
        {
            CityStatus.Text = "Ищу…";
            try
            {
                var r = await WeatherService.GeocodeAsync(CityBox.Text.Trim());
                if (r == null) { CityStatus.Text = "Город не найден"; return; }
                S.City = r.Value.name; S.Latitude = r.Value.lat; S.Longitude = r.Value.lon;
                S.Save();
                CityBox.Text = S.City;
                CityStatus.Text = "Готово: " + S.City;
                await AppHost.Window.RefreshWeather();
            }
            catch (Exception ex) { CityStatus.Text = "Нет связи: " + ex.Message; }
        }

        private void OnProvider(object s, SelectionChangedEventArgs e)
        {
            if (Provider.SelectedItem is not ComboBoxItem item) return;
            var p = (string)item.Tag;
            EndpointPanel.Visibility = p == "openai" ? Visibility.Visible : Visibility.Collapsed;
            if (_loading) return;
            if (p == "anthropic" && (string.IsNullOrWhiteSpace(Model.Text) || !Model.Text.StartsWith("claude"))) Model.Text = "claude-haiku-5-5";
            if (p == "openai" && Model.Text.StartsWith("claude")) Model.Text = "gpt-4o-mini";
        }

        private void OnSaveAi(object s, RoutedEventArgs e)
        {
            S.AiProvider = (Provider.SelectedItem as ComboBoxItem)?.Tag as string ?? "anthropic";
            S.AiKey = Key.Password.Trim();
            S.AiModel = Model.Text.Trim();
            S.AiEndpoint = Endpoint.Text.Trim();
            S.Save();
            ToastBus.Show(new Toast { Kind = ToastKind.Success, Title = "Настройки ИИ сохранены", Icon = Icons.Check, Important = true });
        }

        private void OnDataDir(object s, RoutedEventArgs e)
        {
            System.IO.Directory.CreateDirectory(Storage.Dir);
            SystemControls.Shell(Storage.Dir);
        }

        private void OnExit(object s, RoutedEventArgs e) => Application.Current.Shutdown();
    }
}
