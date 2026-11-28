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

        /// <summary>Скруглённая обрезка содержимого карточки плеера: отступ 14 минус рамка 1, радиус 16 − 1.</summary>
        private void OnPlayerCardSize(object sender, SizeChangedEventArgs e)
        {
            if (sender is not FrameworkElement g) return;
            const double inset = 13;
            g.Clip = new System.Windows.Media.RectangleGeometry(new Rect(-inset, -inset, g.ActualWidth + inset * 2, g.ActualHeight + inset * 2), 15, 15);
        }

        public HomeView()
        {
            InitializeComponent();
            AppHost.Media.Changed += Refresh;
            AppHost.Lyrics.Changed += OnLyricsChanged;
            AppHost.Audio.VolumeChanged += (_, __) => SyncVolume();
            AppHost.Audio.DeviceChanged += () => { SyncVolume(); FillDevices(); };
            _tick.Tick += (_, __) =>
            {
                TickPosition();
                if (++_appVolTicks % 10 != 0) return;
                SyncAppVolume();
                // раз в 5 с — повтор, перемешивание и громкость самого Spotify (их меняют и в приложении Spotify)
                if (_appVolTicks % 50 == 0 && SpotifyControl.Handles(AppHost.Media.Current)) _ = SpotifyControl.RefreshStateAsync();
                // раз в секунду: пауза затянулась — пора спрятать плеер (событий от плеера в это время нет)
                bool idleNow = _pausedSince.HasValue && DateTime.Now - _pausedSince.Value > PlayerIdleTimeout;
                if (idleNow != _shownIdle) Refresh();
            };
            AppVol.ValueChanged += OnAppVolume;
            SpotifyControl.StateChanged += () => { UpdateRepeat(); UpdateShuffle(); SyncAppVolume(); };
            Command.TextChanged += (_, __) => CommandResult.Text = TryCalc(Command.Text, out var r) ? "= " + r : "";
            IsVisibleChanged += (_, __) =>
            {
                // OnShown зовётся ещё до того, как вкладка стала видимой, — SyncAppVolume там ничего не делал,
                // и крутилка ждала секундного тика; синхронизируем сразу, как только видно
                if (IsVisible) { _tick.Start(); Helpers.FrameClock.Tick += LyricsFrame; SyncAppVolume(); }
                else { _tick.Stop(); Helpers.FrameClock.Tick -= LyricsFrame; }
            };
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
                Subtitle = item.IsImage ? item.Text : item.Preview, Private = true, Image = item.IsImage ? item.ThumbNow() : null,
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

        /// <summary>
        /// «+»: список ярлыков с рабочего стола (свой и общий) со значками — отмечаешь нужные.
        /// Меню не закрывается после клика, можно выбрать несколько. «Другой файл…» — обычный выбор файла.
        /// </summary>
        private void OnLauncherAdd(object s, RoutedEventArgs e)
        {
            var menu = new ContextMenu { PlacementTarget = LauncherAddBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Top, MaxHeight = 440, MinWidth = 260 };
            var desk = DesktopShortcuts();
            if (desk.Count == 0) menu.Items.Add(new MenuItem { Header = "На рабочем столе нет ярлыков", IsEnabled = false });
            foreach (var path in desk)
            {
                var item = new MenuItem { StaysOpenOnClick = true };
                // галочка справа — строки не прыгают, как со стандартной галочкой слева
                var row = new Grid { MinWidth = 240 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                row.Children.Add(new System.Windows.Controls.Image { Source = Isle.Native.Win32.ShellIcon(path), Width = 20, Height = 20, Margin = new Thickness(0, 0, 10, 0) });
                var name = new TextBlock { Text = System.IO.Path.GetFileNameWithoutExtension(path), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 300, TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(name, 1); row.Children.Add(name);
                var mark = new TextBlock { Text = "✓", FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Visibility = Has(path) ? Visibility.Visible : Visibility.Hidden };
                Grid.SetColumn(mark, 2); row.Children.Add(mark);
                item.Header = row;
                var p = path;
                item.Click += (_, __) =>
                {
                    if (Has(p)) _launch.Remove(_launch.First(l => string.Equals(l.Path, p, StringComparison.OrdinalIgnoreCase)));
                    else if (_launch.Count < 16) _launch.Add(new LauncherItem { Path = p });
                    mark.Visibility = Has(p) ? Visibility.Visible : Visibility.Hidden;
                    SaveLauncher();
                };
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            var other = new MenuItem { Header = "Другой файл или программа…" };
            other.Click += (_, __) => PickFile();
            menu.Items.Add(other);
            menu.IsOpen = true;
        }

        private bool Has(string path) => _launch.Any(l => string.Equals(l.Path, path, StringComparison.OrdinalIgnoreCase));

        /// <summary>Ярлыки и программы с рабочего стола пользователя и общего (как их видно на экране), по алфавиту.</summary>
        private static List<string> DesktopShortcuts()
        {
            var list = new List<string>();
            foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) })
            {
                try
                {
                    if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir)) continue;
                    list.AddRange(System.IO.Directory.EnumerateFiles(dir).Where(f =>
                    {
                        var ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                        return ext is ".lnk" or ".url" or ".exe" or ".appref-ms";
                    }));
                }
                catch { }
            }
            return list.GroupBy(f => System.IO.Path.GetFileNameWithoutExtension(f), StringComparer.CurrentCultureIgnoreCase).Select(g => g.First())
                .OrderBy(f => System.IO.Path.GetFileNameWithoutExtension(f), StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private void PickFile()
        {
            AppHost.Window.SuppressAutoCollapse = true;
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Добавить ярлык в остров", Multiselect = true, Filter = "Программы и ярлыки|*.exe;*.lnk;*.url;*.bat;*.cmd|Все файлы|*.*" };
                if (dlg.ShowDialog() == true)
                {
                    foreach (var f in dlg.FileNames)
                        if (!Has(f) && _launch.Count < 16)
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
            SyncAppVolume();
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

        private static readonly TimeSpan PlayerIdleTimeout = TimeSpan.FromMinutes(3);
        private DateTime? _pausedSince;
        private bool _shownIdle;

        private void Refresh()
        {
            var m = AppHost.Media.Current;
            UpdateRepeat();
            UpdateShuffle();
            // новый трек или переключились на Spotify — перечитать его повтор/перемешивание/громкость
            var stateKey = m.SourceAppId + "|" + m.Title;
            if (stateKey != _spotifyStateKey && SpotifyControl.Handles(m)) { _spotifyStateKey = stateKey; _ = SpotifyControl.RefreshStateAsync(force: true); }
            // музыка на паузе дольше 3 минут — вместо плеера показываем «Недавно скопировано»
            if (m.HasSession && m.IsPlaying) _pausedSince = null;
            else if (_pausedSince == null) _pausedSince = DateTime.Now;
            bool idle = _pausedSince.HasValue && DateTime.Now - _pausedSince.Value > PlayerIdleTimeout;
            bool nothing = !m.HasSession || string.IsNullOrEmpty(m.Title) || idle;
            _shownIdle = idle;
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
                AppVol.Visibility = Visibility.Collapsed;
                _shownLyricsKey = null; // колонку очистили — при возвращении трека текст надо построить заново
                SetLyrics(null);
                return;
            }
            Source.Text = (m.SourceApp ?? "").ToUpperInvariant();
            SyncAppVolume();
            Title.Text = m.Title;
            Artist.Text = m.Artist;
            Cover.Source = m.Cover;
            CoverGlow.Source = m.Cover;
            PlayIcon.Kind = m.IsPlaying ? "Pause" : "Play";
            DurText.Text = Fmt(m.Duration);
            TickPosition();
            // Раньше здесь сравнивался только ключ трека. Плеер на миг присылал пустое название (смена трека, Spotify),
            // мы шли в ветку «ничего не играет» и чистили колонку (SetLyrics(null)), а ключ оставался прежним; затем
            // LyricsState видел тот же трек с уже загруженным текстом и Changed не поднимал — колонка пустовала до
            // следующего трека, хотя пилюля (она читает Lines каждый кадр) текст показывала. Теперь сверяем и сами строки.
            OnLyricsChanged();
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
                    LyricsBorder.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Card"); // ровный фон под текстом, следует за темой
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

        /// <summary>
        /// Текст берётся из общего состояния (его же показывает пилюля «С текстом»). Сравниваем с тем, что реально
        /// построено в колонке (_lyrics), а не только с ключом трека: иначе после «пустого» обновления плеера
        /// колонка оставалась пустой до следующего трека (см. Refresh).
        /// </summary>
        private void OnLyricsChanged()
        {
            var st = AppHost.Lyrics;
            if (st == null) return;
            if (st.Loading)
            {
                if (_shownLyricsKey == null && _lyrics == null && LyrStatus.Visibility == Visibility.Visible && LyrStatus.Text == "Ищу текст песни…") return;
                _lyrics = null; LyricsList.SetLines(null);
                LyrStatus.Text = "Ищу текст песни…";
                LyrStatus.Visibility = Visibility.Visible;
                LyrSource.Visibility = Visibility.Collapsed;
                _shownLyricsKey = null;
                return;
            }
            if (_shownLyricsKey == st.Key && ReferenceEquals(_lyrics, st.Lines) && (_lyrics != null || LyrStatus.Visibility == Visibility.Visible)) return;
            _shownLyricsKey = st.Key;
            SetLyrics(st.Lines);
        }

        private void SetLyrics(List<LyricLine> lyr)
        {
            _lyrics = lyr;
            bool has = lyr != null && lyr.Count > 0;
            LyricsList.SetLines(has ? lyr : null);
            ShowLyricsSource(has && AppHost.Lyrics?.Source == LyricsState.SourceSpicy);
            LyrSource.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            if (!has)
            {
                LyrStatus.Text = AppHost.Media.Current.HasSession && !string.IsNullOrEmpty(AppHost.Media.Current.Title) ? "Текст песни не найден" : "";
                LyrStatus.Visibility = Visibility.Visible;
                return;
            }
            LyrStatus.Visibility = Visibility.Collapsed;
            // сразу ставим текущую строку на место, без «въезда» всего списка
            var m = AppHost.Media.Current;
            if (m.HasSession) LyricsList.Render(m.LivePosition, snap: true);
        }

        private bool _spicyCredit;

        /// <summary>Подпись источника: обычно «LRCLIB»; текст из Spicy Lyrics — маленькая кликабельная благодарность автору.</summary>
        private void ShowLyricsSource(bool spicy)
        {
            _spicyCredit = spicy;
            LyrSource.Text = spicy ? SpicyLyrics.Credit : "LRCLIB";
            LyrSource.FontSize = spicy ? 10 : 9;
            LyrSource.Cursor = spicy ? System.Windows.Input.Cursors.Hand : null;
            LyrSource.ToolTip = spicy ? "github.com/Spikerko/spicy-lyrics" : null;
        }

        private void OnLyrSourceClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!_spicyCredit) return;
            e.Handled = true;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SpicyLyrics.RepoUrl) { UseShellExecute = true }); } catch { }
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
        }

        /// <summary>Текст песни — каждый кадр (общий FrameClock), только пока «Главная» и колонка видны.</summary>
        private void LyricsFrame(double dt)
        {
            if (_lyrics == null || !LyricsList.HasLines || LyricsBorder.Visibility != Visibility.Visible || PlayerPanel.Visibility != Visibility.Visible) return;
            var m = AppHost.Media.Current;
            if (!m.HasSession) return;
            LyricsList.Render(m.LivePosition, dt);
        }

        private static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : ((int)t.TotalMinutes) + ":" + t.Seconds.ToString("00");

        private int _appVolTicks;
        private DateTime _appVolSetAt;
        private string _spotifyStateKey = "";
        /// <summary>Последняя известная громкость каждого плеера (по AUMID) — показываем её, пока звуковой сессии ещё нет.</summary>
        private readonly Dictionary<string, double> _appVolKnown = new();
        /// <summary>Громкость, которую выставили крутилкой, когда звуковой сессии плеера ещё не было, — применим, как появится.</summary>
        private readonly Dictionary<string, double> _appVolPending = new();

        /// <summary>
        /// Громкость плеера — та же, что в самом приложении и в «Микшере громкости» Windows:
        /// в Spotify стоит 30 % — крутилка показывает 30 %, переключился на браузер со 100 % — покажет 100 %.
        /// Spotify привязан с правом управления — крутилка управляет громкостью самого Spotify (Web API).
        /// Крутилка видна сразу, как есть плеер: звуковая сессия у плеера появляется только когда он начал
        /// выводить звук (через секунду-другую после смены трека), до этого — последнее известное значение.
        /// </summary>
        private void SyncAppVolume()
        {
            if (!IsVisible) return; // вкладка скрыта — не опрашиваем звуковые сессии зря (обновится при показе)
            var m = AppHost.Media.Current;
            if (!m.HasSession || string.IsNullOrEmpty(m.SourceAppId)) { AppVol.Visibility = Visibility.Collapsed; return; }
            AppVol.Visibility = Visibility.Visible;
            if (AppVol.IsMouseCaptured || DateTime.Now - _appVolSetAt < TimeSpan.FromSeconds(1)) return; // крутят сейчас — не перебиваем
            var id = m.SourceAppId;
            if (SpotifyControl.Handles(m) && SpotifyControl.VolumePercent is int sp)
            {
                AppVol.Value = sp / 100.0;
                AppVol.ToolTip = "Громкость Spotify · крути колесиком";
                return;
            }
            var v = AppHost.Audio.AppVolume(id);
            if (v.HasValue)
            {
                if (_appVolPending.Remove(id, out var want) && Math.Abs(want - v.Value) > 0.005)
                {
                    AppHost.Audio.SetAppVolume(id, (float)want); // крутили, пока сессии не было
                    v = (float)want;
                }
                _appVolKnown[id] = v.Value;
                AppVol.Value = v.Value;
            }
            else AppVol.Value = _appVolPending.TryGetValue(id, out var p) ? p : _appVolKnown.TryGetValue(id, out var k) ? k : 1.0;
            AppVol.ToolTip = "Громкость " + (string.IsNullOrEmpty(m.SourceApp) ? "плеера" : m.SourceApp) + " · крути колесиком";
        }

        private async void OnAppVolume(double v)
        {
            _appVolSetAt = DateTime.Now;
            var m = AppHost.Media.Current;
            var id = m.SourceAppId;
            if (string.IsNullOrEmpty(id)) return;
            if (SpotifyControl.Handles(m))
            {
                // громкость самого Spotify; запросы прореживаются, отказ — громкость в микшере Windows
                SpotifyControl.SetVolume(v, x => SetWindowsAppVolume(id, x));
                return;
            }
            if (!SetWindowsAppVolume(id, v)) return;
            // некоторые плееры сразу возвращают свою громкость обратно — проверим и скажем честно
            await System.Threading.Tasks.Task.Delay(600);
            var now = AppHost.Audio.AppVolume(id);
            if (now.HasValue && Math.Abs(now.Value - v) > 0.04)
                AppVol.ToolTip = "Плеер не дал изменить громкость извне — поменяй её в самом приложении";
        }

        /// <summary>Громкость плеера в микшере Windows. false — звуковой сессии пока нет (применим, когда появится).</summary>
        private bool SetWindowsAppVolume(string id, double v)
        {
            if (!AppHost.Audio.SetAppVolume(id, (float)v)) { _appVolPending[id] = v; return false; }
            _appVolPending.Remove(id);
            _appVolKnown[id] = v;
            return true;
        }

        private void OnPrev(object s, RoutedEventArgs e) => AppHost.Media.Previous();

        private async void OnRepeat(object s, RoutedEventArgs e)
        {
            if (!await AppHost.Media.ToggleRepeatAsync())
            {
                var app = AppHost.Media.Current?.SourceApp ?? "";
                ToastBus.Show(new Toast
                {
                    Kind = ToastKind.Info, Title = "Повтор переключается только в самом плеере", Icon = Icons.Repeat, Important = true, Duration = TimeSpan.FromSeconds(3.5),
                    Subtitle = app.Contains("spotify", StringComparison.OrdinalIgnoreCase) ? "Spotify не даёт включать его снаружи — в Spotify: Ctrl+R" : "Этот плеер не даёт включать повтор снаружи",
                });
            }
            await System.Threading.Tasks.Task.Delay(300);
            UpdateRepeat();
        }

        /// <summary>Вид кнопки-переключателя: включено — подложка, яркая иконка и точка; выключено — приглушено.</summary>
        private static void PaintToggle(Helpers.Icon icon, FrameworkElement bg, FrameworkElement dot, bool on, bool supported)
        {
            icon.SetResourceReference(Helpers.Icon.ForegroundProperty, on ? "Accent" : "Muted");
            icon.Opacity = on ? 1 : supported ? 0.6 : 0.35;
            bg.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            dot.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateRepeat()
        {
            var (supported, mode) = AppHost.Media.RepeatInfo;
            bool on = mode != MediaService.RepeatMode.Off, one = mode == MediaService.RepeatMode.One;
            PaintToggle(RepeatIcon, RepeatBg, RepeatDot, on, supported);
            RepeatOne.Visibility = one ? Visibility.Visible : Visibility.Collapsed;
            bool spotify = SpotifyControl.Handles(AppHost.Media.Current);
            RepeatBtn.ToolTip = !supported ? "Повтор — этот плеер не даёт включать его снаружи"
                : one ? "Повтор трека включён — нажми, чтобы выключить"
                : on ? (spotify ? "Повтор плейлиста включён — нажми: повтор трека" : "Повтор включён — нажми, чтобы выключить")
                : spotify ? "Повтор выключен — нажми: повтор плейлиста" : "Повторять трек";
        }

        private void UpdateShuffle()
        {
            var (supported, on) = AppHost.Media.ShuffleInfo;
            PaintToggle(ShuffleIcon, ShuffleBg, ShuffleDot, on, supported);
            ShuffleBtn.ToolTip = !supported ? "Перемешивание — этот плеер не даёт включать его снаружи"
                : on ? "Перемешивание включено — нажми, чтобы выключить" : "Перемешивать";
        }

        private async void OnShuffle(object s, RoutedEventArgs e)
        {
            if (!await AppHost.Media.ToggleShuffleAsync())
            {
                var app = AppHost.Media.Current?.SourceApp ?? "";
                ToastBus.Show(new Toast
                {
                    Kind = ToastKind.Info, Title = "Перемешивание переключается только в самом плеере", Icon = Icons.Shuffle, Important = true, Duration = TimeSpan.FromSeconds(3.5),
                    Subtitle = app.Contains("spotify", StringComparison.OrdinalIgnoreCase) ? "Spotify не даёт включать его снаружи — в Spotify: Ctrl+S" : "Этот плеер не даёт включать перемешивание снаружи",
                });
            }
            await System.Threading.Tasks.Task.Delay(300);
            UpdateShuffle();
        }
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
            VolIcon.Kind = AppHost.Audio.Muted || v < 0.5 ? "Mute" : "Volume"; // как на «Управлении»: на нуле — перечёркнутый
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
                double sec = n * (u.StartsWith("с") || u.StartsWith("s") ? 1 : u.StartsWith("ч") || u.StartsWith("h") ? 3600 : 60);
                if (!(sec >= 1 && sec <= 100 * 3600)) { ToastBus.Show(new Toast { Kind = ToastKind.Info, Title = "Таймер", Subtitle = "Можно от 1 секунды до 100 часов", Icon = Icons.Timer }); return; } // огромное число роняло TimeSpan
                var span = TimeSpan.FromSeconds(sec);
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
