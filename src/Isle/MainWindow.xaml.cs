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
        private const int HkToggle = 1, HkCommand = 2, HkClipboard = 3, HkShot = 4;
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
        private bool _pinned;
        private string _currentTab = "home";

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

            ToastBus.Raised += t => Dispatcher.InvokeAsync(() => EnqueueToast(t));
            AppHost.Media.Changed += () => { if (State == IslandState.Collapsed) ShowCollapsedContent(true); UpdateMini(); };
            AppHost.Timer.Changed += () =>
            {
                MiniTimer.Text = TimerService.Format(AppHost.Timer.Left);
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
                if (State != IslandState.Expanded && AppSettings.Current.ExpandOnHover) _hoverOpen.Start();
            };
            Island.MouseLeave += (_, __) =>
            {
                _hoverOpen.Stop();
                if (State == IslandState.Expanded && !_pinned && !IsActive && !SuppressAutoCollapse) _leaveClose.Start();
                if (State != IslandState.Expanded) ScheduleOledHide();
            };
            Island.PreviewMouseLeftButtonDown += OnIslandMouseDown;
            Island.MouseRightButtonUp += OnIslandRightClick;
            EdgeStrip.MouseEnter += (_, __) => OledReveal();
            Deactivated += (_, __) =>
            {
                MakeNoActivate(true);
                if (State == IslandState.Expanded && !_pinned && !SuppressAutoCollapse && !Island.IsMouseOver) Collapse();
            };
            KeyDown += (_, e) => { if (e.Key == Key.Escape && State == IslandState.Expanded) { Collapse(); e.Handled = true; } };

            Island.DragEnter += (_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) { DropHint.Visibility = Visibility.Visible; e.Effects = DragDropEffects.Link; } };
            Island.DragLeave += (_, __) => DropHint.Visibility = Visibility.Collapsed;
            Island.Drop += OnDrop;

            Loaded += async (_, __) =>
            {
                PlaceWindow();
                Clock();
                _clock.Start();
                _anim.Start();
                AppHost.Watchers.Start();
                await AppHost.Media.InitAsync();
                _ = AppHost.Notifications.StartAsync();
                _weatherTimer.Start();
                await RefreshWeather();
                ApplyOledMode();
            };
        }

        // ===================== Окно и Win32 =====================

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;
            _src = HwndSource.FromHwnd(_hwnd);
            var ex = Win32.GetWindowLong(_hwnd, Win32.GWL_EXSTYLE);
            Win32.SetWindowLong(_hwnd, Win32.GWL_EXSTYLE, ex | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);
            _src.AddHook(WndProc);
            AppHost.Clipboard.Attach(_src);

            Win32.RegisterHotKey(_hwnd, HkToggle, Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_NOREPEAT, 0x20);   // Ctrl+Alt+Space
            Win32.RegisterHotKey(_hwnd, HkCommand, Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_NOREPEAT, 0x4B);  // Ctrl+Alt+K
            Win32.RegisterHotKey(_hwnd, HkClipboard, Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_NOREPEAT, 0x56); // Ctrl+Alt+V
            Win32.RegisterHotKey(_hwnd, HkShot, Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_NOREPEAT, 0x53);     // Ctrl+Alt+S
        }

        protected override void OnClosed(EventArgs e)
        {
            foreach (var id in new[] { HkToggle, HkCommand, HkClipboard, HkShot }) Win32.UnregisterHotKey(_hwnd, id);
            AppHost.Audio?.Dispose();
            base.OnClosed(e);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
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
                    case HkShot:
                        _ = Tools.RegionShot();
                        break;
                }
            }
            return IntPtr.Zero;
        }

        private void MakeNoActivate(bool on)
        {
            if (_hwnd == IntPtr.Zero) return;
            var ex = Win32.GetWindowLong(_hwnd, Win32.GWL_EXSTYLE);
            ex = on ? ex | Win32.WS_EX_NOACTIVATE : ex & ~Win32.WS_EX_NOACTIVATE;
            Win32.SetWindowLong(_hwnd, Win32.GWL_EXSTYLE, ex);
        }

        /// <summary>Даём окну фокус клавиатуры (нужно, чтобы печатать в полях).</summary>
        public void FocusIsland()
        {
            var fg = Win32.GetForegroundWindow();
            if (fg != _hwnd && fg != IntPtr.Zero) PreviousForeground = fg;
            MakeNoActivate(false);
            Activate();
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
                Control input = (Control)FindParent<TextBox>(d) ?? FindParent<PasswordBox>(d);
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
            settings.Click += (_, __) => Expand("settings");
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
            if (_toasts.Count > 0) { NextToast(); return; }
            ShowCollapsedContent(false);
            ScheduleOledHide();
        }

        private void ShowCollapsedContent(bool onlyIfChanged)
        {
            if (State != IslandState.Collapsed) return;
            CollapsedView.Visibility = Visibility.Visible;
            ToastRow.Visibility = Visibility.Collapsed;
            var m = AppHost.Media?.Current;
            bool timer = AppHost.Timer != null && (AppHost.Timer.Running || AppHost.Timer.Paused);
            bool media = !timer && AppSettings.Current.MediaEnabled && m != null && m.HasSession && m.IsPlaying && !string.IsNullOrEmpty(m.Title);

            TimerRow.Visibility = timer ? Visibility.Visible : Visibility.Collapsed;
            MediaRow.Visibility = media ? Visibility.Visible : Visibility.Collapsed;
            IdleRow.Visibility = !timer && !media ? Visibility.Visible : Visibility.Collapsed;

            double w = timer ? 240 : media ? 320 : (string.IsNullOrEmpty(WeatherMini.Text) ? 170 : 210);
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
            if (sender == TabHome) SelectTab("home");
            else if (sender == TabClip) SelectTab("clip");
            else if (sender == TabShelf) SelectTab("shelf");
            else if (sender == TabText) SelectTab("text");
            else if (sender == TabChat) SelectTab("chat");
            else if (sender == TabNotes) SelectTab("notes");
            else if (sender == TabWeather) SelectTab("weather");
            else if (sender == TabControls) SelectTab("controls");
            else if (sender == TabSettings) SelectTab("settings");
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
                "settings" => new Views.SettingsView(),
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
                "weather" => TabWeather, "controls" => TabControls, "settings" => TabSettings, _ => TabHome
            };
            if (rb.IsChecked != true) rb.IsChecked = true; // вызовет OnTab ещё раз — это безопасно
            var view = GetView(tab);
            if (!ReferenceEquals(TabHost.Content, view))
            {
                TabHost.Content = view;
                view.Opacity = 0;
                Fade(view, 1, 180);
                if (State == IslandState.Expanded) (view as Views.IIslandView)?.OnShown();
            }
        }

        private void OnPin(object sender, RoutedEventArgs e) => _pinned = PinChip.IsChecked == true;

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
            ToastTitle.Text = t.Title;
            ToastSub.Text = t.Subtitle ?? "";
            ToastSub.Visibility = string.IsNullOrEmpty(t.Subtitle) ? Visibility.Collapsed : Visibility.Visible;
            ToastIcon.Data = t.Icon;
            ToastIcon.Foreground = new SolidColorBrush(t.Accent);
            ToastImage.Source = t.Image;
            ToastIcon.Visibility = t.Image == null ? Visibility.Visible : Visibility.Collapsed;
            bool compact = string.IsNullOrEmpty(t.Subtitle) && !t.Progress.HasValue;
            ToastIconBox.Width = ToastIconBox.Height = compact ? 26 : 36;
            ToastIconBox.CornerRadius = new CornerRadius(compact ? 13 : 18);
            ToastBar.Visibility = t.Progress.HasValue ? Visibility.Visible : Visibility.Collapsed;

            double textLen = Math.Max(t.Title.Length, (t.Subtitle ?? "").Length);
            double w = Math.Clamp(110 + textLen * 7.2, compact ? 150 : 240, 560);
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
            Scaler.Visibility = _fullscreenHidden && State != IslandState.Expanded ? Visibility.Hidden : Visibility.Visible;
        }

        // ===================== Таймеры =====================

        private void Clock()
        {
            ClockText.Text = DateTime.Now.ToString(AppSettings.Current.Use24h ? "HH:mm" : "h:mm");
            if (AppHost.Timer != null) MiniTimer.Text = TimerService.Format(AppHost.Timer.Left);
        }

        private void Animate()
        {
            if (Scaler.Visibility != Visibility.Visible || _oledHidden) return;
            if (MediaRow.Visibility == Visibility.Visible && CollapsedView.Visibility == Visibility.Visible)
            {
                float peak = AppHost.Audio?.Peak ?? 0;
                var bars = new[] { Bar0, Bar1, Bar2, Bar3 };
                for (int i = 0; i < bars.Length; i++)
                {
                    double k = 0.55 + _rnd.NextDouble() * 0.6;
                    double target = 3 + Math.Min(13, peak * 22 * k);
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

        public void ApplyScale() => ScaleT.ScaleX = ScaleT.ScaleY = Math.Clamp(AppSettings.Current.Scale, 0.7, 1.6);

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
