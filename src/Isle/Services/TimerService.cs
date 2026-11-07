using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Threading;

namespace Isle.Services
{
    /// <summary>Таймер / помодоро. Работает в фоне, оставшееся время видно в свёрнутой чёлке.</summary>
    public class TimerService
    {
        private readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromMilliseconds(250) };
        private DateTime _end;
        private TimeSpan _pausedLeft;

        public TimeSpan Total { get; private set; } = TimeSpan.FromMinutes(5);
        public bool Running { get; private set; }
        public bool Paused => !Running && _pausedLeft > TimeSpan.Zero && _pausedLeft < Total;
        public TimeSpan Left => Running ? (_end - DateTime.Now < TimeSpan.Zero ? TimeSpan.Zero : _end - DateTime.Now) : (_pausedLeft > TimeSpan.Zero ? _pausedLeft : Total);
        public double Fraction => Total.TotalSeconds <= 0 ? 0 : Left.TotalSeconds / Total.TotalSeconds;

        public event Action Changed;

        public TimerService() { _t.Tick += (_, __) => Tick(); }

        public void Set(TimeSpan total)
        {
            Running = false; _t.Stop();
            Total = total; _pausedLeft = TimeSpan.Zero;
            Changed?.Invoke();
        }

        public void Start()
        {
            var left = _pausedLeft > TimeSpan.Zero ? _pausedLeft : Total;
            _end = DateTime.Now + left;
            Running = true;
            _t.Start();
            Changed?.Invoke();
        }

        public void StartNew(TimeSpan total) { Set(total); Start(); }

        public void Pause()
        {
            if (!Running) return;
            _pausedLeft = Left;
            Running = false; _t.Stop();
            Changed?.Invoke();
        }

        public void Reset()
        {
            Running = false; _t.Stop(); _pausedLeft = TimeSpan.Zero;
            Changed?.Invoke();
        }

        private void Tick()
        {
            if (Running && DateTime.Now >= _end)
            {
                Running = false; _t.Stop(); _pausedLeft = TimeSpan.Zero;
                AlarmService.Ring("Время вышло", "Таймер на " + Format(Total));
            }
            Changed?.Invoke();
        }

        /// <summary>«5» = 5 минут, «30с», «90 сек», «1:30», «2м», «1ч 20м», «1.5ч».</summary>
        public static TimeSpan? ParseDuration(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var t = text.Trim().ToLowerInvariant().Replace(',', '.');
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var colon = System.Text.RegularExpressions.Regex.Match(t, @"^(\d+):(\d{1,2})(?::(\d{1,2}))?$");
            if (colon.Success)
            {
                if (!int.TryParse(colon.Groups[1].Value, out int a) || !int.TryParse(colon.Groups[2].Value, out int b)) return null;
                int seconds = 0;
                if (colon.Groups[3].Success)
                {
                    if (b >= 60 || !int.TryParse(colon.Groups[3].Value, out seconds) || seconds >= 60) return null;
                    try
                    {
                        var hms = new TimeSpan(a, b, seconds);
                        return hms > TimeSpan.Zero && hms < TimeSpan.FromHours(24) ? hms : null;
                    }
                    catch (ArgumentOutOfRangeException) { return null; }
                }
                if (b >= 60) return null;
                try
                {
                    var ms = TimeSpan.FromMinutes(a).Add(TimeSpan.FromSeconds(b));
                    return ms > TimeSpan.Zero && ms < TimeSpan.FromHours(24) ? ms : null;
                }
                catch (ArgumentOutOfRangeException) { return null; }
            }
            if (t.Contains(':')) return null;
            var total = TimeSpan.Zero; bool any = false;
            var matches = System.Text.RegularExpressions.Regex.Matches(t, @"(\d+(?:\.\d+)?)\s*([a-zа-яё]*)");
            var residue = System.Text.RegularExpressions.Regex.Replace(t, @"(\d+(?:\.\d+)?)\s*([a-zа-яё]*)", "").Trim();
            if (residue.Length > 0 || matches.Count == 0) return null;
            try
            {
                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    if (!double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, inv, out double n) || !double.IsFinite(n) || n <= 0) return null;
                    var u = m.Groups[2].Value;
                    TimeSpan part;
                    if (u.Length == 0 || u.StartsWith("м") || u.StartsWith("m")) part = TimeSpan.FromMinutes(n);
                    else if (u.StartsWith("с") || u.StartsWith("s")) part = TimeSpan.FromSeconds(n);
                    else if (u.StartsWith("ч") || u.StartsWith("h")) part = TimeSpan.FromHours(n);
                    else return null;
                    total += part;
                    if (total >= TimeSpan.FromHours(24)) return null;
                    any = true;
                }
            }
            catch (OverflowException) { return null; }
            return any && total > TimeSpan.Zero && total < TimeSpan.FromHours(24) ? total : null;
        }

        public static string Format(TimeSpan t) =>
            t.TotalHours >= 1 ? ((int)t.TotalHours) + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00")
                              : ((int)t.TotalMinutes) + ":" + t.Seconds.ToString("00");
    }

    public class ShelfItem
    {
        public string Path { get; set; }
        public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\'));
        public bool IsFolder => Directory.Exists(Path);
        public System.Windows.Media.ImageSource Icon => Isle.Native.Win32.ShellIcon(Path);
        public bool HasIcon => Icon != null;
        public string Ext => IsFolder ? "ПАПКА" : (System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant() is var e && e.Length > 0 ? e : "ФАЙЛ");
    }

    public class ShelfData { public List<string> Paths { get; set; } = new(); }

    /// <summary>Полка: ссылки на файлы, которые бросили на остров (сами файлы не копируются).</summary>
    public class ShelfService
    {
        public ObservableCollection<ShelfItem> Items { get; } = new();

        public void Load()
        {
            foreach (var p in Storage.Load<ShelfData>("shelf.json").Paths.Where(p => File.Exists(p) || Directory.Exists(p)))
                Items.Add(new ShelfItem { Path = p });
        }

        public void Add(IEnumerable<string> paths)
        {
            foreach (var p in paths)
                if (Items.All(i => !string.Equals(i.Path, p, StringComparison.OrdinalIgnoreCase)))
                    Items.Add(new ShelfItem { Path = p });
            Save();
        }

        public void Remove(ShelfItem i) { Items.Remove(i); Save(); }
        public void Clear() { Items.Clear(); Save(); }
        private void Save() => Storage.Save("shelf.json", new ShelfData { Paths = Items.Select(i => i.Path).ToList() });
    }
}
