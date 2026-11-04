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
    public partial class HomeView : UserControl, IIslandView
    {
        private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
        private List<LyricLine> _lyrics;
        private string _lyricsKey = "";
        private bool _fillingDevices;
        private bool _settingVolume;
        private static readonly Regex MathRx = new(@"^[\d\s+\-*/().,%]+$", RegexOptions.Compiled);

        public HomeView()
        {
            InitializeComponent();
            AppHost.Media.Changed += Refresh;
            AppHost.Audio.VolumeChanged += (_, __) => SyncVolume();
            AppHost.Audio.DeviceChanged += () => { SyncVolume(); FillDevices(); };
            _tick.Tick += (_, __) => TickPosition();
            Command.TextChanged += (_, __) => CommandResult.Text = TryCalc(Command.Text, out var r) ? "= " + r : "";
            IsVisibleChanged += (_, __) => { if (IsVisible) _tick.Start(); else _tick.Stop(); };
            Refresh();
            SyncVolume();
            FillDevices();
        }

        public void OnShown()
        {
            Refresh();
            SyncVolume();
        }

        public void FocusCommand()
        {
            Dispatcher.InvokeAsync(() => { Command.Focus(); Command.SelectAll(); }, DispatcherPriority.Input);
        }

        // ---------- Медиа ----------

        private void Refresh()
        {
            var m = AppHost.Media.Current;
            if (!m.HasSession || string.IsNullOrEmpty(m.Title))
            {
                Source.Text = "НИЧЕГО НЕ ИГРАЕТ";
                Title.Text = "Включи музыку в любом плеере";
                Artist.Text = "Spotify, браузер, Яндекс Музыка…";
                Cover.Source = null;
                PlayIcon.Kind = "Play";
                SetLyrics(null);
                return;
            }
            Source.Text = (m.SourceApp ?? "").ToUpperInvariant();
            Title.Text = m.Title;
            Artist.Text = m.Artist;
            Cover.Source = m.Cover;
            PlayIcon.Kind = m.IsPlaying ? "Pause" : "Play";
            DurText.Text = Fmt(m.Duration);
            TickPosition();
            _ = LoadLyrics(m);
        }

        private async System.Threading.Tasks.Task LoadLyrics(MediaInfo m)
        {
            if (!AppSettings.Current.LyricsEnabled) { SetLyrics(null); return; }
            var key = m.Artist + "|" + m.Title;
            if (key == _lyricsKey) return;
            _lyricsKey = key;
            _lyrics = null;
            LyrPrev.Text = ""; LyrNext.Text = ""; LyrCur.Text = "Ищу текст песни…";
            var lyr = await LyricsService.GetAsync(m.Artist, m.Title, m.Duration);
            if (key != _lyricsKey) return;
            SetLyrics(lyr);
        }

        private void SetLyrics(List<LyricLine> lyr)
        {
            _lyrics = lyr;
            if (lyr == null || lyr.Count == 0)
            {
                LyrPrev.Text = "";
                LyrCur.Text = AppHost.Media.Current.HasSession && !string.IsNullOrEmpty(AppHost.Media.Current.Title) ? "Текст песни не найден" : "";
                LyrCur.Foreground = (System.Windows.Media.Brush)FindResource("Muted");
                LyrNext.Text = "";
            }
            else LyrCur.Foreground = (System.Windows.Media.Brush)FindResource("Fg");
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
            {
                int i = _lyrics.FindLastIndex(l => l.At <= pos + TimeSpan.FromMilliseconds(300));
                LyrPrev.Text = i > 0 ? _lyrics[i - 1].Text : "";
                LyrCur.Text = i >= 0 ? (_lyrics[i].Text.Length == 0 ? "♪" : _lyrics[i].Text) : "♪";
                LyrNext.Text = i + 1 < _lyrics.Count ? _lyrics[i + 1].Text : "";
            }
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
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            var line = Command.Text.Trim();
            if (line.Length == 0) return;

            if (TryCalc(line, out var result))
            {
                ClipboardService.SetText(result);
                ToastBus.Show(new Toast { Kind = ToastKind.Success, Title = line + " = " + result, Subtitle = "Результат скопирован", Icon = Icons.Check, Important = true });
                Command.Text = "";
                AppHost.Window.Collapse();
                return;
            }

            var timer = Regex.Match(line, @"^(таймер|timer)\s+(\d+(?:[.,]\d+)?)\s*(с|сек|s|м|мин|m|ч|h)?$", RegexOptions.IgnoreCase);
            if (timer.Success)
            {
                double n = double.Parse(timer.Groups[2].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                var u = timer.Groups[3].Value.ToLowerInvariant();
                var span = u.StartsWith("с") || u == "s" ? TimeSpan.FromSeconds(n) : u.StartsWith("ч") || u == "h" ? TimeSpan.FromHours(n) : TimeSpan.FromMinutes(n);
                AppHost.Timer.StartNew(span);
                Command.Text = "";
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
                Command.Text = "";
                AppHost.Window.Collapse();
                return;
            }

            if (SystemControls.RunCommand(line))
            {
                Command.Text = "";
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
