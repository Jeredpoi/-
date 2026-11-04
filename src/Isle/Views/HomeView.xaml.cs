using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Isle.Services;

namespace Isle.Views
{
    public class LauncherItem
    {
        public string Path { get; set; }
        public string Name => System.IO.Path.GetFileNameWithoutExtension(Path.TrimEnd('\\')) is { Length: > 0 } n ? n : Path;
        public System.Windows.Media.ImageSource Icon => Isle.Native.Win32.ShellIcon(Path);
    }

    public partial class HomeView : UserControl, IIslandView
    {
        private readonly System.Collections.ObjectModel.ObservableCollection<LauncherItem> _launch = new();
        private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(100) };
        private List<LyricLine> _lyrics;
        private string _lyricsKey = "";
        private bool _fillingDevices;
        private bool _settingVolume;
        private static readonly Regex MathRx = new(@"^[\d\s+\-*/().,%]+$", RegexOptions.Compiled);

        public HomeView()
        {
            InitializeComponent();
            AppHost.Media.Changed += Refresh;
            AppHost.Lyrics.Changed += OnLyricsChanged;
            AppHost.Audio.VolumeChanged += (_, __) => SyncVolume();
            AppHost.Audio.DeviceChanged += () => { SyncVolume(); FillDevices(); };
            _tick.Tick += (_, __) => TickPosition();
            Command.TextChanged += (_, __) => CommandResult.Text = TryCalc(Command.Text, out var r) ? "= " + r : "";
            IsVisibleChanged += (_, __) => { if (IsVisible) _tick.Start(); else _tick.Stop(); };
            foreach (var p in AppSettings.Current.Launcher) _launch.Add(new LauncherItem { Path = p });
            Launcher.ItemsSource = _launch;
            _launch.CollectionChanged += (_, __) => UpdateLauncherHint();
            UpdateLauncherHint();
            AppHost.Clipboard.Items.CollectionChanged += (_, __) => RefreshRecent();
            RefreshRecent();
            Refresh();
            SyncVolume();
            FillDevices();
            ApplyPlayerStyle();
        }

        // ---------- Недавно скопировано ----------

        private void RefreshRecent()
        {
            var top = AppHost.Clipboard.Items.Take(4).ToList();
            Recent.ItemsSource = top;
            RecentEmpty.Visibility = top.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnRecentClick(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not ClipItem item) return;
            bool copyOnly = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            var target = AppHost.Window.PreviousForeground;
            AppHost.Window.Collapse();
            AppHost.Clipboard.Paste(item, target, !copyOnly);
            ToastBus.Show(new Toast
            {
                Kind = ToastKind.Success, Title = copyOnly ? "Скопировано" : "Вставлено",
                Subtitle = item.IsImage ? item.Text : item.Preview, Image = item.IsImage ? item.Thumb : null,
                Icon = Icons.Copy, Duration = TimeSpan.FromSeconds(1.6)
            });
        }

        /// <summary>Диктовка: остров сворачивается, фокус возвращается в прошлое окно, текст вставится туда.</summary>
        private void OnVoice(object s, RoutedEventArgs e)
        {
            var target = AppHost.Window.PreviousForeground;
            AppHost.Window.Collapse();
            if (target != IntPtr.Zero) Isle.Native.Win32.SetForegroundWindow(target);
            AppHost.Voice.Toggle();
        }

        private void OnLauncherAdd(object s, RoutedEventArgs e)
        {
            AppHost.Window.SuppressAutoCollapse = true;
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Добавить ярлык в остров", Multiselect = true, Filter = "Программы и ярлыки|*.exe;*.lnk;*.url;*.bat;*.cmd|Все файлы|*.*" };
                if (dlg.ShowDialog() == true)
                {
                    foreach (var f in dlg.FileNames)
                        if (_launch.All(l => !string.Equals(l.Path, f, StringComparison.OrdinalIgnoreCase)) && _launch.Count < 16)
                            _launch.Add(new LauncherItem { Path = f });
                    SaveLauncher();
                }
            }
            finally { AppHost.Window.SuppressAutoCollapse = false; }
        }

        private void OnOpenClipboard(object s, RoutedEventArgs e) => AppHost.Window.SelectTab("clip");

        // ---------- Ярлыки ----------

        private void UpdateLauncherHint() => LauncherHint.Visibility = _launch.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        private void SaveLauncher()
        {
            AppSettings.Current.Launcher = _launch.Select(l => l.Path).ToList();
            AppSettings.Current.Save();
        }

        private void OnLauncherDrop(object s, DragEventArgs e)
        {
            e.Handled = true;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
            foreach (var f in files)
                if (_launch.All(l => !string.Equals(l.Path, f, StringComparison.OrdinalIgnoreCase)) && _launch.Count < 16)
                    _launch.Add(new LauncherItem { Path = f });
            SaveLauncher();
        }

        private void OnLaunch(object s, RoutedEventArgs e)
        {
            if ((s as FrameworkElement)?.Tag is string path)
            {
                AppHost.Window.Collapse();
                SystemControls.Shell(path);
            }
        }

        private void OnLaunchRemove(object s, MouseButtonEventArgs e)
        {
            if ((s as FrameworkElement)?.Tag is not string path) return;
            var item = _launch.FirstOrDefault(l => l.Path == path);
            if (item != null) { _launch.Remove(item); SaveLauncher(); }
            e.Handled = true;
        }

        public void OnShown()
        {
            ApplyPlayerStyle();
            Refresh();
            SyncVolume();
        }

        public void FocusCommand()
        {
            CommandBox.Visibility = Visibility.Visible;
            LauncherBox.Visibility = Visibility.Collapsed;
            Dispatcher.InvokeAsync(() => { Command.Focus(); Command.SelectAll(); }, DispatcherPriority.Input);
        }

        public void HideCommand()
        {
            Command.Text = "";
            CommandBox.Visibility = Visibility.Collapsed;
            LauncherBox.Visibility = Visibility.Visible;
        }

        private void OnCommandLostFocus(object s, KeyboardFocusChangedEventArgs e)
        {
            if (Command.Text.Trim().Length == 0) HideCommand();
        }

        // ---------- Медиа ----------

        private void Refresh()
        {
            var m = AppHost.Media.Current;
            bool nothing = !m.HasSession || string.IsNullOrEmpty(m.Title);
            RecentPanel.Visibility = nothing ? Visibility.Visible : Visibility.Collapsed;
            PlayerPanel.Visibility = nothing ? Visibility.Collapsed : Visibility.Visible;
            SourceCount.Text = Math.Max(1, AppHost.Media.SourceCount).ToString();
            if (nothing)
            {
                Source.Text = "";
                Title.Text = "Включи музыку в любом плеере";
                Artist.Text = "Spotify, браузер, Яндекс Музыка…";
                Cover.Source = null;
                CoverGlow.Source = null;
                PlayIcon.Kind = "Play";
                SetLyrics(null);
                return;
            }
            Source.Text = (m.SourceApp ?? "").ToUpperInvariant();
            Title.Text = m.Title;
            Artist.Text = m.Artist;
            Cover.Source = m.Cover;
            CoverGlow.Source = m.Cover;
            PlayIcon.Kind = m.IsPlaying ? "Pause" : "Play";
            DurText.Text = Fmt(m.Duration);
            TickPosition();
            if (_shownLyricsKey != AppHost.Lyrics.Key || AppHost.Lyrics.Loading) OnLyricsChanged();
        }

        private async void OnSources(object s, RoutedEventArgs e)
        {
            var menu = new ContextMenu { PlacementTarget = SourceBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            AppHost.Window.SuppressAutoCollapse = true;
            menu.Closed += (_, __) => AppHost.Window.SuppressAutoCollapse = false;
            var auto = new MenuItem { Header = "Автоматически — что играет", IsCheckable = true, IsChecked = AppHost.Media.PreferredSource == null };
            auto.Click += (_, __) => AppHost.Media.ChooseSource(null);
            menu.Items.Add(auto);
            menu.Items.Add(new Separator());
            foreach (var src in await AppHost.Media.SourcesAsync())
            {
                var item = new MenuItem
                {
                    Header = (src.Playing ? "▶ " : "") + src.Name + (string.IsNullOrEmpty(src.Title) ? "" : "  ·  " + src.Title),
                    IsCheckable = true, IsChecked = AppHost.Media.PreferredSource == src.Id
                };
                var id = src.Id;
                item.Click += (_, __) => AppHost.Media.ChooseSource(id);
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }

        private string _shownLyricsKey;

        /// <summary>Вид плеера из настроек: мягкое зеркало, заливка или компактный; текст песни справа — по желанию.</summary>
        public void ApplyPlayerStyle()
        {
            var st = AppSettings.Current;
            bool lyricsCol = st.LyricsEnabled && st.LyricsOnHome;
            LyricsColumn.Width = lyricsCol ? new GridLength(210) : new GridLength(0);
            LyricsBorder.Visibility = lyricsCol ? Visibility.Visible : Visibility.Collapsed;
            switch (st.HomePlayer)
            {
                case "fill":
                    Grid.SetColumnSpan(CoverGlow, 2);
                    Grid.SetColumnSpan(FillShade, 2);
                    LyricsBorder.Background = System.Windows.Media.Brushes.Transparent;
                    CoverGlow.Margin = new Thickness(-60);
                    CoverGlow.Opacity = 0.85;
                    CoverGlow.OpacityMask = null;
                    FillShade.Visibility = Visibility.Visible;
                    break;
                case "compact":
                    LyricsBorder.Background = System.Windows.Media.Brushes.Transparent;
                    CoverGlow.Opacity = 0;
                    FillShade.Visibility = Visibility.Collapsed;
                    break;
                default:
                    Grid.SetColumnSpan(CoverGlow, 1);
                    Grid.SetColumnSpan(FillShade, 1);
                    LyricsBorder.Background = (System.Windows.Media.Brush)FindResource("Card"); // ровный фон под текстом
                    CoverGlow.Margin = new Thickness(-30, -40, -10, -40);
                    CoverGlow.Opacity = 0.55;
                    CoverGlow.ClearValue(UIElement.OpacityMaskProperty);
                    CoverGlow.OpacityMask = new System.Windows.Media.RadialGradientBrush(
                        System.Windows.Media.Color.FromArgb(255, 0, 0, 0), System.Windows.Media.Color.FromArgb(0, 0, 0, 0))
                    { RadiusX = 0.55, RadiusY = 0.6, Center = new Point(0.5, 0.45), GradientOrigin = new Point(0.5, 0.45) };
                    FillShade.Visibility = Visibility.Collapsed;
                    break;
            }
            bool compact = st.HomePlayer == "compact";
            CoverHeader.Orientation = compact ? Orientation.Horizontal : Orientation.Vertical;
            CoverHeader.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Center;
            CoverBox.Width = CoverBox.Height = compact ? 72 : 84;
            TitleStack.Margin = compact ? new Thickness(14, 0, 0, 0) : new Thickness(0, 10, 0, 0);
            var align = compact ? HorizontalAlignment.Left : HorizontalAlignment.Center;
            Title.HorizontalAlignment = Artist.HorizontalAlignment = Source.HorizontalAlignment = align;
            TitleStack.VerticalAlignment = VerticalAlignment.Center;
        }

        /// <summary>Текст берётся из общего состояния (его же показывает пилюля «С текстом»).</summary>
        private void OnLyricsChanged()
        {
            var st = AppHost.Lyrics;
            if (st.Loading)
            {
                _lyrics = null; _lines.Clear(); LyricsStack.Children.Clear();
                LyrStatus.Text = "Ищу текст песни…";
                LyrStatus.Visibility = Visibility.Visible;
                _shownLyricsKey = null;
                return;
            }
            if (_shownLyricsKey == st.Key && _lyrics == st.Lines) return;
            _shownLyricsKey = st.Key;
            SetLyrics(st.Lines);
        }

        private int _curLine = -2;
        private readonly List<TextBlock> _lines = new();

        private void SetLyrics(List<LyricLine> lyr)
        {
            _lyrics = lyr;
            _curLine = -2;
            _lines.Clear();
            LyricsStack.Children.Clear();
            LyricsShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
            LyricsShift.Y = 0;
            if (lyr == null || lyr.Count == 0)
            {
                LyrStatus.Text = AppHost.Media.Current.HasSession && !string.IsNullOrEmpty(AppHost.Media.Current.Title) ? "Текст песни не найден" : "";
                LyrStatus.Visibility = Visibility.Visible;
                return;
            }
            LyrStatus.Visibility = Visibility.Collapsed;
            foreach (var l in lyr)
            {
                var tb = new TextBlock
                {
                    Text = l.Text.Length == 0 ? "♪" : l.Text, TextWrapping = TextWrapping.Wrap, FontSize = 13, FontWeight = FontWeights.SemiBold,
                    Foreground = (System.Windows.Media.Brush)FindResource("Fg"), Opacity = 0.3, Margin = new Thickness(0, 0, 0, 10),
                    RenderTransformOrigin = new Point(0, 0.5), RenderTransform = new System.Windows.Media.ScaleTransform(1, 1)
                };
                _lines.Add(tb);
                LyricsStack.Children.Add(tb);
            }
        }

        /// <summary>Плавно прокручивает текст так, чтобы текущая строка стояла на трети высоты; она ярче и крупнее.</summary>
        private void ShowLine(int i)
        {
            if (i == _curLine || _lines.Count == 0) return;
            _curLine = i;
            if (_lines[0].ActualHeight <= 0) LyricsStack.UpdateLayout();
            var dur = TimeSpan.FromMilliseconds(420);
            var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
            for (int k = 0; k < _lines.Count; k++)
            {
                int d = Math.Abs(k - i);
                double op = k == i ? 1 : d == 1 ? 0.45 : d == 2 ? 0.25 : 0.12;
                double sc = k == i ? 1.12 : 1;
                _lines[k].BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(op, dur) { EasingFunction = ease });
                var st = (System.Windows.Media.ScaleTransform)_lines[k].RenderTransform;
                st.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new System.Windows.Media.Animation.DoubleAnimation(sc, dur) { EasingFunction = ease });
                st.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new System.Windows.Media.Animation.DoubleAnimation(sc, dur) { EasingFunction = ease });
            }
            double y = 0;
            for (int k = 0; k < Math.Max(0, i); k++) y += _lines[k].ActualHeight + 10;
            double target = -(y - LyricsViewport.ActualHeight / 3);
            if (i < 0) target = LyricsViewport.ActualHeight / 3;
            LyricsShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
                new System.Windows.Media.Animation.DoubleAnimation(target, TimeSpan.FromMilliseconds(520)) { EasingFunction = ease });
        }

        private void TickPosition()
        {
            var m = AppHost.Media.Current;
            if (!m.HasSession) return;
            var pos = m.LivePosition;
            if (!Progress.IsMouseCaptureWithin)
            {
                Progress.Maximum = Math.Max(1, m.Duration.TotalSeconds);
                Progress.Value = Math.Min(Progress.Maximum, pos.TotalSeconds);
            }
            PosText.Text = Fmt(pos);
            if (_lyrics != null && _lyrics.Count > 0)
                ShowLine(_lyrics.FindLastIndex(l => l.At <= pos + TimeSpan.FromMilliseconds(250)));
        }

        private static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : ((int)t.TotalMinutes) + ":" + t.Seconds.ToString("00");

        private void OnPrev(object s, RoutedEventArgs e) => AppHost.Media.Previous();
        private void OnNext(object s, RoutedEventArgs e) => AppHost.Media.Next();
        private void OnPlay(object s, RoutedEventArgs e) => AppHost.Media.PlayPause();
        private void OnSeek(object s, MouseButtonEventArgs e) => AppHost.Media.Seek(TimeSpan.FromSeconds(Progress.Value));

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
            if (AppHost.Audio.Muted && e.NewValue > 0) { AppHost.Audio.Muted = false; VolIcon.Kind = "Volume"; }
        }

        private void OnMute(object s, RoutedEventArgs e)
        {
            AppHost.Audio.Muted = !AppHost.Audio.Muted;
            VolIcon.Kind = AppHost.Audio.Muted ? "Mute" : "Volume";
        }

        private void FillDevices()
        {
            _fillingDevices = true;
            var list = AppHost.Audio.OutputDevices();
            Devices.ItemsSource = list;
            var cur = AppHost.Audio.DeviceId;
            Devices.SelectedItem = list.FirstOrDefault(d => d.Id == cur);
            _fillingDevices = false;
        }

        private void OnDevicesOpen(object s, EventArgs e) { AppHost.Window.SuppressAutoCollapse = true; FillDevices(); }

        private void OnDevice(object s, SelectionChangedEventArgs e)
        {
            AppHost.Window.SuppressAutoCollapse = false;
            if (_fillingDevices || Devices.SelectedItem is not AudioDevice d) return;
            if (d.Id != AppHost.Audio.DeviceId) AppHost.Audio.SetDefault(d.Id);
        }

        // ---------- Инструменты ----------

        private void OnRegion(object s, RoutedEventArgs e) => _ = Tools.RegionShot();
        private void OnFull(object s, RoutedEventArgs e) => _ = Tools.FullShot();
        private void OnOcr(object s, RoutedEventArgs e) => _ = Tools.OcrShot();
        private void OnQr(object s, RoutedEventArgs e) => _ = Tools.QrShot();
        private void OnPipette(object s, RoutedEventArgs e) => _ = Tools.Pipette();
        private void OnClean(object s, RoutedEventArgs e) => Tools.CleanText();
        private void OnRecord(object s, RoutedEventArgs e) => Tools.Record();
        private void OnCommandBtn(object s, RoutedEventArgs e) { AppHost.Window.FocusIsland(); FocusCommand(); }

        // ---------- Командная строка ----------

        private void OnCommandKey(object s, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { HideCommand(); e.Handled = true; return; }
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            var line = Command.Text.Trim();
            if (line.Length == 0) return;

            if (TryCalc(line, out var result))
            {
                ClipboardService.SetText(result);
                ToastBus.Show(new Toast { Kind = ToastKind.Success, Title = line + " = " + result, Subtitle = "Результат скопирован", Icon = Icons.Check, Important = true });
                HideCommand();
                AppHost.Window.Collapse();
                return;
            }

            var timer = Regex.Match(line, @"^(таймер|timer)\s+(?:на\s+)?(\d+(?:[.,]\d+)?)\s*(с|сек\w*|s|sec\w*|м|мин\w*|m|min\w*|ч|час\w*|h|hour\w*)?$", RegexOptions.IgnoreCase);
            if (timer.Success)
            {
                double n = double.Parse(timer.Groups[2].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                var u = timer.Groups[3].Value.ToLowerInvariant();
                var span = u.StartsWith("с") || u.StartsWith("s") ? TimeSpan.FromSeconds(n) : u.StartsWith("ч") || u.StartsWith("h") ? TimeSpan.FromHours(n) : TimeSpan.FromMinutes(n);
                AppHost.Timer.StartNew(span);
                HideCommand();
                AppHost.Window.Collapse();
                return;
            }

            var rem = Regex.Match(line, @"^(напомни(?:ть)?|remind)\s+(.+)$", RegexOptions.IgnoreCase);
            if (rem.Success)
            {
                var r = AppHost.Reminders.Add(rem.Groups[2].Value);
                ToastBus.Show(r == null
                    ? new Toast { Title = "Не понял, когда напомнить", Subtitle = "Например: «напомни позвонить маме завтра в 9»", Icon = Icons.Bell, Important = true, Duration = TimeSpan.FromSeconds(4) }
                    : new Toast { Kind = ToastKind.Success, Title = "Напомню " + r.WhenText, Subtitle = r.Text, Icon = Icons.Bell, Accent = System.Windows.Media.Color.FromRgb(0xFF, 0xB5, 0x47), Important = true });
                HideCommand();
                AppHost.Window.Collapse();
                return;
            }

            if (SystemControls.RunCommand(line))
            {
                HideCommand();
                AppHost.Window.Collapse();
            }
        }

        public static bool TryCalc(string expr, out string result)
        {
            result = null;
            expr = expr.Trim();
            if (expr.Length < 3 || !MathRx.IsMatch(expr) || !expr.Any(c => "+-*/%".Contains(c)) || !expr.Any(char.IsDigit)) return false;
            try
            {
                var prepared = Regex.Replace(expr.Replace(',', '.'), @"(\d+(?:\.\d+)?)\s*%", "($1/100)");
                // целые числа → дробные, иначе 7/2 посчиталось бы как целочисленное деление
                prepared = Regex.Replace(prepared, @"(?<![\d.])(\d+)(?![\d.])", "$1.0");
                var val = new DataTable().Compute(prepared, null);
                double d = Convert.ToDouble(val, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d)) return false;
                result = Math.Round(d, 10).ToString("0.##########", CultureInfo.CurrentCulture);
                return true;
            }
            catch { return false; }
        }
    }
}
