using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Isle.Services
{
    public sealed class BrowserDownload
    {
        public string Key { get; init; }
        public string Browser { get; init; }
        public string FileName { get; init; }
        public long ReceivedBytes { get; init; }
        public long TotalBytes { get; init; }
        public double BytesPerSecond { get; init; }
        public TimeSpan? Remaining { get; init; }
        public DateTime StartedAt { get; init; }
        public double? Progress => TotalBytes > 0 ? Math.Clamp((double)ReceivedBytes / TotalBytes, 0, 1) : null;
        public string ProgressText => Progress.HasValue ? (int)Math.Round(Progress.Value * 100) + "%" : "Загрузка";
        public string SpeedText => BytesPerSecond > 0 ? FormatBytes(BytesPerSecond) + "/с" : "";
        public string EtaText => Remaining.HasValue ? "~" + FormatTime(Remaining.Value) : "";

        private static string FormatBytes(double value) => value >= 1048576
            ? (value / 1048576).ToString("0.#", CultureInfo.CurrentCulture) + " МБ"
            : Math.Max(1, value / 1024).ToString("0", CultureInfo.CurrentCulture) + " КБ";

        private static string FormatTime(TimeSpan t) => t.TotalHours >= 1
            ? ((int)t.TotalHours) + " ч " + t.Minutes + " мин"
            : t.TotalMinutes >= 1 ? Math.Max(1, (int)Math.Ceiling(t.TotalMinutes)) + " мин" : Math.Max(1, t.Seconds) + " с";
    }

    public sealed class BrowserDownloadSnapshot
    {
        public IReadOnlyList<BrowserDownload> Active { get; init; } = Array.Empty<BrowserDownload>();
        public IReadOnlyList<BrowserDownload> Completed { get; init; } = Array.Empty<BrowserDownload>();
    }

    /// <summary>
    /// Reads only download rows from Chromium-family browser History databases. URLs,
    /// page history, and other browsing data are never queried or persisted.
    /// </summary>
    public static class BrowserDownloadService
    {
        private sealed record Profile(string Browser, string HistoryPath);
        private sealed record Row(string Key, string Browser, string Path, long Received, long Total, int State, long StartTime, long EndTime);
        private sealed record Sample(long Bytes, DateTime At, double Rate);

        private static readonly object Gate = new();
        private static readonly Dictionary<string, Sample> Samples = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DateTime> ObservedActive = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> NotifiedCompleted = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Queue<string> NotifiedOrder = new();
        private static readonly DateTime SessionStartedAt = DateTime.Now;

        public static BrowserDownloadSnapshot ReadSnapshot()
        {
            lock (Gate)
            {
                var now = DateTime.UtcNow;
                var activeRows = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
                var completed = new List<Row>();

                foreach (var profile in FindProfiles())
                {
                    if (!TryReadProfile(profile, out var rows)) continue;
                    foreach (var row in rows)
                    {
                        if (row.State == 0)
                        {
                            activeRows[row.Key] = row;
                            ObservedActive[row.Key] = now;
                            continue;
                        }

                        Samples.Remove(row.Key);
                        bool wasObservedActive = ObservedActive.Remove(row.Key);
                        if (row.State == 1)
                        {
                            bool firstSeen = NotifiedCompleted.Add(row.Key);
                            if (firstSeen)
                            {
                                NotifiedOrder.Enqueue(row.Key);
                                while (NotifiedOrder.Count > 4096) NotifiedCompleted.Remove(NotifiedOrder.Dequeue());
                            }
                            if (firstSeen && ShouldNotifyCompletion(wasObservedActive, FromWebKitTime(row.EndTime), SessionStartedAt))
                                completed.Add(row);
                        }
                    }
                }

                foreach (var key in ObservedActive.Where(p => now - p.Value > TimeSpan.FromMinutes(10)).Select(p => p.Key).ToList())
                {
                    ObservedActive.Remove(key);
                    Samples.Remove(key);
                }

                var active = activeRows.Values
                    .OrderByDescending(r => r.StartTime)
                    .Select(row => ToDownload(row, now))
                    .ToList();

                return new BrowserDownloadSnapshot
                {
                    Active = active,
                    Completed = completed.Select(row => ToDownload(row, now, calculateSpeed: false)).ToList()
                };
            }
        }

        private static BrowserDownload ToDownload(Row row, DateTime now, bool calculateSpeed = true)
        {
            double rate = 0;
            if (calculateSpeed)
            {
                // Браузер обновляет received_bytes в базе рывками. Скорость считаем от момента ПРОШЛОГО изменения
                // байт (а не от прошлого опроса), сглаживаем и держим, пока браузер не записал новое значение.
                if (Samples.TryGetValue(row.Key, out var previous) && row.Received >= previous.Bytes)
                {
                    if (row.Received == previous.Bytes)
                    {
                        rate = (now - previous.At).TotalSeconds <= 15 ? previous.Rate : 0;
                    }
                    else
                    {
                        var seconds = (now - previous.At).TotalSeconds;
                        double fresh = seconds >= 0.5 ? (row.Received - previous.Bytes) / seconds : previous.Rate;
                        rate = previous.Rate > 0 ? previous.Rate * 0.4 + fresh * 0.6 : fresh;
                        Samples[row.Key] = new Sample(row.Received, now, rate);
                    }
                }
                else Samples[row.Key] = new Sample(row.Received, now, 0);
            }

            var remaining = rate > 0 && row.Total > row.Received
                ? TimeSpan.FromSeconds((row.Total - row.Received) / rate)
                : (TimeSpan?)null;
            var fileName = Path.GetFileName(row.Path.Replace('/', '\\'));
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "Файл";

            return new BrowserDownload
            {
                Key = row.Key,
                Browser = row.Browser,
                FileName = fileName,
                ReceivedBytes = row.Received,
                TotalBytes = row.Total,
                BytesPerSecond = rate,
                Remaining = remaining,
                StartedAt = FromWebKitTime(row.StartTime)
            };
        }

        private static IEnumerable<Profile> FindProfiles()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var roots = new (string Name, string Path)[]
            {
                ("Chrome", Path.Combine(local, "Google", "Chrome", "User Data")),
                ("Edge", Path.Combine(local, "Microsoft", "Edge", "User Data")),
                ("Brave", Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data")),
                ("Vivaldi", Path.Combine(local, "Vivaldi", "User Data")),
                ("Yandex Browser", Path.Combine(local, "Yandex", "YandexBrowser", "User Data")),
                // Perplexity Comet is Chromium-based and keeps the normal History DB
                // under its own LocalAppData user-data directory.
                ("Comet", Path.Combine(local, "Perplexity", "Comet", "User Data")),
                // Older/alternate per-user installs used this shorter product root.
                ("Comet", Path.Combine(local, "Comet", "User Data")),
                ("Chromium", Path.Combine(local, "Chromium", "User Data")),
                ("Opera", Path.Combine(roaming, "Opera Software", "Opera Stable")),
                ("Opera GX", Path.Combine(roaming, "Opera Software", "Opera GX Stable"))
            };

            foreach (var (name, root) in roots)
            {
                if (!Directory.Exists(root)) continue;
                var direct = Path.Combine(root, "History");
                if (File.Exists(direct)) yield return new Profile(name, direct);

                string[] directories;
                try { directories = Directory.EnumerateDirectories(root).ToArray(); }
                catch { continue; }
                foreach (var directory in directories)
                {
                    var history = Path.Combine(directory, "History");
                    if (File.Exists(history)) yield return new Profile(name, history);
                }
            }
        }

        private static bool TryReadProfile(Profile profile, out List<Row> rows)
        {
            // Пока браузер открыт, он держит History под эксклюзивной блокировкой — обычное чтение падает
            // с «database is locked». Тогда читаем в режиме immutable: SQLite не берёт блокировок и просто читает файл.
            return TryReadProfile(profile, false, out rows) || TryReadProfile(profile, true, out rows);
        }

        private static bool TryReadProfile(Profile profile, bool immutable, out List<Row> rows)
        {
            rows = new List<Row>();
            try
            {
                var builder = new SqliteConnectionStringBuilder
                {
                    DataSource = immutable ? new Uri(profile.HistoryPath).AbsoluteUri + "?immutable=1" : profile.HistoryPath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                    DefaultTimeout = 1
                };
                using var connection = new SqliteConnection(builder.ToString());
                connection.Open();

                var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var schema = connection.CreateCommand())
                {
                    schema.CommandText = "PRAGMA table_info(downloads)";
                    using var reader = schema.ExecuteReader();
                    while (reader.Read()) columns.Add(reader.GetString(1));
                }
                if (!columns.Contains("id") || !columns.Contains("state") || !columns.Contains("received_bytes")) return false;

                var pathColumns = new[] { "target_path", "current_path", "full_path" }.Where(columns.Contains).ToArray();
                if (pathColumns.Length == 0) return false;
                string pathExpression = "COALESCE(" + string.Join(", ", pathColumns.Select(c => "NULLIF(\"" + c + "\", '')")) + ", '')";
                string startColumn = columns.Contains("start_time") ? "start_time" : "id";
                string endColumn = columns.Contains("end_time") ? "end_time" : "0";
                string totalColumn = columns.Contains("total_bytes") ? "total_bytes" : "0";

                using var command = connection.CreateCommand();
                command.CommandTimeout = 1;
                command.CommandText = $"SELECT id, {pathExpression}, received_bytes, {totalColumn}, state, {startColumn}, {endColumn} FROM downloads ORDER BY id DESC LIMIT 200";
                using var result = command.ExecuteReader();
                while (result.Read())
                {
                    long id = ReadLong(result, 0);
                    string path = result.IsDBNull(1) ? "" : result.GetString(1);
                    long received = Math.Max(0, ReadLong(result, 2));
                    long total = Math.Max(0, ReadLong(result, 3));
                    int state = (int)ReadLong(result, 4);
                    long start = Math.Max(0, ReadLong(result, 5));
                    long end = Math.Max(0, ReadLong(result, 6));
                    string key = profile.HistoryPath + "|" + id.ToString(CultureInfo.InvariantCulture);
                    rows.Add(new Row(key, profile.Browser, path, received, total, state, start, end));
                }
                return true;
            }
            catch { return false; }
        }

        private static long ReadLong(SqliteDataReader reader, int ordinal)
        {
            if (reader.IsDBNull(ordinal)) return 0;
            try { return reader.GetInt64(ordinal); }
            catch { return long.TryParse(reader.GetValue(ordinal)?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0; }
        }

        internal static bool ShouldNotifyCompletion(bool wasObservedActive, DateTime completedAt, DateTime sessionStartedAt) =>
            wasObservedActive || (completedAt != DateTime.MinValue && completedAt >= sessionStartedAt);

        private static DateTime FromWebKitTime(long microseconds)
        {
            if (microseconds <= 0) return DateTime.MinValue;
            try { return DateTime.FromFileTimeUtc(microseconds * 10).ToLocalTime(); }
            catch { return DateTime.MinValue; }
        }
    }
}
