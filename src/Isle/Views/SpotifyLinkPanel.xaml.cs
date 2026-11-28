using System;
using System.Windows;
using System.Windows.Controls;
using Isle.Services;

namespace Isle.Views
{
    /// <summary>
    /// Блок «Spotify: текст песен и управление» для страницы «Музыка» в настройках: статус, Client ID, Привязать/Отвязать.
    /// Самодостаточный — сам читает и сохраняет AppSettings и следит за SpotifyAuth.Changed.
    /// </summary>
    public partial class SpotifyLinkPanel : UserControl
    {
        public SpotifyLinkPanel()
        {
            InitializeComponent();
            RedirectRun.Text = SpotifyAuth.RedirectUri;
            ClientIdBox.Text = AppSettings.Current.SpotifyClientId ?? "";
            Loaded += (_, __) => { SpotifyAuth.Changed += OnAuthChanged; UpdateState(); };
            Unloaded += (_, __) => SpotifyAuth.Changed -= OnAuthChanged;
        }

        private void OnAuthChanged() => Dispatcher.BeginInvoke(new Action(UpdateState));

        private void UpdateState()
        {
            var s = AppSettings.Current;
            bool linked = SpotifyAuth.IsLinked;
            bool linking = SpotifyAuth.IsLinking;
            StatusText.Text = linking ? "Жду вход в браузере…"
                : linked ? "Привязан: " + (string.IsNullOrWhiteSpace(s.SpotifyUserName) ? "аккаунт Spotify" : s.SpotifyUserName)
                : "Не привязан";
            Dot.Fill = (System.Windows.Media.Brush)FindResource(linked ? "Accent" : "Muted");
            Dot.Opacity = linked ? 1 : 0.6;
            LinkBtn.Content = linking ? "Отменить" : linked ? "Привязать заново" : "Привязать Spotify";
            UnlinkBtn.Visibility = linked ? Visibility.Visible : Visibility.Collapsed;
            // токен выдан до появления управления (нет user-modify-playback-state) — просим перепривязать
            ScopeHint.Visibility = linked && !linking && !SpotifyAuth.HasControlScope ? Visibility.Visible : Visibility.Collapsed;
            ClientIdBox.IsEnabled = !linking;
        }

        private void ShowError(string text)
        {
            ErrorText.Text = text ?? "";
            ErrorText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnClientIdLostFocus(object sender, RoutedEventArgs e)
        {
            var id = (ClientIdBox.Text ?? "").Trim();
            var s = AppSettings.Current;
            if (id == (s.SpotifyClientId ?? "")) return;
            // другое приложение Spotify — старые токены к нему не подходят
            if (SpotifyAuth.IsLinked) SpotifyAuth.Unlink();
            s.SpotifyClientId = id;
            s.Save();
            UpdateState();
        }

        private async void OnLink(object sender, RoutedEventArgs e)
        {
            if (SpotifyAuth.IsLinking) { SpotifyAuth.CancelLink(); return; }
            ShowError(null);
            var id = (ClientIdBox.Text ?? "").Trim();
            var task = SpotifyAuth.LinkAsync(id);
            UpdateState();
            var err = await task;
            UpdateState();
            ShowError(err);
        }

        private void OnUnlink(object sender, RoutedEventArgs e)
        {
            ShowError(null);
            SpotifyAuth.Unlink();
            UpdateState();
        }

        private void OnCopyRedirect(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(SpotifyAuth.RedirectUri); } catch { }
        }

        private void OnCredit(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            e.Handled = true;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
        }

        private void OnHowToggle(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            bool open = HowBody.Visibility != Visibility.Visible;
            HowBody.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            HowRot.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,
                new System.Windows.Media.Animation.DoubleAnimation(open ? 90 : 0, TimeSpan.FromMilliseconds(160)));
        }

        private void OnCopyDashboard(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText("https://developer.spotify.com/dashboard"); } catch { }
        }
    }
}
