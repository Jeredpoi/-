using System;
using System.Collections.Generic;
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
using Isle.Helpers;

namespace Isle.Views
{
    /// <summary>Отдельное окно настроек с разделами. Всё сохраняется сразу.</summary>
    public partial class SettingsWindow : Window
    {
        private bool _loading = true;
        private AppSettings S => AppSettings.Current;
        internal static readonly string[] AccentColors = { "#ECECF1", "#F5B544", "#D97757", "#FB7185", "#2DD4BF", "#6EA8FE", "#A78BFA" };

        public SettingsWindow()
        {
            InitializeComponent();
            ApplyTheme();
            SourceInitialized += (_, __) => DarkTitleBar();
            // после сворачивания/перекрытия перерисовываем окно целиком, без «застрявших» кусков
            Activated += (_, __) => InvalidateVisual();
            BuildPresets();
            BuildAccents();
            BuildKeys();
            Load();
            _loading = false;
            // Esc — закрыть окно (запись сочетания клавиш перехватывает Esc раньше, сюда он не доходит);
            // Enter в полях ключей и пароля — сохранить, не тянуться к кнопке
            KeyDown += (_, e) => { if (e.Key == Key.Escape && !e.Handled) { e.Handled = true; Close(); } };
            AiKey.KeyDown += (_, e) => { if (e.Key == Key.Enter) { OnAiSaveKey(AiKey, null); e.Handled = true; } };
            VoiceKey.KeyDown += (_, e) => { if (e.Key == Key.Enter) { OnVoiceSaveKey(VoiceKey, null); e.Handled = true; } };
            ObsPass.KeyDown += (_, e) => { if (e.Key == Key.Enter) { OnObsSave(ObsPass, null); e.Handled = true; } };
            foreach (var tb in new[] { AiBase, AiModel })
                tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { OnAiFieldsChanged(tb, null); e.Handled = true; } };
            UpdateService.Changed += RenderUpdate;
            Closed += (_, __) => UpdateService.Changed -= RenderUpdate;
            // поля ИИ, голоса и OBS сохраняются при уходе из поля — а при закрытии окна фокус не уходит
            Closing += (_, __) =>
            {
                try { OnAiFieldsChanged(this, null); } catch { }
                try { OnVoiceFields(this, null); } catch { }
                try { OnObsFields(this, null); } catch { }
            };
            // подсказка в пустом поле пароля/ключа (у PasswordBox нет своей)
            foreach (var pb in new[] { AiKey, VoiceKey, ObsPass, SpicyKeyBox }) HookPlaceholder(pb);
            SpicyKeyBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { OnSpicySave(SpicyKeyBox, null); e.Handled = true; } };
            Services.SpicyLyrics.KeyProblemChanged += OnSpicyProblem;
            Closed += (_, __) => Services.SpicyLyrics.KeyProblemChanged -= OnSpicyProblem;
            UpdateSpicyStatus();
            RenderUpdate();
        }

        private static void HookPlaceholder(PasswordBox pb)
        {
            void Update()
            {
                pb.ApplyTemplate();
                if (pb.Template?.FindName("ph", pb) is UIElement ph)
                    ph.Visibility = pb.Password.Length == 0 && !pb.IsKeyboardFocused ? Visibility.Visible : Visibility.Collapsed;
            }
            pb.PasswordChanged += (_, __) => Update();
            pb.GotKeyboardFocus += (_, __) => Update();
            pb.LostKeyboardFocus += (_, __) => Update();
            pb.IsVisibleChanged += (_, __) => Update();
            pb.Loaded += (_, __) => Update();
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
            if (UpdateService.Installing) { UpdStatus.Text = "Обновление уже скачивается — смотри чёлку"; return; }
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
            // старые ключи («weather», «record», «modules», «updates») ведут в новые разделы
            var rb = key switch
            {
                "look" => NavLook, "character" => NavCharacter, "notch" or "tabs" => NavNotch, "music" => NavMusic,
                "modules" or "notifications" => NavModules, "clipboard" => NavClipboard, "record" or "capture" => NavRecord,
                "ai" => NavAi, "voice" => NavVoice, "keys" => NavKeys, "updates" or "about" => NavUpdates, _ => NavGeneral
            };
            if (NavSearch.Text.Length > 0) NavSearch.Text = ""; // раздел мог быть скрыт поиском
            rb.IsChecked = true;
        }

        private (RadioButton nav, FrameworkElement sec)[] Sections() => new (RadioButton, FrameworkElement)[]
        {
            (NavGeneral, SecGeneral), (NavLook, SecLook), (NavCharacter, SecCharacter), (NavNotch, SecNotch), (NavMusic, SecMusic),
            (NavModules, SecModules), (NavClipboard, SecClipboard), (NavRecord, SecRecord), (NavAi, SecAi), (NavVoice, SecVoice),
            (NavKeys, SecKeys), (NavUpdates, SecUpdates)
        };

        private void OnNav(object s, RoutedEventArgs e)
        {
            if (SecGeneral == null || SecUpdates == null) return;
            foreach (var (nav, sec) in Sections()) sec.Visibility = nav.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            Scroll?.ScrollToTop();
        }

        /// <summary>Слова для поиска по меню: название раздела и то, что в нём лежит.</summary>
        private (RadioButton nav, string words)[] NavWords() => new (RadioButton, string)[]
        {
            (NavGeneral, "общее запуск windows автозапуск полноэкранн игры oled прятать наведен край скриншот запись погода город"),
            (NavLook, "внешний вид тема закат акцент цвет градиент размер масштаб прозрачн непрозрачн вырез macbook плавность fps кадр"),
            (NavCharacter, "персонаж клоди глаза никого наряд одежда шапка снег"),
            (NavNotch, "чёлка челка вкладки порядок часы секунды 24 формат время музыка свёрнут"),
            (NavMusic, "музыка пилюля плеер главная текст песни lyrics spotify обложка"),
            (NavModules, "уведомления всплывашки громкость раскладка caps lock батарея зарядка vpn steam загрузки журнал баннер"),
            (NavClipboard, "буфер обмена история картинки копировать"),
            (NavRecord, "снимки скриншот printscreen запись экрана obs пометки видеокарта папка rec"),
            (NavAi, "ии ai сервис ключ api модель groq gemini openrouter polza openai claude ollama"),
            (NavVoice, "диктовка голос текст whisper распознавание язык знаки препинания вставлять"),
            (NavKeys, "клавиши сочетания горячие hotkey"),
            (NavUpdates, "о программе обновления версия данные папка журнал отладка знакомство приветствие закрыть выйти"),
        };

        private void OnNavSearch(object s, TextChangedEventArgs e)
        {
            if (NavPanel == null || NavUpdates == null || NavSep == null) return;
            var q = NavSearch.Text.Trim().ToLowerInvariant();
            var all = NavWords();
            foreach (var (nav, words) in all)
                nav.Visibility = q.Length == 0 || words.Contains(q) ? Visibility.Visible : Visibility.Collapsed;
            NavSep.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            // если выбранный раздел скрыт поиском — открываем первый найденный
            var cur = all.FirstOrDefault(x => x.nav.IsChecked == true).nav;
            var first = all.FirstOrDefault(x => x.nav.Visibility == Visibility.Visible).nav;
            if (first != null && cur != null && cur.Visibility != Visibility.Visible) first.IsChecked = true;
        }

        // ---------- Состояния: точка + слово ----------

        private static readonly Brush DotOk = Rgb(0x4A, 0xDE, 0x80), DotOff = Rgb(0x6E, 0x6E, 0x78), DotBad = Rgb(0xF8, 0x71, 0x71);

        private static void SetState(System.Windows.Shapes.Ellipse dot, TextBlock text, string word, Brush color)
        {
            dot.Fill = color;
            text.Text = word;
        }

        private void ShowAiKeyState(string word = null) =>
            SetState(AiKeyDot, AiKeyState, word ?? (string.IsNullOrEmpty(S.AiKey) ? "Ключа нет" : "Ключ сохранён"), string.IsNullOrEmpty(S.AiKey) ? DotOff : DotOk);

        private void ShowVoiceKeyState(string word = null) =>
            SetState(VoiceKeyDot, VoiceKeyState, word ?? (string.IsNullOrEmpty(S.VoiceKey) ? "Берётся из «ИИ»" : "Свой ключ сохранён"), string.IsNullOrEmpty(S.VoiceKey) ? DotOff : DotOk);

        private void ShowNotifState()
        {
            var n = AppHost.Notifications;
            bool ok = n?.Available == true;
            SetState(NotifDot, NotifStatus, n?.Status ?? "—", ok ? DotOk : (n == null ? DotOff : DotBad));
            NotifStatus.ToolTip = n?.Status;
            NotifAccessBtn.Visibility = ok || n == null ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>Нет доступа к уведомлениям — открываем параметры Windows и пробуем снова.</summary>
        private async void OnNotifAccess(object s, RoutedEventArgs e)
        {
            var n = AppHost.Notifications;
            if (n == null || n.Available) return;
            SystemControls.OpenSettings("ms-settings:privacy-notifications");
            try { await n.StartAsync(); } catch { }
            ShowNotifState();
        }

        /// <summary>Зависимые строки гаснут, когда главный переключатель выключен.</summary>
        private void UpdateDeps()
        {
            if (RowClipImg == null) return;
            RowClockSeconds.IsEnabled = ShowClock.IsChecked == true;
            RowLyricsHome.IsEnabled = Lyrics.IsChecked == true;
            RowNotifOnly.IsEnabled = Notif.IsChecked == true;
            RowClipImg.IsEnabled = ClipHist.IsChecked == true;
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
            CaptureSoft.IsChecked = S.CaptureGpuRender;
            DebugLog.IsChecked = S.DebugLog;
            Clock24.IsChecked = S.Use24h;

            ChPixel.IsChecked = S.Character == "pixel";
            ChEyes.IsChecked = S.Character == "eyes";
            ChNone.IsChecked = S.Character == "none";
            foreach (var rb in Outfits.Children.OfType<RadioButton>()) rb.IsChecked = (string)rb.Tag == (S.BuddyOutfit ?? "auto");
            foreach (var rb in NotchModes.Children.OfType<RadioButton>()) rb.IsChecked = (string)rb.Tag == (S.NotchMode ?? "off");
            BuildTabsList();
            foreach (var rb in SnowModes.Children.OfType<RadioButton>()) rb.IsChecked = (string)rb.Tag == (S.WinterSnow ?? "auto");
            SnowWhy.Text = "Сейчас: " + (AppHost.Window?.SnowReason ?? "—");
            foreach (var rb in Themes.Children.OfType<RadioButton>()) rb.IsChecked = (string)rb.Tag == (S.Theme ?? "dark");
            ScaleSlider.Value = Math.Round(S.Scale * 100);
            ScaleText.Text = (int)ScaleSlider.Value + "%";
            OpacitySlider.Value = Math.Round(S.IslandOpacity * 100);
            OpacityText.Text = (int)OpacitySlider.Value + "%";
            UpdatePreview();

            SelectPreset(S.AiPreset, false);
            AiBase.Text = S.AiBaseUrl;
            AiModel.Text = S.AiModel;
            ShowAiKeyState();

            VpGroq.IsChecked = S.VoicePreset == "groq";
            VpOpenAi.IsChecked = S.VoicePreset == "openai";
            VpPolza.IsChecked = S.VoicePreset == "polza";
            VpCustom.IsChecked = S.VoicePreset == "custom";
            VoiceBase.Text = S.VoiceBaseUrl;
            VoiceModel.Text = S.VoiceModel;
            ShowVoiceKeyState();
            (S.MaxFps switch { 30 => Fps30, 120 => Fps120, 0 => FpsMax, _ => Fps60 }).IsChecked = true;
            VlAuto.IsChecked = string.IsNullOrEmpty(S.VoiceLanguage);
            VlRu.IsChecked = S.VoiceLanguage == "ru";
            VlEn.IsChecked = S.VoiceLanguage == "en";
            VoicePaste.IsChecked = S.VoiceAutoPaste;
            VoicePunct.IsChecked = S.VoicePunctuate;

            ObsPort.Text = S.ObsPort.ToString();
            ShowObsFolder();

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
            NotifOnly.IsChecked = S.NotificationsOnlyInIsland;
            RefreshHotkeyNotes();
            ShowNotifState();
            Vpn.IsChecked = S.VpnStatus;
            SteamExact.IsChecked = S.SteamExactSpeed;
            UpdateSteamActivate();
            ClipHist.IsChecked = S.ClipboardHistory;
            ClipImg.IsChecked = S.ClipboardImages;
            AutoUpdate.IsChecked = S.AutoUpdateCheck;
            ObsStatus.IsChecked = S.ObsStatus;
            PrtScr.IsChecked = S.PrintScreenIsle;
            SaveShots.IsChecked = S.SaveScreenshots;
            UpdateDeps();
        }

        private void OnToggle(object s, RoutedEventArgs e)
        {
            UpdateDeps();
            if (_loading) return;
            Dispatcher.BeginInvoke(new Action(UpdatePreview), System.Windows.Threading.DispatcherPriority.Background); // превью — по новым настройкам
            S.HideInFullscreen = Fullscreen.IsChecked == true;
            S.ExpandOnHover = Hover.IsChecked == true;
            S.CaptureGpuRender = CaptureSoft.IsChecked == true;
            if (S.DebugLog != (DebugLog.IsChecked == true))
            {
                S.DebugLog = DebugLog.IsChecked == true;
                Journal.Info("debug", "подробный журнал " + (S.DebugLog ? "включён" : "выключен"));
                Journal.Verbose = S.DebugLog;
                if (S.DebugLog) DebugProbe.Start();
            }
            S.Use24h = Clock24.IsChecked == true;
            S.VoiceAutoPaste = VoicePaste.IsChecked == true;
            S.VoicePunctuate = VoicePunct.IsChecked == true;
            S.VolumeToasts = Volume.IsChecked == true;
            S.LayoutToasts = Layout.IsChecked == true;
            S.CapsToasts = Caps.IsChecked == true;
            S.BatteryToasts = Battery.IsChecked == true;
            S.NotificationToasts = Notif.IsChecked == true;
            S.NotificationsOnlyInIsland = NotifOnly.IsChecked == true;
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
                if (System.IO.File.Exists(path)) MainWindow.ShowInExplorer(path);
                else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Services.Storage.Dir) { UseShellExecute = true });
            }
            catch { }
        }

        /// <summary>Кнопка видна, только пока точная скорость ни разу не заработала (и включена).</summary>
        private const string SteamNote = "Isle один раз перезапустит Steam";

        private async void UpdateSteamActivate()
        {
            SteamActivateBtn.Visibility = Visibility.Collapsed;
            SteamExactStatus.Text = SteamNote;
            if (!S.SteamExactSpeed || S.SteamExactActivated) return;
            if (Services.SteamDownloadService.SteamRoot() == null) return; // Steam не установлен
            bool working = await Services.SteamClientApi.IsWorkingAsync();
            if (!S.SteamExactSpeed || S.SteamExactActivated) return; // пока ждали ответа, настройку могли выключить
            if (working)
            {
                S.SteamExactActivated = true;
                S.Save();
                return;
            }
            SteamActivateBtn.Visibility = Visibility.Visible;
            SteamExactStatus.Text = "Steam закроется и откроется снова";
        }

        private async void OnSteamActivate(object s, RoutedEventArgs e)
        {
            SteamActivateBtn.IsEnabled = false;
            var progress = new Progress<string>(t => SteamExactStatus.Text = t);
            bool ok;
            try { ok = await Services.SteamClientApi.ActivateAsync(progress); }
            catch (Exception ex) { SteamExactStatus.Text = "Ошибка: " + ex.Message; ok = false; }
            if (ok)
            {
                S.SteamExactActivated = true;
                S.Save();
                await System.Threading.Tasks.Task.Delay(2500); // дать прочитать «Готово»
                SteamActivateBtn.Visibility = Visibility.Collapsed;
                SteamExactStatus.Text = SteamNote;
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
            UpdateDeps();
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

        // «Закрыть Isle» — тихая кнопка с подтверждением: первый щелчок спрашивает, второй (в течение 4 с) закрывает
        private DateTime _exitArmed;

        private void OnExit(object s, RoutedEventArgs e)
        {
            if (DateTime.Now - _exitArmed < TimeSpan.FromSeconds(4)) { Application.Current.Shutdown(); return; }
            _exitArmed = DateTime.Now;
            if (s is not Button b) return;
            var old = b.Content;
            b.Content = "Точно закрыть?";
            b.Foreground = DotBad;
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            t.Tick += (_, __) => { t.Stop(); b.Content = old; b.ClearValue(ForegroundProperty); };
            t.Start();
        }

        /// <summary>PrintScreen → снимок через Isle: выделение области, миниатюра в чёлке, файл в «Изображения\Isle».</summary>
        private void OnPrintScreen(object s, RoutedEventArgs e)
        {
            if (!AppHost.Window.SetPrintScreen(PrtScr.IsChecked == true))
            {
                PrtScr.IsChecked = false;
                ToastBus.Show(new Toast { Kind = ToastKind.Error, Title = "Windows не дал занять PrintScreen",
                    Subtitle = "Выключи «PrintScreen открывает Ножницы» в Параметры → Специальные возможности → Клавиатура",
                    Icon = Icons.Keyboard, Important = true, Duration = TimeSpan.FromSeconds(6) });
            }
        }

        // сохранять ли снимки Isle файлом или только в буфер обмена
        private void OnSaveShots(object s, RoutedEventArgs e)
        {
            S.SaveScreenshots = SaveShots.IsChecked == true;
            S.Save();
        }

        // ---------- Внешний вид ----------

        /// <summary>Градиенты: «откуда>куда». Подобраны так, чтобы оба конца читались на тёмном.</summary>
        internal static readonly (string value, string name)[] AccentGradients =
        {
            ("#22D3EE>#A855F7", "Аврора"), ("#FB923C>#F43F5E", "Персик"), ("#38BDF8>#34D399", "Лагуна"),
            ("#F472B6>#818CF8", "Неон"), ("#FBBF24>#F97316", "Огонь"), ("#A3E635>#2DD4BF", "Мята"),
        };

        private void BuildAccents()
        {
            var all = AccentColors.Select(h => (h, h)).Concat(AccentGradients.Select(g => (g.value, "Градиент «" + g.name + "»")));
            foreach (var (hex, tip) in all)
            {
                var rb = new RadioButton
                {
                    Style = (Style)FindResource("SSegment"), GroupName = "accent", Tag = hex, Padding = new Thickness(0), Width = 28,
                    Content = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Background = Helpers.AccentPaint.Brush(hex, 45) },
                    IsChecked = string.Equals(hex, S.AccentHex, StringComparison.OrdinalIgnoreCase), ToolTip = tip
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
            try { WelcomeWindow.ShowOnce(); } catch (System.Exception ex) { App.Log("welcome: " + ex); }
        }

        private void OnNotchMode(object s, RoutedEventArgs e)
        {
            S.NotchMode = (string)((FrameworkElement)s).Tag;
            S.Save();
            Isle.Helpers.NotchBar.Update(); // окна сдвигаются сразу
        }

        /// <summary>
        /// Тема окна настроек. «Закат»: фото пальм под плотной лиловой вуалью, меню и карточки — матовое стекло.
        /// Тёмная: убираем подмены — остаются цвета из ресурсов окна.
        /// </summary>
        private void SetCityStatus(string text)
        {
            CityStatus.Text = text ?? "";
            CityStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ApplyTheme()
        {
            var keys = new[] { "SBg", "SSide", "SCard", "SCardBorder", "SField", "SFieldBorder", "SBtn", "SBtnBorder", "SSeg", "SSegHover", "SSegSel",
                "SNavHover", "SNavSel", "SSep", "SNote", "SRowNote", "SLabel", "STile", "SKeyCap", "SKeyCapEdge" };
            if (!_darkRes.ContainsKey("SBg")) foreach (var k in keys) _darkRes[k] = Resources[k];
            // в «Закате» акцент всегда розовый — выбор цвета акцента не действует, показываем это
            // в «Закате» кружки не гасим (выглядело как поломка) — прячем и пишем, почему
            AccentsBox.Visibility = Helpers.SunsetTheme.On ? Visibility.Collapsed : Visibility.Visible;
            AccentNote.Text = Helpers.SunsetTheme.On ? "В «Закате» — розовый" : "Цвет или градиент";
            if (!Helpers.SunsetTheme.On)
            {
                foreach (var k in keys) Resources[k] = _darkRes[k];
                return;
            }
            static SolidColorBrush A(byte a, byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromArgb(a, r, g, b)); x.Freeze(); return x; }
            Brush back;
            try
            {
                var photo = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/sunset-palms.jpg"));
                var dg = new DrawingGroup();
                dg.Children.Add(new ImageDrawing(photo, new Rect(0, 0, 1, (double)photo.PixelHeight / photo.PixelWidth)));
                dg.Children.Add(new GeometryDrawing(A(0xD0, 0x14, 0x0C, 0x22), null, new RectangleGeometry(new Rect(0, 0, 1, (double)photo.PixelHeight / photo.PixelWidth))));
                back = new DrawingBrush(dg) { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Center };
                back.Freeze();
            }
            catch { back = A(0xFF, 0x14, 0x0C, 0x22); }
            Resources["SBg"] = back;
            Resources["SSide"] = A(0xB3, 0x0D, 0x0A, 0x14);
            Resources["SCard"] = A(0x14, 0xFF, 0xFF, 0xFF);
            Resources["SCardBorder"] = A(0x2E, 0xFF, 0xFF, 0xFF);
            Resources["SField"] = A(0x80, 0x0D, 0x0A, 0x14);
            Resources["SFieldBorder"] = A(0x33, 0xFF, 0xFF, 0xFF);
            Resources["SBtn"] = A(0x24, 0xFF, 0xFF, 0xFF);
            Resources["SBtnBorder"] = A(0x2E, 0xFF, 0xFF, 0xFF);
            Resources["SSeg"] = A(0x1A, 0xFF, 0xFF, 0xFF);
            Resources["SSegHover"] = A(0x2E, 0xFF, 0xFF, 0xFF);
            Resources["SNavHover"] = A(0x1A, 0xFF, 0xFF, 0xFF);
            Resources["SNavSel"] = A(0x40, 0xF2, 0x67, 0x9F);
            Resources["SSep"] = A(0x1F, 0xFF, 0xFF, 0xFF);
            Resources["SNote"] = A(0xFF, 0xCF, 0xC7, 0xDA);
            Resources["SSegSel"] = A(0x38, 0xFF, 0xFF, 0xFF);
            Resources["SRowNote"] = A(0xFF, 0xB9, 0xB0, 0xC8);
            Resources["SLabel"] = A(0xFF, 0xA8, 0x9C, 0xBC);
            Resources["STile"] = A(0x1F, 0xFF, 0xFF, 0xFF);
            Resources["SKeyCap"] = A(0x1E, 0xFF, 0xFF, 0xFF);
            Resources["SKeyCapEdge"] = A(0x40, 0x00, 0x00, 0x00);
        }
        private readonly System.Collections.Generic.Dictionary<string, object> _darkRes = new();

        private void OnTheme(object s, RoutedEventArgs e)
        {
            S.Theme = (string)((FrameworkElement)s).Tag;
            S.Save();
            AppHost.Window.ApplyAppearance(); // фон, акцент и полоски — сразу; Клоди переоденется на следующем кадре
            ApplyTheme();
            UpdatePreview();
        }

        private void OnSnowMode(object s, RoutedEventArgs e)
        {
            S.WinterSnow = (string)((FrameworkElement)s).Tag;
            S.Save(); // чёлка подхватит на следующем кадре
            Dispatcher.InvokeAsync(() => SnowWhy.Text = "Сейчас: " + (AppHost.Window?.SnowReason ?? "—"), System.Windows.Threading.DispatcherPriority.Background);
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

        // ---------- вкладки чёлки: порядок и видимость ----------
        private void BuildTabsList()
        {
            if (TabsList == null) return;
            TabsList.Children.Clear();
            var order = IslandTabs.Order();
            var hidden = new HashSet<string>(order.Where(IslandTabs.IsHidden));
            for (int i = 0; i < order.Count; i++)
            {
                var tab = IslandTabs.Get(order[i]);
                int index = i;
                bool isHome = tab.Id == "home", visible = !hidden.Contains(tab.Id);
                if (i > 0) TabsList.Children.Add(new Border { Style = (Style)FindResource("Sep") });
                var row = new Grid { Margin = new Thickness(16, 0, 16, 0), Height = 44 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var fg = (Brush)FindResource(visible ? "Fg" : "Muted");
                row.Children.Add(new Icon { Kind = tab.Icon, Width = 16, Height = 16, Foreground = fg, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left });
                var title = new TextBlock { Text = tab.Title + (isHome ? "  · всегда" : ""), Foreground = fg, VerticalAlignment = VerticalAlignment.Center, FontSize = 13.5, FontWeight = FontWeights.Medium };
                Grid.SetColumn(title, 1); row.Children.Add(title);
                var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                Button Arrow(string glyph, int delta, bool enabled)
                {
                    var b = new Button { Style = (Style)FindResource("SBtnBase"), Width = 28, Height = 28, Padding = new Thickness(0), IsEnabled = enabled,
                        Content = new TextBlock { Text = glyph, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center }, ToolTip = delta < 0 ? "Левее" : "Правее" };
                    b.Click += (_, __) => MoveTab(index, delta);
                    return b;
                }
                tools.Children.Add(Arrow("↑", -1, i > 0));
                tools.Children.Add(Arrow("↓", +1, i < order.Count - 1));
                var cb = new CheckBox { IsChecked = visible, IsEnabled = !isHome, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = isHome ? 0.4 : 1 };
                var id = tab.Id;
                cb.Click += (_, __) => { if (cb.IsChecked == true) hidden.Remove(id); else hidden.Add(id); ApplyTabs(order, hidden); };
                tools.Children.Add(cb);
                Grid.SetColumn(tools, 2); row.Children.Add(tools);
                TabsList.Children.Add(row);
            }
            UpdateTabsPreview();
        }

        private void MoveTab(int index, int delta)
        {
            var order = IslandTabs.Order();
            int j = index + delta;
            if (j < 0 || j >= order.Count) return;
            (order[index], order[j]) = (order[j], order[index]);
            ApplyTabs(order, order.Where(IslandTabs.IsHidden).ToList());
        }

        private void ApplyTabs(List<string> order, IEnumerable<string> hidden)
        {
            IslandTabs.Save(order, hidden);
            BuildTabsList();
            AppHost.Window.ApplyTabs(); // в самой чёлке — сразу
        }

        /// <summary>Превью ряда вкладок: как в чёлке — значки, у первой подпись.</summary>
        private void UpdateTabsPreview()
        {
            if (PreviewTabs == null) return;
            PreviewTabs.Children.Clear();
            bool first = true;
            foreach (var id in IslandTabs.Visible())
            {
                var tab = IslandTabs.Get(id);
                var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                sp.Children.Add(new Icon { Kind = tab.Icon, Width = 15, Height = 15, Foreground = (Brush)FindResource(first ? "Fg" : "Muted"), VerticalAlignment = VerticalAlignment.Center });
                if (first) sp.Children.Add(new TextBlock { Text = tab.Title, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                PreviewTabs.Children.Add(new Border
                {
                    Child = sp, Height = 28, Padding = new Thickness(8, 0, 8, 0), CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 0, 2, 0),
                    Background = first ? new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x2B)) : Brushes.Transparent,
                    ToolTip = tab.Title,
                });
                first = false;
            }
        }

        private void UpdatePreview()
        {
            if (Preview == null) return;
            if (PreviewScale != null && ScaleSlider != null) AnimatePreview(1.0); // размер в превью — сразу
            PreviewBuddy.Visibility = S.Character == "pixel" ? Visibility.Visible : Visibility.Collapsed;
            PreviewBuddy.Outfit = MainWindow.OutfitForWeather();
            PreviewEyes.Visibility = S.Character == "eyes" ? Visibility.Visible : Visibility.Collapsed;
            Preview.Background = new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Clamp(S.IslandOpacity, 0.5, 1)), 0, 0, 0));
            PreviewTime.Text = DateTime.Now.ToString((S.Use24h ? "HH:mm" : "h:mm") + (S.ClockSeconds ? ":ss" : "")); // как в самой чёлке
        }

        // ---------- ИИ ----------

        // сервисов много — сегменты переносились бы на вторую строку, поэтому выпадающий список
        private bool _presetSync;

        private void BuildPresets()
        {
            foreach (var p in AiService.Presets)
                AiPresets.Items.Add(new ComboBoxItem { Content = p.Title + (p.Free ? " · бесплатно" : ""), Tag = p.Id });
        }

        private void OnAiPresetPicked(object s, SelectionChangedEventArgs e)
        {
            if (_presetSync || _loading) return;
            if (AiPresets.SelectedItem is ComboBoxItem it && it.Tag is string id) SelectPreset(id, true);
        }

        private void SelectPreset(string id, bool apply)
        {
            var p = AiService.Preset(id) ?? AiService.Presets[0];
            _presetSync = true;
            AiPresets.SelectedItem = AiPresets.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == p.Id);
            _presetSync = false;
            AiNote.Text = p.Note;
            AiNote.ToolTip = p.Note;
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
            ShowAiKeyState();
        }

        private void OnAiDeleteKey(object s, RoutedEventArgs e)
        {
            S.AiKey = "";
            S.Save();
            ShowAiKeyState("Ключ удалён");
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
            ShowVoiceKeyState();
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
            ShowVoiceKeyState("Удалён · берётся из «ИИ»");
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

        private void ShowObsFolder() => ObsFolder.Text = string.IsNullOrWhiteSpace(S.ObsRecordFolder)
            ? "Как в OBS (Вывод → Путь записи)"
            : S.ObsRecordFolder;

        private void OnObsFolder(object s, RoutedEventArgs e)
        {
            var start = string.IsNullOrWhiteSpace(S.ObsRecordFolder)
                ? System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyVideos), "Isle")
                : S.ObsRecordFolder;
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Куда сохранять записи OBS", InitialDirectory = System.IO.Directory.Exists(start) ? start : System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyVideos) };
            if (dlg.ShowDialog(this) != true) return;
            S.ObsRecordFolder = dlg.FolderName;
            S.Save();
            ShowObsFolder();
        }

        private void OnObsFolderReset(object s, RoutedEventArgs e)
        {
            S.ObsRecordFolder = "";
            S.Save();
            ShowObsFolder();
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
            SetCityStatus("Ищу…");
            try
            {
                var r = await WeatherService.GeocodeAsync(CityBox.Text.Trim());
                if (r == null) { SetCityStatus("Город не найден"); return; }
                S.City = r.Value.name; S.Latitude = r.Value.lat; S.Longitude = r.Value.lon;
                S.Save();
                CityBox.Text = S.City;
                SetCityStatus("Готово: " + S.City);
                await AppHost.Window.RefreshWeather();
            }
            catch (Exception ex) { SetCityStatus("Нет связи: " + ex.Message); }
        }

        // ---------- Клавиши ----------

        private static readonly System.Collections.Generic.Dictionary<string, string> DefaultHotkeys = new()
        {
            ["island"] = "Ctrl+Alt+Space", ["command"] = "Ctrl+Alt+K", ["clipboard"] = "Ctrl+Alt+V",
            ["screenshot"] = "Ctrl+Alt+S", ["voice"] = "Ctrl+Alt+D", ["voiceCommand"] = "Ctrl+Alt+A", ["record"] = "Ctrl+Alt+R",
            ["collapse"] = "Escape"
        };

        private static Brush Rgb(byte r, byte g, byte b, byte a = 255) { var x = new SolidColorBrush(Color.FromArgb(a, r, g, b)); x.Freeze(); return x; }
        private static readonly Brush CapFg = Rgb(0xEC, 0xEC, 0xF1),
            CapMuted = Rgb(0x8B, 0x8B, 0x95), HostHover = Rgb(0xFF, 0xFF, 0xFF, 0x0C), HostRec = Rgb(0xFF, 0xFF, 0xFF, 0x16);

        /// <summary>Сочетание клавишами-«фишками»: [Ctrl] + [Alt] + [K]. Цвета — из ресурсов окна (в «Закате» светлое стекло).</summary>
        private static FrameworkElement Keycaps(string hotkey)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            if (string.IsNullOrWhiteSpace(hotkey))
            {
                row.Children.Add(new TextBlock { Text = "не назначено", FontSize = 12, FontStyle = FontStyles.Italic, Foreground = CapMuted, VerticalAlignment = VerticalAlignment.Center });
                return row;
            }
            var parts = hotkey.Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) row.Children.Add(new TextBlock { Text = "+", Foreground = CapMuted, FontSize = 11, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
                var chip = new Border
                {
                    Height = 24, MinWidth = 24, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(0, 0, 0, 2), Padding = new Thickness(7, 0, 7, 0),
                    Child = new TextBlock { Text = parts[i].Replace("Space", "Пробел").Replace("Escape", "Esc"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = CapFg,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                };
                chip.SetResourceReference(Border.BackgroundProperty, "SKeyCap");
                chip.SetResourceReference(Border.BorderBrushProperty, "SKeyCapEdge");
                row.Children.Add(chip);
            }
            return row;
        }

        /// <summary>Маленькая круглая кнопка со значком (сбросить, удалить).</summary>
        private static Border IconButton(string path, string tip, Action click)
        {
            var b = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(7), Background = Brushes.Transparent, Cursor = Cursors.Hand,
                Margin = new Thickness(4, 0, 0, 0), ToolTip = tip, VerticalAlignment = VerticalAlignment.Center,
                Child = new Isle.Helpers.Icon { Data = path, Width = 14, Height = 14, Foreground = CapMuted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            b.MouseEnter += (_, __) => { b.Background = HostHover; ((Isle.Helpers.Icon)b.Child).Foreground = CapFg; };
            b.MouseLeave += (_, __) => { b.Background = Brushes.Transparent; ((Isle.Helpers.Icon)b.Child).Foreground = CapMuted; };
            b.MouseLeftButtonUp += (_, e) => { click(); e.Handled = true; };
            return b;
        }

        private void BuildKeys()
        {
            var keys = new[]
            {
                ("island", "Остров", "Открыть или закрыть чёлку"),
                ("command", "Командная строка", "Калькулятор, программы, таймер"),
                ("clipboard", "Буфер обмена", "История скопированного"),
                ("screenshot", "Снимок экрана", "Выделить область и снять"),
                ("voice", "Диктовка", "Начать или закончить"),
                ("voiceCommand", "Голосовая команда", "Сказать команду ИИ"),
                ("record", "Запись видео", "Выделить область и записать"),
                ("collapse", "Свернуть остров", "Когда остров активен"),
            };
            const string ResetIcon = "M4.5 12a7.5 7.5 0 1 0 2.2-5.3 M4.5 4.5v4h4";
            const string DeleteIcon = "M7 7L17 17 M17 7L7 17";
            for (int n = 0; n < keys.Length; n++)
            {
                var (name, title, note) = keys[n];
                if (n > 0) KeysList.Children.Add(new Border { Style = (Style)FindResource("Sep") });
                // колонки: текст | сочетание (ровно 200 — фишки выстраиваются по одной линии) | ↺ ×
                var g = new Grid { Margin = new Thickness(16, 10, 10, 10), MinHeight = 32, Background = Brushes.Transparent };
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });

                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
                text.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("RowTitle") });
                text.Children.Add(new TextBlock { Text = note, Style = (Style)FindResource("RowNote") });
                g.Children.Add(text);

                // поле сочетания: нажми — оно ждёт новое сочетание (Esc — отмена, кроме самой клавиши сворачивания)
                var host = new Border
                {
                    CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 3, 6, 3), Background = Brushes.Transparent,
                    BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(1), Focusable = true, Cursor = Cursors.Hand,
                    FocusVisualStyle = null, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right,
                    ToolTip = "Нажми, затем нажми новое сочетание клавиш"
                };
                bool recording = false;
                void Show()
                {
                    recording = false;
                    host.Child = Keycaps(GetHotkey(name));
                    host.BorderBrush = Brushes.Transparent;
                    host.Background = host.IsMouseOver ? HostHover : Brushes.Transparent;
                }
                void Record()
                {
                    recording = true;
                    host.Child = new TextBlock { Text = "Нажми сочетание…", FontSize = 12, Foreground = CapFg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 3, 4, 3) };
                    host.SetResourceReference(Border.BorderBrushProperty, "Accent"); // фокус — акцентом
                    host.Background = HostRec;
                }
                void Apply(string next)
                {
                    if (AppHost.Window.TrySetHotkey(name, next, out var error)) { Show(); RefreshHotkeyNotes(); AppHost.Tray?.Refresh(); }
                    else { Show(); ToastBus.Show(new Toast { Kind = ToastKind.Error, Title = "Сочетание не подошло", Subtitle = error, Icon = Icons.Keyboard, Important = true, Duration = TimeSpan.FromSeconds(4) }); }
                }
                Show();
                host.MouseEnter += (_, __) => { if (!recording) host.Background = HostHover; };
                host.MouseLeave += (_, __) => { if (!recording) host.Background = Brushes.Transparent; };
                host.MouseLeftButtonDown += (_, e) => { host.Focus(); Keyboard.Focus(host); Record(); e.Handled = true; };
                host.LostKeyboardFocus += (_, __) => { if (recording) Show(); };
                host.PreviewKeyDown += (_, e) =>
                {
                    if (!recording) return;
                    var k = e.Key == Key.System ? e.SystemKey : e.Key;
                    // Tab — уйти из поля, Alt+F4 — закрыть окно: не перехватываем, запись отменяется
                    if (k == Key.Tab || (k == Key.F4 && Keyboard.Modifiers == ModifierKeys.Alt)) { Show(); return; }
                    e.Handled = true;
                    if (k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
                    var mods = Keyboard.Modifiers;
                    if (k == Key.Escape && mods == ModifierKeys.None && name != "collapse") { Show(); return; }
                    var parts = new System.Collections.Generic.List<string>();
                    if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
                    if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
                    if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
                    if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
                    parts.Add(k.ToString());
                    Apply(string.Join("+", parts));
                };
                Grid.SetColumn(host, 1);
                g.Children.Add(host);

                // ↺ и × видны только при наведении на строку
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Visibility = Visibility.Hidden };
                buttons.Children.Add(IconButton(ResetIcon, "Вернуть как было: " + KeyName(DefaultHotkeys[name]), () => Apply(DefaultHotkeys[name])));
                buttons.Children.Add(IconButton(DeleteIcon, "Убрать сочетание", () => Apply("")));
                Grid.SetColumn(buttons, 2);
                g.Children.Add(buttons);
                g.MouseEnter += (_, __) => buttons.Visibility = Visibility.Visible;
                g.MouseLeave += (_, __) => buttons.Visibility = Visibility.Hidden;
                KeysList.Children.Add(g);
            }
        }

        private static string KeyName(string hotkey) => hotkey.Replace("Space", "Пробел");

        /// <summary>Подсказки с сочетаниями клавиш — по текущим настройкам (их можно поменять или убрать).</summary>
        private void RefreshHotkeyNotes()
        {
            HoverNote.ToolTip = "Подведи курсор к самому верху экрана над чёлкой. Просто водить по чёлке можно — она не откроется. Если выключить — только кликом"
                + (string.IsNullOrWhiteSpace(S.Hotkey) ? "." : " или " + KeyName(S.Hotkey) + ".");
            VoiceNote.Text = string.IsNullOrWhiteSpace(S.HotkeyVoice)
                ? "Назначь сочетание в «Клавишах»"
                : KeyName(S.HotkeyVoice) + " — скажи, и текст в буфере";
        }

        private string GetHotkey(string name) => name switch
        {
            "island" => S.Hotkey, "command" => S.HotkeyCommand, "clipboard" => S.HotkeyClipboard,
            "screenshot" => S.HotkeyScreenshot, "voice" => S.HotkeyVoice, "voiceCommand" => S.HotkeyVoiceCommand, "record" => S.HotkeyRecord,
            "collapse" => S.HotkeyCollapse, _ => ""
        };

        private void OnSpicySite(object sender, RoutedEventArgs e)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://developers.spicylyrics.org/dashboard/applications") { UseShellExecute = true }); } catch { }
        }

        private void OnSpicyProblem() => Dispatcher.BeginInvoke(new Action(UpdateSpicyStatus));

        private void UpdateSpicyStatus()
        {
            bool has = !string.IsNullOrEmpty(S.SpicyKeyProtected);
            var problem = Services.SpicyLyrics.KeyProblem;
            SpicyStatus.Text = !has ? "" : problem ?? "Ключ сохранён — текст берётся из Spicy Lyrics, если он там есть.";
            SpicyStatus.Foreground = problem != null ? (Brush)FindResource("Danger") : (Brush)FindResource("SNote");
        }

        private void OnSpicySave(object sender, RoutedEventArgs e)
        {
            var k = SpicyKeyBox.Password.Trim();
            if (k.Length == 0 && string.IsNullOrEmpty(S.SpicyKeyProtected)) return;
            // пустое поле + «Сохранить» — убрать ключ
            S.SpicyKey = k;
            S.Save();
            SpicyKeyBox.Password = "";
            Services.SpicyLyrics.KeyChanged();
            AppHost.Lyrics?.Reload();
            UpdateSpicyStatus();
            if (k.Length == 0) SpicyStatus.Text = "Ключ удалён — текст из LRCLIB.";
        }
    }
}
