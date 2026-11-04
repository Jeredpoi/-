using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Isle.Services;

namespace Isle.Views
{
    /// <summary>Отдельное окно настроек с разделами. Всё сохраняется сразу.</summary>
    public partial class SettingsWindow : Window
    {
        private bool _loading = true;
        private AppSettings S => AppSettings.Current;
        private static readonly string[] AccentColors = { "#ECECF1", "#FFB547", "#D97757", "#F87171", "#4ADE80", "#7DB5FF", "#C084FC" };

        public SettingsWindow()
        {
            InitializeComponent();
            SourceInitialized += (_, __) => DarkTitleBar();
            BuildPresets();
            BuildAccents();
            BuildKeys();
            Load();
            _loading = false;
            UpdateService.Changed += RenderUpdate;
            Closed += (_, __) => UpdateService.Changed -= RenderUpdate;
            RenderUpdate();
        }

        // ---------- Обновления ----------

        private void RenderUpdate()
        {
            Dispatcher.InvokeAsync(() =>
            {
                var cur = UpdateService.Current.ToString(3);
                var a = UpdateService.Available;
                UpdateDot.Visibility = a != null ? Visibility.Visible : Visibility.Collapsed;
                if (a != null)
                {
                    UpdTitle.Text = "Доступна версия " + a.Version.ToString(3);
                    UpdStatus.Text = "Сейчас установлена " + cur + " · вышла " + a.Published.ToString("dd.MM.yyyy HH:mm") + (a.Size > 0 ? " · " + (a.Size / 1048576.0).ToString("0.#") + " МБ" : "");
                    UpdInstallBtn.Visibility = Visibility.Visible;
                    UpdNotes.Text = string.IsNullOrWhiteSpace(a.Notes) ? "" : a.Notes.Trim();
                    UpdNotes.Visibility = UpdNotes.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                }
                else
                {
                    UpdTitle.Text = "Isle " + cur;
                    UpdStatus.Text = UpdateService.LastCheck == null ? "Нажми «Проверить»" : "Установлена последняя версия · проверено в " + UpdateService.LastCheck.Value.ToString("HH:mm");
                    UpdInstallBtn.Visibility = Visibility.Collapsed;
                    UpdNotes.Visibility = Visibility.Collapsed;
                }
            });
        }

        private async void OnUpdateCheck(object s, RoutedEventArgs e)
        {
            UpdCheckBtn.IsEnabled = false;
            UpdStatus.Text = "Проверяю…";
            try { await UpdateService.CheckAsync(false); RenderUpdate(); }
            catch (Exception ex) { UpdStatus.Text = "Не удалось проверить: " + ex.Message; }
            finally { UpdCheckBtn.IsEnabled = true; }
        }

        private async void OnUpdateInstall(object s, RoutedEventArgs e)
        {
            var a = UpdateService.Available;
            if (a == null) return;
            UpdInstallBtn.IsEnabled = UpdCheckBtn.IsEnabled = false;
            UpdProgress.Visibility = Visibility.Visible;
            var bar = UpdBar;
            var progress = new Progress<double>(p =>
            {
                bar.Width = Math.Max(0, (UpdProgress.ActualWidth) * p);
                UpdStatus.Text = p < 1 ? "Скачиваю… " + (int)(p * 100) + "%" : "Устанавливаю — Isle перезапустится сам";
            });
            try { await UpdateService.DownloadAndInstallAsync(a, progress); }
            catch (Exception ex)
            {
                UpdStatus.Text = "Не получилось: " + ex.Message;
                UpdInstallBtn.IsEnabled = UpdCheckBtn.IsEnabled = true;
            }
        }

        private void OnReleases(object s, RoutedEventArgs e) => SystemControls.Shell("https://github.com/Jeredpoi/-/releases");

        private void DarkTitleBar()
        {
            try
            {
                var h = new WindowInteropHelper(this).Handle;
                int on = 1;
                DwmSetWindowAttribute(h, 20, ref on, 4); // DWMWA_USE_IMMERSIVE_DARK_MODE (Win10 20H1+ / Win11)
                DwmSetWindowAttribute(h, 19, ref on, 4); // то же для старых сборок Windows 10
                int none = 1;
                DwmSetWindowAttribute(h, 38, ref none, 4); // без Mica/Acrylic — фон окна ровно наш, без осветления
                int caption = 0x000C0A0A;             // цвет заголовка #0A0A0C (формат 0x00BBGGRR)
                DwmSetWindowAttribute(h, 35, ref caption, 4);
                int text = 0x00F1ECEC;
                DwmSetWindowAttribute(h, 36, ref text, 4);
            }
            catch { }
        }

        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // ---------- Разделы ----------

        public void ShowSection(string key)
        {
            var rb = key switch
            {
                "look" => NavLook, "music" => NavMusic, "ai" => NavAi, "voice" => NavVoice, "record" => NavRecord, "weather" => NavWeather,
                "modules" => NavModules, "keys" => NavKeys, "updates" => NavUpdates, _ => NavGeneral
            };
            rb.IsChecked = true;
        }

        private void OnNav(object s, RoutedEventArgs e)
        {
            if (SecGeneral == null) return;
            var map = new (RadioButton nav, FrameworkElement sec)[]
            {
                (NavGeneral, SecGeneral), (NavLook, SecLook), (NavMusic, SecMusic), (NavAi, SecAi), (NavVoice, SecVoice), (NavRecord, SecRecord),
                (NavWeather, SecWeather), (NavModules, SecModules), (NavKeys, SecKeys), (NavUpdates, SecUpdates)
            };
            foreach (var (nav, sec) in map) sec.Visibility = nav.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            Scroll?.ScrollToTop();
        }

        // ---------- Загрузка ----------

        private void Load()
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = "Версия " + v.Major + "." + v.Minor + "." + v.Build;
            DataDirText.Text = Storage.Dir;

            Autostart.IsChecked = SystemControls.Autostart;
            Fullscreen.IsChecked = S.HideInFullscreen;
            Oled.IsChecked = S.OledMode;
            Captures.IsChecked = S.ShowInCaptures;
            Hover.IsChecked = S.ExpandOnHover;
            Clock24.IsChecked = S.Use24h;

            ChPixel.IsChecked = S.Character == "pixel";
            ChEyes.IsChecked = S.Character == "eyes";
            ChNone.IsChecked = S.Character == "none";
            ScaleSlider.Value = Math.Round(S.Scale * 100);
            ScaleText.Text = (int)ScaleSlider.Value + "%";
            OpacitySlider.Value = Math.Round(S.IslandOpacity * 100);
            OpacityText.Text = (int)OpacitySlider.Value + "%";
            UpdatePreview();

            SelectPreset(S.AiPreset, false);
            AiBase.Text = S.AiBaseUrl;
            AiModel.Text = S.AiModel;
            AiKeyState.Text = string.IsNullOrEmpty(S.AiKey) ? "Ключ не сохранён" : "•••••••• ключ сохранён";

            VpGroq.IsChecked = S.VoicePreset == "groq";
            VpOpenAi.IsChecked = S.VoicePreset == "openai";
            VpCustom.IsChecked = S.VoicePreset == "custom";
            VoiceBase.Text = S.VoiceBaseUrl;
            VoiceModel.Text = S.VoiceModel;
            VoiceKeyState.Text = string.IsNullOrEmpty(S.VoiceKey) ? "Используется ключ из раздела «ИИ», если сервис тот же" : "•••••••• свой ключ сохранён";
            VlAuto.IsChecked = string.IsNullOrEmpty(S.VoiceLanguage);
            VlRu.IsChecked = S.VoiceLanguage == "ru";
            VlEn.IsChecked = S.VoiceLanguage == "en";
            VoicePaste.IsChecked = S.VoiceAutoPaste;

            ObsPort.Text = S.ObsPort.ToString();

            CityBox.Text = S.City;

            Media.IsChecked = S.MediaEnabled;
            LyricsHome.IsChecked = S.LyricsOnHome;
            ShowClock.IsChecked = S.ShowClock;
            ClockSeconds.IsChecked = S.ClockSeconds;
            PillBars.IsChecked = S.MusicPill == "bars";
            PillTitle.IsChecked = S.MusicPill == "title";
            PillMini.IsChecked = S.MusicPill == "mini";
            PillLyrics.IsChecked = S.MusicPill == "lyrics";
            HpMirror.IsChecked = S.HomePlayer == "mirror";
            HpFill.IsChecked = S.HomePlayer == "fill";
            HpCompact.IsChecked = S.HomePlayer == "compact";
            Lyrics.IsChecked = S.LyricsEnabled;
            Volume.IsChecked = S.VolumeToasts;
            Layout.IsChecked = S.LayoutToasts;
            Caps.IsChecked = S.CapsToasts;
            Battery.IsChecked = S.BatteryToasts;
            Notif.IsChecked = S.NotificationToasts;
            NotifStatus.Text = "Статус: " + (AppHost.Notifications?.Status ?? "—");
            Vpn.IsChecked = S.VpnStatus;
            ClipHist.IsChecked = S.ClipboardHistory;
            ClipImg.IsChecked = S.ClipboardImages;
            AutoUpdate.IsChecked = S.AutoUpdateCheck;
            ObsStatus.IsChecked = S.ObsStatus;
        }

        private void OnToggle(object s, RoutedEventArgs e)
        {
            if (_loading) return;
            S.HideInFullscreen = Fullscreen.IsChecked == true;
            S.ExpandOnHover = Hover.IsChecked == true;
            S.Use24h = Clock24.IsChecked == true;
            S.VoiceAutoPaste = VoicePaste.IsChecked == true;
            S.VolumeToasts = Volume.IsChecked == true;
            S.LayoutToasts = Layout.IsChecked == true;
            S.CapsToasts = Caps.IsChecked == true;
            S.BatteryToasts = Battery.IsChecked == true;
            S.NotificationToasts = Notif.IsChecked == true;
            S.VpnStatus = Vpn.IsChecked == true;
            S.ClipboardHistory = ClipHist.IsChecked == true;
            S.ClipboardImages = ClipImg.IsChecked == true;
            S.AutoUpdateCheck = AutoUpdate.IsChecked == true;
            S.ObsStatus = ObsStatus.IsChecked == true;
            S.Save();
        }

        private void OnMusicToggle(object s, RoutedEventArgs e)
        {
            if (_loading) return;
            S.MediaEnabled = Media.IsChecked == true;
            S.LyricsEnabled = Lyrics.IsChecked == true;
            S.LyricsOnHome = LyricsHome.IsChecked == true;
            S.ShowClock = ShowClock.IsChecked == true;
            S.ClockSeconds = ClockSeconds.IsChecked == true;
            S.Save();
            if (S.LyricsEnabled) _ = AppHost.Lyrics.LoadAsync(AppHost.Media.Current);
            AppHost.Window.ApplyMusicSettings();
        }

        private void OnPill(object s, RoutedEventArgs e)
        {
            S.MusicPill = (string)((FrameworkElement)s).Tag;
            S.Save();
            AppHost.Window.ApplyMusicSettings();
        }

        private void OnHomePlayer(object s, RoutedEventArgs e)
        {
            S.HomePlayer = (string)((FrameworkElement)s).Tag;
            S.Save();
            AppHost.Window.ApplyMusicSettings();
        }

        private void OnAutostart(object s, RoutedEventArgs e)
        {
            try { SystemControls.Autostart = Autostart.IsChecked == true; } catch { }
        }

        private void OnOled(object s, RoutedEventArgs e)
        {
            S.OledMode = Oled.IsChecked == true;
            S.Save();
            AppHost.Window.ApplyOledMode();
        }

        private void OnCaptures(object s, RoutedEventArgs e)
        {
            S.ShowInCaptures = Captures.IsChecked == true;
            S.Save();
            AppHost.Window.ApplyCaptureVisibility();
        }

        private void OnDataDir(object s, RoutedEventArgs e)
        {
            System.IO.Directory.CreateDirectory(Storage.Dir);
            SystemControls.Shell(Storage.Dir);
        }

        private void OnExit(object s, RoutedEventArgs e) => Application.Current.Shutdown();

        // ---------- Внешний вид ----------

        private void BuildAccents()
        {
            foreach (var hex in AccentColors)
            {
                var c = (Color)ColorConverter.ConvertFromString(hex);
                var rb = new RadioButton
                {
                    Style = (Style)FindResource("SSegment"), GroupName = "accent", Tag = hex, Padding = new Thickness(0),
                    Content = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = new SolidColorBrush(c) },
                    IsChecked = string.Equals(hex, S.AccentHex, StringComparison.OrdinalIgnoreCase), ToolTip = hex
                };
                rb.Click += (_, __) =>
                {
                    S.AccentHex = hex;
                    S.Save();
                    AppHost.Window.ApplyAppearance();
                };
                Accents.Children.Add(rb);
            }
        }

        private void OnCharacter(object s, RoutedEventArgs e)
        {
            S.Character = (string)((FrameworkElement)s).Tag;
            S.Save();
            UpdatePreview();
            AppHost.Window.ApplyAppearance();
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

        private void OnOpacity(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (OpacityText == null) return;
            OpacityText.Text = (int)e.NewValue + "%";
            if (_loading) return;
            S.IslandOpacity = e.NewValue / 100.0;
            S.Save();
            UpdatePreview();
            AppHost.Window.ApplyAppearance();
        }

        private void UpdatePreview()
        {
            if (Preview == null) return;
            PreviewBuddy.Visibility = S.Character == "pixel" ? Visibility.Visible : Visibility.Collapsed;
            PreviewEyes.Visibility = S.Character == "eyes" ? Visibility.Visible : Visibility.Collapsed;
            Preview.Background = new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Clamp(S.IslandOpacity, 0.5, 1)), 0, 0, 0));
            PreviewTime.Text = DateTime.Now.ToString("HH:mm");
        }

        // ---------- ИИ ----------

        private void BuildPresets()
        {
            foreach (var p in AiService.Presets)
            {
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(new TextBlock { Text = p.Title });
                if (p.Free)
                    content.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x2A, 0x22)), CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(8, 0, 0, 0),
                        Child = new TextBlock { Text = "бесплатно", FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0xD4, 0x9E)) }
                    });
                var rb = new RadioButton { Style = (Style)FindResource("SSegment"), GroupName = "ai", Tag = p.Id, Content = content };
                rb.Click += (_, __) => SelectPreset(p.Id, true);
                AiPresets.Children.Add(rb);
            }
        }

        private void SelectPreset(string id, bool apply)
        {
            var p = AiService.Preset(id) ?? AiService.Presets[0];
            foreach (RadioButton rb in AiPresets.Children) rb.IsChecked = (string)rb.Tag == p.Id;
            AiNote.Text = p.Note;
            AiKeyLink.Tag = p.KeyUrl;
            if (!apply) return;
            S.AiPreset = p.Id;
            S.AiBaseUrl = p.BaseUrl;
            S.AiModel = p.Model;
            S.Save();
            AiBase.Text = p.BaseUrl;
            AiModel.Text = p.Model;
            AiTestResult.Text = "";
        }

        private void OnAiKeyLink(object s, RoutedEventArgs e)
        {
            if (AiKeyLink.Tag is string url) SystemControls.Shell(url);
        }

        private void OnAiSaveKey(object s, RoutedEventArgs e)
        {
            if (AiKey.Password.Trim().Length == 0) return;
            S.AiKey = AiKey.Password.Trim();
            S.Save();
            AiKey.Password = "";
            AiKeyState.Text = "•••••••• ключ сохранён";
        }

        private void OnAiDeleteKey(object s, RoutedEventArgs e)
        {
            S.AiKey = "";
            S.Save();
            AiKeyState.Text = "Ключ удалён";
        }

        private void OnAiFieldsChanged(object s, RoutedEventArgs e)
        {
            if (_loading) return;
            S.AiBaseUrl = AiBase.Text.Trim();
            S.AiModel = AiModel.Text.Trim();
            S.Save();
        }

        private string CurrentAiKey() => AiKey.Password.Trim().Length > 0 ? AiKey.Password.Trim() : S.AiKey;

        private async void OnModels(object s, RoutedEventArgs e)
        {
            var menu = new ContextMenu { PlacementTarget = ModelsBtn, Placement = PlacementMode.Bottom, MaxHeight = 420 };
            menu.Items.Add(new MenuItem { Header = "Загружаю список…", IsEnabled = false });
            menu.IsOpen = true;
            try
            {
                var models = await AiService.ModelsAsync(S.AiPreset, AiBase.Text.Trim(), CurrentAiKey());
                menu.Items.Clear();
                if (models.Count == 0) menu.Items.Add(new MenuItem { Header = "Сервис не вернул моделей", IsEnabled = false });
                foreach (var m in models)
                {
                    var item = new MenuItem { Header = m, IsChecked = m == AiModel.Text };
                    item.Click += (_, __) => { AiModel.Text = m; OnAiFieldsChanged(null, null); };
                    menu.Items.Add(item);
                }
            }
            catch (Exception ex)
            {
                menu.Items.Clear();
                menu.Items.Add(new MenuItem { Header = "Ошибка: " + ex.Message, IsEnabled = false });
            }
        }

        private async void OnAiTest(object s, RoutedEventArgs e)
        {
            OnAiFieldsChanged(null, null);
            AiTestResult.Text = "Проверяю…";
            AiTestResult.Text = await AiService.TestAsync(S.AiPreset, AiBase.Text.Trim(), CurrentAiKey(), AiModel.Text.Trim());
        }

        // ---------- Голос ----------

        private void OnVoicePreset(object s, RoutedEventArgs e)
        {
            var id = (string)((FrameworkElement)s).Tag;
            S.VoicePreset = id;
            if (id == "groq") { S.VoiceBaseUrl = "https://api.groq.com/openai/v1"; S.VoiceModel = "whisper-large-v3-turbo"; }
            if (id == "openai") { S.VoiceBaseUrl = "https://api.openai.com/v1"; S.VoiceModel = "whisper-1"; }
            S.Save();
            VoiceBase.Text = S.VoiceBaseUrl;
            VoiceModel.Text = S.VoiceModel;
        }

        private void OnVoiceFields(object s, RoutedEventArgs e)
        {
            if (_loading) return;
            S.VoiceBaseUrl = VoiceBase.Text.Trim();
            S.VoiceModel = VoiceModel.Text.Trim();
            S.Save();
        }

        private void OnVoiceSaveKey(object s, RoutedEventArgs e)
        {
            S.VoiceKey = VoiceKey.Password.Trim();
            S.Save();
            VoiceKey.Password = "";
            VoiceKeyState.Text = string.IsNullOrEmpty(S.VoiceKey) ? "Используется ключ из раздела «ИИ», если сервис тот же" : "•••••••• свой ключ сохранён";
        }

        private void OnVoiceLang(object s, RoutedEventArgs e)
        {
            S.VoiceLanguage = (string)((FrameworkElement)s).Tag;
            S.Save();
        }

        private async void OnVoiceTest(object s, RoutedEventArgs e)
        {
            OnVoiceFields(null, null);
            VoiceTestResult.Text = "Проверяю…";
            VoiceTestResult.Text = await VoiceService.TestAsync();
        }

        // ---------- OBS ----------

        private void OnObsFields(object s, RoutedEventArgs e)
        {
            if (_loading) return;
            if (int.TryParse(ObsPort.Text.Trim(), out var port) && port > 0 && port < 65536) { S.ObsPort = port; S.Save(); }
        }

        private void OnObsSave(object s, RoutedEventArgs e)
        {
            S.ObsPassword = ObsPass.Password;
            S.Save();
            ObsPass.Password = "";
            ObsResult.Text = "Пароль сохранён";
        }

        private async void OnObsTest(object s, RoutedEventArgs e)
        {
            OnObsFields(null, null);
            if (ObsPass.Password.Length > 0) OnObsSave(null, null);
            if (!ObsService.IsObsRunning()) { ObsResult.Text = "OBS не запущен"; return; }
            ObsResult.Text = "Подключаюсь…";
            ObsResult.Text = await ObsService.TestAsync();
        }

        // ---------- Погода ----------

        private void OnCityKey(object s, KeyEventArgs e) { if (e.Key == Key.Enter) OnCity(null, null); }

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

        // ---------- Клавиши ----------

        private void BuildKeys()
        {
            var keys = new[]
            {
                ("Ctrl + Alt + Space", "Открыть / закрыть остров"),
                ("Ctrl + Alt + K", "Командная строка: калькулятор, запуск программ, таймер, напоминание"),
                ("Ctrl + Alt + V", "История буфера обмена"),
                ("Ctrl + Alt + S", "Снимок области экрана"),
                ("Ctrl + Alt + D", "Голос в текст: начать / закончить диктовку"),
                ("Esc", "Свернуть остров"),
                ("Клик мимо острова", "Свернуть (и спрятать в OLED-режиме)"),
            };
            foreach (var (k, d) in keys)
            {
                var g = new Grid { Margin = new Thickness(0, 8, 0, 8) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                var key = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x20)), CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 5, 10, 5), HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new TextBlock { Text = k, FontWeight = FontWeights.SemiBold, FontSize = 12 }
                };
                var desc = new TextBlock { Text = d, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(desc, 1);
                g.Children.Add(key);
                g.Children.Add(desc);
                KeysList.Children.Add(g);
            }
        }
    }
}
