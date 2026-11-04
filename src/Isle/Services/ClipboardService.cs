using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Isle.Native;

namespace Isle.Services
{
    public class ClipItem
    {
        public string Text { get; set; } = "";
        public string Source { get; set; } = "";
        public DateTime Time { get; set; } = DateTime.Now;
        public bool Pinned { get; set; }

        public string Preview => Text.Length > 140 ? Text.Substring(0, 140).Replace("\r", " ").Replace("\n", " ") + "…" : Text.Replace("\r", " ").Replace("\n", " ");
        public string Meta => (string.IsNullOrEmpty(Source) ? "" : Source + " · ") + Ago(Time) + (Pinned ? " · закреплено" : "");

        public static string Ago(DateTime t)
        {
            var d = DateTime.Now - t;
            if (d.TotalSeconds < 45) return "сейчас";
            if (d.TotalMinutes < 60) return (int)d.TotalMinutes + " мин";
            if (d.TotalHours < 24) return (int)d.TotalHours + " ч";
            return t.ToString("dd.MM HH:mm");
        }
    }

    public class ClipboardData { public List<ClipItem> Items { get; set; } = new(); }

    /// <summary>История буфера обмена (только текст), хранится локально в %AppData%\Isle.</summary>
    public class ClipboardService
    {
        private HwndSource _src;
        private bool _ignoreNext;
        public ObservableCollection<ClipItem> Items { get; } = new();

        public void Attach(HwndSource src)
        {
            _src = src;
            foreach (var i in Storage.Load<ClipboardData>("clipboard.json").Items) Items.Add(i);
            Win32.AddClipboardFormatListener(src.Handle);
            src.AddHook(Hook);
        }

        private IntPtr Hook(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == Win32.WM_CLIPBOARDUPDATE) _ = OnChangedAsync();
            return IntPtr.Zero;
        }

        private async Task OnChangedAsync()
        {
            if (!AppSettings.Current.ClipboardHistory) return;
            if (_ignoreNext) { _ignoreNext = false; return; }
            string text = null;
            for (int i = 0; i < 5 && text == null; i++)
            {
                try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); else return; }
                catch { await Task.Delay(60); }
            }
            if (string.IsNullOrWhiteSpace(text)) return;
            if (text.Length > 100_000) text = text.Substring(0, 100_000);

            var existing = Items.FirstOrDefault(x => x.Text == text);
            bool pinned = existing?.Pinned ?? false;
            if (existing != null) Items.Remove(existing);
            Items.Insert(0, new ClipItem { Text = text, Source = Win32.ForegroundProcessName(), Pinned = pinned });

            int limit = Math.Max(5, AppSettings.Current.ClipboardLimit);
            while (Items.Count > limit)
            {
                var victim = Items.LastOrDefault(x => !x.Pinned);
                if (victim == null) break;
                Items.Remove(victim);
            }
            Save();
        }

        public void Save() => Storage.Save("clipboard.json", new ClipboardData { Items = Items.ToList() });

        public void Clear()
        {
            foreach (var i in Items.Where(x => !x.Pinned).ToList()) Items.Remove(i);
            Save();
        }

        public static bool SetText(string text)
        {
            for (int i = 0; i < 5; i++)
            {
                try { Clipboard.SetText(text); return true; }
                catch { System.Threading.Thread.Sleep(50); }
            }
            return false;
        }

        /// <summary>Скопировать и вставить в окно, которое было активным до открытия острова.</summary>
        public async void Paste(ClipItem item, IntPtr target, bool pasteToo)
        {
            _ignoreNext = false;
            if (!SetText(item.Text)) return;
            // поднимаем элемент наверх
            Items.Remove(item);
            item.Time = DateTime.Now;
            Items.Insert(0, item);
            Save();
            if (!pasteToo || target == IntPtr.Zero) return;
            Win32.SetForegroundWindow(target);
            await Task.Delay(120);
            Win32.SendKeyCombo(Win32.VK_CONTROL, Win32.VK_V);
        }
    }
}
