using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Isle.Native;

namespace Isle.Services
{
    /// <summary>Что лежит в записи: от этого зависят значок, фильтр и быстрые действия.</summary>
    public enum ClipKind { Text, Link, Code, Color, Email, Phone, Image, Files }

    /// <summary>Группа для фильтра «по типу» (файлы делятся на фото, видео и прочие).</summary>
    public enum ClipCategory { Text, Link, Code, Color, Image, Video, Files }

    public class ClipItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private void Changed(params string[] names) { foreach (var n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n)); }

        public string Text { get; set; } = "";
        public string Source { get; set; } = "";
        private DateTime _time = DateTime.Now;
        public DateTime Time { get => _time; set { _time = value; Changed(nameof(Time), nameof(Meta), nameof(AgoText)); } }
        private bool _pinned;
        public bool Pinned { get => _pinned; set { _pinned = value; Changed(nameof(Pinned), nameof(Meta)); } }
        /// <summary>Своя папка пользователя (null — просто история). Записи в папках не вытесняются лимитом.</summary>
        private string _folder;
        public string Folder { get => _folder; set { _folder = value; Changed(nameof(Folder), nameof(Meta)); } }
        /// <summary>Для картинок: имя PNG-файла в %AppData%\Isle\clips и хэш для поиска повторов.</summary>
        public string ImageFile { get; set; }
        public string ImageHash { get; set; }
        /// <summary>Файлы, скопированные в Проводнике (храним только пути).</summary>
        public List<string> Files { get; set; }
        /// <summary>Тип строкой (в старых записях его нет — определяется при загрузке).</summary>
        [JsonPropertyName("Kind")] public string KindName { get; set; }

        [JsonIgnore] public ClipKind Kind
        {
            get => Enum.TryParse<ClipKind>(KindName, out var k) ? k : IsImage ? ClipKind.Image : Files is { Count: > 0 } ? ClipKind.Files : ClipKind.Text;
            set => KindName = value.ToString();
        }

        [JsonIgnore] public bool IsImage => !string.IsNullOrEmpty(ImageFile);
        [JsonIgnore] public bool IsFiles => Kind == ClipKind.Files && Files is { Count: > 0 };
        [JsonIgnore] public string ImagePath => IsImage ? Path.Combine(ClipboardService.ImagesDir, ImageFile) : null;

        private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".mov", ".avi", ".webm", ".wmv", ".m4v", ".flv", ".mpg", ".mpeg", ".3gp", ".ts" };
        private static readonly HashSet<string> PhotoExt = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico", ".heic" };
        private bool AllFiles(HashSet<string> ext) => IsFiles && Files.All(f => ext.Contains(Path.GetExtension(f)));

        [JsonIgnore] public ClipCategory Category => Kind switch
        {
            ClipKind.Link => ClipCategory.Link,
            ClipKind.Code => ClipCategory.Code,
            ClipKind.Color => ClipCategory.Color,
            ClipKind.Image => ClipCategory.Image,
            ClipKind.Files => AllFiles(PhotoExt) ? ClipCategory.Image : AllFiles(VideoExt) ? ClipCategory.Video : ClipCategory.Files,
            _ => ClipCategory.Text,
        };

        /// <summary>Как рисовать значок слева: letter — буква, icon — линейная иконка, thumb — миниатюра, swatch — квадрат цвета.</summary>
        [JsonIgnore] public string Visual => Kind switch
        {
            ClipKind.Image => "thumb",
            ClipKind.Files when Category == ClipCategory.Image && Files.Any(File.Exists) => "thumb",
            ClipKind.Color when Swatch != null => "swatch",
            ClipKind.Text or ClipKind.Email or ClipKind.Phone => "letter",
            _ => "icon",
        };
        [JsonIgnore] public string Letter => Kind switch { ClipKind.Email => "@", ClipKind.Phone => "☏", _ => "T" };
        [JsonIgnore] public string IconKind => Kind switch
        {
            ClipKind.Link => "Link", ClipKind.Code => "Code", ClipKind.Color => "Pipette",
            ClipKind.Files => Category == ClipCategory.Video ? "Film" : Category == ClipCategory.Image ? "Image" : "File",
            _ => "Text",
        };

        /// <summary>Цвет из текста (#RGB, #RRGGBB, #AARRGGBB, rgb()).</summary>
        [JsonIgnore] public Brush Swatch
        {
            get
            {
                if (Kind != ClipKind.Color) return null;
                var c = ClipboardService.ParseColor(Text.Trim());
                if (c == null) return null;
                var b = new SolidColorBrush(c.Value); b.Freeze(); return b;
            }
        }

        /// <summary>Быстрое действие справа: ссылка — открыть, картинка — сохранить, файлы — показать в Проводнике.</summary>
        [JsonIgnore] public string ActionIcon => Kind switch { ClipKind.Link => "External", ClipKind.Image => "Download", ClipKind.Files => "Folder", _ => null };
        [JsonIgnore] public string ActionTip => Kind switch { ClipKind.Link => "Открыть в браузере", ClipKind.Image => "Сохранить как…", ClipKind.Files => "Показать в Проводнике", _ => null };
        [JsonIgnore] public bool HasAction => ActionIcon != null;

        private ImageSource _thumb;
        [JsonIgnore]
        public ImageSource Thumb
        {
            get
            {
                string path = IsImage ? ImagePath : Visual == "thumb" ? Files.FirstOrDefault(File.Exists) : null;
                if (_thumb != null || path == null || !File.Exists(path)) return _thumb;
                try
                {
                    var b = new BitmapImage();
                    b.BeginInit();
                    b.CacheOption = BitmapCacheOption.OnLoad;
                    b.DecodePixelHeight = 80;
                    b.UriSource = new Uri(path);
                    b.EndInit();
                    b.Freeze();
                    _thumb = b;
                }
                catch { }
                return _thumb;
            }
        }

        [JsonIgnore] public string Preview
        {
            get
            {
                if (IsFiles)
                {
                    var names = string.Join(", ", Files.Take(3).Select(Path.GetFileName));
                    return Files.Count > 3 ? names + " и ещё " + (Files.Count - 3) : names;
                }
                return Text.Length > 140 ? Text.Substring(0, 140).Replace("\r", " ").Replace("\n", " ") + "…" : Text.Replace("\r", " ").Replace("\n", " ");
            }
        }

        private string KindText => Kind switch
        {
            ClipKind.Link => "ссылка", ClipKind.Code => "код", ClipKind.Color => "цвет", ClipKind.Email => "почта", ClipKind.Phone => "телефон",
            ClipKind.Files => (Category == ClipCategory.Video ? "видео" : Category == ClipCategory.Image ? "фото" : "файлы") + (Files.Count > 1 ? " · " + Files.Count + " шт." : ""),
            _ => null,
        };

        [JsonIgnore] public string Meta => string.Join(" · ", new[] { KindText, string.IsNullOrEmpty(Source) ? null : Source, Ago(Time),
            Pinned ? "закреплено" : null, Folder != null ? "в «" + Folder + "»" : null }.Where(x => x != null));
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

    public class ClipboardData
    {
        public List<ClipItem> Items { get; set; } = new();
        /// <summary>Папки пользователя (порядок как он их создал).</summary>
        public List<string> Folders { get; set; } = new();
        /// <summary>Папка, куда сейчас сохраняются все новые копии (null — никуда).</summary>
        public string ActiveFolder { get; set; }
        /// <summary>Папка → приложения (имена процессов): снимки, сделанные в них, сами попадают в папку.</summary>
        public Dictionary<string, List<string>> FolderApps { get; set; } = new();
    }

    /// <summary>История буфера обмена: текст и картинки. Хранится локально в %AppData%\Isle.</summary>
    public class ClipboardService
    {
        public static readonly string ImagesDir = Path.Combine(Storage.Dir, "clips");
        private const int MaxImages = 25;
        public ObservableCollection<ClipItem> Items { get; } = new();
        public ObservableCollection<string> Folders { get; } = new();

        private string _activeFolder;
        /// <summary>Пока задано — каждая новая копия сразу попадает в эту папку.</summary>
        public string ActiveFolder
        {
            get => _activeFolder;
            set { if (_activeFolder == value) return; _activeFolder = value; Save(); ActiveFolderChanged?.Invoke(); }
        }
        public event Action ActiveFolderChanged;

        /// <summary>Папка → приложения, снимки из которых сами в неё попадают.</summary>
        public Dictionary<string, List<string>> FolderApps { get; } = new();
        public event Action FolderAppsChanged;

        public IReadOnlyList<string> AppsOf(string folder) => FolderApps.TryGetValue(folder, out var l) ? l : Array.Empty<string>();

        /// <summary>Включить или выключить «снимки из приложения → в папку». У приложения может быть только одна папка.</summary>
        public void SetFolderApp(string folder, string app, bool on)
        {
            if (on && !Folders.Contains(folder)) return; // папку удалили, пока было открыто меню
            foreach (var kv in FolderApps) kv.Value.RemoveAll(a => a.Equals(app, StringComparison.OrdinalIgnoreCase));
            if (on)
            {
                if (!FolderApps.TryGetValue(folder, out var list)) FolderApps[folder] = list = new List<string>();
                list.Add(app);
            }
            foreach (var k in FolderApps.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList()) FolderApps.Remove(k);
            Save();
            FolderAppsChanged?.Invoke();
        }

        private string FolderForApp(string app) => string.IsNullOrEmpty(app) ? null
            : FolderApps.FirstOrDefault(kv => kv.Value.Any(a => a.Equals(app, StringComparison.OrdinalIgnoreCase))).Key;

        // ---------- из какого приложения снимок ----------
        // Снимок Isle: приложение запоминается в момент, когда открывается слой выделения (или снимается весь
        // экран), и расходуется ОДИН раз — следующей картинкой от Isle. Отменили снимок — забываем.
        // Снимок «Ножниц» / Win+Shift+S: последнее «настоящее» активное приложение (без самого Isle и инструмента
        // снимков) — его отслеживаем постоянно, поэтому прошлый снимок из игры не подменит Discord.
        private static string _shotApp;
        private static DateTime _shotAppAt;
        private static string _lastApp;
        private static Win32.WinEventProc _fgProc; // держим ссылку — иначе сборщик мусора заберёт делегат

        /// <summary>Запомнить приложение, которое сейчас снимает Isle. Если впереди сама чёлка — окно, которое было до неё.</summary>
        public static void NoteShotApp()
        {
            var name = Win32.ForegroundProcessName();
            if (!IsRealApp(name)) name = Win32.ProcessNameOf(AppHost.Window?.PreviousForeground ?? IntPtr.Zero);
            if (!IsRealApp(name)) name = _lastApp;
            _shotApp = IsRealApp(name) ? name : null;
            _shotAppAt = DateTime.Now;
        }

        /// <summary>Снимок отменили (или это был не снимок: текст с экрана, пипетка) — приложение не должно достаться следующей картинке.</summary>
        public static void ForgetShotApp() => _shotApp = null;

        /// <summary>Снимок готов и сейчас ляжет в буфер: приложение действительно ещё 30 секунд.</summary>
        public static void ShotTaken() => _shotAppAt = DateTime.Now;

        private static readonly string OwnName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        private static bool IsOwnProcess(string name) => string.Equals(name, OwnName, StringComparison.OrdinalIgnoreCase);
        private static bool IsRealApp(string name) => !string.IsNullOrEmpty(name) && !IsOwnProcess(name) && !IsScreenshotOwner(name);

        /// <summary>Следим, какое приложение активно (для снимков «Ножницами»).</summary>
        private static void TrackForeground()
        {
            _lastApp = Win32.ForegroundProcessName() is var n && IsRealApp(n) ? n : null;
            _fgProc = (_, __, hwnd, ___, ____, _____, ______) =>
            {
                var name = Win32.ProcessNameOf(hwnd);
                if (IsRealApp(name)) _lastApp = name;
            };
            Win32.SetWinEventHook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _fgProc, 0, 0, Win32.WINEVENT_OUTOFCONTEXT);
        }

        /// <summary>Из какого приложения картинка.</summary>
        private static string ImageSource(string source)
        {
            if (IsOwnProcess(source))
            {
                // картинку положил сам Isle: снимок — тогда приложение снимка (один раз); иначе (копия из истории,
                // из редактора) — ни к какому приложению не относим
                var app = _shotApp != null && DateTime.Now - _shotAppAt < TimeSpan.FromSeconds(30) ? _shotApp : null;
                _shotApp = null;
                return app ?? source;
            }
            // «Ножницы», Win+Shift+S, PrintScreen: активным может оказаться сам инструмент снимков
            if (IsScreenshotOwner(source) || source.Length == 0) return _lastApp ?? source;
            return source;
        }

        public void Attach(HwndSource src)
        {
            var data = Storage.Load<ClipboardData>("clipboard.json");
            foreach (var f in data.Folders.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct()) Folders.Add(f);
            foreach (var i in data.Items)
            {
                if (i.IsImage && !File.Exists(i.ImagePath)) continue;
                if (i.KindName == null) i.Kind = i.IsImage ? ClipKind.Image : i.Files is { Count: > 0 } ? ClipKind.Files : Classify(i.Text, i.Source);
                if (i.Folder != null && !Folders.Contains(i.Folder)) Folders.Add(i.Folder);
                Items.Add(i);
            }
            _activeFolder = data.ActiveFolder != null && Folders.Contains(data.ActiveFolder) ? data.ActiveFolder : null;
            foreach (var kv in data.FolderApps ?? new())
                if (Folders.Contains(kv.Key) && kv.Value is { Count: > 0 }) FolderApps[kv.Key] = kv.Value.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _ = Task.Run(CleanOrphans);
            try { TrackForeground(); } catch (Exception ex) { App.Log("слежение за активным окном: " + ex.Message); }
            Win32.AddClipboardFormatListener(src.Handle);
            src.AddHook(Hook);
        }

        /// <summary>PNG в папке clips, на которые больше нет записи (падение посреди сохранения, отменённая очистка).</summary>
        private void CleanOrphans()
        {
            try
            {
                if (!Directory.Exists(ImagesDir)) return;
                HashSet<string> used = null;
                Application.Current?.Dispatcher.Invoke(() => used = new HashSet<string>(Items.Where(x => x.IsImage).Select(x => x.ImageFile), StringComparer.OrdinalIgnoreCase));
                if (used == null) return;
                int n = 0;
                foreach (var f in Directory.EnumerateFiles(ImagesDir, "*.png"))
                    if (!used.Contains(Path.GetFileName(f)) && DateTime.Now - File.GetCreationTime(f) > TimeSpan.FromMinutes(5)) { try { File.Delete(f); n++; } catch { } }
                if (n > 0) Journal.Info("clipboard", "удалены лишние картинки: " + n);
            }
            catch { }
        }

        // ---------- тип текста ----------
        private static readonly Regex ColorRx = new(@"^(#([0-9a-f]{3}|[0-9a-f]{6}|[0-9a-f]{8})|rgba?\(\s*\d{1,3}\s*,\s*\d{1,3}\s*,\s*\d{1,3}\s*(,\s*[\d.]+\s*)?\))$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex EmailRx = new(@"^[^@\s]+@[^@\s]+\.[a-zа-я]{2,}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex PhoneRx = new(@"^\+?[\d\s\-()]{7,20}$", RegexOptions.Compiled);
        private static readonly Regex DateRx = new(@"^\d{1,4}[-./]\d{1,2}[-./]\d{1,4}$", RegexOptions.Compiled);
        private static readonly HashSet<string> CodeApps = new(StringComparer.OrdinalIgnoreCase)
            { "Code", "devenv", "rider64", "idea64", "pycharm64", "webstorm64", "clion64", "sublime_text", "notepad++", "Cursor", "zed" };

        public static ClipKind Classify(string text, string source)
        {
            var s = (text ?? "").Trim();
            if (s.Length == 0) return ClipKind.Text;
            bool oneLine = s.IndexOfAny(new[] { '\n', '\r' }) < 0;
            if (oneLine && s.Length <= 2048 && !s.Contains(' '))
            {
                if ((Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https"))
                    || (s.StartsWith("www.", StringComparison.OrdinalIgnoreCase) && s.IndexOf('.', 4) > 0)) return ClipKind.Link;
            }
            if (oneLine && s.Length <= 40 && ColorRx.IsMatch(s)) return ClipKind.Color;
            if (oneLine && s.Length <= 254 && EmailRx.IsMatch(s)) return ClipKind.Email;
            // телефон: с «+» или 10–15 цифр; дата «2024-01-15» и просто длинные числа — не телефон
            if (oneLine && PhoneRx.IsMatch(s) && !DateRx.IsMatch(s) && (s.StartsWith("+") ? s.Count(char.IsDigit) >= 7 : s.Count(char.IsDigit) is >= 10 and <= 15)
                && (s.Any(ch => !char.IsDigit(ch)) || (s.Length == 11 && (s[0] == '7' || s[0] == '8')))) return ClipKind.Phone;
            var lines = s.Split('\n');
            if (CodeApps.Contains(source ?? "") && (lines.Length > 1 || s.IndexOfAny(new[] { '(', '{', ';', '=' }) >= 0)) return ClipKind.Code;
            if (lines.Length >= 2)
            {
                int score = lines.Count(l =>
                {
                    var t = l.TrimEnd();
                    return t.EndsWith(";") || t.EndsWith("{") || t.TrimStart().StartsWith("}") || t.Contains("=>") || t.Contains("</")
                        || t.TrimStart().StartsWith("def ") || t.TrimStart().StartsWith("import ") || t.TrimStart().StartsWith("#include") || t.Contains("function ");
                });
                if (score >= Math.Max(2, lines.Length / 3)) return ClipKind.Code;
            }
            return ClipKind.Text;
        }

        public static Color? ParseColor(string s)
        {
            try
            {
                if (s.StartsWith("#"))
                {
                    var h = s.Substring(1);
                    if (h.Length == 3) h = string.Concat(h.Select(c => new string(c, 2)));
                    return (Color)ColorConverter.ConvertFromString("#" + h);
                }
                var m = Regex.Matches(s, @"[\d.]+");
                if (m.Count >= 3)
                {
                    byte a = m.Count >= 4 ? (byte)Math.Clamp(double.Parse(m[3].Value, System.Globalization.CultureInfo.InvariantCulture) * 255, 0, 255) : (byte)255;
                    return Color.FromArgb(a, (byte)Math.Clamp(int.Parse(m[0].Value), 0, 255), (byte)Math.Clamp(int.Parse(m[1].Value), 0, 255), (byte)Math.Clamp(int.Parse(m[2].Value), 0, 255));
                }
            }
            catch { }
            return null;
        }

        // ---------- папки ----------
        public bool AddFolder(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || Folders.Contains(name, StringComparer.CurrentCultureIgnoreCase)) return false;
            Folders.Add(name); Save(); return true;
        }

        public bool RenameFolder(string old, string name)
        {
            name = (name ?? "").Trim();
            int i = Folders.IndexOf(old);
            if (i < 0 || name.Length == 0 || (name != old && Folders.Contains(name, StringComparer.CurrentCultureIgnoreCase) && !string.Equals(name, old, StringComparison.CurrentCultureIgnoreCase))) return false;
            // сначала записи, приложения и «запись в папку», и только потом сам список: его изменение сразу
            // перестраивает колонку — раньше она успевала увидеть папку пустой и без значка снимка
            foreach (var it in Items.Where(x => x.Folder == old)) it.Folder = name;
            bool hadApps = FolderApps.Remove(old, out var apps);
            if (hadApps) FolderApps[name] = apps;
            bool wasActive = _activeFolder == old;
            if (wasActive) _activeFolder = name;
            Folders[i] = name;
            Save();
            if (wasActive) ActiveFolderChanged?.Invoke();
            if (hadApps) FolderAppsChanged?.Invoke();
            return true;
        }

        /// <summary>Что вернуть, если удаление папки отменили.</summary>
        public class FolderUndo
        {
            public string Name; public int Index; public List<ClipItem> Moved; public List<string> Apps; public bool WasActive;
            public List<(int index, ClipItem item)> Evicted;
        }

        /// <summary>
        /// Удалить папку: её записи переходят в общую историю. Если история от этого перерастает лимит,
        /// самые старые обычные записи убираются сразу (а не молча при следующем копировании) — и всё это
        /// можно отменить: <see cref="UndoDeleteFolder"/>, файлы картинок — <see cref="Purge"/>.
        /// </summary>
        public FolderUndo DeleteFolder(string name)
        {
            var u = new FolderUndo { Name = name, Index = Folders.IndexOf(name), Moved = Items.Where(x => x.Folder == name).ToList(), WasActive = _activeFolder == name };
            if (FolderApps.Remove(name, out var apps)) u.Apps = apps;
            foreach (var it in u.Moved) it.Folder = null;
            if (u.WasActive) _activeFolder = null;
            Folders.Remove(name);
            u.Evicted = SoftRemove(OverLimit());
            Save();
            if (u.WasActive) ActiveFolderChanged?.Invoke();
            if (u.Apps != null) FolderAppsChanged?.Invoke();
            return u;
        }

        public void UndoDeleteFolder(FolderUndo u)
        {
            Restore(u.Evicted);
            if (!Folders.Contains(u.Name)) Folders.Insert(Math.Clamp(u.Index, 0, Folders.Count), u.Name);
            foreach (var it in u.Moved) it.Folder = u.Name;
            if (u.Apps != null) FolderApps[u.Name] = u.Apps;
            if (u.WasActive) _activeFolder = u.Name;
            Save();
            if (u.WasActive) ActiveFolderChanged?.Invoke();
            if (u.Apps != null) FolderAppsChanged?.Invoke();
        }

        /// <summary>Обычные (не закреплённые и не в папках) записи сверх лимита истории — самые старые.</summary>
        private List<ClipItem> OverLimit()
        {
            int limit = Math.Max(5, AppSettings.Current.ClipboardLimit);
            var loose = Items.Where(x => !x.Pinned && x.Folder == null).ToList();
            return loose.Count > limit ? loose.Skip(limit).ToList() : new List<ClipItem>();
        }

        public void MoveToFolder(ClipItem item, string folder) { item.Folder = folder; Save(); }

        private IntPtr Hook(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == Win32.WM_CLIPBOARDUPDATE) _ = OnChangedAsync();
            return IntPtr.Zero;
        }

        /// <summary>Когда Isle последний раз сам показал «Снимок экрана» (чтобы не дублировать уведомление Windows).</summary>
        public static DateTime LastScreenshotToast { get; private set; }

        /// <summary>Снимок системными «Ножницами» / Win+Shift+S / PrintScreen: владелец буфера — инструмент снимков или никто.</summary>
        public static bool IsScreenshotOwner(string process) =>
            process.Length == 0 || process.Equals("ScreenClippingHost", StringComparison.OrdinalIgnoreCase)
            || process.StartsWith("SnippingTool", StringComparison.OrdinalIgnoreCase)
            || process.Equals("ScreenSketch", StringComparison.OrdinalIgnoreCase);

        /// <summary>До этого момента копирования делает сам Isle (правка текста ИИ) — в историю их не пишем.</summary>
        public static DateTime IgnoreUntil { get; set; }

        /// <summary>
        /// Одно изменение буфера за раз. Windows часто шлёт два события подряд на один снимок
        /// (картинка кладётся в нескольких форматах) — раньше оба считали хэш параллельно,
        /// не находили друг друга в истории, и снимок появлялся в буфере дважды.
        /// </summary>
        private readonly SemaphoreSlim _changeGate = new(1, 1);

        private async Task OnChangedAsync()
        {
            if (DateTime.Now < IgnoreUntil) return;
            await _changeGate.WaitAsync();
            try { await HandleChangeAsync(); }
            finally { _changeGate.Release(); }
        }

        private async Task HandleChangeAsync()
        {
            var s = AppSettings.Current;
            string owner = Win32.ClipboardOwnerProcess(); // сразу, пока владелец ещё тот
            await Task.Delay(40);
            await DetectScreenshotAsync(owner);
            if (!s.ClipboardHistory) return;

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

            bool hasText = false, hasImage = false, hasFiles = false;
            for (int i = 0; i < 5; i++)
            {
                try { hasFiles = Clipboard.ContainsFileDropList(); hasText = Clipboard.ContainsText(); hasImage = Clipboard.ContainsImage(); break; }
                catch { await Task.Delay(60); }
            }

            string source = !string.IsNullOrEmpty(owner) && !IsScreenshotOwner(owner) ? owner : Win32.ForegroundProcessName();
            if (hasFiles) await AddFilesAsync(source);
            else if (hasText) await AddTextAsync(source);
            else if (hasImage && s.ClipboardImages) { await AddImageAsync(ImageSource(source)); Helpers.MemoryTrim.Soon("картинка в буфере"); }
        }

        /// <summary>
        /// Снимок экрана средствами Windows всегда даёт карточку в чёлке с миниатюрой —
        /// не полагаемся на уведомление «Ножниц», которое приходит не всегда.
        /// </summary>
        private async Task DetectScreenshotAsync(string owner)
        {
            if (!IsScreenshotOwner(owner)) return;
            BitmapSource img = null;
            for (int i = 0; i < 5 && img == null; i++)
            {
                try { if (!Clipboard.ContainsImage()) return; img = Clipboard.GetImage(); }
                catch { await Task.Delay(60); }
            }
            if (img == null) return;
            LastScreenshotToast = DateTime.Now;
            ToastBus.Show(new Toast
            {
                Kind = ToastKind.Success, Title = "Снимок экрана", Subtitle = "В буфере обмена · " + img.PixelWidth + "×" + img.PixelHeight,
                Image = img, Icon = Icons.Crop, Important = true, Duration = TimeSpan.FromSeconds(3), ReplaceKey = "screenshot"
            });
        }

        /// <summary>Файлы из Проводника (фото, видео, документы): храним только пути — вставляются обратно как файлы.</summary>
        private async Task AddFilesAsync(string source)
        {
            List<string> files = null;
            for (int i = 0; i < 5 && files == null; i++)
            {
                try { files = Clipboard.GetFileDropList().Cast<string>().Where(f => !string.IsNullOrEmpty(f)).ToList(); }
                catch { await Task.Delay(60); }
            }
            if (files == null || files.Count == 0) return;
            var existing = Items.FirstOrDefault(x => x.IsFiles && x.Files.SequenceEqual(files, StringComparer.OrdinalIgnoreCase));
            Insert(existing, new ClipItem { Kind = ClipKind.Files, Files = files, Text = string.Join("\n", files), Source = source });
        }

        private async Task AddTextAsync(string source)
        {
            string text = null;
            for (int i = 0; i < 5 && text == null; i++)
            {
                try { text = Clipboard.GetText(); }
                catch { await Task.Delay(60); }
            }
            if (string.IsNullOrWhiteSpace(text)) return;
            if (text.Length > 100_000) text = text.Substring(0, 100_000);
            var existing = Items.FirstOrDefault(x => !x.IsImage && !x.IsFiles && x.Text == text);
            Insert(existing, new ClipItem { Text = text, Source = source, Kind = Classify(text, source) });
        }

        private async Task AddImageAsync(string source)
        {
            try
            {
                // буфер может быть на мгновение занят другой программой (CLIPBRD_E_CANT_OPEN) — пробуем ещё пару раз
                BitmapSource img = null;
                for (int attempt = 0; ; attempt++)
                {
                    try { img = Clipboard.GetImage(); break; }
                    catch (System.Runtime.InteropServices.COMException) when (attempt < 4) { await Task.Delay(60 + attempt * 60); }
                }
                if (img == null) return;
                // Хэш и PNG для снимка 4K — это сотни миллисекунд: считаем в фоне, чтобы чёлка не замирала при копировании картинки
                if (img.CanFreeze) img.Freeze();
                if (!img.IsFrozen) img = new WriteableBitmap(img).GetAsFrozen() as BitmapSource ?? img; // в фоне можно работать только с «замороженной» картинкой
                var file = Guid.NewGuid().ToString("N") + ".png";
                var (hash, w, h) = await Task.Run(() =>
                {
                    var px = Services.ScreenTools.Bgra(img, out int pw, out int ph);
                    return (Convert.ToHexString(SHA1.HashData(px)), pw, ph);
                });
                var existing = Items.FirstOrDefault(x => x.ImageHash == hash);
                if (existing != null) { Insert(existing, null); return; }
                // та же картинка, пришедшая вторым событием в другом формате (пиксели чуть отличаются) — не дублируем
                var last = Items.FirstOrDefault();
                if (last != null && last.IsImage && DateTime.Now - last.Time < TimeSpan.FromSeconds(2)
                    && last.Text == "Изображение " + w + "×" + h) return;

                await Task.Run(() =>
                {
                    Directory.CreateDirectory(ImagesDir);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(img));
                    using var fs = File.Create(Path.Combine(ImagesDir, file));
                    enc.Save(fs);
                });
                Insert(null, new ClipItem
                {
                    Text = "Изображение " + w + "×" + h, ImageFile = file, ImageHash = hash, Source = source, Kind = ClipKind.Image
                });
            }
            catch (Exception ex) { App.Log("clip image: " + ex.Message); }
        }

        /// <summary>Добавляет новый элемент наверх (или поднимает уже существующий).</summary>
        private void Insert(ClipItem existing, ClipItem fresh)
        {
            // содержимое буфера в журнал не пишем (там бывают пароли) — только тип, длину и откуда
            var it = existing ?? fresh;
            Journal.Info("clipboard", (existing != null ? "повтор — поднят наверх" : "новое") + ": "
                + (it == null ? "?" : it.IsImage ? "картинка" : it.IsFiles ? "файлы, " + it.Files.Count + " шт." : it.Kind.ToString().ToLowerInvariant() + ", " + (it.Text?.Length ?? 0) + " симв.")
                + (_activeFolder != null ? ", в папку" : "")
                + (it?.Source is string src && src.Length > 0 ? ", из " + src : ""));
            if (existing != null)
            {
                Items.Remove(existing);
                existing.Time = DateTime.Now;
                if (existing.Folder == null && _activeFolder != null) existing.Folder = _activeFolder;
                Items.Insert(0, existing);
            }
            else
            {
                // снимок из приложения, привязанного к папке, — в эту папку (точнее, чем общая «запись в папку»)
                var byApp = fresh.IsImage ? FolderForApp(fresh.Source) : null;
                if (byApp != null) fresh.Folder = byApp;
                else if (_activeFolder != null) fresh.Folder = _activeFolder;
                Items.Insert(0, fresh);
            }

            // лимит истории: закреплённые и разложенные по папкам не вытесняются
            static bool Kept(ClipItem x) => x.Pinned || x.Folder != null;
            int limit = Math.Max(5, AppSettings.Current.ClipboardLimit);
            int loose = Items.Count(x => !Kept(x)), images = Items.Count(x => x.IsImage && !Kept(x));
            while (loose > limit || images > MaxImages)
            {
                bool tooManyImages = images > MaxImages;
                var victim = Items.LastOrDefault(x => !Kept(x) && (!tooManyImages || x.IsImage));
                if (victim == null) break;
                Remove(victim, false);
                loose--; if (victim.IsImage) images--;
            }
            Save();
        }

        public void Save() => Storage.Save("clipboard.json", new ClipboardData
        {
            Items = Items.ToList(), Folders = Folders.ToList(), ActiveFolder = _activeFolder,
            FolderApps = FolderApps.ToDictionary(kv => kv.Key, kv => kv.Value.ToList())
        });

        public void Remove(ClipItem item, bool save = true)
        {
            Items.Remove(item);
            if (item.IsImage) { try { File.Delete(item.ImagePath); } catch { } }
            if (save) Save();
        }

        /// <summary>
        /// «Мягкая» очистка: записи убираются из списка, но файлы картинок пока остаются —
        /// чтобы «Отменить» могло всё вернуть. Окончательно — <see cref="Purge"/>.
        /// </summary>
        public List<(int index, ClipItem item)> SoftRemove(IEnumerable<ClipItem> items)
        {
            var set = new HashSet<ClipItem>(items);
            var removed = new List<(int, ClipItem)>();
            for (int i = 0; i < Items.Count; i++) if (set.Contains(Items[i])) removed.Add((i, Items[i]));
            for (int k = removed.Count - 1; k >= 0; k--) Items.RemoveAt(removed[k].Item1);
            Save();
            return removed;
        }

        public void Restore(List<(int index, ClipItem item)> removed)
        {
            foreach (var (i, it) in removed.OrderBy(x => x.index)) Items.Insert(Math.Min(i, Items.Count), it);
            Save();
        }

        public void Purge(List<(int index, ClipItem item)> removed)
        {
            foreach (var (_, it) in removed)
                if (it.IsImage && !Items.Contains(it)) { try { File.Delete(it.ImagePath); } catch { } }
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
            if (item.IsFiles)
            {
                var alive = item.Files.Where(f => File.Exists(f) || Directory.Exists(f)).ToArray();
                if (alive.Length == 0)
                {
                    ToastBus.Show(new Toast { Kind = ToastKind.Error, Title = "Файлов больше нет", Subtitle = "Их переместили или удалили", Icon = Icons.Folder, Important = true });
                    return false;
                }
                try
                {
                    var sc = new StringCollection(); sc.AddRange(alive);
                    var dobj = new DataObject();
                    dobj.SetFileDropList(sc);
                    dobj.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1))); // копировать, а не перемещать
                    for (int i = 0; i < 5; i++)
                    {
                        try { Clipboard.SetDataObject(dobj, true); return true; }
                        catch { Thread.Sleep(50); }
                    }
                }
                catch { }
                return false;
            }
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
