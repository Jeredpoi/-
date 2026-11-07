using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Isle.Services
{
    /// <summary>Файл, который браузер сейчас качает (недокачанный .crdownload / .part …).</summary>
    public sealed record PartialDownload(string Path, string Name, string Browser, long Bytes, double BytesPerSecond, string Folder)
    {
        public string SizeText => FormatBytes(Bytes);
        public string SpeedText => BytesPerSecond > 0 ? FormatBytes(BytesPerSecond) + "/с" : "";

        public static string FormatBytes(double v) => v >= 1073741824 ? (v / 1073741824).ToString("0.##") + " ГБ"
            : v >= 1048576 ? (v / 1048576).ToString("0.#") + " МБ" : Math.Max(1, v / 1024).ToString("0") + " КБ";
    }

    /// <summary>Загрузка браузера завершилась: итоговый файл и папка.</summary>
    public sealed record FinishedDownload(string Name, string Browser, string Folder);

    /// <summary>
    /// Загрузки браузеров по файлам в папке загрузок — работает всегда, даже когда браузер не даёт прочитать
    /// свою историю: пока файл качается, у него «временное» расширение (.crdownload у Chrome/Edge/Яндекса/Comet,
    /// .part у Firefox, .opdownload у Opera). Скорость считаем по росту файла — она точная.
    /// Размер всего файла браузер в таком файле не хранит, поэтому прогресс — «крутилка» с размером и скоростью.
    /// </summary>
    public static class PartialDownloadService
    {
        private static readonly string[] Extensions = { ".crdownload", ".part", ".opdownload", ".download", ".partial" };
        private sealed class Track { public long Bytes; public DateTime At; public double Rate; public DateTime LastGrow; public string Folder; public string Name; public string Browser; }
        private static readonly Dictionary<string, Track> Tracks = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Queue<FinishedDownload> Finished = new();
        private static readonly object Gate = new();

        public static FinishedDownload TakeFinished() { lock (Gate) return Finished.Count > 0 ? Finished.Dequeue() : null; }

        /// <summary>Папки, куда качают браузеры: «Загрузки» Windows и папки из настроек профилей Chromium.</summary>
        private static List<string> _folders;
        private static DateTime _foldersAt;

        public static List<string> Folders()
        {
            // настройки профилей читаем не чаще раза в минуту — папки меняются редко
            if (_folders != null && DateTime.Now - _foldersAt < TimeSpan.FromMinutes(1)) return _folders;
            _foldersAt = DateTime.Now;
            return _folders = ReadFolders();
        }

        private static List<string> ReadFolders()
        {
            var result = new List<string>();
            void Add(string d) { if (!string.IsNullOrWhiteSpace(d) && Directory.Exists(d) && !result.Contains(d, StringComparer.OrdinalIgnoreCase)) result.Add(d); }
            Add(KnownDownloads());
            Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
            try
            {
                foreach (var (dir, _) in BrowserDownloadService.WatchTargets())
                {
                    var prefs = Path.Combine(dir, "Preferences");
                    if (!File.Exists(prefs)) continue;
                    try
                    {
                        using var fs = new FileStream(prefs, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var doc = JsonDocument.Parse(fs);
                        if (doc.RootElement.TryGetProperty("download", out var dl) && dl.TryGetProperty("default_directory", out var d))
                            Add(d.GetString());
                    }
                    catch { }
                }
            }
            catch { }
            return result;
        }

        /// <summary>Проверить папки: идущие загрузки (самая свежая — первой); завершившиеся попадают в очередь TakeFinished.</summary>
        public static List<PartialDownload> Scan()
        {
            lock (Gate)
            {
                var now = DateTime.Now;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var folder in Folders())
                {
                    IEnumerable<string> files;
                    try { files = Directory.EnumerateFiles(folder).Where(f => Extensions.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase))).ToList(); }
                    catch { continue; }
                    foreach (var f in files)
                    {
                        FileInfo info;
                        try { info = new FileInfo(f); } catch { continue; }
                        if (now - info.LastWriteTime > TimeSpan.FromMinutes(2)) continue; // брошенный недокачанный файл
                        seen.Add(f);
                        long bytes = info.Length;
                        if (!Tracks.TryGetValue(f, out var t))
                        {
                            Tracks[f] = new Track { Bytes = bytes, At = now, LastGrow = now, Folder = folder, Name = CleanName(f), Browser = BrowserFor(f) };
                            continue;
                        }
                        double dt = (now - t.At).TotalSeconds;
                        if (bytes > t.Bytes && dt >= 0.3)
                        {
                            double r = (bytes - t.Bytes) / dt;
                            t.Rate = t.Rate > 0 ? t.Rate * 0.5 + r * 0.5 : r;
                            t.Bytes = bytes; t.At = now; t.LastGrow = now;
                        }
                        else if (now - t.LastGrow > TimeSpan.FromSeconds(4)) t.Rate *= 0.5; // файл не растёт — скорость плавно падает
                    }
                }

                // временный файл исчез — загрузка закончилась (или её отменили: тогда итогового файла нет)
                foreach (var gone in Tracks.Keys.Where(k => !seen.Contains(k)).ToList())
                {
                    var t = Tracks[gone];
                    Tracks.Remove(gone);
                    var final = FinalFile(gone, t.Folder, now);
                    if (final != null) Finished.Enqueue(new FinishedDownload(Path.GetFileName(final), t.Browser, t.Folder));
                }

                return Tracks.Where(p => now - p.Value.LastGrow < TimeSpan.FromSeconds(30))
                    .OrderByDescending(p => p.Value.LastGrow)
                    .Select(p => new PartialDownload(p.Key, p.Value.Name, p.Value.Browser, p.Value.Bytes, p.Value.Rate, p.Value.Folder))
                    .ToList();
            }
        }

        private static string FinalFile(string partial, string folder, DateTime now)
        {
            var direct = partial.Substring(0, partial.Length - Path.GetExtension(partial).Length);
            if (File.Exists(direct)) return direct;
            // «Неподтверждённый 12345.crdownload» у Chrome переименовывается в настоящее имя — берём самый свежий файл
            try
            {
                return Directory.EnumerateFiles(folder)
                    .Where(f => !Extensions.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                    .Select(f => new FileInfo(f))
                    .Where(i => now - i.LastWriteTime < TimeSpan.FromSeconds(20))
                    .OrderByDescending(i => i.LastWriteTime)
                    .FirstOrDefault()?.FullName;
            }
            catch { return null; }
        }

        internal static string CleanName(string partial)
        {
            var name = Path.GetFileNameWithoutExtension(partial);
            if (name.StartsWith("Unconfirmed ", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Не подтверждено", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Неподтвержденный", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Неподтверждённый", StringComparison.OrdinalIgnoreCase))
                return "Новый файл";
            return name.Length > 0 ? name : "Файл";
        }

        private static string BrowserFor(string partial) =>
            partial.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ? "Firefox"
            : partial.EndsWith(".opdownload", StringComparison.OrdinalIgnoreCase) ? "Opera" : "Браузер";

        [DllImport("shell32.dll")]
        private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

        /// <summary>Настоящая папка «Загрузки» (её могут перенести на другой диск).</summary>
        private static string KnownDownloads()
        {
            try
            {
                if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var p) == 0)
                {
                    try { return Marshal.PtrToStringUni(p); } finally { Marshal.FreeCoTaskMem(p); }
                }
            }
            catch { }
            return null;
        }
    }
}
