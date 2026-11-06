using System;
using System.Collections.Generic;
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
        public bool EstimatedProgress { get; init; }
        public string ProgressText => (EstimatedProgress ? "~" : "") + (int)Math.Round(Progress * 100) + "%";
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
        private static readonly Regex AppLine = new(@"AppID\s+(\d+)\s+(?:update|state)\s+(started|changed|canceled|finished|paused)\s*:?\s*(.*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex BytesLine = new(@"download\s+(\d+)\s*/\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RateLine = new(@"Current download rate:\s*([\d.,]+)\s*Mbps", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Dictionary<string, ImageSource> IconCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> NameCache = new(StringComparer.OrdinalIgnoreCase);

        private sealed class AppState
        {
            public int Id;
            public bool Active;
            public long Done, Total;
            public DateTime Updated;
            public DateTime StartedAt;
        }

        private sealed record RateSample(DateTime At, double BytesPerSecond);

        public static SteamDownload ReadCurrent()
        {
            try
            {
                var root = FindSteamRoot();
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
                    var action = m.Groups[2].Value.ToLowerInvariant();
                    var detail = m.Groups[3].Value;
                    states.TryGetValue(id, out var prior);
                    var state = new AppState
                    {
                        Id = id, Updated = timestamp, Done = prior?.Done ?? 0, Total = prior?.Total ?? 0,
                        StartedAt = prior?.StartedAt ?? DateTime.MinValue
                    };
                    var bytes = BytesLine.Match(detail);
                    if (bytes.Success && long.TryParse(bytes.Groups[1].Value, out var done) && long.TryParse(bytes.Groups[2].Value, out var total))
                    { state.Done = done; state.Total = total; }
                    if (action == "started") state.StartedAt = timestamp;
                    state.Active = action == "started" ||
                        (action == "changed" && (detail.Contains("Downloading", StringComparison.OrdinalIgnoreCase) || detail.Contains("Update Started", StringComparison.OrdinalIgnoreCase))) ||
                        (action == "changed" && detail.Contains("Running", StringComparison.OrdinalIgnoreCase) && detail.Contains("Staging", StringComparison.OrdinalIgnoreCase));
                    if (action is "canceled" or "finished" or "paused") state.Active = false;
                    states[id] = state;
                }

                var current = states.Values.Where(x => x.Active && x.Total > 0)
                    .OrderByDescending(x => x.Updated).FirstOrDefault();
                if (current == null || DateTime.Now - current.Updated > TimeSpan.FromMinutes(10)) return null;
                var now = DateTime.Now;
                var samples = rates.Where(x => x.At >= current.StartedAt && x.At <= now).OrderBy(x => x.At).ToList();
                var rateBytes = samples.Count > 0 && now - samples[^1].At < TimeSpan.FromMinutes(3) ? samples[^1].BytesPerSecond : 0;
                double estimatedDone = current.Done;
                bool estimatedProgress = !ReadManifestProgress(root, current.Id, out var manifestDone, out var manifestTotal);
                if (!estimatedProgress)
                {
                    estimatedDone = manifestDone;
                    current.Total = manifestTotal;
                }
                else if (current.StartedAt != DateTime.MinValue && samples.Count > 0)
                {
                    var cursor = current.StartedAt;
                    var segmentRate = samples[0].BytesPerSecond;
                    foreach (var sample in samples)
                    {
                        estimatedDone += Math.Max(0, (sample.At - cursor).TotalSeconds) * segmentRate;
                        cursor = sample.At;
                        segmentRate = sample.BytesPerSecond;
                    }
                    estimatedDone += Math.Max(0, (now - cursor).TotalSeconds) * segmentRate;
                }
                estimatedDone = Math.Clamp(estimatedDone, current.Done, current.Total);
                var remaining = rateBytes > 0 ? TimeSpan.FromSeconds(Math.Max(0, current.Total - estimatedDone) / rateBytes) : (TimeSpan?)null;
                return new SteamDownload
                {
                    Name = FindAppName(root, current.Id) ?? "Steam · App " + current.Id,
                    Progress = Math.Clamp(estimatedDone / current.Total, 0, 1),
                    EstimatedProgress = estimatedProgress,
                    BytesPerSecond = rateBytes,
                    Remaining = remaining,
                    Icon = FindAppIcon(root, current.Id)
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

        private static bool ReadManifestProgress(string root, int appId, out long downloaded, out long total)
        {
            downloaded = total = 0;
            foreach (var library in Libraries(root))
            {
                var manifest = Path.Combine(library, "steamapps", "appmanifest_" + appId + ".acf");
                try
                {
                    var text = File.ReadAllText(manifest);
                    var doneMatch = Regex.Match(text, @"""BytesDownloaded""\s+""(\d+)""", RegexOptions.IgnoreCase);
                    var totalMatch = Regex.Match(text, @"""BytesToDownload""\s+""(\d+)""", RegexOptions.IgnoreCase);
                    if (!doneMatch.Success || !totalMatch.Success || !long.TryParse(doneMatch.Groups[1].Value, out downloaded)
                        || !long.TryParse(totalMatch.Groups[1].Value, out total) || total <= 0) continue;
                    downloaded = Math.Clamp(downloaded, 0, total);
                    return true;
                }
                catch { }
            }
            return false;
        }

        private static ImageSource FindAppIcon(string root, int appId)
        {
            string cacheKey = root + "|" + appId;
            if (IconCache.TryGetValue(cacheKey, out var cached)) return cached;
            foreach (var library in Libraries(root))
            {
                var cache = Path.Combine(library, "steamapps", "librarycache");
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
    }
}
