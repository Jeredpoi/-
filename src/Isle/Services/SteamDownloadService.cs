using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using System.Net.NetworkInformation;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Isle.Services
{
    public sealed class SteamDownload
    {
        public string Name { get; init; }
        public double Progress { get; init; }
        public double BytesPerSecond { get; init; }
        public TimeSpan? Remaining { get; init; }
        public ImageSource Icon { get; init; }
        public string FolderPath { get; init; }
        public bool IsPaused { get; init; }
        public bool IsEstimatedSpeed { get; init; }
        // Steam presents completed percent as an integer floor, so don't round 10.6% up to 11%.
        public string ProgressText => (int)Math.Floor(Math.Clamp(Progress, 0, 1) * 100) + "%";
        public string SpeedText => BytesPerSecond > 0 ? (IsEstimatedSpeed ? "~" : "") + FormatBytes(BytesPerSecond) + "/с" : "";
        public string EtaText => Remaining.HasValue ? "~" + FormatTime(Remaining.Value) : "";

        private static string FormatBytes(double value) => value >= 1048576
            ? (value / 1048576).ToString("0.#", CultureInfo.CurrentCulture) + " МБ"
            : Math.Max(1, value / 1024).ToString("0", CultureInfo.CurrentCulture) + " КБ";

        private static string FormatTime(TimeSpan t) => t.TotalHours >= 1
            ? ((int)t.TotalHours) + " ч " + t.Minutes + " мин"
            : t.TotalMinutes >= 1 ? Math.Max(1, (int)Math.Ceiling(t.TotalMinutes)) + " мин" : Math.Max(1, t.Seconds) + " с";
    }

    /// <summary>
    /// Reads Steam's local SteamPipe content log. Valve documents content_log.txt as the
    /// client log for SteamPipe operations; progress and rates are taken only from its records.
    /// </summary>
    public static class SteamDownloadService
    {
        private static readonly Regex AppLine = new(@"AppID\s+(\d+)\s+(?:(?:App|Workshop)\s+)?(?<kind>update|state)\s+(?<action>started|changed|canceled|finished|paused)\s*:?\s*(?<detail>.*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex BytesLine = new(@"download\s+([\d,]+)\s*/\s*([\d,]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RateLine = new(@"Current download rate:\s*([\d.,]+)\s*Mbps", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Dictionary<string, ImageSource> IconCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> NameCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<int, (long Bytes, DateTime At, double Rate)> DownloadSamples = new();
        private static readonly Dictionary<string, (long Bytes, DateTime At)> NetworkSamples = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<int> ObservedActive = new();
        private static readonly ConcurrentQueue<SteamDownload> Completed = new();
        private static readonly ConcurrentQueue<SteamDownload> Paused = new();

        private sealed class AppState
        {
            public int Id;
            public bool Active;
            public bool Paused;
            public bool Cancelled;
            public bool Finished;
            public bool Downloading;
            public bool Staging;
            public long Done, Total;
            public DateTime Updated;
            public DateTime StartedAt;
        }

        private sealed record RateSample(DateTime At, double BytesPerSecond);

        private sealed record ManifestProgress(string Name, long Downloaded, long DownloadTotal, long Staged, long StageTotal, string InstallDir, string Library);

        public static SteamDownload TakeCompleted()
        {
            return Completed.TryDequeue(out var download) ? download : null;
        }

        public static SteamDownload TakePaused()
        {
            return Paused.TryDequeue(out var download) ? download : null;
        }

        public static SteamDownload ReadCurrent()
        {
            try
            {
                var procs = System.Diagnostics.Process.GetProcessesByName("steam");
                bool running = procs.Length > 0;
                foreach (var p in procs) p.Dispose();
                if (!running) return null;
            }
            catch { return null; }
            return ReadCurrent(FindSteamRoot(), DateTime.Now);
        }

        internal static SteamDownload ReadCurrent(string root, DateTime now)
        {
            try
            {
                if (root == null) return null;
                var log = Path.Combine(root, "logs", "content_log.txt");
                if (!File.Exists(log)) return null;
                var text = ReadTail(log, 600_000);
                var states = new Dictionary<int, AppState>();
                var rates = new List<RateSample>();
                foreach (var line in text.Split('\n'))
                {
                    var timestamp = ParseTime(line);
                    var rate = RateLine.Match(line);
                    if (rate.Success && double.TryParse(rate.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedRate))
                        rates.Add(new RateSample(timestamp, parsedRate * 125_000));

                    var m = AppLine.Match(line);
                    if (!m.Success || !int.TryParse(m.Groups[1].Value, out int id)) continue;
                    var kind = m.Groups["kind"].Value.ToLowerInvariant();
                    var action = m.Groups["action"].Value.ToLowerInvariant();
                    var detail = m.Groups["detail"].Value;
                    states.TryGetValue(id, out var prior);
                    var state = new AppState
                    {
                        Id = id, Updated = timestamp, Done = prior?.Done ?? 0, Total = prior?.Total ?? 0,
                        StartedAt = prior?.StartedAt ?? DateTime.MinValue,
                        Active = prior?.Active ?? false, Paused = prior?.Paused ?? false,
                        Cancelled = prior?.Cancelled ?? false, Finished = prior?.Finished ?? false,
                        Downloading = prior?.Downloading ?? false, Staging = prior?.Staging ?? false
                    };
                    var bytes = BytesLine.Match(detail);
                    if (bytes.Success && long.TryParse(bytes.Groups[1].Value.Replace(",", ""), out var done) && long.TryParse(bytes.Groups[2].Value.Replace(",", ""), out var total))
                    { state.Done = done; state.Total = total; }

                    var normalized = detail.ToLowerInvariant();
                    if (kind == "update")
                    {
                        if (action == "started")
                        {
                            state.StartedAt = timestamp;
                            state.Active = true; state.Paused = false; state.Cancelled = false; state.Finished = false;
                            state.Downloading = normalized.Contains("download");
                            state.Staging = normalized.Contains("stage");
                        }
                        else if (action == "canceled")
                        {
                            state.Active = false;
                            state.Paused = normalized.Contains("suspend") || normalized.Contains("paus");
                            state.Cancelled = !state.Paused;
                        }
                        else if (action == "finished")
                        {
                            state.Active = false; state.Paused = false; state.Cancelled = false; state.Finished = true;
                        }
                        else if (action == "paused")
                        {
                            state.Active = false; state.Paused = true;
                        }
                        else if (action == "changed")
                        {
                            if (normalized.Contains("suspend") || normalized.Contains("paus"))
                            { state.Active = false; state.Paused = true; }
                            else if (normalized.Contains("stopping") || normalized.Trim() == "none")
                            { state.Active = false; }
                            else if (normalized.Contains("running update") && (normalized.Contains("download") || normalized.Contains("stag") || normalized.Contains("preallocat") || normalized.Contains("verifying")))
                            {
                                state.Active = true; state.Paused = false; state.Cancelled = false; state.Finished = false;
                                state.Downloading = normalized.Contains("download");
                                state.Staging = normalized.Contains("stag");
                            }
                        }
                    }
                    else if (kind == "state" && action == "changed")
                    {
                        if (normalized.Contains("update paused") || normalized.Contains("suspended"))
                        { state.Active = false; state.Paused = true; }
                        else if (state.Cancelled)
                        { state.Active = false; state.Paused = false; }
                        else if (normalized.Contains("update running"))
                        { state.Active = true; state.Paused = false; }
                        else if (normalized.Contains("fully installed") && !normalized.Contains("update required") && !normalized.Contains("update queued") && !normalized.Contains("update started"))
                        { state.Active = false; state.Paused = false; state.Finished = true; }
                    }
                    states[id] = state;
                }

                foreach (var state in states.Values)
                {
                    if (state.Active && !state.Paused && !state.Cancelled) ObservedActive.Add(state.Id);
                    else if (state.Paused)
                    {
                        if (ObservedActive.Remove(state.Id))
                        {
                            var paused = ReadManifestProgress(root, state.Id);
                            long done = paused?.Staged ?? 0, total = paused?.StageTotal ?? 0;
                            Paused.Enqueue(new SteamDownload
                            {
                                Name = !string.IsNullOrWhiteSpace(paused?.Name) ? paused.Name : FindAppName(root, state.Id) ?? "Steam · App " + state.Id,
                                Progress = total > 0 ? Math.Clamp((double)done / total, 0, 1) : 0,
                                Icon = FindAppIcon(root, state.Id),
                                FolderPath = paused == null ? null : GetInstallFolder(paused),
                                IsPaused = true
                            });
                        }
                    }
                    else if (state.Cancelled) ObservedActive.Remove(state.Id);
                    else if (state.Finished && ObservedActive.Remove(state.Id))
                    {
                        var finished = ReadManifestProgress(root, state.Id);
                        Completed.Enqueue(new SteamDownload
                        {
                            Name = !string.IsNullOrWhiteSpace(finished?.Name) ? finished.Name : FindAppName(root, state.Id) ?? "Steam · App " + state.Id,
                            Icon = FindAppIcon(root, state.Id),
                            FolderPath = finished == null ? null : GetInstallFolder(finished),
                            Progress = 1
                        });
                    }
                }

                var current = states.Values.Where(x => x.Active && !x.Paused && !x.Cancelled && !x.Finished)
                    .OrderByDescending(x => x.Updated).FirstOrDefault();
                if (current == null || current.Updated == DateTime.MinValue) return null;
                var samples = rates.Where(x => x.At >= current.StartedAt && x.At <= now).OrderBy(x => x.At).ToList();
                var manifest = ReadManifestProgress(root, current.Id);
                // 1) «Current download rate» из лога Steam — то же, что показывает сам клиент (среднее последних записей);
                // 2) прирост байт в appmanifest; 3) входящий трафик системы (помечается «~»).
                double manifestRate = !current.Paused ? GetManifestRate(current.Id, manifest?.Downloaded ?? 0, now) : 0;
                double rateBytes = 0;
                bool estimatedSpeed = false;
                var fresh = samples.Where(x => now - x.At < TimeSpan.FromSeconds(20)).TakeLast(3).ToList();
                if (current.Downloading && fresh.Count > 0) rateBytes = fresh.Average(x => x.BytesPerSecond);
                if (rateBytes <= 0) rateBytes = manifestRate;
                if (rateBytes <= 0 && samples.Count > 0 && now - samples[^1].At < TimeSpan.FromSeconds(90))
                {
                    rateBytes = samples[^1].BytesPerSecond; // последняя известная скорость из лога — как оценка
                    estimatedSpeed = rateBytes > 0;
                }
                if (rateBytes <= 0 && current.Downloading)
                {
                    rateBytes = GetSystemReceiveRate(now);
                    estimatedSpeed = rateBytes > 0;
                }
                var useStage = current.Staging || (!current.Downloading && manifest?.Staged > 0);
                long progressDone = useStage ? manifest?.Staged ?? 0 : manifest?.Downloaded ?? current.Done;
                long progressTotal = useStage ? manifest?.StageTotal ?? 0 : manifest?.DownloadTotal ?? current.Total;
                // Steam пишет байты в appmanifest редко (раз в десятки секунд), поэтому проценты «прыгали».
                // Между записями досчитываем по скорости, как это делает сам клиент Steam.
                if (!useStage) progressDone = Smooth(current.Id, progressDone, progressTotal, rateBytes, now);
                var progress = progressTotal > 0 ? Math.Clamp((double)progressDone / progressTotal, 0, 1) : 0;
                var remainingBytes = Math.Max(0, progressTotal - progressDone);
                var downloadRemaining = Math.Max(0, (manifest?.DownloadTotal ?? current.Total) - (manifest?.Downloaded ?? current.Done));
                var remaining = rateBytes > 0 && (!useStage || downloadRemaining > 0)
                    ? TimeSpan.FromSeconds((useStage ? downloadRemaining : remainingBytes) / rateBytes)
                    : (TimeSpan?)null;
                return new SteamDownload
                {
                    Name = !string.IsNullOrWhiteSpace(manifest?.Name) ? manifest.Name : FindAppName(root, current.Id) ?? "Steam · App " + current.Id,
                    Progress = progress,
                    BytesPerSecond = rateBytes,
                    IsEstimatedSpeed = estimatedSpeed,
                    Remaining = remaining,
                    Icon = FindAppIcon(root, current.Id),
                    FolderPath = manifest == null ? null : GetInstallFolder(manifest),
                    IsPaused = false
                };
            }
            catch { return null; }
        }

        private static readonly Dictionary<int, (long Real, double Est, DateTime At)> Estimates = new();

        /// <summary>
        /// Плавный прогресс между редкими записями Steam: растёт со скоростью загрузки, никогда не идёт назад,
        /// не убегает дальше реального значения больше чем на 90 секунд загрузки и не доходит до 100% раньше Steam.
        /// </summary>
        internal static long Smooth(int appId, long real, long total, double rate, DateTime now)
        {
            if (total <= 0) return real;
            if (!Estimates.TryGetValue(appId, out var e) || real < e.Real - total / 100)
                e = (real, real, now);                                   // новая загрузка или Steam начал заново
            double dt = Math.Clamp((now - e.At).TotalSeconds, 0, 10);
            double est = Math.Max(e.Est, real);
            if (rate > 0) est = Math.Min(est + rate * dt, real + rate * 90);
            est = Math.Min(est, total * 0.999);
            est = Math.Max(est, real);
            Estimates[appId] = (Math.Max(real, e.Real), est, now);
            return (long)est;
        }

        /// <summary>Папки и файлы, изменение которых означает новости о загрузке: лог Steam и appmanifest каждой библиотеки.</summary>
        public static System.Collections.Generic.IEnumerable<(string Dir, string Filter)> WatchTargets()
        {
            var root = FindSteamRoot();
            if (root == null) yield break;
            var logs = Path.Combine(root, "logs");
            if (Directory.Exists(logs)) yield return (logs, "content_log.txt");
            foreach (var lib in Libraries(root))
            {
                var apps = Path.Combine(lib, "steamapps");
                if (Directory.Exists(apps)) yield return (apps, "appmanifest_*.acf");
            }
        }

        private static string FindSteamRoot()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                var path = key?.GetValue("SteamPath") as string ?? key?.GetValue("InstallPath") as string;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    path = path.Replace('/', '\\');
                    if (File.Exists(Path.Combine(path, "logs", "content_log.txt"))) return path;
                }
            }
            catch { }
            var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
            return Directory.Exists(fallback) ? fallback : null;
        }

        private static string ReadTail(string path, int maxBytes)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Math.Max(0, stream.Length - maxBytes), SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            return reader.ReadToEnd();
        }

        private static DateTime ParseTime(string line)
        {
            var match = Regex.Match(line, @"\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\]");
            return match.Success && DateTime.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                ? DateTime.SpecifyKind(time, DateTimeKind.Local) : DateTime.MinValue;
        }

        private static List<string> Libraries(string root)
        {
            var result = new List<string> { root };
            try
            {
                var file = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (File.Exists(file))
                    foreach (Match m in Regex.Matches(File.ReadAllText(file), @"""path""\s+""([^""]+)""", RegexOptions.IgnoreCase))
                    {
                        var p = m.Groups[1].Value.Replace("\\\\", "\\");
                        if (Directory.Exists(p) && !result.Contains(p, StringComparer.OrdinalIgnoreCase)) result.Add(p);
                    }
            }
            catch { }
            return result;
        }

        private static string FindAppName(string root, int appId)
        {
            string cacheKey = root + "|" + appId;
            if (NameCache.TryGetValue(cacheKey, out var cached)) return cached;
            foreach (var library in Libraries(root))
            {
                var manifest = Path.Combine(library, "steamapps", "appmanifest_" + appId + ".acf");
                try
                {
                    var match = Regex.Match(File.ReadAllText(manifest), @"""name""\s+""([^""]+)""", RegexOptions.IgnoreCase);
                    if (match.Success) return NameCache[cacheKey] = match.Groups[1].Value;
                }
                catch { }
            }
            return null;
        }

        private static ManifestProgress ReadManifestProgress(string root, int appId)
        {
            foreach (var library in Libraries(root))
            {
                var manifest = Path.Combine(library, "steamapps", "appmanifest_" + appId + ".acf");
                try
                {
                    var text = File.ReadAllText(manifest);
                    string name = Regex.Match(text, @"""name""\s+""([^""]+)""", RegexOptions.IgnoreCase).Groups[1].Value;
                    long downloadDone = ReadManifestLong(text, "BytesDownloaded");
                    long downloadTotal = ReadManifestLong(text, "BytesToDownload");
                    long staged = ReadManifestLong(text, "BytesStaged");
                    long stageTotal = ReadManifestLong(text, "BytesToStage");
                    string installDir = Regex.Match(text, @"""installdir""\s+""([^""]+)""", RegexOptions.IgnoreCase).Groups[1].Value;
                    return new ManifestProgress(name, downloadDone, downloadTotal, staged, stageTotal, installDir, library);
                }
                catch { }
            }
            return null;
        }

        private static long ReadManifestLong(string text, string field)
        {
            var match = Regex.Match(text, "\\\"" + Regex.Escape(field) + "\\\"\\s+\\\"(\\d+)\\\"", RegexOptions.IgnoreCase);
            return match.Success && long.TryParse(match.Groups[1].Value, out var value) ? Math.Max(0, value) : 0;
        }

        /// <summary>
        /// Скорость по appmanifest: Steam пишет его редко, поэтому делим прирост байт на время
        /// с ПРОШЛОГО ИЗМЕНЕНИЯ (а не с прошлого опроса) — иначе получались всплески в разы больше реальных.
        /// </summary>
        internal static double GetManifestRate(int appId, long downloaded, DateTime now)
        {
            if (downloaded <= 0) return 0;
            if (!DownloadSamples.TryGetValue(appId, out var previous) || downloaded < previous.Bytes)
            {
                DownloadSamples[appId] = (downloaded, now, 0);
                return 0;
            }
            if (downloaded == previous.Bytes)
                return (now - previous.At).TotalSeconds <= 45 ? previous.Rate : 0; // держим последнюю скорость, пока Steam не обновил файл
            var elapsed = (now - previous.At).TotalSeconds;
            double rate = elapsed >= 1 ? (downloaded - previous.Bytes) / elapsed : previous.Rate;
            if (previous.Rate > 0) rate = previous.Rate * 0.4 + rate * 0.6;
            DownloadSamples[appId] = (downloaded, now, rate);
            return rate;
        }

        /// <summary>При отсутствии свежей Steam-выборки оценивает входящий трафик активного скачивания по сетевым счётчикам Windows.</summary>
        private static double GetSystemReceiveRate(DateTime now)
        {
            double bestRate = 0;
            try
            {
                foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (network.OperationalStatus != OperationalStatus.Up || network.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    long bytes = network.GetIPStatistics().BytesReceived;
                    if (NetworkSamples.TryGetValue(network.Id, out var previous))
                    {
                        double seconds = (now - previous.At).TotalSeconds;
                        if (bytes >= previous.Bytes && seconds >= 0.5 && seconds <= 15)
                            bestRate = Math.Max(bestRate, (bytes - previous.Bytes) / seconds);
                    }
                    NetworkSamples[network.Id] = (bytes, now);
                }
            }
            catch { return 0; }
            return bestRate;
        }

        private static ImageSource FindAppIcon(string root, int appId)
        {
            string cacheKey = root + "|" + appId;
            if (IconCache.TryGetValue(cacheKey, out var cached)) return cached;
            foreach (var path in ArtCandidates(root, appId))
            {
                try
                {
                    // миниатюра для кружка в чёлке: декодируем маленькой, чтобы не держать в памяти обложки 600×900
                    var image = new BitmapImage();
                    image.BeginInit(); image.UriSource = new Uri(path); image.CacheOption = BitmapCacheOption.OnLoad;
                    image.DecodePixelHeight = 96; image.EndInit(); image.Freeze();
                    return IconCache[cacheKey] = image;
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Картинки игры из кэша библиотеки Steam, лучшие для маленького кружка — первыми:
        /// квадратная иконка игры, затем обложка, шапка, логотип. Новый Steam кладёт их в
        /// appcache\librarycache\&lt;appid&gt;\&lt;хэш&gt;\…, старый — прямо в librarycache с префиксом appid.
        /// </summary>
        internal static IEnumerable<string> ArtCandidates(string root, int appId)
        {
            string id = appId.ToString(CultureInfo.InvariantCulture);
            var names = new[] { "library_600x900.jpg", "library_capsule.jpg", "library_header.jpg", "header.jpg", "logo.png" };
            foreach (var library in Libraries(root))
            foreach (var cache in new[] { Path.Combine(library, "appcache", "librarycache"), Path.Combine(library, "steamapps", "librarycache") })
            {
                if (!Directory.Exists(cache)) continue;
                var appDir = Path.Combine(cache, id);
                if (Directory.Exists(appDir))
                {
                    List<string> files;
                    try { files = Directory.EnumerateFiles(appDir, "*.*", SearchOption.AllDirectories).ToList(); }
                    catch { files = new List<string>(); }
                    // квадратная иконка игры: файл с именем-хэшем (40 шестнадцатеричных символов) прямо в папке игры
                    foreach (var f in files.Where(f => Path.GetDirectoryName(f) == appDir
                                 && Regex.IsMatch(Path.GetFileNameWithoutExtension(f), "^[0-9a-f]{40}$") && f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)))
                        yield return f;
                    foreach (var name in names)
                        foreach (var f in files.Where(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase)))
                            yield return f;
                }
                foreach (var name in new[] { id + "_icon.jpg", id + "_library_600x900.jpg", id + "_header.jpg", id + "_logo.png" })
                {
                    var f = Path.Combine(cache, name);
                    if (File.Exists(f)) yield return f;
                }
            }
        }

        private static string GetInstallFolder(ManifestProgress manifest)
        {
            if (string.IsNullOrWhiteSpace(manifest.InstallDir)) return null;
            var path = Path.Combine(manifest.Library, "steamapps", "common", manifest.InstallDir);
            return Directory.Exists(path) ? path : null;
        }
    }
}
