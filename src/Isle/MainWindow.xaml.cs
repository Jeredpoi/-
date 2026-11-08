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
        private bool _suppressCollapse;
        /// <summary>Не сворачивать остров сам: открыто меню или выпадающий список.</summary>
        public bool SuppressAutoCollapse { get => _suppressCollapse || _openMenus > 0; set => _suppressCollapse = value; }
        /// <summary>Сколько наших меню сейчас открыто — пока они открыты, остров не поднимается поверх них.</summary>
        private static int _openMenus;

        public MainWindow()
        {
            InitializeComponent();
            _characters = new[] { Buddy, BuddyMedia, BuddyTimer };
            // пока видна подсказка — не поднимаем остров поверх неё (иначе она «уходит за чёлку»)
            AddHandler(ToolTipService.ToolTipOpeningEvent, new ToolTipEventHandler((_, __) => _tipUntil = DateTime.Now.AddSeconds(8)), true);
            AddHandler(ToolTipService.ToolTipClosingEvent, new ToolTipEventHandler((_, __) => _tipUntil = DateTime.MinValue), true);
            // любое меню приложения: пока открыто — не сворачиваемся и не перекрываем его чёлкой (раньше оно «уходило за окно»)
            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((_, __) => _openMenus++));
            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.ClosedEvent, new RoutedEventHandler((_, __) => _openMenus = Math.Max(0, _openMenus - 1)));
            var s = AppSettings.Current;
            ScaleT.ScaleX = ScaleT.ScaleY = Math.Clamp(s.Scale, 0.7, 1.6);
            DndChip.IsChecked = s.DoNotDisturb; // «Не беспокоить» сохраняется — кнопка должна это показывать после перезапуска

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
                VoiceWave.Push(lvl); // столбики волны: чем громче — тем выше
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
                    Kind = ToastKind.Volume, Title = muted ? "Звук выключен" : "Громкость", SpeedText = muted ? "0" : pct.ToString(),
                    Icon = muted ? Icons.Mute : Icons.Volume, On = muted ? false : null,
                    Progress = muted ? 0 : v, ReplaceKey = "volume", Duration = TimeSpan.FromSeconds(1.3)
                });
            };
            AppHost.Watchers.LanguageChanged += () =>
            {
                if (LangChip.Text == AppHost.Watchers.LanguageCode) return;
                LangChip.Text = AppHost.Watchers.LanguageCode;
                AnimateLangChip();
            };
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
            _dlDoneTimer.Tick += (_, __) => ClearDownloadDone();
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
            ApplyPrintScreenHook();
        }

        // ---------- PrintScreen через Isle ----------
        // Обычная горячая клавиша не помогает: Windows 11 сама ловит PrintScreen и открывает «Ножницы» —
        // получалось два окна захвата сразу, медленно и вперемешку. Низкоуровневый перехват клавиатуры
        // забирает нажатие раньше Windows: «Ножницы» не открываются, сразу выделение области Isle.
        // Alt+PrintScreen и Win+PrintScreen не трогаем — они работают как в Windows.
        // Перехват живёт в отдельном потоке со своим циклом сообщений: если окно острова занято (анимации, отрисовка),
        // Windows не ждёт его и не «теряет» отпускание клавиш — раньше в играх могло казаться, что клавиша залипла.
        private volatile IntPtr _kbHook;
        private Win32.LowLevelKeyboardProc _kbProc;
        private System.Threading.Thread _kbThread;
        private uint _kbThreadId;

        private void ApplyPrintScreenHook()
        {
            bool want = AppSettings.Current.PrintScreenIsle;
            if (want && _kbThread == null)
            {
                _kbProc = KeyboardHook;
                var ready = new System.Threading.ManualResetEventSlim();
                _kbThread = new System.Threading.Thread(() =>
                {
                    _kbThreadId = Win32.GetCurrentThreadId();
                    _kbHook = Win32.SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, _kbProc, Win32.GetModuleHandle(null), 0);
                    ready.Set();
                    if (_kbHook == IntPtr.Zero) return;
                    while (Win32.GetMessage(out var m, IntPtr.Zero, 0, 0) > 0) { }
                    Win32.UnhookWindowsHookEx(_kbHook);
                    _kbHook = IntPtr.Zero;
                }) { IsBackground = true, Name = "Isle keyboard hook" };
                _kbThread.Start();
                ready.Wait(2000);
                if (_kbHook == IntPtr.Zero) _kbThread = null;
            }
            else if (!want && _kbThread != null)
            {
                Win32.PostThreadMessage(_kbThreadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
                _kbThread.Join(1000);
                _kbThread = null;
                _kbHook = IntPtr.Zero;
            }
        }

        private IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                var k = System.Runtime.InteropServices.Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
                bool alt = (k.flags & 0x20) != 0;                                             // LLKHF_ALTDOWN
                bool win = (Win32.GetAsyncKeyState(0x5B) & 0x8000) != 0 || (Win32.GetAsyncKeyState(0x5C) & 0x8000) != 0;
                if (k.vkCode == 0x2C && !alt && !win)                                          // VK_SNAPSHOT
                {
                    int msg = wParam.ToInt32();
                    if (msg == 0x0100 || msg == 0x0104) Dispatcher.BeginInvoke(new Action(() => _ = Tools.RegionShot())); // WM_KEYDOWN / WM_SYSKEYDOWN
                    return new IntPtr(1); // Windows эту клавишу уже не увидит — «Ножницы» не откроются
                }
            }
            return Win32.CallNextHookEx(_kbHook, code, wParam, lParam);
        }

        /// <summary>Включить/выключить PrintScreen через Isle. false — Windows не дала поставить перехват.</summary>
        public bool SetPrintScreen(bool on)
        {
            AppSettings.Current.PrintScreenIsle = on;
            ApplyPrintScreenHook();
            bool ok = !on || _kbHook != IntPtr.Zero;
            if (!ok) AppSettings.Current.PrintScreenIsle = false;
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
            if (_kbHook != IntPtr.Zero) { Win32.UnhookWindowsHookEx(_kbHook); _kbHook = IntPtr.Zero; }
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

        private bool _displayHooked;

        private void PlaceWindow()
        {
            if (!_displayHooked)
            {
                _displayHooked = true;
                // сменили разрешение, монитор или масштаб — чёлка должна остаться сверху по центру
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, __) => Dispatcher.InvokeAsync(PlaceWindow, DispatcherPriority.Background);
                DpiChanged += (_, __) => Dispatcher.InvokeAsync(PlaceWindow, DispatcherPriority.Background);
            }
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
            Island.CornerRadius = new CornerRadius(0, 0, 40, 40); // раскрытая панель — нижние углы 40
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
            if (dl) { w = 580; h = 76; }
            if (Buddy.Visibility == Visibility.Visible && (media || timer)) w += 36; // место для Клоди
            if (_hoverGrown) { w += 16; h = Math.Max(38, h); }
            // сравниваем с целью анимации, а не с текущим (ещё анимируемым) размером: таймер дёргает это 4 раза в секунду,
            // и раньше «желе» перезапускалось каждые 250 мс — чёлка дрожала, пока шёл таймер
            SetIslandRadius(dl ? 32 : 18); // у пилюли загрузки углы крупнее
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

        private void SetIslandRadius(double r)
        {
            if (Math.Abs(Island.CornerRadius.BottomLeft - r) < 0.5) return;
            Island.CornerRadius = new CornerRadius(0, 0, r, r);
            DropHint.CornerRadius = IslandShadow.CornerRadius = Island.CornerRadius;
            Edge.CornerRadius = r;
            UpdateClip();
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
                _ = AppHost.Voice.StopAsync(t => _ = RunVoiceCommand(t), command: true);
                return;
            }
            AppHost.Voice.Start("Говори команду и отпусти клавиши · например: «открой телеграм» или «сделай текст официальнее»");
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
                    _ = AppHost.Voice.StopAsync(t => _ = RunVoiceCommand(t), command: true);
                };
            }
            _voiceKeyPoll.Start();
        }

        /// <summary>Последние голосовые команды и ответы: ИИ помнит их 5 минут («а теперь на 10 минут», «повтори»).</summary>
        private readonly List<(DateTime at, ChatMessage msg)> _voiceContext = new();

        private async Task RunVoiceCommand(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            // «сделай этот текст официальнее», «исправь ошибки в сообщении» — правим текст в поле ввода, где стоит курсор
            if (TextEditor.IsEditRequest(text)) { await TextEditor.EditFocusedAsync(text); return; }
            ToastBus.Show(new Toast { Title = "«" + text + "»", Subtitle = "Думаю…", Icon = Icons.Chat, ReplaceKey = "voice", Important = true, Duration = TimeSpan.FromSeconds(30) });
            _voiceContext.RemoveAll(c => DateTime.Now - c.at > TimeSpan.FromMinutes(5));
            var history = _voiceContext.Select(c => c.msg).ToList();
            history.Add(new() { Role = "user", Text = text + "\n\n(Это голосовая команда: если просят что-то сделать — выполни командой, ответ дай одной короткой фразой. " +
                "Если просят переписать, исправить, перевести, сократить или оформить текст, который пользователь набрал или выделил, — " +
                "ты его не видишь, Isle сам возьмёт его из поля ввода: НЕ проси прислать текст, ответь только строкой [[edit: что сделать с текстом]].)" });
            string answer;
            try { answer = await AiService.AskAsync(Views.ChatView.BuildHistory(history)); }
            catch (Exception ex) { answer = "Ошибка: " + ex.Message; }
            bool ok = !(answer ?? "").StartsWith("Ошибка") && !(answer ?? "").StartsWith("Нужен API-ключ");
            if (ok)
            {
                _voiceContext.Add((DateTime.Now, new ChatMessage { Role = "user", Text = text }));
                _voiceContext.Add((DateTime.Now, new ChatMessage { Role = "assistant", Text = answer }));
                if (_voiceContext.Count > 12) _voiceContext.RemoveRange(0, _voiceContext.Count - 12);
            }
            var (clean, actions) = AiActions.Extract(answer ?? "");
            // ИИ понял, что это правка набранного текста — берём текст из поля, переписываем и вставляем на место
            var edit = actions.FirstOrDefault(a => a.Name == "edit");
            if (edit != null)
            {
                await TextEditor.EditFocusedAsync(string.IsNullOrWhiteSpace(edit.Arg) ? text : edit.Arg);
                return;
            }
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
                Kind = failed ? ToastKind.Error : ToastKind.Success,
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
            // Куда переключаем: окно, где ты печатаешь. Если сейчас активен остров, рабочий стол или обои —
            // берём последнее обычное окно (раньше раскладка менялась у самой чёлки или рабочего стола — и «ничего не происходило»).
            var fg = Win32.GetForegroundWindow();
            bool fgReal = fg != IntPtr.Zero && fg != _hwnd && !IsDesktopWindow(fg);
            var target = fgReal ? fg : AppHost.Watchers.LastRealWindow;
            if (target == IntPtr.Zero || !Win32.IsWindow(target)) target = fgReal ? fg : PreviousForeground;
            if (target == IntPtr.Zero || !Win32.IsWindow(target)) target = fg;

            var before = (int)((long)Win32.LayoutOf(Win32.FocusWindowOf(target)) & 0xFFFF);
            // 1) просим окно включить следующую раскладку (так делают переключатели вроде Punto)
            var next = (int)((long)Win32.RequestNextLayout(target) & 0xFFFF);
            for (int i = 0; i < 10 && (int)((long)Win32.LayoutOf(Win32.FocusWindowOf(target)) & 0xFFFF) == before; i++) await Task.Delay(50);
            bool changed = (int)((long)Win32.LayoutOf(Win32.FocusWindowOf(target)) & 0xFFFF) != before;
            // 2) окно проигнорировало просьбу (консоли, некоторые игры) — жмём сочетание пользователя, но только если
            //    это окно и правда активно: сочетание приходит в активное окно
            if (!changed && fgReal && target == fg)
            {
                await WaitModifiersUp();
                if (Win32.PressLayoutHotkey())
                {
                    await Task.Delay(200);
                    changed = (int)((long)Win32.LayoutOf(Win32.FocusWindowOf(target)) & 0xFFFF) != before;
                }
                Win32.ReleaseModifiers(); // страховка от «залипших» Alt/Shift/Win
            }
            int now = (int)((long)Win32.LayoutOf(Win32.FocusWindowOf(target)) & 0xFFFF);
            int shown = changed ? now : next;
            if (shown != 0 && shown != before)
            {
                AppHost.Watchers.ForceLanguage(shown);
                // своя всплывашка: в раскрытой панели обычные не показываются, а тут ты сам нажал — нужна отдача
                ToastBus.Show(new Toast
                {
                    Kind = ToastKind.Layout, Title = Services.SystemWatchers.LangName(shown), Badge = AppHost.Watchers.LanguageCode,
                    Icon = Icons.Keyboard, ReplaceKey = "layout", Important = true, Duration = TimeSpan.FromSeconds(1.2)
                });
            }
            else
                ToastBus.Show(new Toast
                {
                    Kind = ToastKind.Error, Title = "Раскладка не переключилась", Icon = Icons.Keyboard, ReplaceKey = "layout", Important = true, Duration = TimeSpan.FromSeconds(5),
                    Subtitle = "Включи Alt+Shift: Параметры → Время и язык → Ввод → Дополнительные параметры клавиатуры → Сочетания клавиш"
                });
        }

        /// <summary>Значок раскладки в панели: короткий «переворот», чтобы смена была заметна.</summary>
        private void AnimateLangChip()
        {
            if (LangChip.RenderTransform is not ScaleTransform sc)
            {
                sc = new ScaleTransform(1, 1);
                LangChip.RenderTransform = sc;
                LangChip.RenderTransformOrigin = new Point(0.5, 0.5);
            }
            var flip = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(320) };
            flip.KeyFrames.Add(new EasingDoubleKeyFrame(0.2, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            flip.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320)), new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut }));
            sc.BeginAnimation(ScaleTransform.ScaleYProperty, flip);
            LangChip.BeginAnimation(OpacityProperty, new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(260)));
        }

        private static async Task WaitModifiersUp()
        {
            for (int i = 0; i < 20; i++)
            {
                bool down = false;
                foreach (int vk in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }) down |= (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;
                if (!down) return;
                await Task.Delay(50);
            }
        }

        private static bool IsDesktopWindow(IntPtr h)
        {
            var sb = new System.Text.StringBuilder(64);
            Win32.GetClassName(h, sb, sb.Capacity);
            var c = sb.ToString();
            return c is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
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
            bool on = AppSettings.Current.DoNotDisturb;
            ToastBus.Show(new Toast
            {
                Title = on ? "Не беспокоить" : "Уведомления включены", Subtitle = on ? "Уведомления приложений не покажутся" : "Снова показываю уведомления",
                Icon = Icons.Moon, On = on, ReplaceKey = "dnd", Important = true, Duration = TimeSpan.FromSeconds(2.5)
            });
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
            SetIslandRadius(18);
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
            bool swatch = t.Swatch.HasValue;
            ToastSwatch.Visibility = swatch ? Visibility.Visible : Visibility.Collapsed;
            if (swatch) ToastSwatchFill.Background = new SolidColorBrush(t.Swatch.Value);
            // плитка значка — карточка #0E0E10; у квадратика цвета подложки нет, у него своя рамка
            ToastIconBox.Background = swatch ? Brushes.Transparent : new SolidColorBrush(Color.FromRgb(0x0E, 0x0E, 0x10));
            ToastTitle.Foreground = (Brush)FindResource("Fg");
            ToastSpeed.Foreground = (Brush)FindResource("Fg");
            ToastIcon.Visibility = t.Image == null && !weatherIcon && !swatch ? Visibility.Visible : Visibility.Collapsed;
            bool download = t.Kind == ToastKind.Download && t.Progress.HasValue;
            ToastRingTrack.Visibility = download ? Visibility.Visible : Visibility.Collapsed;
            ToastRing.Data = download ? ProgressArc(Math.Clamp(t.Progress.Value, 0, 1), 18, 18, 17) : null;
            ToastRing.Visibility = download ? Visibility.Visible : Visibility.Collapsed;
            bool compact = string.IsNullOrEmpty(t.Subtitle) && !t.Progress.HasValue;
            // плитка-значок: скруглённый квадрат 36 (одна строка) / 40 (две строки) / 48 (пипетка), как в макете
            double tile = swatch ? 48 : download ? 40 : compact ? 36 : 40;
            ToastIconBox.Width = ToastIconBox.Height = tile;
            ToastIconBox.CornerRadius = new CornerRadius(download || t.RoundImage ? tile / 2 : swatch ? 14 : 12);
            ToastIcon.Width = ToastIcon.Height = tile >= 40 ? 18 : 17;
            if (download) ToastRing.Data = ProgressArc(Math.Clamp(t.Progress.Value, 0, 1), tile / 2, tile / 2, tile / 2 - 1);
            ToastBar.Visibility = t.Progress.HasValue && !download ? Visibility.Visible : Visibility.Collapsed;
            ToastFolderButton.Tag = t;
            bool hasFolder = (!string.IsNullOrWhiteSpace(t.FolderPath) && Directory.Exists(t.FolderPath)) || (!string.IsNullOrEmpty(t.FilePath) && File.Exists(t.FilePath));
            ToastFolderButton.Visibility = hasFolder ? Visibility.Visible : Visibility.Collapsed;
            ToastEditButton.Tag = t;
            ToastEditButton.Visibility = t.EditImage != null ? Visibility.Visible : Visibility.Collapsed;
            PlayToastStatus(t);

            double textLen = Math.Max(t.Title.Length + (t.SpeedText?.Length ?? 0) + 2, (t.Subtitle ?? "").Length * 0.86);
            double w = Math.Clamp(tile + 64 + textLen * 8.3, compact ? 230 : 280, 600);
            if (t.Progress.HasValue) w = Math.Max(w, 360);
            if (ToastFolderButton.Visibility == Visibility.Visible || ToastEditButton.Visibility == Visibility.Visible) w += 38;
            double h = swatch ? 72 : compact ? 56 : 64;
            if (ToastFolderButton.Visibility == Visibility.Visible && ToastEditButton.Visibility == Visibility.Visible) h = Math.Max(h, 70);
            // запись голоса — вместо полоски волна, как в ChatGPT
            bool voiceWave = t.ReplaceKey == "voice" && AppHost.Voice?.Recording == true;
            if (voiceWave && VoiceWave.Visibility != Visibility.Visible) VoiceWave.Clear();
            VoiceWave.Visibility = voiceWave ? Visibility.Visible : Visibility.Collapsed;
            if (voiceWave) { ToastBar.Visibility = Visibility.Collapsed; h += 18; w = Math.Max(w, 320); }
            double imageSize = tile;
            // обложка игры — круглая миниатюра (заполняет круг, без полос), остальные картинки — скруглённый квадрат
            ToastImage.Stretch = t.RoundImage ? Stretch.UniformToFill : Stretch.Uniform;
            ToastImage.Clip = t.Image == null ? null : t.RoundImage
                ? new EllipseGeometry(new Point(imageSize / 2, imageSize / 2), imageSize / 2, imageSize / 2)
                : new RectangleGeometry(new Rect(1, 1, imageSize - 2, imageSize - 2), 6, 6);
            if (t.Progress.HasValue)
            {
                double barW = w - 16 * 2 - tile - 12;
                ToastBarFill.BeginAnimation(WidthProperty, new DoubleAnimation(Math.Clamp(t.Progress.Value, 0, 1) * barW, TimeSpan.FromMilliseconds(160)));
            }
            AnimateSize(w, h, 360);
            PlayToastEntrance(t);
        }

        private string _lastToastKey;

        /// <summary>
        /// Появление содержимого всплывашки: съезжает сверху и проявляется (360 мс, ease-out, как в макете).
        /// Повторные всплывашки того же вида (громкость колёсиком, раскладка) не анимируются заново — только обновляются.
        /// </summary>
        private void PlayToastEntrance(Toast t)
        {
            string key = t.ReplaceKey ?? t.Kind + "|" + t.Title;
            bool same = key == _lastToastKey && State == IslandState.Toast;
            _lastToastKey = key;
            if (ToastRow.RenderTransform is not TranslateTransform shift) ToastRow.RenderTransform = shift = new TranslateTransform();
            var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
            if (!same)
            {
                ToastRow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
                shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-8, 0, TimeSpan.FromMilliseconds(360)) { EasingFunction = ease });
            }
            AnimateToastGlyph(t, same);
            // пипетка: зелёная «змейка» прорисовывается по краю плитки и замыкается (~1,1 с)
            ToastSnake.Visibility = t.Swatch.HasValue ? Visibility.Visible : Visibility.Collapsed;
            if (t.Swatch.HasValue)
            {
                double perimeter = (4 * (48 - 28) + 2 * Math.PI * 14) / ToastSnake.StrokeThickness; // в толщинах линии
                ToastSnake.StrokeDashArray = new DoubleCollection { perimeter, perimeter };
                ToastSnake.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty, new DoubleAnimation(perimeter, 0, TimeSpan.FromMilliseconds(1100))
                { BeginTime = TimeSpan.FromMilliseconds(120), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
                if (ToastSwatchFill.RenderTransform is not ScaleTransform pop)
                {
                    ToastSwatchFill.RenderTransform = pop = new ScaleTransform(1, 1);
                    ToastSwatchFill.RenderTransformOrigin = new Point(0.5, 0.5);
                }
                var grow = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
                grow.KeyFrames.Add(new EasingDoubleKeyFrame(0.3, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                grow.KeyFrames.Add(new EasingDoubleKeyFrame(1.12, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260)), new QuinticEase { EasingMode = EasingMode.EaseOut }));
                grow.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420)), new SineEase { EasingMode = EasingMode.EaseInOut }));
                pop.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                pop.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            }
        }

        /// <summary>
        /// «Живые» значки: будильник — красный колокольчик раскачивается ±22°, «Не беспокоить» — луна покачивается,
        /// низкий заряд — значок пульсирует, Wi-Fi подключён и текст распознан — значок «выпрыгивает».
        /// </summary>
        private void AnimateToastGlyph(Toast t, bool same)
        {
            var scale = ToastIconScale();
            var rot = (RotateTransform)((TransformGroup)ToastIcon.RenderTransform).Children[1];
            rot.BeginAnimation(RotateTransform.AngleProperty, null);
            ToastIcon.RenderTransformOrigin = new Point(0.5, 0.5);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            if (same) return;

            if (t.Kind == ToastKind.Alarm && t.Icon == Icons.Bell)
            {
                ToastIcon.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)); // таймер и будильник — красные
                Swing(22, 450, RepeatBehavior.Forever, new Point(0.5, 0.15));
            }
            else if (t.Icon == Icons.Moon)
                Swing(10, 1200, new RepeatBehavior(2), new Point(0.5, 0.5));
            else if (t.Kind == ToastKind.Error && t.Icon == Icons.Battery)
            {
                var pulse = new DoubleAnimation(1, 0.82, TimeSpan.FromMilliseconds(500)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
            }
            else if (t.Kind == ToastKind.Success && (t.Icon == Icons.Wifi || t.Icon == Icons.Scan || t.Icon == Icons.Bluetooth))
            {
                var pop = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(520), BeginTime = TimeSpan.FromMilliseconds(120) };
                pop.KeyFrames.Add(new EasingDoubleKeyFrame(0.5, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.12, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300)), new QuinticEase { EasingMode = EasingMode.EaseOut }));
                pop.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(520)), new SineEase { EasingMode = EasingMode.EaseInOut }));
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            }
        }

        /// <summary>Поворот и масштаб значка всплывашки (одна группа трансформаций, чтобы анимации не мешали друг другу).</summary>
        private ScaleTransform ToastIconScale()
        {
            if (ToastIcon.RenderTransform is TransformGroup g && g.Children.Count == 2 && g.Children[0] is ScaleTransform existing) return existing;
            var sc = new ScaleTransform(1, 1);
            var group = new TransformGroup();
            group.Children.Add(sc);
            group.Children.Add(new RotateTransform(0));
            ToastIcon.RenderTransform = group;
            ToastIcon.RenderTransformOrigin = new Point(0.5, 0.5);
            return sc;
        }

        private void Swing(double degrees, int periodMs, RepeatBehavior repeat, Point origin)
        {
            ToastIconScale();
            var rot = (RotateTransform)((TransformGroup)ToastIcon.RenderTransform).Children[1];
            ToastIcon.RenderTransformOrigin = origin;
            var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(periodMs), RepeatBehavior = repeat };
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(degrees, KeyTime.FromPercent(0.25), ease));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(-degrees, KeyTime.FromPercent(0.75), ease));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1), ease));
            rot.BeginAnimation(RotateTransform.AngleProperty, a);
        }

        private void OnToastFolder(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (ToastFolderButton.Tag is not Toast t) return;
            try
            {
                // снимок — открыть Проводник с выделенным файлом; игра — её папку
                if (!string.IsNullOrEmpty(t.FilePath) && File.Exists(t.FilePath))
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + t.FilePath + "\"");
                else if (!string.IsNullOrWhiteSpace(t.FolderPath) && Directory.Exists(t.FolderPath))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(t.FolderPath) { UseShellExecute = true });
            }
            catch (Exception ex) { App.Trace("не удалось открыть папку: " + ex.Message); }
        }

        private void OnToastEdit(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (ToastEditButton.Tag is not Toast t || t.EditImage == null) return;
            try { new Overlays.ImageEditor(t.EditImage, t.FilePath).Show(); }
            catch (Exception ex) { App.Log("editor: " + ex); }
        }

        private static readonly System.Text.RegularExpressions.Regex FailTitle =
            new(@"^(Не |Ошибка|Тишина|Слишком коротко|Нет |✗|Лимит ИИ закончился)", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Итог действия видно сразу: получилось — значок зелёный и «подпрыгивает»,
        /// не вышло — красный и качается из стороны в сторону (как «нет» головой).
        /// </summary>
        private void PlayToastStatus(Toast t)
        {
            // надпись вместо значка (раскладка) и «включено / выключено» для переключателей
            bool badge = !string.IsNullOrEmpty(t.Badge);
            ToastBadge.Text = t.Badge ?? "";
            ToastBadge.Visibility = badge ? Visibility.Visible : Visibility.Collapsed;
            if (badge) ToastIcon.Visibility = Visibility.Collapsed;
            // косая черта — только у микрофона (своего «выключенного» значка нет); остальные выключенные просто серые
            ToastOffSlash.Visibility = t.On == false && !badge && t.Icon == Icons.Mic ? Visibility.Visible : Visibility.Collapsed;
            var grey = new SolidColorBrush(Color.FromRgb(0x8B, 0x8B, 0x95));
            if (t.On.HasValue)
            {
                double box = ToastIconBox.Width;
                ToastOffSlash.X1 = ToastOffSlash.Y1 = box * 0.26; ToastOffSlash.X2 = ToastOffSlash.Y2 = box * 0.74;
                // по макету: включено — белая плитка и чёрный значок; выключено — тёмная плитка, серые значок и текст
                ToastIconBox.Background = t.On.Value ? (Brush)FindResource("Fg") : new SolidColorBrush(Color.FromRgb(0x0E, 0x0E, 0x10));
                ToastIcon.Foreground = t.On.Value ? Brushes.Black : grey;
                if (!t.On.Value) { ToastTitle.Foreground = grey; ToastSpeed.Foreground = grey; }
            }
            if (badge) ToastBadge.Foreground = (Brush)FindResource("Fg"); // раскладка: код «RU» белым на тёмной плитке

            bool error = t.Kind == ToastKind.Error || (t.Kind == ToastKind.Info && FailTitle.IsMatch(t.Title ?? ""));
            bool success = !error && t.Kind == ToastKind.Success;
            // цвет — только у значка, не у фона (как в макете)
            if (!t.On.HasValue && !badge)
            {
                if (error) ToastIcon.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
                else if (success && t.Icon != Icons.Pipette) ToastIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
            }
            // переключатель «включился» — короткий «прыжок», чтобы смена была заметна
            if (t.On == true || badge) success = true;
            if (ToastIconBox.RenderTransform is not TransformGroup tg)
            {
                tg = new TransformGroup();
                tg.Children.Add(new ScaleTransform(1, 1));
                tg.Children.Add(new TranslateTransform());
                ToastIconBox.RenderTransform = tg;
                ToastIconBox.RenderTransformOrigin = new Point(0.5, 0.5);
            }
            var sc = (ScaleTransform)tg.Children[0];
            var tr = (TranslateTransform)tg.Children[1];
            sc.BeginAnimation(ScaleTransform.ScaleXProperty, null); sc.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            tr.BeginAnimation(TranslateTransform.XProperty, null);
            // Анимации стартуют чуть позже, чем чёлка начинает расти (иначе две анимации в первый кадр — рывок),
            // и движутся мягко: без резких скачков масштаба, тряска затухает.
            var delay = TimeSpan.FromMilliseconds(110);
            if (error)
            {
                var shake = new DoubleAnimationUsingKeyFrames { BeginTime = delay, Duration = TimeSpan.FromMilliseconds(300) };
                double[] xs = { 0, -4, 4, -3, 2, 0 };
                for (int i = 0; i < xs.Length; i++)
                    shake.KeyFrames.Add(new EasingDoubleKeyFrame(xs[i], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(i * 60)),
                        new SineEase { EasingMode = EasingMode.EaseInOut }));
                tr.BeginAnimation(TranslateTransform.XProperty, shake);
            }
            else if (success)
            {
                var pop = new DoubleAnimationUsingKeyFrames { BeginTime = delay, Duration = TimeSpan.FromMilliseconds(420) };
                pop.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.12, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(190)), new SineEase { EasingMode = EasingMode.EaseOut }));
                pop.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420)), new SineEase { EasingMode = EasingMode.EaseInOut }));
                sc.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
                sc.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            }
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
                {
                    // свёрнутая чёлка показывает «Установлена» прямо в пилюле загрузки (кольцо зеленеет, кнопка папки)
                    if (CanShowDownloadPill())
                    {
                        ShowDownloadDone(new DownloadState(completed.Name, "Steam", 1, null, null, completed.Icon, 0, completed.Total, true, completed.FolderPath));
                        continue;
                    }
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
                }

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
                foreach (var completed in result.browser.Completed)
                {
                    // о том же файле уже сообщили по временному файлу (история браузера пишется позже)
                    bool shown = _recentDone.TryGetValue(completed.FileName ?? "", out var shownAt) && DateTime.Now - shownAt < TimeSpan.FromMinutes(1);
                    _recentDone[completed.FileName ?? ""] = DateTime.Now;
                    if (shown) continue;
                    if (CanShowDownloadPill())
                    {
                        ShowDownloadDone(new DownloadState(completed.FileName, completed.Browser, 1, null, null, null, 0, completed.TotalBytes, true,
                            Services.PartialDownloadService.Folders().FirstOrDefault()));
                        continue;
                    }
                    ToastBus.Show(new Toast
                    {
                        Kind = ToastKind.Success,
                        Title = "Загрузка завершена",
                        Subtitle = completed.Browser + " · " + completed.FileName,
                        Icon = Icons.Check,
                        FolderPath = Services.PartialDownloadService.Folders().FirstOrDefault(),
                        Important = true,
                        Duration = TimeSpan.FromSeconds(7)
                    });
                }

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
                    SetDownload(new DownloadState(steam.Name, "Steam", steam.Progress, steam.SpeedText, steam.EtaText, steam.Icon, steam.Got, steam.Total));
                else if (download != null)
                {
                    // скорость из истории браузера рваная — если есть недокачанный файл, берём его скорость (по росту файла)
                    var firstPartial = result.partials.FirstOrDefault();
                    var speed = firstPartial != null && firstPartial.BytesPerSecond > 0 ? firstPartial.SpeedText : download.SpeedText;
                    SetDownload(new DownloadState(download.FileName, download.Browser, download.Progress, speed, download.EtaText, null, download.ReceivedBytes, download.TotalBytes));
                }
                else if (result.partials.FirstOrDefault() is { } part)
                    SetDownload(new DownloadState(part.Name, part.Browser, null, part.SpeedText, null, null, part.Bytes)); // размер неизвестен — «крутилка» и сколько скачано
                else SetDownload(null);
            }
            catch (Exception ex) { App.Trace("ошибка отслеживания загрузок: " + ex.Message); }
            finally { _downloadPollInProgress = false; }
        }

        /// <summary>Текущая загрузка для пилюли: название, источник, доля 0..1 (null — размер неизвестен), скорость, остаток, обложка.</summary>
        private readonly Dictionary<string, DateTime> _recentDone = new(StringComparer.OrdinalIgnoreCase);

        private sealed record DownloadState(string Name, string Source, double? Progress, string Speed, string Eta, ImageSource Art,
            long Got = 0, long Total = 0, bool Done = false, string Folder = null);
        private DownloadState _download;
        private double _dlShown;      // плавно догоняет настоящий прогресс
        private double _dlSpin;       // вращение «неизвестного» прогресса
        private DownloadState _dlDone; // «Установлена» держится в пилюле 8 с
        private readonly DispatcherTimer _dlDoneTimer = new() { Interval = TimeSpan.FromSeconds(8) };
        private bool _dlArrowPulse;
        private static readonly Color DlWhite = Color.FromRgb(0xEC, 0xEC, 0xF1), DlGreen = Color.FromRgb(0x4A, 0xDE, 0x80);

        /// <summary>Пилюля загрузки видна, только когда чёлка свёрнута и не занята музыкой или таймером.</summary>
        private bool CanShowDownloadPill()
        {
            if (State != IslandState.Collapsed) return false;
            var m = AppHost.Media?.Current;
            bool media = AppSettings.Current.MediaEnabled && m != null && m.HasSession && m.IsPlaying && !string.IsNullOrEmpty(m.Title);
            bool timer = AppHost.Timer != null && (AppHost.Timer.Running || AppHost.Timer.Paused);
            return !media && !timer;
        }

        private void ShowDownloadDone(DownloadState d)
        {
            _dlDone = d;
            _dlDoneTimer.Stop();
            _dlDoneTimer.Start();
            SetDownload(d);
        }

        private void ClearDownloadDone()
        {
            _dlDoneTimer.Stop();
            if (_dlDone == null) return;
            _dlDone = null;
            SetDownload(null);
            RefreshSteamDownload(); // если качается что-то ещё — вернётся на следующем опросе
        }

        private void OnDownloadFolder(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            var folder = _download?.Folder;
            try
            {
                if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
            }
            catch (Exception ex) { App.Trace("не удалось открыть папку: " + ex.Message); }
            ClearDownloadDone(); // клик — открыть папку и свернуть пилюлю
        }

        private static (double div, string unit) SizeUnit(double v) =>
            v >= 1073741824 ? (1073741824d, "ГБ") : v >= 1048576 ? (1048576d, "МБ") : (1024d, "КБ");

        private static string SizeText(double v)
        {
            var (div, unit) = SizeUnit(v);
            return (v / div).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) + " " + unit;
        }

        /// <summary>«5,1 из 12,4 ГБ» — обе цифры в единицах полного размера.</summary>
        private static string SizePair(long got, long total)
        {
            var (div, unit) = SizeUnit(total);
            var c = System.Globalization.CultureInfo.CurrentCulture;
            return (got / div).ToString("0.#", c) + " из " + (total / div).ToString("0.#", c) + " " + unit;
        }

        private void SetDownload(DownloadState d)
        {
            // пока висит «Установлена» — новые опросы её не перебивают
            if (_dlDone != null && !ReferenceEquals(d, _dlDone)) d = _dlDone;
            bool had = _download != null;
            bool wasDone = _download?.Done == true;
            _download = d;
            // пока идёт загрузка — частые обновления (скорость, остаток); в простое — редкие, новости придут по событиям
            var interval = TimeSpan.FromSeconds(d != null && !d.Done ? 1 : 8);
            if (_steamTimer.Interval != interval) _steamTimer.Interval = interval;
            if (d == null)
            {
                MediaDownload.Visibility = Visibility.Collapsed;
                PulseDownloadArrow(false);
                if (had && State == IslandState.Collapsed) ShowCollapsedContent(false);
                return;
            }
            if (!had || wasDone) _dlShown = 0;
            DlName.Text = d.Name;
            DlArt.Source = d.Art;
            DlIcon.Visibility = d.Art == null ? Visibility.Visible : Visibility.Collapsed;

            if (d.Done)
            {
                DlSpeed.Text = "";
                DlArrow.Visibility = Visibility.Collapsed;
                PulseDownloadArrow(false);
                DlSub.Text = (d.Source == "Steam" ? "Установлена" : "Загружено") + (d.Total > 0 ? " · " + SizeText(d.Total) : "");
                DlPercent.Visibility = Visibility.Collapsed;
                DlDonePanel.Visibility = Visibility.Visible;
                DlFolderButton.Visibility = !string.IsNullOrWhiteSpace(d.Folder) ? Visibility.Visible : Visibility.Collapsed;
                MediaDownload.Text = "✓";
                if (!wasDone) PlayDownloadDone();
            }
            else
            {
                if (wasDone) DlRing.Stroke = new SolidColorBrush(DlWhite);
                DlDonePanel.Visibility = Visibility.Collapsed;
                string speed = d.Speed ?? "";
                DlSpeed.Text = speed;
                DlArrow.Visibility = speed.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                PulseDownloadArrow(speed.Length > 0);
                var rest = new List<string>();
                if (speed.Length == 0) rest.Add(d.Source);
                if (d.Total > 0) rest.Add(SizePair(Math.Min(d.Got, d.Total), d.Total));
                else if (d.Got > 0) rest.Add(SizeText(d.Got));
                if (!string.IsNullOrEmpty(d.Eta)) rest.Add("осталось " + d.Eta.TrimStart('~'));
                DlSub.Text = rest.Count == 0 ? "" : (speed.Length > 0 ? " · " : "") + string.Join(" · ", rest);
                string pct = d.Progress.HasValue ? (int)Math.Floor(Math.Clamp(d.Progress.Value, 0, 1) * 100) + "%" : "";
                DlPercent.Text = pct;
                DlPercent.Visibility = pct.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                MediaDownload.Text = "↓ " + (pct.Length > 0 ? pct : "…");
            }
            if (State == IslandState.Collapsed) ShowCollapsedContent(had);
        }

        /// <summary>Стрелка у скорости «дышит», пока идёт загрузка.</summary>
        private void PulseDownloadArrow(bool on)
        {
            if (on == _dlArrowPulse) return;
            _dlArrowPulse = on;
            DlArrow.BeginAnimation(OpacityProperty, on
                ? new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(800)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } }
                : null);
        }

        /// <summary>Загрузка завершена: кольцо зеленеет, рисуется галочка, справа выезжает кнопка папки.</summary>
        private void PlayDownloadDone()
        {
            var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
            var ring = new SolidColorBrush(DlWhite);
            DlRing.Stroke = ring;
            ring.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(DlGreen, TimeSpan.FromMilliseconds(300)));

            const double checkLen = 11; // длина галочки в толщинах штриха (≈ 27 px / 2,5)
            DlCheck.StrokeDashArray = new DoubleCollection { checkLen, checkLen };
            DlCheck.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty,
                new DoubleAnimation(checkLen, 0, TimeSpan.FromMilliseconds(500)) { BeginTime = TimeSpan.FromMilliseconds(150), EasingFunction = ease });

            DlFolderShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
            DlFolderButton.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)));
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
                    DlRing.Data = ProgressArc(Math.Max(0.01, _dlShown), 26, 26, 24.5);
                }
            }
            else
            {
                _dlSpin = (_dlSpin + dt * 300) % 360;
                if (DlRing.RenderTransform is not RotateTransform rt) DlRing.RenderTransform = rt = new RotateTransform(0, 26, 26);
                rt.Angle = _dlSpin;
                if (DlRing.Tag as string != "spin") { DlRing.Data = ProgressArc(0.28, 26, 26, 24.5); DlRing.Tag = "spin"; }
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
            if (DateTime.Now < _tipUntil || _openMenus > 0 || CursorOverOurPopup()) return;
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

        /// <summary>Последняя ошибка загрузки погоды (null — всё хорошо) — вкладка «Погода» показывает её вместо вечного «Загружаю…».</summary>
        public string WeatherError { get; private set; }

        public async Task RefreshWeather()
        {
            try
            {
                var s = AppSettings.Current;
                var weather = await WeatherService.LoadAsync(s.Latitude, s.Longitude, s.City);
                WeatherError = null;
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
            catch (Exception ex) { WeatherError = ex.Message; App.Log("weather: " + ex.Message); }
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
