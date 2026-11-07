using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Isle.Native;
using Isle.Services;

namespace Isle
{
    public enum IslandState { Collapsed, Toast, Expanded }

    public partial class MainWindow : Window
    {
        private const int HkToggle = 1, HkCommand = 2, HkClipboard = 3, HkShot = 4, HkVoice = 5, HkVoiceCmd = 6, HkPrint = 7;
        private static readonly Size ExpandedSize = new(900, 470);

        private HwndSource _src;
        private IntPtr _hwnd;
        private readonly List<int> _registeredHotkeys = new();
        private readonly Dictionary<string, FrameworkElement> _views = new();
        private readonly Queue<Toast> _toasts = new();
        private Helpers.Character[] _characters;
        private Toast _currentToast;
        private readonly DispatcherTimer _toastTimer = new();
        private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer _hoverOpen = new() { Interval = TimeSpan.FromMilliseconds(220) };
        private readonly DispatcherTimer _leaveClose = new() { Interval = TimeSpan.FromMilliseconds(550) };
        private readonly DispatcherTimer _oledHide = new();
        private readonly DispatcherTimer _weatherTimer = new() { Interval = TimeSpan.FromMinutes(20) };
        private readonly DispatcherTimer _steamTimer = new() { Interval = TimeSpan.FromSeconds(8) };
        private readonly DispatcherTimer _downloadKick = new() { Interval = TimeSpan.FromMilliseconds(300) };
        private bool _downloadPollInProgress;
        private readonly Random _rnd = new();
        private DateTime _nextBlink = DateTime.Now.AddSeconds(3);
        private bool _oledHidden;
        private bool _fullscreenHidden;
        private bool _fullscreenPeek;
        private readonly DispatcherTimer _peekTimer = new();
        private readonly DispatcherTimer _topmost = new() { Interval = TimeSpan.FromSeconds(2) };
        private bool _pinned;
        private string _currentTab = "home";
        /// <summary>Вкладки нельзя строить, пока не созданы сервисы (RadioButton шлёт Checked прямо из InitializeComponent).</summary>
        private bool _ready;

        public IslandState State { get; private set; } = IslandState.Collapsed;
        /// <summary>Окно, которое было активным до того, как пользователь кликнул в остров (туда вставляем из буфера).</summary>
        public IntPtr PreviousForeground { get; private set; }
        public bool SuppressAutoCollapse { get; set; }

        public MainWindow()
        {
            InitializeComponent();
            _characters = new[] { Buddy, BuddyMedia, BuddyTimer };
            // пока видна подсказка — не поднимаем остров поверх неё (иначе она «уходит за чёлку»)
            AddHandler(ToolTipService.ToolTipOpeningEvent, new ToolTipEventHandler((_, __) => _tipUntil = DateTime.Now.AddSeconds(8)), true);
            AddHandler(ToolTipService.ToolTipClosingEvent, new ToolTipEventHandler((_, __) => _tipUntil = DateTime.MinValue), true);
            var s = AppSettings.Current;
            ScaleT.ScaleX = ScaleT.ScaleY = Math.Clamp(s.Scale, 0.7, 1.6);

            AppHost.Timer = new TimerService();
            AppHost.Shelf = new ShelfService();
            AppHost.Shelf.Load();
            AppHost.Clipboard = new ClipboardService();
            AppHost.Reminders = new ReminderService();
            AppHost.Reminders.Start();
            AppHost.Watchers = new SystemWatchers();
            AppHost.Media = new MediaService(Dispatcher);
            AppHost.Audio = new AudioService(Dispatcher);
            AppHost.Notifications = new NotificationService();
            AppHost.Voice = new VoiceService();
            AppHost.Spectrum = new SpectrumService();
            AlarmService.Stopped += () => Dispatcher.InvokeAsync(() =>
            {
                StopAlarmPulse();
                if (_currentToast?.Kind == ToastKind.Alarm) { _toastTimer.Stop(); NextToast(); }
            });
            // живой индикатор громкости голоса в чёлке, пока идёт диктовка
            AppHost.Voice.Level += lvl => Dispatcher.InvokeAsync(() =>
            {
                if (_currentToast?.ReplaceKey != "voice" || !AppHost.Voice.Recording) return;
                ToastBar.Visibility = Visibility.Visible;
                double barW = Math.Max(0, Island.ActualWidth - 16 * 2 - 36 - 12);
                ToastBarFill.Width = Math.Clamp(lvl * 2.2, 0.03, 1) * barW;
            });
            AppHost.Lyrics = new LyricsState(AppHost.Media);
            AppHost.Lyrics.Changed += () => { if (State == IslandState.Collapsed) ShowCollapsedContent(true); };
            Edge.Spectrum = () => AppHost.Spectrum.Running ? AppHost.Spectrum.Snapshot() : null;
            AppHost.Media.TrackChanged += m => Edge.SpikeColor = DominantColor(m.Cover) ?? AccentColor();
            AiService.BusyChanged += busy => Dispatcher.InvokeAsync(() => { _aiBusy = busy; if (!busy) { Buddy.Cheer(); Edge.DropBead(); } });
            if (!s.ScaleFixV2)
            {
                // прошлая версия сама ставила 130% на 4K — получалось слишком крупно и обрезалось. Возвращаем 100%.
                if (Math.Abs(s.Scale - 1.3) < 0.01) s.Scale = 1.0;
                s.FirstRunDone = true;
                s.ScaleFixV2 = true;
                s.Save();
            }
            ApplyScale();
            if (!s.VoiceDefaultsV2) { s.VoiceAutoPaste = true; s.VoiceDefaultsV2 = true; s.Save(); } // диктовка сразу вставляет текст, где стоит курсор
            AppHost.Vpn = new VpnService();
            AppHost.Vpn.Changed += () =>
            {
                VpnChip.Visibility = AppHost.Vpn.Active ? Visibility.Visible : Visibility.Collapsed;
                VpnText.Text = string.IsNullOrEmpty(AppHost.Vpn.Country) ? "VPN" : AppHost.Vpn.Country;
                VpnChip.ToolTip = AppHost.Vpn.Tooltip;
            };

            ToastBus.Raised += t => Dispatcher.InvokeAsync(() => EnqueueToast(t));
            AppHost.Media.Changed += () => { if (State == IslandState.Collapsed) ShowCollapsedContent(true); UpdateMini(); };
            AppHost.Timer.Changed += () =>
            {
                MiniTimer.Text = (AppHost.Timer.Paused ? "пауза · " : "") + TimerService.Format(AppHost.Timer.Left);
                MediaTimer.Text = (AppHost.Timer.Paused ? "❚❚ " : "") + TimerService.Format(AppHost.Timer.Left);
                if (State == IslandState.Collapsed) ShowCollapsedContent(true);
            };
            AppHost.Audio.VolumeChanged += (v, muted) =>
            {
                if (!AppSettings.Current.VolumeToasts) return;
                int pct = (int)Math.Round(v * 100);
                EnqueueToast(new Toast
                {
                    Kind = ToastKind.Volume, Title = muted ? "Звук выключен" : "Громкость " + pct, Icon = muted ? Icons.Mute : Icons.Volume,
                    Progress = muted ? 0 : v, ReplaceKey = "volume", Duration = TimeSpan.FromSeconds(1.3)
                });
            };
            AppHost.Watchers.LanguageChanged += () => LangChip.Text = AppHost.Watchers.LanguageCode;
            AppHost.Watchers.FullscreenChanged += fs => { _fullscreenHidden = fs && AppSettings.Current.HideInFullscreen; ApplyVisibility(); };
            AppHost.WeatherChanged += UpdateWeatherMini;

            _toastTimer.Tick += (_, __) => { _toastTimer.Stop(); NextToast(); };
            _clock.Tick += (_, __) => Clock();
            _ungrow.Tick += (_, __) =>
            {
                if (CursorNearIsland(10)) return; // ещё рядом — ждём
                _ungrow.Stop();
                _hoverGrown = false;
                if (State == IslandState.Collapsed) ShowCollapsedContent(false);
            };
            _hoverOpen.Tick += (_, __) => { _hoverOpen.Stop(); if (State != IslandState.Expanded && Island.IsMouseOver && !TimerRow.IsMouseOver) Expand(null); };
            _leaveClose.Tick += (_, __) =>
            {
                if (CursorNearIsland(14) || SuppressAutoCollapse || CursorOverOurPopup()) return; // ещё рядом или в нашем меню — ждём
                _leaveClose.Stop();
                if (!IsActive) Collapse();
            };
            _oledHide.Tick += (_, __) => { _oledHide.Stop(); TryOledHide(); };
            _weatherTimer.Tick += async (_, __) => await RefreshWeather();
            _steamTimer.Tick += (_, __) => RefreshSteamDownload();
            // Загрузки по событиям: Steam/браузер записал файл — проверяем через 0,3 с (несколько записей подряд склеиваются).
            // Таймер остаётся подстраховкой: раз в секунду, пока что-то качается (скорость, остаток), и раз в 8 с в простое.
            _downloadKick.Tick += (_, __) => { _downloadKick.Stop(); RefreshSteamDownload(); };
            // не перезапускаем таймер на каждое событие: браузер пишет файл непрерывно, и проверка иначе откладывалась бы бесконечно
            DownloadWatch.Changed += () => Dispatcher.InvokeAsync(() => { if (!_downloadKick.IsEnabled) _downloadKick.Start(); });

            Island.MouseEnter += (_, __) =>
            {
                _leaveClose.Stop();
                _oledHide.Stop();
                _ungrow.Stop();
                if (State == IslandState.Collapsed && !_hoverGrown) { _hoverGrown = true; AnimateSize(Island.Width + 16, Math.Max(38, Island.Height), 260); } // остров «тянется» к курсору
                bool miniPlayer = MediaRow.Visibility == Visibility.Visible && AppSettings.Current.MusicPill == "mini";
                // навёл на всплывашку — она не исчезает, пока курсор на ней (и не превращается в панель)
                if (State == IslandState.Toast) { _toastTimer.Stop(); return; }
                if (State != IslandState.Expanded && AppSettings.Current.ExpandOnHover && !miniPlayer && !TimerRow.IsMouseOver) _hoverOpen.Start();
            };
            Island.MouseLeave += (_, __) =>
            {
                _hoverOpen.Stop();
                // ушёл со всплывашки — она ещё немного побудет и закроется
                if (State == IslandState.Toast && _currentToast != null && !AlarmService.Ringing)
                { _toastTimer.Stop(); _toastTimer.Interval = TimeSpan.FromSeconds(2.5); _toastTimer.Start(); }
                if (State == IslandState.Expanded && !_pinned && !IsActive && !SuppressAutoCollapse) _leaveClose.Start();
                // не сжимаемся сразу: курсор на самой границе иначе дёргает остров туда-сюда
                if (State == IslandState.Collapsed && _hoverGrown) { _ungrow.Stop(); _ungrow.Start(); }
                if (State != IslandState.Expanded) ScheduleOledHide(TimeSpan.FromSeconds(1.2));
            };
            Island.PreviewMouseLeftButtonDown += OnIslandMouseDown;
            Island.MouseRightButtonUp += OnIslandRightClick;
            EdgeStrip.MouseEnter += (_, __) =>
            {
                if (_fullscreenHidden) PeekOverFullscreen();
                else OledReveal();
            };
            _peekTimer.Tick += (_, __) =>
            {
                if (Island.IsMouseOver || State == IslandState.Expanded) return; // пока пользуются — не прячем
                _peekTimer.Stop();
                _fullscreenPeek = false;
                ApplyVisibility();
            };
            // Другие программы (Fences, виджеты, плееры) тоже ставят себя «поверх всех» — возвращаем остров наверх.
            _topmost.Tick += (_, __) => KeepOnTop();
            Deactivated += (_, __) =>
            {
                MakeNoActivate(true);
                if (State == IslandState.Expanded && !_pinned && !SuppressAutoCollapse && !CursorNearIsland(14) && !CursorOverOurPopup()) Collapse();
            };
            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape && AlarmService.Ringing) { AlarmService.Stop(); e.Handled = true; return; }
                if (MatchesCollapseHotkey(e) && State == IslandState.Expanded) { Collapse(); e.Handled = true; }
            };
            // таймер в свёрнутой чёлке: правый клик — отменить, средний — пауза/продолжить
            TimerRow.MouseRightButtonUp += (_, e) =>
            {
                AppHost.Timer.Reset();
                ToastBus.Show(new Toast { Title = "Таймер отменён", Icon = Icons.Timer, Duration = TimeSpan.FromSeconds(2) });
                e.Handled = true;
            };
            TimerRow.MouseDown += (_, e) =>
            {
                if (e.ChangedButton != MouseButton.Middle) return;
                if (AppHost.Timer.Running) AppHost.Timer.Pause(); else AppHost.Timer.Start();
                e.Handled = true;
            };
            TimerRow.Background = Brushes.Transparent; // чтобы клик ловился по всей строке
            TimerRow.ToolTip = "Клик — открыть · кнопка × — отменить · средняя кнопка мыши — пауза";

            Island.SizeChanged += (_, __) =>
            {
                UpdateClip();
                Edge.Margin = new Thickness(24, Math.Max(0, Island.ActualHeight - 1), 24, 0);
                Edge.CornerRadius = Island.CornerRadius.BottomLeft;
            };
            // Подсказку «положу на полку» показываем только у свёрнутой чёлки: в открытой файл можно бросить на нужное место (ярлыки, полка)
            Island.DragEnter += (_, e) => { if (State != IslandState.Expanded && e.Data.GetDataPresent(DataFormats.FileDrop)) { DropHint.Visibility = Visibility.Visible; e.Effects = DragDropEffects.Link; } };
            Island.DragLeave += (_, __) => DropHint.Visibility = Visibility.Collapsed;
            Island.Drop += OnDrop;

            _ready = true;

            Loaded += async (_, __) =>
            {
                PlaceWindow();
                App.Trace("остров на экране: " + Left + "," + Top + ", экран " + SystemParameters.PrimaryScreenWidth + "×" + SystemParameters.PrimaryScreenHeight);
                Clock();
                _clock.Start();
                _topmost.Start();
                KeepOnTop();
                Helpers.FrameClock.MaxFps = AppSettings.Current.MaxFps;
                Helpers.FrameClock.Tick += dt => { _frameDt = dt; Animate(); }; // общий кадр с Клоди и краем — в такт монитору

                ApplyAppearance();
                AppHost.Watchers.Start();
                AppHost.Vpn.Start();
                await AppHost.Media.InitAsync();
                _ = AppHost.Notifications.StartAsync();
                _weatherTimer.Start();
                _steamTimer.Start();
                _ = Task.Run(DownloadWatch.Start);
                RefreshSteamDownload();
                await RefreshWeather();
                ApplyOledMode();
                UpdateService.StartAutoCheck();
                ObsService.StartMonitor();
                ObsService.Changed += () => Dispatcher.InvokeAsync(() =>
                {
                    RecChip.Visibility = ObsService.Recording || ObsService.Streaming ? Visibility.Visible : Visibility.Collapsed;
                    RecText.Text = ObsService.Streaming ? (ObsService.Recording ? "LIVE · REC" : "LIVE") : "REC";
                    if (State == IslandState.Collapsed) ShowCollapsedContent(false);
                });
                // экономим память: свёрнутому острову не нужно держать лишнее в RAM
                var trim = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
                trim.Tick += (_, __) =>
                {
                    if (State != IslandState.Collapsed) return;
                    try { Win32.SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, new IntPtr(-1), new IntPtr(-1)); } catch { }
                };
                trim.Start();
                App.Trace("все модули запущены");
                // заранее создаём вкладки в простое — первое открытие без рывка
                _ = Dispatcher.InvokeAsync(() => { foreach (var t in TabOrder) GetView(t); }, DispatcherPriority.ApplicationIdle);
            };
        }

        // ===================== Окно и Win32 =====================

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;
            _src = HwndSource.FromHwnd(_hwnd);
            var ex = Win32.GetWindowLong(_hwnd, Win32.GWL_EXSTYLE);
            // Без WS_EX_NOACTIVATE: активацию решаем сами в WM_MOUSEACTIVATE (клик в поле ввода — активируемся, иначе нет)
            Win32.SetWindowLong(_hwnd, Win32.GWL_EXSTYLE, (ex | Win32.WS_EX_TOOLWINDOW) & ~Win32.WS_EX_NOACTIVATE);
            _src.AddHook(WndProc);
            ApplyCaptureVisibility();
            AppHost.Clipboard.Attach(_src);

            ApplyHotkeys();
        }

        private sealed record HotkeySpec(int Id, string Name, string Value);

        private static List<HotkeySpec> HotkeySpecs(AppSettings s) => new()
        {
            new(HkToggle, "island", s.Hotkey), new(HkCommand, "command", s.HotkeyCommand),
            new(HkClipboard, "clipboard", s.HotkeyClipboard), new(HkShot, "screenshot", s.HotkeyScreenshot),
            new(HkVoice, "voice", s.HotkeyVoice), new(HkVoiceCmd, "voiceCommand", s.HotkeyVoiceCommand),
            new(0, "collapse", s.HotkeyCollapse)
        };

        private void ApplyHotkeys()
        {
            foreach (var id in _registeredHotkeys) Win32.UnregisterHotKey(_hwnd, id);
            _registeredHotkeys.Clear();
            foreach (var spec in HotkeySpecs(AppSettings.Current))
                if (spec.Id != 0 && TryParseHotkey(spec.Value, out uint modifiers, out uint key) && Win32.RegisterHotKey(_hwnd, spec.Id, modifiers | Win32.MOD_NOREPEAT, key))
                    _registeredHotkeys.Add(spec.Id);
            // PrintScreen без модификаторов — снимок через Isle (по желанию, в настройках клавиш)
            if (AppSettings.Current.PrintScreenIsle && Win32.RegisterHotKey(_hwnd, HkPrint, Win32.MOD_NOREPEAT, 0x2C))
                _registeredHotkeys.Add(HkPrint);
        }

        /// <summary>Включить/выключить перехват PrintScreen. false — Windows не дала занять клавишу.</summary>
        public bool SetPrintScreen(bool on)
        {
            AppSettings.Current.PrintScreenIsle = on;
            ApplyHotkeys();
            bool ok = !on || _registeredHotkeys.Contains(HkPrint);
            if (!ok) { AppSettings.Current.PrintScreenIsle = false; ApplyHotkeys(); }
            AppSettings.Current.Save();
            return ok;
        }

        public bool TrySetHotkey(string name, string gesture, out string error)
        {
            error = null;
            if (!TryParseHotkey(gesture, out uint mods, out uint key, name == "collapse"))
            { error = name == "collapse" ? "Выбери клавишу или сочетание клавиш." : "Выбери сочетание из клавиши и хотя бы одного модификатора (Ctrl, Alt, Shift или Win)."; return false; }
            var settings = AppSettings.Current;
            var bindings = HotkeySpecs(settings);
            foreach (var b in bindings.Where(b => b.Name != name))
                if (TryParseHotkey(b.Value, out uint otherMods, out uint otherKey, b.Name == "collapse") && mods == otherMods && key == otherKey)
                { error = "Это сочетание уже назначено: " + b.Value; return false; }

            var spec = bindings.FirstOrDefault(b => b.Name == name);
            if (spec == null) { error = "Неизвестная команда."; return false; }
            string old = spec.Value;
            SetHotkeyValue(settings, name, gesture);
            if (spec.Id == 0) { settings.Save(); return true; }
            ApplyHotkeys();
            if (!_registeredHotkeys.Contains(spec.Id))
            {
                SetHotkeyValue(settings, name, old);
                ApplyHotkeys();
                error = "Windows не смог зарегистрировать это сочетание. Возможно, его заняло другое приложение.";
                return false;
            }
            settings.Save();
            return true;
        }

        private static void SetHotkeyValue(AppSettings s, string name, string value)
        {
            switch (name)
            {
                case "island": s.Hotkey = value; break;
                case "command": s.HotkeyCommand = value; break;
                case "clipboard": s.HotkeyClipboard = value; break;
                case "screenshot": s.HotkeyScreenshot = value; break;
                case "voice": s.HotkeyVoice = value; break;
                case "voiceCommand": s.HotkeyVoiceCommand = value; break;
                case "collapse": s.HotkeyCollapse = value; break;
            }
        }

        private static bool TryParseHotkey(string gesture, out uint modifiers, out uint key, bool allowNoModifier = false)
        {
            modifiers = 0; key = 0;
            if (string.IsNullOrWhiteSpace(gesture)) return false;
            var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < (allowNoModifier ? 1 : 2)) return false;
            Key parsed = Key.None;
            for (int i = 0; i < parts.Length; i++)
            {
                switch (parts[i].ToLowerInvariant())
                {
                    case "ctrl": case "control": modifiers |= Win32.MOD_CONTROL; continue;
                    case "alt": modifiers |= Win32.MOD_ALT; continue;
                    case "shift": modifiers |= Win32.MOD_SHIFT; continue;
                    case "win": case "windows": modifiers |= Win32.MOD_WIN; continue;
                }
                if (parsed != Key.None || !Enum.TryParse(parts[i], true, out parsed) || parsed == Key.None || parsed == Key.System) return false;
            }
            if (parsed == Key.None || (!allowNoModifier && modifiers == 0)) return false;
            key = (uint)KeyInterop.VirtualKeyFromKey(parsed);
            return key is > 0 and <= 0xFF;
        }

        private static bool MatchesCollapseHotkey(KeyEventArgs e)
        {
            if (!TryParseHotkey(AppSettings.Current.HotkeyCollapse, out uint required, out uint key, true)) return false;
            var pressed = e.Key == Key.System ? e.SystemKey : e.Key;
            uint actual = 0;
            var modifiers = Keyboard.Modifiers;
            if (modifiers.HasFlag(ModifierKeys.Control)) actual |= Win32.MOD_CONTROL;
            if (modifiers.HasFlag(ModifierKeys.Alt)) actual |= Win32.MOD_ALT;
            if (modifiers.HasFlag(ModifierKeys.Shift)) actual |= Win32.MOD_SHIFT;
            if (modifiers.HasFlag(ModifierKeys.Windows)) actual |= Win32.MOD_WIN;
            return key == (uint)KeyInterop.VirtualKeyFromKey(pressed) && required == actual;
        }

        protected override void OnClosed(EventArgs e)
        {
            foreach (var id in _registeredHotkeys) Win32.UnregisterHotKey(_hwnd, id);
            AppHost.Audio?.Dispose();
            AppHost.Spectrum?.Dispose();
            base.OnClosed(e);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == 0x0021) // WM_MOUSEACTIVATE
            {
                handled = true;
                var fg = Win32.GetForegroundWindow();
                if (fg != _hwnd && fg != IntPtr.Zero) PreviousForeground = fg;
                // MA_ACTIVATE = 1, MA_NOACTIVATE = 3: окно не крадёт фокус, если кликнули не в поле ввода
                return new IntPtr(ClickIsOnTextInput() ? 1 : 3);
            }
            if (msg == Win32.WM_HOTKEY)
            {
                handled = true;
                switch (w.ToInt32())
                {
                    case HkToggle:
                        if (AlarmService.Ringing) { AlarmService.Stop(); break; } // Ctrl+Alt+Пробел выключает звонок
                        if (State == IslandState.Expanded) Collapse(); else { Expand(null); FocusIsland(); }
                        break;
                    case HkCommand:
                        Expand("home"); FocusIsland();
                        (GetView("home") as Views.HomeView)?.FocusCommand();
                        break;
                    case HkClipboard:
                        Expand("clip"); FocusIsland();
                        (GetView("clip") as Views.ClipboardView)?.FocusSearch();
                        break;
                    case HkVoice:
                        AppHost.Voice.Toggle();
                        break;
                    case HkVoiceCmd:
                        VoiceCommandKey();
                        break;
                    case HkShot:
                    case HkPrint:
                        _ = Tools.RegionShot();
                        break;
                }
            }
            return IntPtr.Zero;
        }

        /// <summary>Оставлено для совместимости: активацией теперь управляет WM_MOUSEACTIVATE.</summary>
        private void MakeNoActivate(bool on) { }

        private bool ClickIsOnTextInput()
        {
            try
            {
                if (!Win32.GetCursorPos(out var p)) return false;
                var pt = Root.PointFromScreen(new Point(p.X, p.Y));
                var hit = Root.InputHitTest(pt) as DependencyObject;
                return FindParent<System.Windows.Controls.Primitives.TextBoxBase>(hit) != null || FindParent<PasswordBox>(hit) != null;
            }
            catch { return false; }
        }

        /// <summary>Даём окну фокус клавиатуры (нужно, чтобы печатать в полях).</summary>
        public void FocusIsland()
        {
            var fg = Win32.GetForegroundWindow();
            if (fg != _hwnd && fg != IntPtr.Zero) PreviousForeground = fg;
            MakeNoActivate(false);
            Activate();
            Win32.ForceForeground(_hwnd); // без этого Windows часто не даёт печатать в остров
        }

        private void PlaceWindow()
        {
            Left = SystemParameters.PrimaryScreenWidth / 2 - Width / 2;
            Top = 0;
        }

        private void OnIslandMouseDown(object sender, MouseButtonEventArgs e)
        {
            // клик по уведомлению другого приложения — открываем это приложение
            if (State == IslandState.Toast && !AlarmService.Ringing && _currentToast?.LaunchAppId is string appId
                && FindParent<Button>(e.OriginalSource as DependencyObject) == null)
            {
                NotificationService.LaunchApp(appId);
                _toastTimer.Stop();
                NextToast();
                e.Handled = true;
                return;
            }
            if (AlarmService.Ringing && State != IslandState.Expanded)
            {
                AlarmService.Stop(); // клик по звенящей чёлке (в любом виде) — выключить будильник
                e.Handled = true;
                return;
            }
            if (State != IslandState.Expanded)
            {
                if (FindParent<Button>(e.OriginalSource as DependencyObject) != null) return; // кнопки мини-плеера
                Expand(null);
                e.Handled = true;
                return;
            }
            if (!IsActive)
            {
                // Фокус берём только если кликнули в поле ввода — иначе кнопки работают без перехвата фокуса.
                var d = e.OriginalSource as DependencyObject;
                Control input = (Control)FindParent<System.Windows.Controls.Primitives.TextBoxBase>(d) ?? FindParent<PasswordBox>(d);
                if (input != null)
                {
                    FocusIsland();
                    Dispatcher.InvokeAsync(() => input.Focus(), DispatcherPriority.Input);
                }
            }
        }

        private void OnIslandRightClick(object sender, MouseButtonEventArgs e)
        {
            if (State == IslandState.Expanded) return;
            var menu = new ContextMenu();
            var open = new MenuItem { Header = "Открыть" };
            open.Click += (_, __) => Expand(null);
            var settings = new MenuItem { Header = "Настройки" };
            settings.Click += (_, __) => OpenSettings();
            var exit = new MenuItem { Header = "Выйти из Isle" };
            exit.Click += (_, __) => Application.Current.Shutdown();
            menu.Items.Add(open);
            menu.Items.Add(settings);
            menu.Items.Add(new Separator());
            menu.Items.Add(exit);
            menu.IsOpen = true;
            e.Handled = true;
        }

        private static T FindParent<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null)
            {
                if (d is T t) return t;
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }

        // ===================== Состояния =====================

        public void Expand(string tab)
        {
            if (tab == "settings") { OpenSettings(); return; }
            _hoverOpen.Stop();
            _leaveClose.Stop();
            _oledHide.Stop();
            if (_oledHidden) OledReveal(false);
            var fg = Win32.GetForegroundWindow();
            if (fg != _hwnd && fg != IntPtr.Zero) PreviousForeground = fg;

            if (tab != null) SelectTab(tab);
            else if (TabHost.Content == null) SelectTab(_currentTab);

            if (State == IslandState.Expanded) return;
            _toastTimer.Stop();
            State = IslandState.Expanded;
            CollapsedView.Visibility = Visibility.Collapsed;
            ExpandedView.Visibility = Visibility.Visible;
            Island.CornerRadius = new CornerRadius(0, 0, 30, 30);
            DropHint.CornerRadius = IslandShadow.CornerRadius = Island.CornerRadius; // тень с тем же скруглением, иначе её угол торчит из-под острова
            _collapseVersion++;
            MoveTabIndicator(TabButton(_currentTab), false);
            AnimateSize(ExpandedSize.Width, ExpandedSize.Height, 620, spring: true);
            // содержимое проявляется из лёгкого размытия и чуть «подрастает»
            var st = new ScaleTransform(0.95, 0.95);
            ExpandedView.RenderTransformOrigin = new Point(0.5, 0);
            ExpandedView.RenderTransform = st;
            var blur = new System.Windows.Media.Effects.BlurEffect { Radius = 14, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance };
            ExpandedView.Effect = blur;
            var dur = TimeSpan.FromMilliseconds(340);
            var begin = TimeSpan.FromMilliseconds(90);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, dur) { BeginTime = begin, EasingFunction = ease });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, dur) { BeginTime = begin, EasingFunction = ease });
            var unblur = new DoubleAnimation(0, dur) { BeginTime = begin, EasingFunction = ease };
            unblur.Completed += (_, __) => { if (State == IslandState.Expanded) ExpandedView.Effect = null; }; // без эффекта рисуется быстрее
            blur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, unblur);
            ExpandedView.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { BeginTime = begin, EasingFunction = ease });
            (TabHost.Content as Views.IIslandView)?.OnShown();
        }

        public void Collapse()
        {
            if (State == IslandState.Collapsed) return;
            _leaveClose.Stop();
            if (FocusManager.GetFocusedElement(this) is IInputElement) Keyboard.ClearFocus();
            MakeNoActivate(true);
            State = IslandState.Collapsed;
            (_views.TryGetValue("home", out var hv) ? hv as Views.HomeView : null)?.HideCommand();
            // сначала гаснет содержимое, потом остров сжимается
            int version = ++_collapseVersion;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            fade.Completed += (_, __) =>
            {
                if (version != _collapseVersion || State == IslandState.Expanded) return;
                ExpandedView.Visibility = Visibility.Collapsed;
                ExpandedView.Effect = null;
                Island.CornerRadius = new CornerRadius(0, 0, 18, 18);
                DropHint.CornerRadius = IslandShadow.CornerRadius = Island.CornerRadius; // тень с тем же скруглением, иначе её угол торчит из-под острова
                // Expand() останавливает таймер тоста. Верни текущую плашку загрузки
                // после сворачивания, чтобы пользователь не терял её при выходе из меню.
                if (_currentToast?.Kind == ToastKind.Download)
                {
                    var downloadToast = _currentToast;
                    _currentToast = null;
                    if (!_toasts.Any(t => t.ReplaceKey == downloadToast.ReplaceKey)) _toasts.Enqueue(downloadToast);
                }
                if (_toasts.Count > 0) { NextToast(); return; }
                ShowCollapsedContent(false);
                ScheduleOledHide(TimeSpan.FromSeconds(1.2));
            };
            ExpandedView.BeginAnimation(OpacityProperty, fade);
        }

        private void ShowCollapsedContent(bool onlyIfChanged)
        {
            if (State != IslandState.Collapsed) return;
            CollapsedView.Visibility = Visibility.Visible;
            ToastRow.Visibility = Visibility.Collapsed;
            var m = AppHost.Media?.Current;
            bool timerRunning = AppHost.Timer != null && AppHost.Timer.Running;
            bool timerPaused = AppHost.Timer != null && AppHost.Timer.Paused;
            bool media = AppSettings.Current.MediaEnabled && m != null && m.HasSession && m.IsPlaying && !string.IsNullOrEmpty(m.Title);
            // музыка важнее: таймер тогда показывается маленьким рядом с треком
            bool timer = !media && (timerRunning || timerPaused);
            MediaTimer.Visibility = media && (timerRunning || timerPaused) ? Visibility.Visible : Visibility.Collapsed;
            // загрузка: своя пилюля, а под музыку — маленькая метка «↓ 45%» рядом с треком
            bool dl = _download != null && !media && !timer;
            MediaDownload.Visibility = _download != null && media ? Visibility.Visible : Visibility.Collapsed;

            TimerRow.Visibility = timer ? Visibility.Visible : Visibility.Collapsed;
            MediaRow.Visibility = media ? Visibility.Visible : Visibility.Collapsed;
            DownloadRow.Visibility = dl ? Visibility.Visible : Visibility.Collapsed;
            IdleRow.Visibility = !timer && !media && !dl ? Visibility.Visible : Visibility.Collapsed;
            RevealRow(timer ? TimerRow : media ? MediaRow : dl ? DownloadRow : (UIElement)IdleRow);

            double h = 34;
            double w = timer ? 240 : (string.IsNullOrEmpty(WeatherMini.Text) ? 170 : 210) + (BatteryMini.Visibility == Visibility.Visible ? 52 : 0) + (RecChip.Visibility == Visibility.Visible ? 60 : 0);
            if (!AppSettings.Current.ShowClock && !timer && !media) w -= 50;
            else if (AppSettings.Current.ClockSeconds && !timer && !media) w += 24;
            if (media) (w, h) = ApplyPillMode();
            if (MediaTimer.Visibility == Visibility.Visible) w += 60;
            if (MediaDownload.Visibility == Visibility.Visible) w += 54;
            if (dl) { w = 330; h = 46; }
            if (Buddy.Visibility == Visibility.Visible && (media || timer)) w += 36; // место для Клоди
            if (_hoverGrown) { w += 16; h = Math.Max(38, h); }
            // сравниваем с целью анимации, а не с текущим (ещё анимируемым) размером: таймер дёргает это 4 раза в секунду,
            // и раньше «желе» перезапускалось каждые 250 мс — чёлка дрожала, пока шёл таймер
            if (onlyIfChanged && Math.Abs(_sizeTarget.Width - w) < 1 && Math.Abs(_sizeTarget.Height - h) < 1) return;
            AnimateSize(w, h, 380);
        }

        /// <summary>Вид пилюли во время музыки (Настройки → Музыка).</summary>
        private (double w, double h) ApplyPillMode()
        {
            var mode = AppSettings.Current.MusicPill;
            bool hasLyrics = AppSettings.Current.LyricsEnabled && AppHost.Lyrics?.Lines != null && AppHost.Lyrics.Lines.Count > 0;
            MiniButtons.Visibility = mode == "mini" ? Visibility.Visible : Visibility.Collapsed;
            MiniSub.Visibility = mode == "mini" ? Visibility.Visible : Visibility.Collapsed;
            MiniBar.Visibility = mode == "title" || mode == "mini" ? Visibility.Visible : Visibility.Collapsed;
            MiniCenter.Visibility = mode == "bars" ? Visibility.Collapsed : Visibility.Visible;
            MiniCoverBox.Width = MiniCoverBox.Height = mode == "mini" ? 44 : 22;
            MiniCoverBox.CornerRadius = new CornerRadius(mode == "mini" ? 10 : 6);
            UpdateMini();
            return mode switch
            {
                "bars" => (170, 34),
                "mini" => (440, 64),
                "lyrics" => (hasLyrics ? 440 : 340, 36),
                _ => (340, 42)
            };
        }

        private void UpdateMiniLive()
        {
            if (MediaRow.Visibility != Visibility.Visible) return;
            var m = AppHost.Media.Current;
            if (MiniBar.Visibility == Visibility.Visible && m.Duration > TimeSpan.Zero)
                MiniBarFill.Width = Math.Max(0, MiniBar.ActualWidth * Math.Clamp(m.LivePosition.TotalSeconds / m.Duration.TotalSeconds, 0, 1));
            if (AppSettings.Current.MusicPill == "lyrics")
            {
                var line = AppHost.Lyrics?.LineAt(m.LivePosition);
                var text = string.IsNullOrEmpty(line) ? (string.IsNullOrEmpty(m.Artist) ? m.Title : m.Title + " — " + m.Artist) : line;
                if (MiniTitle.Text != text)
                {
                    MiniTitle.Text = text;
                    MiniTitle.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(260)));
                }
            }
            MiniPlayIcon.Kind = m.IsPlaying ? "Pause" : "Play";
        }

        private void OnMiniPrev(object s, RoutedEventArgs e) => AppHost.Media.Previous();
        private void OnMiniNext(object s, RoutedEventArgs e) => AppHost.Media.Next();
        private void OnMiniPlay(object s, RoutedEventArgs e) => AppHost.Media.PlayPause();

        private void UpdateMini()
        {
            var m = AppHost.Media.Current;
            var mode = AppSettings.Current.MusicPill;
            if (mode == "mini") { MiniTitle.Text = m.Title; MiniSub.Text = m.Artist; }
            else if (mode != "lyrics") MiniTitle.Text = string.IsNullOrEmpty(m.Artist) ? m.Title : m.Title + " — " + m.Artist;
            MiniCover.Source = m.Cover;
        }

        private int _collapseVersion;
        private int _slideDir = 1;
        private UIElement _lastRow;

        private Size _sizeTarget = new(210, 34);

        private void AnimateSize(double w, double h, int ms, bool spring = false)
        {
            _sizeTarget = new Size(w, h);
            // раскрытие — пружиной, остальное — мягким «желе»
            IEasingFunction ease = spring ? new Helpers.Spring { Damping = 10, Frequency = 10 }
                : new Helpers.Spring { Damping = 9, Frequency = 9 };
            Island.BeginAnimation(WidthProperty, new DoubleAnimation(w, TimeSpan.FromMilliseconds(spring ? ms : Math.Max(ms, 460))) { EasingFunction = ease });
            Island.BeginAnimation(HeightProperty, new DoubleAnimation(h, TimeSpan.FromMilliseconds(spring ? ms : Math.Max(ms, 460))) { EasingFunction = ease });
        }

        /// <summary>Новое состояние свёрнутой чёлки проявляется и чуть съезжает на место.</summary>
        private void RevealRow(UIElement row)
        {
            if (row == null || ReferenceEquals(row, _lastRow)) return;
            _lastRow = row;
            var tt = new TranslateTransform(0, 5);
            row.RenderTransform = tt;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease });
            row.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { BeginTime = TimeSpan.FromMilliseconds(60) });
        }

        private static void Fade(UIElement el, double to, int ms, int delayMs = 0)
        {
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { BeginTime = TimeSpan.FromMilliseconds(delayMs) });
        }

        // ===================== Вкладки =====================

        private void OnTab(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            if (sender == TabHome) SelectTab("home");
            else if (sender == TabClip) SelectTab("clip");
            else if (sender == TabShelf) SelectTab("shelf");
            else if (sender == TabText) SelectTab("text");
            else if (sender == TabChat) SelectTab("chat");
            else if (sender == TabNotes) SelectTab("notes");
            else if (sender == TabWeather) SelectTab("weather");
            else if (sender == TabControls) SelectTab("controls");
        }

        public FrameworkElement GetView(string tab)
        {
            if (_views.TryGetValue(tab, out var v)) return v;
            v = tab switch
            {
                "clip" => new Views.ClipboardView(),
                "shelf" => new Views.ShelfView(),
                "text" => new Views.TextView(),
                "chat" => new Views.ChatView(),
                "notes" => new Views.NotesView(),
                "weather" => new Views.WeatherView(),
                "controls" => new Views.ControlsView(),
                _ => new Views.HomeView()
            };
            _views[tab] = v;
            return v;
        }

        private static readonly string[] TabOrder = { "home", "clip", "shelf", "text", "chat", "notes", "weather", "controls" };

        /// <summary>Подсветка плавно переезжает под выбранную вкладку и подстраивается по ширине.</summary>
        private void MoveTabIndicator(RadioButton rb, bool animate)
        {
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var x = rb.TranslatePoint(new Point(0, 0), TabBar).X;
                    double w = rb.ActualWidth - 2; // у вкладок справа отступ 2
                    var dur = TimeSpan.FromMilliseconds(animate ? 380 : 0);
                    var ease = new Helpers.Spring { Damping = 9, Frequency = 9 };
                    TabIndicatorT.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, dur) { EasingFunction = ease });
                    TabIndicator.BeginAnimation(WidthProperty, new DoubleAnimation(Math.Max(30, w), dur) { EasingFunction = ease });
                }
                catch { }
            }, DispatcherPriority.Loaded);
        }

        private RadioButton TabButton(string tab) => tab switch
        {
            "clip" => TabClip, "shelf" => TabShelf, "text" => TabText, "chat" => TabChat, "notes" => TabNotes,
            "weather" => TabWeather, "controls" => TabControls, _ => TabHome
        };

        public void SelectTab(string tab)
        {
            int from = Array.IndexOf(TabOrder, _currentTab), to = Array.IndexOf(TabOrder, tab);
            _slideDir = to >= from ? 1 : -1;
            _currentTab = tab;
            var rb = TabButton(tab);
            if (rb.IsChecked != true) rb.IsChecked = true; // вызовет OnTab ещё раз — это безопасно
            MoveTabIndicator(rb, State == IslandState.Expanded);
            var view = GetView(tab);
            if (!ReferenceEquals(TabHost.Content, view))
            {
                TabHost.Content = view;
                view.Opacity = 0;
                Fade(view, 1, 200);
                // содержимое въезжает со стороны выбранной вкладки
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                TabSlide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(26 * _slideDir, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
                TabSlide.BeginAnimation(TranslateTransform.YProperty, null);
                if (State == IslandState.Expanded) (view as Views.IIslandView)?.OnShown();
            }
        }

        private void OnPin(object sender, RoutedEventArgs e) => _pinned = PinChip.IsChecked == true;

        private Views.SettingsWindow _settings;

        public void OpenSettings(string section = null)
        {
            Collapse();
            if (_settings == null || !_settings.IsLoaded)
            {
                _settings = new Views.SettingsWindow();
                _settings.Closed += (_, __) => _settings = null;
                _settings.Show();
            }
            if (section != null) _settings.ShowSection(section);
            if (_settings.WindowState == WindowState.Minimized) _settings.WindowState = WindowState.Normal;
            _settings.Activate();
            Win32.ForceForeground(new WindowInteropHelper(_settings).Handle);
        }

        private void OnSettingsClick(object sender, RoutedEventArgs e) => OpenSettings();

        // ===================== Голосовая команда ИИ (Ctrl+Alt+A) =====================

        private readonly DispatcherTimer _voiceKeyPoll = new() { Interval = TimeSpan.FromMilliseconds(50) };
        private DateTime _voiceKeyDown;

        /// <summary>
        /// Зажал Ctrl+Alt+A — слушаем, отпустил — ИИ выполняет сказанное.
        /// Если просто коротко нажать — запись идёт до следующего нажатия.
        /// </summary>
        private void VoiceCommandKey()
        {
            if (AppHost.Voice.Recording)
            {
                // второе нажатие в режиме «нажал-нажал»
                _voiceKeyPoll.Stop();
                _ = AppHost.Voice.StopAsync(t => _ = RunVoiceCommand(t));
                return;
            }
            AppHost.Voice.Start("Говори команду и отпусти клавиши · например: «открой телеграм»");
            if (!AppHost.Voice.Recording) return;
            _voiceKeyDown = DateTime.Now;
            if (_voiceKeyPoll.Tag == null)
            {
                _voiceKeyPoll.Tag = true;
                _voiceKeyPoll.Tick += (_, __) =>
                {
                    if (!AppHost.Voice.Recording) { _voiceKeyPoll.Stop(); return; }
                    // основная клавиша сочетания (по умолчанию A) ещё зажата — сочетание можно переназначить
                    uint vk = TryParseHotkey(AppSettings.Current.HotkeyVoiceCommand, out uint vkMods, out uint k) ? k : 0x41;
                    bool held = (Win32.GetAsyncKeyState((int)vk) & 0x8000) != 0;
                    if (held) return;
                    _voiceKeyPoll.Stop();
                    if (DateTime.Now - _voiceKeyDown < TimeSpan.FromMilliseconds(400)) return; // короткое нажатие — запись идёт до второго нажатия
                    _ = AppHost.Voice.StopAsync(t => _ = RunVoiceCommand(t));
                };
            }
            _voiceKeyPoll.Start();
        }

        private async Task RunVoiceCommand(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            ToastBus.Show(new Toast { Title = "«" + text + "»", Subtitle = "Думаю…", Icon = Icons.Chat, ReplaceKey = "voice", Important = true, Duration = TimeSpan.FromSeconds(30) });
            var history = new List<ChatMessage>
            {
                new() { Role = "user", Text = text + "\n\n(Это голосовая команда: если просят что-то сделать — выполни командой, ответ дай одной короткой фразой.)" }
            };
            string answer;
            try { answer = await AiService.AskAsync(history); }
            catch (Exception ex) { answer = "Ошибка: " + ex.Message; }
            var (clean, actions) = AiActions.Extract(answer ?? "");
            var reports = new List<string>();
            var confirm = new List<AiAction>();
            foreach (var a in actions)
            {
                if (a.NeedsConfirm) confirm.Add(a);
                else reports.Add(AiActions.Run(a));
            }
            bool failed = (answer ?? "").StartsWith("Ошибка") || (answer ?? "").StartsWith("Нужен API-ключ") || reports.Any(r => r.StartsWith("✗"));
            var sub = reports.Count > 0 ? string.Join(" · ", reports) : clean;
            if (sub.Length > 160) sub = sub.Substring(0, 160) + "…";
            ToastBus.Show(new Toast
            {
                Kind = failed ? ToastKind.Info : ToastKind.Success,
                Title = reports.Count > 0 && clean.Length > 0 && clean.Length <= 80 ? clean : "«" + text + "»",
                Subtitle = sub, Icon = failed ? Icons.Info : Icons.Check, ReplaceKey = "voice", Important = true,
                Duration = TimeSpan.FromSeconds(Math.Clamp(2 + sub.Length / 25.0, 3, 9))
            });
            if (confirm.Count > 0)
            {
                // опасное (консоль, сон, блокировка) — только по кнопке: открываем чат с подтверждением
                Expand("chat");
                (GetView("chat") as Views.ChatView)?.AddExchange(text, clean, confirm);
            }
        }

        private async void OnLangClick(object sender, RoutedEventArgs e)
        {
            var target = Win32.GetForegroundWindow();
            if (target == _hwnd || target == IntPtr.Zero) target = PreviousForeground;
            if (target == IntPtr.Zero) return;
            // Переключаем так же, как это делает сам пользователь: возвращаем фокус в его окно и жмём Win+Пробел.
            // Просьба окну (WM_INPUTLANGCHANGEREQUEST) в Windows 10/11 часто игнорируется — поэтому только системный способ.
            Win32.ForceForeground(target);
            await Task.Delay(90);
            Win32.PressWinSpace();
            await Task.Delay(250);
            var code = Win32.LangCode(Win32.LayoutOf(Win32.FocusWindowOf(target)));
            if (code != null) LangChip.Text = code;
        }

        private async void OnVpnClick(object sender, RoutedEventArgs e)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                VpnText.Text = "…";
                var msg = await AppHost.Vpn.DisconnectAsync();
                ToastBus.Show(new Toast { Title = msg, Icon = Icons.Shield, Important = true, Duration = TimeSpan.FromSeconds(3) });
                return;
            }
            Collapse();
            var err = AppHost.Vpn.OpenClient();
            if (err != null) ToastBus.Show(new Toast { Title = err, Icon = Icons.Shield, Important = true, Duration = TimeSpan.FromSeconds(3) });
        }

        private void OnVpnRightClick(object sender, MouseButtonEventArgs e)
        {
            AppHost.Vpn.Recheck();
            e.Handled = true;
        }

        /// <summary>Обрезка по настоящей форме острова (скруглённые нижние углы).</summary>
        private void UpdateClip()
        {
            double w = Island.ActualWidth, h = Island.ActualHeight, r = Math.Min(Island.CornerRadius.BottomLeft, Math.Min(w, h) / 2);
            if (w <= 0 || h <= 0) return;
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(0, 0), true, true);
                c.LineTo(new Point(w, 0), false, false);
                c.LineTo(new Point(w, h - r), false, false);
                c.ArcTo(new Point(w - r, h), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
                c.LineTo(new Point(r, h), false, false);
                c.ArcTo(new Point(0, h - r), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
            }
            g.Freeze();
            Island.Clip = g;

            // контур по краю — на полпикселя внутрь, чтобы линия не резалась обрезкой и шла гладко
            const double i = 0.5;
            var o = new StreamGeometry();
            using (var c = o.Open())
            {
                c.BeginFigure(new Point(i, 0), false, false);
                c.LineTo(new Point(i, h - r), true, true);
                c.ArcTo(new Point(r, h - i), new Size(Math.Max(0, r - i), Math.Max(0, r - i)), 0, false, SweepDirection.Counterclockwise, true, true);
                c.LineTo(new Point(w - r, h - i), true, true);
                c.ArcTo(new Point(w - i, h - r), new Size(Math.Max(0, r - i), Math.Max(0, r - i)), 0, false, SweepDirection.Counterclockwise, true, true);
                c.LineTo(new Point(w - i, 0), true, true);
            }
            o.Freeze();
            IslandOutline.Data = o;
        }

        /// <summary>Настройки музыки поменялись — перестроить пилюлю и плеер.</summary>
        public void ApplyMusicSettings()
        {
            if (_views.TryGetValue("home", out var hv)) (hv as Views.HomeView)?.ApplyPlayerStyle();
            Clock();
            if (State == IslandState.Collapsed) ShowCollapsedContent(false);
        }

        /// <summary>Акцент, прозрачность и персонаж из настроек.</summary>
        public void ApplyAppearance()
        {
            var s = AppSettings.Current;
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(s.AccentHex);
                Application.Current.Resources["Accent"] = new SolidColorBrush(c); // все {DynamicResource Accent} обновятся сразу
                foreach (var bar in new[] { Bar0, Bar1, Bar2, Bar3, Bar4, Bar5 }) bar.Background = new SolidColorBrush(c);
            }
            catch { }
            byte a = (byte)Math.Round(255 * Math.Clamp(s.IslandOpacity, 0.5, 1.0));
            Island.Background = new SolidColorBrush(Color.FromArgb(a, 0, 0, 0));
            Edge.Fill = Island.Background;
            IslandShadow.Opacity = s.IslandOpacity;
            Buddy.Visibility = s.Character == "pixel" ? Visibility.Visible : Visibility.Collapsed;
            BuddyMedia.Visibility = BuddyTimer.Visibility = Buddy.Visibility; // Клоди и во время музыки, и с таймером
            Eyes.Visibility = s.Character == "eyes" ? Visibility.Visible : Visibility.Collapsed;
            ApplyScale();
            if (State == IslandState.Collapsed) ShowCollapsedContent(false);
        }

        private bool _aiBusy;
        private bool _hoverGrown;
        private readonly DispatcherTimer _ungrow = new() { Interval = TimeSpan.FromMilliseconds(260) };
        /// <summary>Остров был спрятан (OLED) до всплывашки — после неё прячемся снова, не показывая музыку и Клоди.</summary>
        private bool _hideAfterToast;

        /// <summary>Курсор над нашим выпадающим списком, меню или окном настроек.</summary>
        private bool CursorOverOurPopup()
        {
            if (!Win32.GetCursorPos(out var p)) return false;
            var h = WindowFromPoint(p);
            if (h == IntPtr.Zero || h == _hwnd) return false;
            Win32.GetWindowThreadProcessId(h, out uint pid);
            return pid == (uint)Environment.ProcessId;
        }

        /// <summary>Курсор у острова с запасом в несколько пикселей (чтобы край не «дрожал»).</summary>
        private bool CursorNearIsland(double margin)
        {
            if (!Win32.GetCursorPos(out var p)) return false;
            try
            {
                var tl = Island.PointToScreen(new Point(-margin, 0));
                var br = Island.PointToScreen(new Point(Island.ActualWidth + margin, Island.ActualHeight + margin));
                return p.X >= tl.X && p.X <= br.X && p.Y >= tl.Y - 2 && p.Y <= br.Y;
            }
            catch { return false; }
        }

        /// <summary>Клоди одевается по погоде за окном.</summary>
        public static Helpers.Outfit OutfitForWeather()
        {
            var w = AppHost.Weather;
            if (w == null) return Helpers.Outfit.None;
            bool rain = w.Code >= 51 && w.Code <= 67 || w.Code >= 80 && w.Code <= 82 || w.Code >= 95;
            if (rain) return Helpers.Outfit.Rain;
            if (w.Temp <= 3 || w.Code >= 71 && w.Code <= 77 || w.Code == 85 || w.Code == 86) return Helpers.Outfit.Winter;
            if (w.Temp >= 25) return Helpers.Outfit.Summer;
            if (w.Temp >= 15 && w.Code <= 2) return Helpers.Outfit.Sunny; // тепло и ясно — кепка
            return Helpers.Outfit.None;
        }
        private bool _pulsing;

        /// <summary>Будильник: чёлка переливается красным и серым и чуть «дышит».</summary>
        private void StartAlarmPulse()
        {
            if (_pulsing) return;
            _pulsing = true;
            var brush = new SolidColorBrush(Color.FromRgb(0x5A, 0x14, 0x18));
            Island.Background = brush;
            Edge.Fill = brush;
            brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Color.FromRgb(0x5A, 0x14, 0x18), Color.FromRgb(0x2A, 0x2A, 0x30), TimeSpan.FromMilliseconds(520))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });
            ToastIcon.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.3, TimeSpan.FromMilliseconds(260)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            foreach (var b in _characters) b.Mood = Helpers.Mood.Attention;
        }

        private void StopAlarmPulse()
        {
            if (!_pulsing) return;
            _pulsing = false;
            ToastIcon.BeginAnimation(OpacityProperty, null);
            ToastIcon.Opacity = 1;
            ApplyAppearance(); // вернуть обычный чёрный фон
        }
        private DateTime _workingUntil = DateTime.MinValue, _nextWork = DateTime.Now.AddSeconds(40), _nextClaudeCheck;
        private bool _claudeRunning;

        /// <summary>
        /// Клоди иногда садится за ноутбук «писать код» — секунд на 25, раз в несколько минут.
        /// Если запущен Claude Code, работает заметно чаще.
        /// </summary>
        private void UpdateWorking(TimeSpan idle)
        {
            if (DateTime.Now >= _nextClaudeCheck)
            {
                _nextClaudeCheck = DateTime.Now.AddSeconds(15);
                // перебор процессов занимает заметное время — в фоне, чтобы чёлка не дёргалась раз в 15 с
                _ = Task.Run(() =>
                {
                    try
                    {
                        var procs = System.Diagnostics.Process.GetProcessesByName("claude");
                        _claudeRunning = procs.Length > 0;
                        foreach (var p in procs) p.Dispose();
                    }
                    catch { }
                });
            }
            if (DateTime.Now >= _nextWork && idle < TimeSpan.FromMinutes(4))
            {
                _workingUntil = DateTime.Now.AddSeconds(_claudeRunning ? 45 : 25);
                _nextWork = _workingUntil.AddSeconds(_claudeRunning ? 30 + _rnd.Next(60) : 120 + _rnd.Next(180));
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(Win32.POINT p);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

        private static TimeSpan IdleTime()
        {
            var li = new LASTINPUTINFO { cbSize = 8 };
            if (!GetLastInputInfo(ref li)) return TimeSpan.Zero;
            return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - li.dwTime));
        }

        /// <summary>Курсор над островом или над его всплывающими окнами (списки, меню)?</summary>
        private bool CursorOverIsland()
        {
            if (!Win32.GetCursorPos(out var p)) return true;
            try
            {
                var tl = Island.PointToScreen(new Point(0, 0));
                var br = Island.PointToScreen(new Point(Island.ActualWidth, Island.ActualHeight));
                if (p.X >= tl.X && p.X <= br.X && p.Y >= tl.Y - 2 && p.Y <= br.Y) return true;
            }
            catch { return true; }
            var h = WindowFromPoint(p);
            if (h != IntPtr.Zero && h != _hwnd)
            {
                Win32.GetWindowThreadProcessId(h, out uint pid);
                if (pid == (uint)Environment.ProcessId) return true; // наш выпадающий список или меню
            }
            return false;
        }

        private bool _mouseWasDown;

        /// <summary>Клик мимо открытого острова — сворачиваем (и прячем в OLED-режиме).</summary>
        private void CheckOutsideClick()
        {
            bool down = (Win32.GetAsyncKeyState(0x01) & 0x8000) != 0 || (Win32.GetAsyncKeyState(0x02) & 0x8000) != 0;
            if (down && !_mouseWasDown && State == IslandState.Expanded && !_pinned && !SuppressAutoCollapse && !CursorOverIsland())
                Collapse();
            _mouseWasDown = down;
        }

        private void OnDnd(object sender, RoutedEventArgs e)
        {
            AppSettings.Current.DoNotDisturb = DndChip.IsChecked == true;
            AppSettings.Current.Save();
        }

        // ===================== Всплывашки =====================

        private void EnqueueToast(Toast t)
        {
            if (t.Kind == ToastKind.Alarm && State == IslandState.Expanded && !_pinned) Collapse(); // будильник важнее открытой панели
            if (State == IslandState.Expanded && !t.Important) return;
            if (_currentToast?.Kind == ToastKind.Download && t.Kind != ToastKind.Download)
            {
                if (t.ReplaceKey != null)
                {
                    var queued = _toasts.Where(x => x.ReplaceKey != t.ReplaceKey).ToList();
                    _toasts.Clear();
                    foreach (var x in queued) _toasts.Enqueue(x);
                }
                _toasts.Enqueue(t);
                _toastTimer.Stop();
                NextToast();
                return;
            }
            if (t.ReplaceKey != null && _currentToast?.ReplaceKey == t.ReplaceKey)
            {
                RenderToast(t);
                _toastTimer.Stop();
                _toastTimer.Interval = t.Duration;
                _toastTimer.Start();
                return;
            }
            if (t.ReplaceKey != null)
            {
                var rest = _toasts.Where(x => x.ReplaceKey != t.ReplaceKey).ToList();
                _toasts.Clear();
                foreach (var x in rest) _toasts.Enqueue(x);
            }
            _toasts.Enqueue(t);
            if (State == IslandState.Collapsed) NextToast();
        }

        private void NextToast()
        {
            if (State == IslandState.Expanded) { _currentToast = null; return; }
            if (_toasts.Count == 0)
            {
                if (_currentToast?.Kind == ToastKind.Alarm) AlarmService.Stop();
                StopAlarmPulse();
                _currentToast = null;
                State = IslandState.Collapsed;
                if (_hideAfterToast && AppSettings.Current.OledMode && !Island.IsMouseOver)
                {
                    // уведомил — и сразу обратно за край, не переключаясь на музыку/Клоди
                    _hideAfterToast = false;
                    TryOledHide();
                    // содержимое меняем, когда остров уже уехал — чтобы не мелькала музыка
                    var later = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(380) };
                    later.Tick += (_, __) => { later.Stop(); if (State == IslandState.Collapsed) ShowCollapsedContent(false); };
                    later.Start();
                    return;
                }
                _hideAfterToast = false;
                ShowCollapsedContent(false);
                ScheduleOledHide();
                return;
            }
            var t = _toasts.Dequeue();
            if (_oledHidden) { _hideAfterToast = true; OledReveal(false); }
            State = IslandState.Toast;
            CollapsedView.Visibility = Visibility.Visible;
            IdleRow.Visibility = MediaRow.Visibility = TimerRow.Visibility = DownloadRow.Visibility = Visibility.Collapsed;
            ToastRow.Visibility = Visibility.Visible;
            _lastRow = null;
            RevealRow(ToastRow);
            RenderToast(t);
            _toastTimer.Interval = t.Duration;
            _toastTimer.Start();
        }

        private void RenderToast(Toast t)
        {
            _currentToast = t;
            if (t.Kind == ToastKind.Success) { Buddy.Cheer(); Edge.DropBead(); }
            if (t.Kind == ToastKind.Alarm) StartAlarmPulse(); else StopAlarmPulse();
            ToastTitle.Text = t.Title;
            ToastSub.Text = t.Subtitle ?? "";
            ToastSub.Visibility = string.IsNullOrEmpty(t.Subtitle) ? Visibility.Collapsed : Visibility.Visible;
            ToastSpeed.Text = t.SpeedText ?? "";
            ToastSpeed.Visibility = string.IsNullOrEmpty(t.SpeedText) ? Visibility.Collapsed : Visibility.Visible;
            ToastEta.Text = t.EtaText ?? "";
            ToastEta.Visibility = string.IsNullOrEmpty(t.EtaText) ? Visibility.Collapsed : Visibility.Visible;
            ToastIcon.Data = t.Icon;
            ToastIcon.Foreground = t.Icon == Icons.Pipette || t.Icon == Icons.Shield
                ? new SolidColorBrush(t.Accent)
                : (Brush)FindResource("Fg");
            ToastImage.Source = t.Image;
            bool weatherIcon = !string.IsNullOrEmpty(t.WeatherKind);
            if (weatherIcon) ToastWeather.Kind = t.WeatherKind;
            ToastWeather.Visibility = weatherIcon ? Visibility.Visible : Visibility.Collapsed; // анимированная погода в предупреждении
            ToastIcon.Visibility = t.Image == null && !weatherIcon ? Visibility.Visible : Visibility.Collapsed;
            bool download = t.Kind == ToastKind.Download && t.Progress.HasValue;
            ToastRingTrack.Visibility = download ? Visibility.Visible : Visibility.Collapsed;
            ToastRing.Data = download ? ProgressArc(Math.Clamp(t.Progress.Value, 0, 1), 18, 18, 17) : null;
            ToastRing.Visibility = download ? Visibility.Visible : Visibility.Collapsed;
            bool compact = string.IsNullOrEmpty(t.Subtitle) && string.IsNullOrEmpty(t.SpeedText) && !t.Progress.HasValue;
            ToastIconBox.Width = ToastIconBox.Height = compact ? 26 : 36;
            ToastIconBox.CornerRadius = new CornerRadius(compact ? 13 : 18);
            ToastBar.Visibility = t.Progress.HasValue && !download ? Visibility.Visible : Visibility.Collapsed;
            ToastFolderButton.Tag = t.FolderPath;
            ToastFolderButton.Visibility = !string.IsNullOrWhiteSpace(t.FolderPath) && Directory.Exists(t.FolderPath) ? Visibility.Visible : Visibility.Collapsed;

            double textLen = Math.Max(t.Title.Length, (t.Subtitle ?? "").Length);
            double w = Math.Clamp(120 + textLen * 7.4, compact ? 210 : 260, 600);
            if (t.Progress.HasValue) w = Math.Max(w, 280);
            if (ToastFolderButton.Visibility == Visibility.Visible) w += 38;
            double h = compact ? 40 : (string.IsNullOrEmpty(t.Subtitle) ? 52 : 64);
            double imageSize = compact ? 26 : 36;
            // обложка игры — круглая миниатюра (заполняет круг, без полос), остальные картинки — скруглённый квадрат
            ToastImage.Stretch = t.RoundImage ? Stretch.UniformToFill : Stretch.Uniform;
            ToastImage.Clip = t.Image == null ? null : t.RoundImage
                ? new EllipseGeometry(new Point(imageSize / 2, imageSize / 2), imageSize / 2, imageSize / 2)
                : new RectangleGeometry(new Rect(1, 1, imageSize - 2, imageSize - 2), 6, 6);
            if (t.Progress.HasValue)
            {
                double barW = w - 16 * 2 - 36 - 12;
                ToastBarFill.BeginAnimation(WidthProperty, new DoubleAnimation(Math.Clamp(t.Progress.Value, 0, 1) * barW, TimeSpan.FromMilliseconds(160)));
            }
            AnimateSize(w, h, 360);
        }

        private void OnToastFolder(object sender, RoutedEventArgs e)
        {
            if (ToastFolderButton.Tag is string folder && Directory.Exists(folder))
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true }); }
                catch (Exception ex) { App.Trace("не удалось открыть папку игры: " + ex.Message); }
            }
            e.Handled = true;
        }

        private static Geometry ProgressArc(double progress, double cx, double cy, double radius)
        {
            if (progress <= 0) return Geometry.Empty;
            progress = Math.Min(progress, 0.9999);
            double angle = progress * Math.PI * 2;
            var start = new Point(cx, cy - radius);
            var end = new Point(cx + radius * Math.Sin(angle), cy - radius * Math.Cos(angle));
            var figure = new PathFigure { StartPoint = start, IsClosed = false };
            figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, progress > 0.5, SweepDirection.Clockwise, true));
            return new PathGeometry(new[] { figure });
        }

        private void OnTimerCancel(object sender, RoutedEventArgs e)
        {
            AppHost.Timer.Reset();
            ToastBus.Show(new Toast { Title = "Таймер отменён", Icon = Icons.Timer, Duration = TimeSpan.FromSeconds(2) });
            e.Handled = true;
        }

        private void RefreshSteamDownload()
        {
            if (_downloadPollInProgress) return;
            _downloadPollInProgress = true;
            _ = RefreshDownloadsAsync();
        }

        private async Task RefreshDownloadsAsync()
        {
            try
            {
                var result = await Task.Run(() =>
                {
                    var steam = SteamDownloadService.ReadCurrent();
                    var steamCompleted = new List<SteamDownload>();
                    while (SteamDownloadService.TakeCompleted() is { } completed) steamCompleted.Add(completed);
                    var steamPaused = new List<SteamDownload>();
                    while (SteamDownloadService.TakePaused() is { } paused) steamPaused.Add(paused);
                    var browser = BrowserDownloadService.ReadSnapshot();
                    // загрузки браузера по недокачанным файлам в папке «Загрузки» — работают, даже если историю браузера прочитать не вышло
                    var partials = PartialDownloadService.Scan();
                    var partialDone = new List<FinishedDownload>();
                    while (PartialDownloadService.TakeFinished() is { } f) partialDone.Add(f);
                    return (steam, steamCompleted, steamPaused, browser, partials, partialDone);
                });

                foreach (var completed in result.steamCompleted)
                    ToastBus.Show(new Toast
                    {
                        Kind = ToastKind.Success,
                        Title = "Загружено в Steam",
                        Subtitle = completed.Name,
                        Image = completed.Icon, RoundImage = true,
                        FolderPath = completed.FolderPath,
                        Icon = Icons.Check,
                        Important = true,
                        Duration = TimeSpan.FromSeconds(8) // чтобы успеть заметить и нажать «папку»
                    });

                // завершённые по файлам — если история браузера уже не сообщила о том же файле
                foreach (var done in result.partialDone)
                {
                    if (result.browser.Completed.Any(c => string.Equals(c.FileName, done.Name, StringComparison.OrdinalIgnoreCase))) continue;
                    if (_recentDone.TryGetValue(done.Name, out var doneAt) && DateTime.Now - doneAt < TimeSpan.FromMinutes(1)) continue;
                    _recentDone[done.Name] = DateTime.Now;
                    ToastBus.Show(new Toast
                    {
                        Kind = ToastKind.Success, Title = "Загрузка завершена", Subtitle = done.Browser + " · " + done.Name,
                        Icon = Icons.Check, FolderPath = done.Folder, Important = true, Duration = TimeSpan.FromSeconds(7)
                    });
                }
                foreach (var c in result.browser.Completed) _recentDone[c.FileName] = DateTime.Now;

                foreach (var completed in result.browser.Completed)
                    ToastBus.Show(new Toast
                    {
                        Kind = ToastKind.Success,
                        Title = "Загрузка завершена",
                        Subtitle = completed.Browser + " · " + completed.FileName,
                        Icon = Icons.Check,
                        Important = true,
                        Duration = TimeSpan.FromSeconds(7)
                    });

                foreach (var paused in result.steamPaused)
                    ToastBus.Show(new Toast
                    {
                        Kind = ToastKind.Info,
                        Title = "Загрузка приостановлена",
                        Subtitle = paused.Name + " · " + paused.ProgressText,
                        Image = paused.Icon, RoundImage = true,
                        FolderPath = paused.FolderPath,
                        Icon = Icons.Bolt,
                        Important = true,
                        Duration = TimeSpan.FromSeconds(4)
                    });

                // Идущая загрузка живёт в самой чёлке (как музыка или таймер), а не всплывашками каждые 2 секунды
                var steam = result.steam;
                var download = result.browser.Active.FirstOrDefault();
                if (steam is { IsPaused: false })
                    SetDownload(new DownloadState(steam.Name, "Steam", steam.Progress, steam.SpeedText, steam.EtaText, steam.Icon));
                else if (download != null)
                {
                    // скорость из истории браузера рваная — если есть недокачанный файл, берём его скорость (по росту файла)
                    var firstPartial = result.partials.FirstOrDefault();
                    var speed = firstPartial != null && firstPartial.BytesPerSecond > 0 ? firstPartial.SpeedText : download.SpeedText;
                    SetDownload(new DownloadState(download.FileName, download.Browser, download.Progress, speed, download.EtaText, null));
                }
                else if (result.partials.FirstOrDefault() is { } part)
                    SetDownload(new DownloadState(part.Name, part.Browser, null, part.SpeedText, part.SizeText, null)); // размер неизвестен — «крутилка» и сколько скачано
                else SetDownload(null);
            }
            catch (Exception ex) { App.Trace("ошибка отслеживания загрузок: " + ex.Message); }
            finally { _downloadPollInProgress = false; }
        }

        /// <summary>Текущая загрузка для пилюли: название, источник, доля 0..1 (null — размер неизвестен), скорость, остаток, обложка.</summary>
        private readonly Dictionary<string, DateTime> _recentDone = new(StringComparer.OrdinalIgnoreCase);

        private sealed record DownloadState(string Name, string Source, double? Progress, string Speed, string Eta, ImageSource Art);
        private DownloadState _download;
        private double _dlShown;      // плавно догоняет настоящий прогресс
        private double _dlSpin;       // вращение «неизвестного» прогресса

        private void SetDownload(DownloadState d)
        {
            bool had = _download != null;
            _download = d;
            // пока идёт загрузка — частые обновления (скорость, остаток); в простое — редкие, новости придут по событиям
            var interval = TimeSpan.FromSeconds(d != null ? 1 : 8);
            if (_steamTimer.Interval != interval) _steamTimer.Interval = interval;
            if (d == null)
            {
                MediaDownload.Visibility = Visibility.Collapsed;
                if (had && State == IslandState.Collapsed) ShowCollapsedContent(false);
                return;
            }
            if (!had) _dlShown = 0;
            DlName.Text = d.Name;
            var parts = new List<string> { d.Source };
            if (!string.IsNullOrEmpty(d.Speed)) parts.Add(d.Speed);
            if (!string.IsNullOrEmpty(d.Eta)) parts.Add(d.Eta);
            DlSub.Text = string.Join(" · ", parts);
            DlArt.Source = d.Art;
            DlIcon.Visibility = d.Art == null ? Visibility.Visible : Visibility.Collapsed;
            string pct = d.Progress.HasValue ? (int)Math.Floor(Math.Clamp(d.Progress.Value, 0, 1) * 100) + "%" : "";
            DlPercent.Text = pct;
            DlPercent.Visibility = pct.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            MediaDownload.Text = "↓ " + (pct.Length > 0 ? pct : "…");
            if (State == IslandState.Collapsed) ShowCollapsedContent(had);
        }

        /// <summary>Кольцо прогресса загрузки: плавно догоняет реальное значение; без размера — крутится.</summary>
        private void AnimateDownloadRing(double dt)
        {
            if (_download == null || DownloadRow.Visibility != Visibility.Visible) return;
            if (_download.Progress is double target)
            {
                double before = _dlShown;
                _dlShown += (Math.Clamp(target, 0, 1) - _dlShown) * Math.Min(1, dt * 4);
                if (Math.Abs(_dlShown - before) > 0.0005 || DlRing.Data == null)
                {
                    DlRing.RenderTransform = null;
                    DlRing.Data = ProgressArc(Math.Max(0.01, _dlShown), 17, 17, 15.75);
                }
            }
            else
            {
                _dlSpin = (_dlSpin + dt * 300) % 360;
                if (DlRing.RenderTransform is not RotateTransform rt) DlRing.RenderTransform = rt = new RotateTransform(0, 17, 17);
                rt.Angle = _dlSpin;
                if (DlRing.Tag as string != "spin") { DlRing.Data = ProgressArc(0.28, 17, 17, 15.75); DlRing.Tag = "spin"; }
                return;
            }
            DlRing.Tag = null;
        }

        private string _lastWeatherAlert;

        // ===================== OLED-режим =====================

        public void ApplyOledMode()
        {
            if (AppSettings.Current.OledMode) ScheduleOledHide(TimeSpan.FromSeconds(3));
            else OledReveal(false);
        }

        private void ScheduleOledHide(TimeSpan? after = null)
        {
            if (!AppSettings.Current.OledMode) return;
            _oledHide.Stop();
            _oledHide.Interval = after ?? TimeSpan.FromSeconds(Math.Max(2, AppSettings.Current.OledRevealSeconds));
            _oledHide.Start();
        }

        private void TryOledHide()
        {
            if (!AppSettings.Current.OledMode) return;
            if (State != IslandState.Collapsed || Island.IsMouseOver) { ScheduleOledHide(); return; }
            _oledHidden = true;
            var to = -(Island.ActualHeight + 40);
            SlideT.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
            EdgeStrip.Visibility = Visibility.Visible;
        }

        /// <summary>Показать чёлку (в OLED-режиме — на N секунд).</summary>
        public void OledReveal(bool scheduleHide = true)
        {
            if (_oledHidden || SlideT.Y < 0)
            {
                _oledHidden = false;
                SlideT.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(360)) { EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut } });
            }
            EdgeStrip.Visibility = Visibility.Collapsed;
            if (scheduleHide) ScheduleOledHide();
        }

        private void ApplyVisibility()
        {
            bool hide = _fullscreenHidden && !_fullscreenPeek && State != IslandState.Expanded;
            Scaler.Visibility = hide ? Visibility.Hidden : Visibility.Visible;
            // Полоса у края остаётся: навёл курсор наверх — остров выглянет даже поверх полноэкранного окна.
            if (hide) EdgeStrip.Visibility = Visibility.Visible;
            else if (!_oledHidden) EdgeStrip.Visibility = Visibility.Collapsed;
        }

        private void PeekOverFullscreen()
        {
            _fullscreenPeek = true;
            ApplyVisibility();
            KeepOnTop();
            _peekTimer.Stop();
            _peekTimer.Interval = TimeSpan.FromSeconds(Math.Max(3, AppSettings.Current.OledRevealSeconds));
            _peekTimer.Start();
        }

        private DateTime _tipUntil;

        private void KeepOnTop()
        {
            if (_hwnd == IntPtr.Zero || !IsVisible) return;
            // открыта подсказка или выпадающий список — не поднимаем остров над ними, иначе они «уходят за чёлку»
            if (DateTime.Now < _tipUntil || CursorOverOurPopup()) return;
            // HWND_TOPMOST, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER
            SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0002 | 0x0001 | 0x0010 | 0x0200);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        /// <summary>Показать остров (вызывается при повторном запуске exe и из значка в трее).</summary>
        public void BringUp(string tab = null)
        {
            Visibility = Visibility.Visible;
            if (_fullscreenHidden) PeekOverFullscreen();
            KeepOnTop();
            Expand(tab);
        }

        // ===================== Таймеры =====================

        private void Clock()
        {
            var cs = AppSettings.Current;
            ClockText.Text = DateTime.Now.ToString((cs.Use24h ? "HH:mm" : "h:mm") + (cs.ClockSeconds ? ":ss" : ""));
            ClockText.Visibility = cs.ShowClock ? Visibility.Visible : Visibility.Collapsed;
            if (AppHost.Timer != null)
            {
                MiniTimer.Text = (AppHost.Timer.Paused ? "пауза · " : "") + TimerService.Format(AppHost.Timer.Left);
                MediaTimer.Text = (AppHost.Timer.Paused ? "❚❚ " : "") + TimerService.Format(AppHost.Timer.Left);
                TimerCancel.Visibility = AppHost.Timer.Running || AppHost.Timer.Paused ? Visibility.Visible : Visibility.Collapsed;
            }
            var w = AppHost.Watchers;
            bool showBattery = w != null && w.HasBattery && w.BatteryPercent >= 0;
            BatteryMini.Visibility = showBattery ? Visibility.Visible : Visibility.Collapsed;
            if (showBattery) BatteryMini.Text = (w.Charging ? "⚡" : "") + w.BatteryPercent + "%";
        }

        private Color AccentColor()
        {
            try { return (Color)ColorConverter.ConvertFromString(AppSettings.Current.AccentHex); } catch { return Colors.White; }
        }

        /// <summary>Самый «живой» цвет обложки — им светятся всплески музыки на краю.</summary>
        private static Color? DominantColor(System.Windows.Media.Imaging.BitmapSource cover)
        {
            if (cover == null) return null;
            try
            {
                var small = new System.Windows.Media.Imaging.TransformedBitmap(cover, new ScaleTransform(24.0 / cover.PixelWidth, 24.0 / cover.PixelHeight));
                var px = ScreenTools.Bgra(small, out int w, out int h);
                double r = 0, g = 0, b = 0, wsum = 0;
                for (int i = 0; i < px.Length; i += 4)
                {
                    double B = px[i], G = px[i + 1], R = px[i + 2];
                    double max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B));
                    double sat = max <= 0 ? 0 : (max - min) / max;
                    double weight = sat * sat * (max / 255.0) + 0.02;
                    r += R * weight; g += G * weight; b += B * weight; wsum += weight;
                }
                if (wsum <= 0) return null;
                var c = Color.FromRgb((byte)(r / wsum), (byte)(g / wsum), (byte)(b / wsum));
                // слишком тёмный цвет на чёрном не видно — осветляем
                double lum = 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
                if (lum < 110)
                {
                    double k = 110 / Math.Max(1, lum);
                    c = Color.FromRgb((byte)Math.Min(255, c.R * k), (byte)Math.Min(255, c.G * k), (byte)Math.Min(255, c.B * k));
                }
                return c;
            }
            catch { return null; }
        }

        private double _specIdle;
        private double _frameDt = 1 / 30.0;
        private const double BarMax = 18;
        private Border[] _bars;
        private ScaleTransform[] _barScale;

        private Border[] InitBars()
        {
            var bars = new[] { Bar0, Bar1, Bar2, Bar3, Bar4, Bar5 };
            _barScale = new ScaleTransform[bars.Length];
            for (int i = 0; i < bars.Length; i++)
            {
                double h = bars[i].Height;
                bars[i].Height = BarMax;
                bars[i].RenderTransformOrigin = new Point(0.5, 1);
                bars[i].RenderTransform = _barScale[i] = new ScaleTransform(1, h / BarMax);
            }
            return bars;
        }

        private double _pushTicks; // сколько секунд курсор упирается в верх над островом

        /// <summary>Курсор упёрся в самый верх экрана над островом — открываем (как в оригинале).</summary>
        private void CheckTopPush()
        {
            if (State == IslandState.Expanded || !Win32.GetCursorPos(out var p)) { _pushTicks = 0; return; }
            bool atTop = false;
            try
            {
                var tl = Island.PointToScreen(new Point(0, 0));
                var br = Island.PointToScreen(new Point(Island.ActualWidth, Island.ActualHeight));
                atTop = p.Y <= tl.Y + 1 && p.X >= tl.X && p.X <= br.X; // строго над островом
            }
            catch { }
            if (!atTop) { _pushTicks = 0; return; }
            if (_oledHidden) { OledReveal(); return; }
            double was = _pushTicks; _pushTicks += _frameDt;
            if (was < 0.2 && _pushTicks >= 0.2 && Scaler.Visibility == Visibility.Visible) Expand(null);
        }

        private void Animate()
        {
            CheckOutsideClick();
            CheckTopPush();

            // спектр включаем только пока играет музыка и чёлка свёрнута
            var md = AppHost.Media?.Current;
            bool wantSpectrum = md != null && md.HasSession && md.IsPlaying && State != IslandState.Expanded && !_oledHidden && Scaler.Visibility == Visibility.Visible;
            if (wantSpectrum) { _specIdle = 0; if (!AppHost.Spectrum.Running) AppHost.Spectrum.Start(); }
            else if (AppHost.Spectrum.Running && (_specIdle += _frameDt) > 2) AppHost.Spectrum.Stop();
            Edge.Active = State != IslandState.Expanded && !_oledHidden;
            Helpers.FrameClock.Hidden = _oledHidden; // спрятанный остров не рисуем — экономим процессор
            var idle = IdleTime();
            Edge.Away = idle > TimeSpan.FromMinutes(2);
            Edge.Raining = AppHost.Weather != null && (AppHost.Weather.Code >= 51 && AppHost.Weather.Code <= 67 || AppHost.Weather.Code >= 80);

            if (Scaler.Visibility != Visibility.Visible || _oledHidden) return;
            UpdateMiniLive();
            AnimateDownloadRing(_frameDt);
            if (RecChip.Visibility == Visibility.Visible) RecDot.Opacity = (DateTime.Now.Millisecond / 500) % 2 == 0 ? 1 : 0.25;
            if (Buddy.Visibility == Visibility.Visible)
            {
                var m = AppHost.Media?.Current;
                bool playing = m != null && m.HasSession && m.IsPlaying;
                // такт: всплеск на каждый удар баса (не зависит от громкости), иначе — пик громкости
                float beat = 0;
                double bpm = 0, energy = 0;
                if (playing && AppHost.Spectrum.Running)
                {
                    bpm = AppHost.Spectrum.Bpm; energy = AppHost.Spectrum.Energy;
                    beat = (float)Math.Max(Math.Exp(-AppHost.Spectrum.SinceBeat * 7) * 0.5, energy * 0.6);
                }
                else if (playing) beat = AppHost.Audio?.Peak ?? 0;
                bool snowing = AppHost.Weather != null && (AppHost.Weather.Code >= 71 && AppHost.Weather.Code <= 77 || AppHost.Weather.Code == 85 || AppHost.Weather.Code == 86);
                UpdateWorking(idle);
                // ночью (23:00–6:00) Клоди хочет спать: засыпает уже через минуту без тебя, а пока ты здесь — зевает
                int hour = DateTime.Now.Hour;
                bool night = hour >= 23 || hour < 6;
                var mood = _aiBusy ? Helpers.Mood.Thinking
                    : State == IslandState.Toast && _currentToast?.Kind == ToastKind.Notification ? Helpers.Mood.Attention
                    : playing ? Helpers.Mood.Music
                    : idle > TimeSpan.FromMinutes(night ? 1 : 5) ? Helpers.Mood.Sleep
                    : DateTime.Now < _workingUntil ? Helpers.Mood.Working
                    : night ? Helpers.Mood.Drowsy
                    : Helpers.Mood.Idle;
                var outfit = OutfitForWeather();
                foreach (var b in _characters) { b.Beat = beat; b.Bpm = bpm; b.Energy = energy; b.Mood = mood; b.Outfit = outfit; b.Night = night; b.Snowing = snowing; }
            }
            if (MediaRow.Visibility == Visibility.Visible && CollapsedView.Visibility == Visibility.Visible)
            {
                var bars = _bars ??= InitBars();
                var spec = AppHost.Spectrum.Running ? AppHost.Spectrum.Snapshot() : null;
                float peak = AppHost.Audio?.Peak ?? 0;
                for (int i = 0; i < bars.Length; i++)
                {
                    double level;
                    if (spec != null)
                    {
                        // 6 полосок = 6 частей спектра: басы слева → высокие справа (среднее по своей части)
                        int from = i * spec.Length / bars.Length, to = (i + 1) * spec.Length / bars.Length;
                        double sum = 0;
                        for (int b = from; b < to; b++) sum += spec[b];
                        level = sum / Math.Max(1, to - from);
                    }
                    else level = peak * (0.55 + _rnd.NextDouble() * 0.6);
                    double target = 3 + Math.Min(15, level * 15);
                    // вверх быстро, вниз плавно — как у настоящего эквалайзера.
                    // Меняем масштаб, а не высоту: без пересчёта раскладки 30 раз в секунду
                    var st = _barScale[i];
                    double cur = st.ScaleY * BarMax;
                    double k = Helpers.FrameClock.Lerp(target > cur ? 0.75 : 0.28);
                    double next = cur + (target - cur) * k;
                    if (Math.Abs(next - cur) > 0.05) st.ScaleY = next / BarMax;
                }
            }
            else if (IdleRow.Visibility == Visibility.Visible && DateTime.Now >= _nextBlink)
            {
                _nextBlink = DateTime.Now.AddSeconds(3 + _rnd.NextDouble() * 5);
                foreach (var eye in new[] { EyeL, EyeR })
                {
                    var a = new DoubleAnimation(14, 2, TimeSpan.FromMilliseconds(90)) { AutoReverse = true };
                    eye.BeginAnimation(HeightProperty, a);
                }
            }
        }

        public async Task RefreshWeather()
        {
            try
            {
                var s = AppSettings.Current;
                var weather = await WeatherService.LoadAsync(s.Latitude, s.Longitude, s.City);
                AppHost.SetWeather(weather);
                // Предупреждаем заранее: о грозе, граде и гололёде — за 2 часа, об остальном (дождь, снег, туман, ветер, мороз, жара) — за час.
                if (weather.NextAlert is { } alert && alert.At > weather.ForecastNow)
                {
                    bool severe = alert.Kind is "thunder" or "hail" or "sleet";
                    string key = alert.Kind + "|" + alert.At.ToString("yyyyMMddHH");
                    if (alert.At <= weather.ForecastNow.AddHours(severe ? 2 : 1) && key != _lastWeatherAlert)
                    {
                        _lastWeatherAlert = key;
                        ToastBus.Show(new Toast
                        {
                            Title = alert.Title + " около " + alert.At.ToString("HH:mm"),
                            Subtitle = weather.City, Icon = alert.Kind == "snow" ? Icons.Snow : Icons.Bell,
                            WeatherKind = Helpers.WeatherGlyph.KindForAlert(alert.Kind),
                            Important = severe, Duration = TimeSpan.FromSeconds(severe ? 7 : 5)
                        });
                    }
                }
            }
            catch (Exception ex) { App.Log("weather: " + ex.Message); }
        }

        private void UpdateWeatherMini()
        {
            var w = AppHost.Weather;
            WeatherMini.Text = w == null ? "" : w.Glyph + " " + Math.Round(w.Temp) + "°";
            if (State == IslandState.Collapsed) ShowCollapsedContent(true);
        }

        /// <summary>Скрывает остров от скриншотов, записи экрана и трансляций (если не включено обратное).</summary>
        public void ApplyCaptureVisibility()
        {
            if (_hwnd == IntPtr.Zero) return;
            Win32.SetWindowDisplayAffinity(_hwnd, AppSettings.Current.ShowInCaptures ? 0u : 0x11u);
        }

        /// <summary>Масштаб острова. Окно растёт вместе с ним — иначе края панели обрезаются.</summary>
        public void ApplyScale()
        {
            double sc = Math.Clamp(AppSettings.Current.Scale, 0.7, 2.0);
            ScaleT.ScaleX = ScaleT.ScaleY = sc;
            // запас по краям: пружина на миг делает остров чуть больше конечного размера
            Width = 1010 * sc;
            Height = 660 * sc;
            PlaceWindow();
        }

        // ===================== Перетаскивание на полку =====================

        private void OnDrop(object sender, DragEventArgs e)
        {
            DropHint.Visibility = Visibility.Collapsed;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                AppHost.Shelf.Add(files);
                ToastBus.Show(new Toast
                {
                    Kind = ToastKind.Success, Title = "На полке: " + AppHost.Shelf.Items.Count,
                    Subtitle = files.Length == 1 ? System.IO.Path.GetFileName(files[0]) : "Добавлено файлов: " + files.Length,
                    Icon = Icons.Tray, Accent = Color.FromRgb(0xFF, 0xB5, 0x47)
                });
            }
        }
    }
}
