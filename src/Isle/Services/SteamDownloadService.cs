using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
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
        public string ProgressText => (int)Math.Round(Progress * 100) + "%";
        public string SpeedText => BytesPerSecond > 0 ? FormatBytes(BytesPerSecond) + "/с" : "";
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
        private static readonly HashSet<int> ObservedActive = new();
        private static readonly ConcurrentQueue<SteamDownload> Completed = new();

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

        public static SteamDownload ReadCurrent() => ReadCurrent(FindSteamRoot(), DateTime.Now);

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
                    else if (state.Paused || state.Cancelled) ObservedActive.Remove(state.Id);
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

                var current = states.Values.Where(x => (x.Active || x.Paused) && !x.Cancelled && !x.Finished)
                    .OrderByDescending(x => x.Updated).FirstOrDefault();
                if (current == null || current.Updated == DateTime.MinValue || now - current.Updated > TimeSpan.FromMinutes(10)) return null;
                var samples = rates.Where(x => x.At >= current.StartedAt && x.At <= now).OrderBy(x => x.At).ToList();
                var rateBytes = !current.Paused && samples.Count > 0 && now - samples[^1].At < TimeSpan.FromSeconds(90) ? samples[^1].BytesPerSecond : 0;
                var manifest = ReadManifestProgress(root, current.Id);
                var useStage = current.Paused || (!current.Downloading && current.Staging) || (rateBytes <= 0 && current.Staging);
                long progressDone = useStage ? manifest?.Staged ?? 0 : manifest?.Downloaded ?? current.Done;
                long progressTotal = useStage ? manifest?.StageTotal ?? 0 : manifest?.DownloadTotal ?? current.Total;
                var progress = progressTotal > 0 ? Math.Clamp((double)progressDone / progressTotal, 0, 1) : 0;
                var remainingBytes = Math.Max(0, progressTotal - progressDone);
                var remaining = rateBytes > 0 && !useStage ? TimeSpan.FromSeconds(remainingBytes / rateBytes) : (TimeSpan?)null;
                return new SteamDownload
                {
                    Name = !string.IsNullOrWhiteSpace(manifest?.Name) ? manifest.Name : FindAppName(root, current.Id) ?? "Steam · App " + current.Id,
                    Progress = progress,
                    BytesPerSecond = rateBytes,
                    Remaining = remaining,
                    Icon = FindAppIcon(root, current.Id),
                    FolderPath = manifest == null ? null : GetInstallFolder(manifest),
                    IsPaused = current.Paused
                };
            }
            catch { return null; }
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

        private static ImageSource FindAppIcon(string root, int appId)
        {
            string cacheKey = root + "|" + appId;
            if (IconCache.TryGetValue(cacheKey, out var cached)) return cached;
            foreach (var library in Libraries(root))
            {
                foreach (var cache in new[] { Path.Combine(library, "steamapps", "librarycache"), Path.Combine(library, "appcache", "librarycache") })
                foreach (var file in new[] { appId + "_header.jpg", appId + "_library_600x900.jpg", appId + "_logo.png" })
                {
                    var path = Path.Combine(cache, file);
                    if (!File.Exists(path)) continue;
                    try
                    {
                        var image = new BitmapImage();
                        image.BeginInit(); image.UriSource = new Uri(path); image.CacheOption = BitmapCacheOption.OnLoad; image.EndInit(); image.Freeze();
                        return IconCache[cacheKey] = image;
                    }
                    catch { }
                }
            }
            return null;
        }

        private static string GetInstallFolder(ManifestProgress manifest)
        {
            if (string.IsNullOrWhiteSpace(manifest.InstallDir)) return null;
            var path = Path.Combine(manifest.Library, "steamapps", "common", manifest.InstallDir);
            return Directory.Exists(path) ? path : null;
        }
    }
}
