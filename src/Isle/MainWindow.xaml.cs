using System;
using System.Collections.Generic;
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
        private const int HkToggle = 1, HkCommand = 2, HkClipboard = 3, HkShot = 4, HkVoice = 5;
        private static readonly Size ExpandedSize = new(900, 470);

        private HwndSource _src;
        private IntPtr _hwnd;
        private readonly Dictionary<string, FrameworkElement> _views = new();
        private readonly Queue<Toast> _toasts = new();
        private Toast _currentToast;
        private readonly DispatcherTimer _toastTimer = new();
        private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer _anim = new() { Interval = TimeSpan.FromMilliseconds(45) };
        private readonly DispatcherTimer _hoverOpen = new() { Interval = TimeSpan.FromMilliseconds(220) };
        private readonly DispatcherTimer _leaveClose = new() { Interval = TimeSpan.FromMilliseconds(380) };
        private readonly DispatcherTimer _oledHide = new();
        private readonly DispatcherTimer _weatherTimer = new() { Interval = TimeSpan.FromMinutes(20) };
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
            _anim.Tick += (_, __) => Animate();
            _hoverOpen.Tick += (_, __) => { _hoverOpen.Stop(); if (State != IslandState.Expanded && Island.IsMouseOver) Expand(null); };
            _leaveClose.Tick += (_, __) => { _leaveClose.Stop(); if (!Island.IsMouseOver && !IsActive) Collapse(); };
            _oledHide.Tick += (_, __) => { _oledHide.Stop(); TryOledHide(); };
            _weatherTimer.Tick += async (_, __) => await RefreshWeather();

            Island.MouseEnter += (_, __) =>
            {
                _leaveClose.Stop();
                _oledHide.Stop();
                if (State == IslandState.Collapsed) AnimateSize(Island.Width + 16, 38, 260); // остров «тянется» к курсору
                if (State != IslandState.Expanded && AppSettings.Current.ExpandOnHover) _hoverOpen.Start();
            };
            Island.MouseLeave += (_, __) =>
            {
                _hoverOpen.Stop();
                if (State == IslandState.Expanded && !_pinned && !IsActive && !SuppressAutoCollapse) _leaveClose.Start();
                if (State == IslandState.Collapsed) ShowCollapsedContent(false);
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
                if (State == IslandState.Expanded && !_pinned && !SuppressAutoCollapse && !Island.IsMouseOver) Collapse();
            };
            KeyDown += (_, e) => { if (e.Key == Key.Escape && State == IslandState.Expanded) { Collapse(); e.Handled = true; } };

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
                _anim.Start();
                ApplyAppearance();
                AppHost.Watchers.Start();
                AppHost.Vpn.Start();
                await AppHost.Media.InitAsync();
                _ = AppHost.Notifications.StartAsync();
                _weatherTimer.Start();
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

            Win32.RegisterHotKey(_hwnd, HkToggle, Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_NOREPEAT, 0x20);   // Ctrl+Alt+Space
            Win32.RegisterHotKey(_hwnd, HkCommand, Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_NOREPEAT, 0x4B);  // Ctrl+Alt+K
            Win32.RegisterHotKey(_hwnd, HkClipboard, Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_NOREPEAT, 0x56); // Ctrl+Alt+V
            Win32.RegisterHotKey(_hwnd, HkShot, Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_NOREPEAT, 0x53);     // Ctrl+Alt+S
            Win32.RegisterHotKey(_hwnd, HkVoice, Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_NOREPEAT, 0x44);    // Ctrl+Alt+D
        }

        protected override void OnClosed(EventArgs e)
        {
            foreach (var id in new[] { HkToggle, HkCommand, HkClipboard, HkShot, HkVoice }) Win32.UnregisterHotKey(_hwnd, id);
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
                    case HkShot:
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
            if (State != IslandState.Expanded)
            {
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
            DropHint.CornerRadius = Island.CornerRadius;
            AnimateSize(ExpandedSize.Width, ExpandedSize.Height, 420);
            Fade(ExpandedView, 1, 260, 90);
            (TabHost.Content as Views.IIslandView)?.OnShown();
        }

        public void Collapse()
        {
            if (State == IslandState.Collapsed) return;
            _leaveClose.Stop();
            if (FocusManager.GetFocusedElement(this) is IInputElement) Keyboard.ClearFocus();
            MakeNoActivate(true);
            State = IslandState.Collapsed;
            ExpandedView.Opacity = 0;
            ExpandedView.Visibility = Visibility.Collapsed;
            Island.CornerRadius = new CornerRadius(0, 0, 18, 18);
            DropHint.CornerRadius = Island.CornerRadius;
            (_views.TryGetValue("home", out var hv) ? hv as Views.HomeView : null)?.HideCommand();
            if (_toasts.Count > 0) { NextToast(); return; }
            ShowCollapsedContent(false);
            ScheduleOledHide(TimeSpan.FromSeconds(1.2));
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

            TimerRow.Visibility = timer ? Visibility.Visible : Visibility.Collapsed;
            MediaRow.Visibility = media ? Visibility.Visible : Visibility.Collapsed;
            IdleRow.Visibility = !timer && !media ? Visibility.Visible : Visibility.Collapsed;

            double w = timer ? 240 : media ? (MediaTimer.Visibility == Visibility.Visible ? 380 : 320) : (string.IsNullOrEmpty(WeatherMini.Text) ? 170 : 210) + (BatteryMini.Visibility == Visibility.Visible ? 52 : 0) + (RecChip.Visibility == Visibility.Visible ? 60 : 0);
            if (onlyIfChanged && Math.Abs(Island.Width - w) < 1 && Math.Abs(Island.Height - 34) < 1) return;
            AnimateSize(w, 34, 380);
        }

        private void UpdateMini()
        {
            var m = AppHost.Media.Current;
            MiniTitle.Text = string.IsNullOrEmpty(m.Artist) ? m.Title : m.Title + " — " + m.Artist;
            MiniCover.Source = m.Cover;
        }

        private void AnimateSize(double w, double h, int ms)
        {
            var ease = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut };
            Island.BeginAnimation(WidthProperty, new DoubleAnimation(w, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
            Island.BeginAnimation(HeightProperty, new DoubleAnimation(h, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
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

        public void SelectTab(string tab)
        {
            _currentTab = tab;
            var rb = tab switch
            {
                "clip" => TabClip, "shelf" => TabShelf, "text" => TabText, "chat" => TabChat, "notes" => TabNotes,
                "weather" => TabWeather, "controls" => TabControls, _ => TabHome
            };
            if (rb.IsChecked != true) rb.IsChecked = true; // вызовет OnTab ещё раз — это безопасно
            var view = GetView(tab);
            if (!ReferenceEquals(TabHost.Content, view))
            {
                TabHost.Content = view;
                view.Opacity = 0;
                Fade(view, 1, 200);
                TabSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
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

        private void OnLangClick(object sender, RoutedEventArgs e)
        {
            var target = Win32.GetForegroundWindow();
            if (target == _hwnd || target == IntPtr.Zero) target = PreviousForeground;
            var code = Win32.SwitchToNextLayout(target);
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
        }

        /// <summary>Акцент, прозрачность и персонаж из настроек.</summary>
        public void ApplyAppearance()
        {
            var s = AppSettings.Current;
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(s.AccentHex);
                if (Application.Current.Resources["Accent"] is SolidColorBrush b && !b.IsFrozen) b.Color = c;
                else Application.Current.Resources["Accent"] = new SolidColorBrush(c);
                foreach (var bar in new[] { Bar0, Bar1, Bar2, Bar3 }) bar.Background = new SolidColorBrush(c);
            }
            catch { }
            byte a = (byte)Math.Round(255 * Math.Clamp(s.IslandOpacity, 0.5, 1.0));
            Island.Background = new SolidColorBrush(Color.FromArgb(a, 0, 0, 0));
            Edge.Fill = Island.Background;
            IslandShadow.Opacity = s.IslandOpacity;
            Buddy.Visibility = s.Character == "pixel" ? Visibility.Visible : Visibility.Collapsed;
            Eyes.Visibility = s.Character == "eyes" ? Visibility.Visible : Visibility.Collapsed;
            ApplyScale();
            if (State == IslandState.Collapsed) ShowCollapsedContent(false);
        }

        private bool _aiBusy;

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
            if (State == IslandState.Expanded && !t.Important) return;
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
                _currentToast = null;
                State = IslandState.Collapsed;
                ShowCollapsedContent(false);
                ScheduleOledHide();
                return;
            }
            var t = _toasts.Dequeue();
            if (_oledHidden) OledReveal(false);
            State = IslandState.Toast;
            CollapsedView.Visibility = Visibility.Visible;
            IdleRow.Visibility = MediaRow.Visibility = TimerRow.Visibility = Visibility.Collapsed;
            ToastRow.Visibility = Visibility.Visible;
            RenderToast(t);
            _toastTimer.Interval = t.Duration;
            _toastTimer.Start();
        }

        private void RenderToast(Toast t)
        {
            _currentToast = t;
            if (t.Kind == ToastKind.Success) { Buddy.Cheer(); Edge.DropBead(); }
            ToastTitle.Text = t.Title;
            ToastSub.Text = t.Subtitle ?? "";
            ToastSub.Visibility = string.IsNullOrEmpty(t.Subtitle) ? Visibility.Collapsed : Visibility.Visible;
            ToastIcon.Data = t.Icon;
            ToastIcon.Foreground = t.Icon == Icons.Pipette ? new SolidColorBrush(t.Accent) : (Brush)FindResource("Fg");
            ToastImage.Source = t.Image;
            ToastIcon.Visibility = t.Image == null ? Visibility.Visible : Visibility.Collapsed;
            bool compact = string.IsNullOrEmpty(t.Subtitle) && !t.Progress.HasValue;
            ToastIconBox.Width = ToastIconBox.Height = compact ? 26 : 36;
            ToastIconBox.CornerRadius = new CornerRadius(compact ? 13 : 18);
            ToastBar.Visibility = t.Progress.HasValue ? Visibility.Visible : Visibility.Collapsed;

            double textLen = Math.Max(t.Title.Length, (t.Subtitle ?? "").Length);
            double w = Math.Clamp(120 + textLen * 7.4, compact ? 210 : 260, 600);
            if (t.Progress.HasValue) w = Math.Max(w, 280);
            double h = compact ? 40 : (string.IsNullOrEmpty(t.Subtitle) ? 52 : 64);
            if (t.Progress.HasValue)
            {
                double barW = w - 16 * 2 - 36 - 12;
                ToastBarFill.BeginAnimation(WidthProperty, new DoubleAnimation(Math.Clamp(t.Progress.Value, 0, 1) * barW, TimeSpan.FromMilliseconds(160)));
            }
            AnimateSize(w, h, 360);
        }

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

        private void KeepOnTop()
        {
            if (_hwnd == IntPtr.Zero || !IsVisible) return;
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
            ClockText.Text = DateTime.Now.ToString(AppSettings.Current.Use24h ? "HH:mm" : "h:mm");
            if (AppHost.Timer != null)
            {
                MiniTimer.Text = (AppHost.Timer.Paused ? "пауза · " : "") + TimerService.Format(AppHost.Timer.Left);
                MediaTimer.Text = (AppHost.Timer.Paused ? "❚❚ " : "") + TimerService.Format(AppHost.Timer.Left);
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

        private int _specIdle;

        private void Animate()
        {
            CheckOutsideClick();

            // спектр включаем только пока играет музыка и чёлка свёрнута
            var md = AppHost.Media?.Current;
            bool wantSpectrum = md != null && md.HasSession && md.IsPlaying && State != IslandState.Expanded && !_oledHidden && Scaler.Visibility == Visibility.Visible;
            if (wantSpectrum) { _specIdle = 0; if (!AppHost.Spectrum.Running) AppHost.Spectrum.Start(); }
            else if (AppHost.Spectrum.Running && ++_specIdle > 60) AppHost.Spectrum.Stop();
            Edge.Active = State != IslandState.Expanded && !_oledHidden;
            Edge.Away = IdleTime() > TimeSpan.FromMinutes(2);
            Edge.Raining = AppHost.Weather != null && (AppHost.Weather.Code >= 51 && AppHost.Weather.Code <= 67 || AppHost.Weather.Code >= 80);

            if (Scaler.Visibility != Visibility.Visible || _oledHidden) return;
            if (RecChip.Visibility == Visibility.Visible) RecDot.Opacity = (DateTime.Now.Millisecond / 500) % 2 == 0 ? 1 : 0.25;
            if (Buddy.Visibility == Visibility.Visible)
            {
                var m = AppHost.Media?.Current;
                bool playing = m != null && m.HasSession && m.IsPlaying;
                Buddy.Beat = playing ? (AppHost.Audio?.Peak ?? 0) : 0;
                Buddy.Mood = _aiBusy ? Helpers.Mood.Thinking
                    : State == IslandState.Toast && _currentToast?.Kind == ToastKind.Notification ? Helpers.Mood.Attention
                    : playing ? Helpers.Mood.Music
                    : IdleTime() > TimeSpan.FromMinutes(5) ? Helpers.Mood.Sleep
                    : Helpers.Mood.Idle;
            }
            if (MediaRow.Visibility == Visibility.Visible && CollapsedView.Visibility == Visibility.Visible)
            {
                var bars = new[] { Bar0, Bar1, Bar2, Bar3 };
                var spec = AppHost.Spectrum.Running ? AppHost.Spectrum.Snapshot() : null;
                float peak = AppHost.Audio?.Peak ?? 0;
                for (int i = 0; i < bars.Length; i++)
                {
                    double level;
                    if (spec != null)
                    {
                        // 4 полоски = 4 части спектра: басы → высокие
                        int from = i * spec.Length / 4, to = (i + 1) * spec.Length / 4;
                        double mx = 0;
                        for (int b = from; b < to; b++) mx = Math.Max(mx, spec[b]);
                        level = mx;
                    }
                    else level = peak * (0.55 + _rnd.NextDouble() * 0.6);
                    double target = 3 + Math.Min(13, level * 14);
                    bars[i].Height = bars[i].Height + (target - bars[i].Height) * 0.55;
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
                AppHost.SetWeather(await WeatherService.LoadAsync(s.Latitude, s.Longitude, s.City));
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
            Width = 960 * sc;
            Height = 620 * sc;
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
