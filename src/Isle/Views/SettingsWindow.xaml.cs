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
            // после сворачивания/перекрытия перерисовываем окно целиком, без «застрявших» кусков
            Activated += (_, __) => InvalidateVisual();
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
                // Окно рисуем программно и с непрозрачным тёмным фоном: на части видеокарт/HDR-мониторов
                // аппаратная отрисовка WPF давала «молочную» белёсую пелену поверх всего окна,
                // а чистыми оставались только перерисованные куски.
                if (HwndSource.FromHwnd(h)?.CompositionTarget is HwndTarget t)
                {
                    t.RenderMode = RenderMode.SoftwareOnly;
                    t.BackgroundColor = Color.FromRgb(0x0A, 0x0A, 0x0C);
                }
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
            foreach (var rb in Outfits.Children.OfType<RadioButton>()) rb.IsChecked = (string)rb.Tag == (S.BuddyOutfit ?? "auto");
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
            VpPolza.IsChecked = S.VoicePreset == "polza";
            VpCustom.IsChecked = S.VoicePreset == "custom";
            VoiceBase.Text = S.VoiceBaseUrl;
            VoiceModel.Text = S.VoiceModel;
            VoiceKeyState.Text = string.IsNullOrEmpty(S.VoiceKey) ? "Используется ключ из раздела «ИИ», если сервис тот же" : "•••••••• свой ключ сохранён";
            (S.MaxFps switch { 30 => Fps30, 120 => Fps120, 0 => FpsMax, _ => Fps60 }).IsChecked = true;
            VlAuto.IsChecked = string.IsNullOrEmpty(S.VoiceLanguage);
            VlRu.IsChecked = S.VoiceLanguage == "ru";
            VlEn.IsChecked = S.VoiceLanguage == "en";
            VoicePaste.IsChecked = S.VoiceAutoPaste;
            VoicePunct.IsChecked = S.VoicePunctuate;

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
            SteamExact.IsChecked = S.SteamExactSpeed;
            UpdateSteamActivate();
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
            S.VoicePunctuate = VoicePunct.IsChecked == true;
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

        private void OnSteamLog(object s, RoutedEventArgs e)
        {
            var path = Services.Diag.PathToLog;
            try
            {
                if (System.IO.File.Exists(path)) System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Services.Storage.Dir) { UseShellExecute = true });
            }
            catch { }
        }

        /// <summary>Кнопка видна, только пока точная скорость ни разу не заработала (и включена).</summary>
        private async void UpdateSteamActivate()
        {
            SteamActivateRow.Visibility = Visibility.Collapsed;
            if (!S.SteamExactSpeed || S.SteamExactActivated) return;
            if (Services.SteamDownloadService.SteamRoot() == null) return; // Steam не установлен
            if (await Services.SteamClientApi.IsWorkingAsync())
            {
                S.SteamExactActivated = true;
                S.Save();
                return;
            }
            SteamActivateRow.Visibility = Visibility.Visible;
        }

        private async void OnSteamActivate(object s, RoutedEventArgs e)
        {
            SteamActivateBtn.IsEnabled = false;
            var progress = new Progress<string>(t => SteamExactStatus.Text = t);
            bool ok = await Services.SteamClientApi.ActivateAsync(progress);
            if (ok)
            {
                S.SteamExactActivated = true;
                S.Save();
                await System.Threading.Tasks.Task.Delay(2500); // дать прочитать «Готово»
                SteamActivateRow.Visibility = Visibility.Collapsed;
            }
            else SteamActivateBtn.IsEnabled = true;
        }

        private void OnSteamExact(object s, RoutedEventArgs e)
        {
            if (_loading) return;
            S.SteamExactSpeed = SteamExact.IsChecked == true;
            S.Save();
            bool on = S.SteamExactSpeed;
            System.Threading.Tasks.Task.Run(() => Services.SteamClientApi.Apply(on));
            UpdateSteamActivate();
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
            try
            {
                SystemControls.Autostart = Autostart.IsChecked == true;
                ToastBus.Show(new Toast
                {
                    Kind = ToastKind.Success,
                    Title = Autostart.IsChecked == true ? "Автозапуск включён" : "Автозапуск выключен", On = Autostart.IsChecked == true,
                    Icon = Icons.Check
                });
            }
            catch (Exception ex)
            {
                Autostart.IsChecked = SystemControls.Autostart;
                ToastBus.Show(new Toast { Title = "Не удалось изменить автозапуск", Subtitle = ex.Message, Icon = Icons.Info, Important = true });
            }
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

        /// <summary>Показать приветствие первого запуска ещё раз (из «Обновлений»).</summary>
        private void OnShowWelcome(object s, RoutedEventArgs e)
        {
            Services.Journal.Info("welcome", "открыто вручную из Настроек");
            try { new WelcomeWindow { Owner = null }.Show(); } catch (System.Exception ex) { App.Log("welcome: " + ex); }
        }

        private void OnOutfit(object s, RoutedEventArgs e)
        {
            S.BuddyOutfit = (string)((FrameworkElement)s).Tag;
            S.Save();
            UpdatePreview();
            AppHost.Window.ApplyAppearance(); // Клоди в чёлке переодевается сразу
        }

        private void OnScale(object s, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ScaleText == null) return;
            ScaleText.Text = (int)e.NewValue + "%";
            if (_loading) return;
            S.Scale = e.NewValue / 100.0;
            S.Save();
            AppHost.Window.ApplyScale();
            UpdatePreview();
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

        /// <summary>Превью ведёт себя как настоящий остров: «набухает» при наведении.</summary>
        private void OnPreviewEnter(object s, System.Windows.Input.MouseEventArgs e) => AnimatePreview(1.08);
        private void OnPreviewLeave(object s, System.Windows.Input.MouseEventArgs e) => AnimatePreview(1.0);

        private void AnimatePreview(double k)
        {
            double sc = (ScaleSlider?.Value ?? 100) / 100.0 * k;
            var ease = new Helpers.Spring { Damping = 9, Frequency = 9 };
            var d = TimeSpan.FromMilliseconds(420);
            PreviewScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new System.Windows.Media.Animation.DoubleAnimation(sc, d) { EasingFunction = ease });
            PreviewScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new System.Windows.Media.Animation.DoubleAnimation(sc, d) { EasingFunction = ease });
        }

        private void UpdatePreview()
        {
            if (Preview == null) return;
            if (PreviewScale != null && ScaleSlider != null) AnimatePreview(1.0); // размер в превью — сразу
            PreviewBuddy.Visibility = S.Character == "pixel" ? Visibility.Visible : Visibility.Collapsed;
            PreviewBuddy.Outfit = MainWindow.OutfitForWeather();
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
            if (id == "groq") { S.VoiceBaseUrl = "https://api.groq.com/openai/v1"; S.VoiceModel = "whisper-large-v3"; }
            if (id == "openai") { S.VoiceBaseUrl = "https://api.openai.com/v1"; S.VoiceModel = "whisper-1"; }
            if (id == "polza") { S.VoiceBaseUrl = "https://polza.ai/api/v1"; S.VoiceModel = "openai/whisper-1"; }
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

        private async void OnVoiceModels(object s, RoutedEventArgs e)
        {
            OnVoiceFields(null, null);
            var menu = new ContextMenu { PlacementTarget = VoiceModelsBtn, Placement = PlacementMode.Bottom, MaxHeight = 420 };
            menu.Items.Add(new MenuItem { Header = "Загружаю список…", IsEnabled = false });
            menu.IsOpen = true;
            try
            {
                var models = await VoiceService.ModelsAsync(VoiceBase.Text.Trim());
                menu.Items.Clear();
                if (models.Count == 0) menu.Items.Add(new MenuItem { Header = "У сервиса нет моделей распознавания речи", IsEnabled = false });
                else menu.Items.Add(new MenuItem { Header = "Голос → текст · от дешёвых к дорогим", IsEnabled = false });
                foreach (var m in models)
                {
                    var header = m.Id + (string.IsNullOrEmpty(m.PriceText) ? "" : "   ·   " + m.PriceText) + (m.PaidOnly ? "   ·   только платно" : "");
                    var item = new MenuItem { Header = header, IsChecked = m.Id == VoiceModel.Text.Trim(), ToolTip = string.IsNullOrEmpty(m.Title) ? null : m.Title };
                    var id = m.Id;
                    item.Click += (_, __) => { VoiceModel.Text = id; OnVoiceFields(null, null); VoiceTestResult.Text = "Модель: " + id + " — нажми «Проверить»"; };
                    menu.Items.Add(item);
                }
            }
            catch (Exception ex)
            {
                menu.Items.Clear();
                menu.Items.Add(new MenuItem { Header = "Не удалось загрузить: " + ex.Message, IsEnabled = false });
            }
        }

        private void OnVoiceDeleteKey(object s, RoutedEventArgs e)
        {
            S.VoiceKey = "";
            S.Save();
            VoiceKey.Password = "";
            VoiceKeyState.Text = "Свой ключ удалён · используется ключ из раздела «ИИ», если сервис тот же";
        }

        private void OnFps(object s, RoutedEventArgs e)
        {
            S.MaxFps = int.Parse((string)((FrameworkElement)s).Tag);
            Helpers.FrameClock.MaxFps = S.MaxFps;
            S.Save();
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
                ("island", "Открыть / закрыть остров", S.Hotkey),
                ("command", "Командная строка: калькулятор, запуск программ, таймер, напоминание", S.HotkeyCommand),
                ("clipboard", "История буфера обмена", S.HotkeyClipboard),
                ("screenshot", "Снимок области экрана", S.HotkeyScreenshot),
                ("voice", "Голос в текст: начать / закончить диктовку", S.HotkeyVoice),
                ("voiceCommand", "Голосовая команда ИИ", S.HotkeyVoiceCommand),
                ("collapse", "Свернуть остров, когда он активен", S.HotkeyCollapse),
            };
            foreach (var (name, description, gesture) in keys)
            {
                var g = new Grid { Margin = new Thickness(0, 8, 0, 8) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(205) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                var key = new TextBox
                {
                    Text = gesture, IsReadOnly = true, Focusable = true, Cursor = Cursors.Hand,
                    Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x20)),
                    Foreground = (Brush)FindResource("Fg"), BorderThickness = new Thickness(0),
                    Padding = new Thickness(10, 5, 10, 5), HorizontalAlignment = HorizontalAlignment.Left,
                    ToolTip = "Нажми сюда, затем нажми новое сочетание клавиш"
                };
                key.PreviewMouseLeftButtonDown += (_, __) => { key.Focus(); key.Text = "Нажми сочетание…"; };
                // ушли из поля, ничего не нажав, — возвращаем текущее сочетание
                key.LostKeyboardFocus += (_, __) => { if (key.Text == "Нажми сочетание…") key.Text = GetHotkey(name); };
                key.PreviewKeyDown += (_, e) =>
                {
                    var k = e.Key == Key.System ? e.SystemKey : e.Key;
                    if (k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
                    { e.Handled = true; return; }
                    var mods = Keyboard.Modifiers;
                    var parts = new System.Collections.Generic.List<string>();
                    if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
                    if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
                    if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
                    if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
                    parts.Add(k.ToString());
                    var next = string.Join("+", parts);
                    if (AppHost.Window.TrySetHotkey(name, next, out var error)) key.Text = next;
                    else { key.Text = GetHotkey(name); MessageBox.Show(error, "Горячие клавиши", MessageBoxButton.OK, MessageBoxImage.Information); }
                    e.Handled = true;
                };
                var reset = new Button { Content = "Сбросить", Style = (Style)FindResource("Ghost"), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(8, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
                reset.Click += (_, __) =>
                {
                    var defaults = new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["island"] = "Ctrl+Alt+Space", ["command"] = "Ctrl+Alt+K", ["clipboard"] = "Ctrl+Alt+V",
                        ["screenshot"] = "Ctrl+Alt+S", ["voice"] = "Ctrl+Alt+D", ["voiceCommand"] = "Ctrl+Alt+A",
                        ["collapse"] = "Escape"
                    };
                    if (AppHost.Window.TrySetHotkey(name, defaults[name], out var error)) key.Text = defaults[name];
                    else MessageBox.Show(error, "Горячие клавиши", MessageBoxButton.OK, MessageBoxImage.Information);
                };
                var desc = new TextBlock { Text = description, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(reset, 1);
                Grid.SetColumn(desc, 2);
                g.Children.Add(key);
                g.Children.Add(reset);
                g.Children.Add(desc);
                KeysList.Children.Add(g);
            }

            // PrintScreen → снимок через Isle: выделение области, миниатюра в чёлке, файл в Изображения\Isle
            var prtRow = new Grid { Margin = new Thickness(0, 14, 0, 4) };
            prtRow.ColumnDefinitions.Add(new ColumnDefinition());
            prtRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            prtRow.Children.Add(new TextBlock { Text = "PrintScreen делает снимок через Isle (вместо «Ножниц» Windows)", FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            var prt = new CheckBox { IsChecked = S.PrintScreenIsle, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            Grid.SetColumn(prt, 1);
            prtRow.Children.Add(prt);
            prt.Click += (_, __) =>
            {
                if (!AppHost.Window.SetPrintScreen(prt.IsChecked == true))
                {
                    prt.IsChecked = false;
                    MessageBox.Show("Windows не дал занять PrintScreen. Выключи «Использовать PrintScreen для открытия Ножниц» в Параметры → Специальные возможности → Клавиатура и попробуй снова.",
                                    "Горячие клавиши", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            };
            KeysList.Children.Add(prtRow);

            // сохранять ли снимки Isle файлом или только в буфер обмена
            var saveRow = new Grid { Margin = new Thickness(0, 6, 0, 4) };
            saveRow.ColumnDefinitions.Add(new ColumnDefinition());
            saveRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            saveRow.Children.Add(new TextBlock { Text = "Сохранять снимки Isle в «Изображения\\Isle» (выключено — только в буфер обмена)", FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            var save = new CheckBox { IsChecked = S.SaveScreenshots, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            save.Click += (_, __) => { S.SaveScreenshots = save.IsChecked == true; S.Save(); };
            Grid.SetColumn(save, 1);
            saveRow.Children.Add(save);
            KeysList.Children.Add(saveRow);
        }

        private string GetHotkey(string name) => name switch
        {
            "island" => S.Hotkey, "command" => S.HotkeyCommand, "clipboard" => S.HotkeyClipboard,
            "screenshot" => S.HotkeyScreenshot, "voice" => S.HotkeyVoice, "voiceCommand" => S.HotkeyVoiceCommand,
            "collapse" => S.HotkeyCollapse, _ => ""
        };
    }
}
