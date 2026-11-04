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
                ToastBus.Show(new Toast
                {
                    Kind = ToastKind.Timer, Title = "Время вышло", Subtitle = "Таймер на " + Format(Total),
                    Icon = Icons.Timer, Accent = System.Windows.Media.Color.FromRgb(0xFF, 0xB5, 0x47),
                    Duration = TimeSpan.FromSeconds(8), Important = true
                });
                try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
            }
            Changed?.Invoke();
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
