using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Isle.Native;

namespace Isle.Services
{
    public class ClipItem
    {
        public string Text { get; set; } = "";
        public string Source { get; set; } = "";
        public DateTime Time { get; set; } = DateTime.Now;
        public bool Pinned { get; set; }
        /// <summary>Для картинок: имя PNG-файла в %AppData%\Isle\clips и хэш для поиска повторов.</summary>
        public string ImageFile { get; set; }
        public string ImageHash { get; set; }

        [JsonIgnore] public bool IsImage => !string.IsNullOrEmpty(ImageFile);
        [JsonIgnore] public string ImagePath => IsImage ? Path.Combine(ClipboardService.ImagesDir, ImageFile) : null;

        private ImageSource _thumb;
        [JsonIgnore]
        public ImageSource Thumb
        {
            get
            {
                if (_thumb != null || !IsImage || !File.Exists(ImagePath)) return _thumb;
                try
                {
                    var b = new BitmapImage();
                    b.BeginInit();
                    b.CacheOption = BitmapCacheOption.OnLoad;
                    b.DecodePixelHeight = 80;
                    b.UriSource = new Uri(ImagePath);
                    b.EndInit();
                    b.Freeze();
                    _thumb = b;
                }
                catch { }
                return _thumb;
            }
        }

        [JsonIgnore] public string Preview => Text.Length > 140 ? Text.Substring(0, 140).Replace("\r", " ").Replace("\n", " ") + "…" : Text.Replace("\r", " ").Replace("\n", " ");
        [JsonIgnore] public string Meta => (string.IsNullOrEmpty(Source) ? "" : Source + " · ") + Ago(Time) + (Pinned ? " · закреплено" : "");
        [JsonIgnore] public string AgoText => Ago(Time);

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

    /// <summary>История буфера обмена: текст и картинки. Хранится локально в %AppData%\Isle.</summary>
    public class ClipboardService
    {
        public static readonly string ImagesDir = Path.Combine(Storage.Dir, "clips");
        private const int MaxImages = 25;
        public ObservableCollection<ClipItem> Items { get; } = new();

        public void Attach(HwndSource src)
        {
            foreach (var i in Storage.Load<ClipboardData>("clipboard.json").Items)
                if (!i.IsImage || File.Exists(i.ImagePath)) Items.Add(i);
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
            var s = AppSettings.Current;
            if (!s.ClipboardHistory) return;
            await Task.Delay(40);

            // Менеджеры паролей помечают секретные копии — такие не сохраняем.
            try
            {
                if (Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing") ||
                    Clipboard.ContainsData("Clipboard Viewer Ignore")) return;
                // KeePass, Bitwarden, 1Password и др. просят Windows не класть пароль в журнал: CanIncludeInClipboardHistory = 0
                if (Clipboard.ContainsData("CanIncludeInClipboardHistory") &&
                    Clipboard.GetData("CanIncludeInClipboardHistory") is MemoryStream ms && ms.Length >= 4)
                {
                    var b = ms.ToArray();
                    if (BitConverter.ToInt32(b, 0) == 0) return;
                }
            }
            catch { }

            bool hasText = false, hasImage = false;
            for (int i = 0; i < 5; i++)
            {
                try { hasText = Clipboard.ContainsText(); hasImage = Clipboard.ContainsImage(); break; }
                catch { await Task.Delay(60); }
            }

            if (hasText) await AddTextAsync();
            else if (hasImage && s.ClipboardImages) AddImage();
        }

        private async Task AddTextAsync()
        {
            string text = null;
            for (int i = 0; i < 5 && text == null; i++)
            {
                try { text = Clipboard.GetText(); }
                catch { await Task.Delay(60); }
            }
            if (string.IsNullOrWhiteSpace(text)) return;
            if (text.Length > 100_000) text = text.Substring(0, 100_000);
            var existing = Items.FirstOrDefault(x => !x.IsImage && x.Text == text);
            Insert(existing, new ClipItem { Text = text, Source = Win32.ForegroundProcessName() });
        }

        private void AddImage()
        {
            try
            {
                var img = Clipboard.GetImage();
                if (img == null) return;
                var px = Services.ScreenTools.Bgra(img, out int w, out int h);
                var hash = Convert.ToHexString(SHA1.HashData(px));
                var existing = Items.FirstOrDefault(x => x.ImageHash == hash);
                if (existing != null) { Insert(existing, null); return; }

                Directory.CreateDirectory(ImagesDir);
                var file = Guid.NewGuid().ToString("N") + ".png";
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(img));
                using (var fs = File.Create(Path.Combine(ImagesDir, file))) enc.Save(fs);
                Insert(null, new ClipItem
                {
                    Text = "Изображение " + w + "×" + h, ImageFile = file, ImageHash = hash, Source = Win32.ForegroundProcessName()
                });
            }
            catch (Exception ex) { App.Log("clip image: " + ex.Message); }
        }

        /// <summary>Добавляет новый элемент наверх (или поднимает уже существующий).</summary>
        private void Insert(ClipItem existing, ClipItem fresh)
        {
            if (existing != null)
            {
                Items.Remove(existing);
                existing.Time = DateTime.Now;
                Items.Insert(0, existing);
            }
            else Items.Insert(0, fresh);

            int limit = Math.Max(5, AppSettings.Current.ClipboardLimit);
            while (Items.Count > limit || Items.Count(x => x.IsImage) > MaxImages)
            {
                bool tooManyImages = Items.Count(x => x.IsImage) > MaxImages;
                var victim = Items.LastOrDefault(x => !x.Pinned && (!tooManyImages || x.IsImage));
                if (victim == null) break;
                Remove(victim, false);
            }
            Save();
        }

        public void Save() => Storage.Save("clipboard.json", new ClipboardData { Items = Items.ToList() });

        public void Remove(ClipItem item, bool save = true)
        {
            Items.Remove(item);
            if (item.IsImage) { try { File.Delete(item.ImagePath); } catch { } }
            if (save) Save();
        }

        public void Clear()
        {
            foreach (var i in Items.Where(x => !x.Pinned).ToList()) Remove(i, false);
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

        private static bool SetItem(ClipItem item)
        {
            if (!item.IsImage) return SetText(item.Text);
            try
            {
                var b = new BitmapImage();
                b.BeginInit();
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.UriSource = new Uri(item.ImagePath);
                b.EndInit();
                ScreenTools.CopyImage(b);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Скопировать и вставить в окно, которое было активным до открытия острова.</summary>
        public async void Paste(ClipItem item, IntPtr target, bool pasteToo)
        {
            if (!SetItem(item)) return;
            if (!pasteToo || target == IntPtr.Zero) return;
            // обычный SetForegroundWindow Windows часто блокирует — тогда вставка улетала в никуда
            Win32.ForceForeground(target);
            await Task.Delay(120);
            Win32.SendKeyCombo(Win32.VK_CONTROL, Win32.VK_V);
        }
    }
}
