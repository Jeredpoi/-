using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Isle.Services
{
    public class LyricLine
    {
        public TimeSpan At; public string Text;
        /// <summary>Конец строки (если сервис его знает) и время начала каждого слова — для точного караоке.</summary>
        public TimeSpan? End;
        public List<LyricWord> Words;
    }

    public class LyricWord
    {
        public TimeSpan At; public string Text;
        /// <summary>Конец слова или слога, если сервис его знает (Spicy Lyrics); иначе — до начала следующего.</summary>
        public TimeSpan? End;
    }

    /// <summary>Синхронизированный текст песен с lrclib.net (бесплатно, без ключа).</summary>
    public static class LyricsService
    {
        private static readonly HttpClient Http = CreateClient();
        private static readonly Dictionary<string, List<LyricLine>> Cache = new();
        private static readonly Regex LineRx = new(@"^\[(\d+):(\d+(?:\.\d+)?)\](.*)$", RegexOptions.Compiled);

        private static HttpClient CreateClient()
        {
            // ответы lrclib теперь с разметкой по словам (lyricsfile) — в разы тяжелее; через VPN 10 с не хватало
            var c = Helpers.Net.Client(TimeSpan.FromSeconds(30));
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Isle/0.1 (desktop island app)");
            return c;
        }

        /// <summary>Последний запрос упал из-за сети (а не «текста нет») — стоит повторить.</summary>
        public static bool LastFailed { get; private set; }

        public static async Task<List<LyricLine>> GetAsync(string artist, string title, TimeSpan duration)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;
            title = CleanTitle(title);
            var key = artist + "|" + title;
            if (Cache.TryGetValue(key, out var cached)) return cached;
            List<LyricLine> result = null;
            try
            {
                var url = "https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(title) +
                          (string.IsNullOrWhiteSpace(artist) ? "" : "&artist_name=" + Uri.EscapeDataString(CleanArtist(artist)));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var json = await Http.GetStringAsync(url);
                // по исполнителю ничего (у плеера бывает «Исполнитель — Topic», feat. и т. п.) — ищем общим запросом
                if (!json.Contains("syncedLyrics\":\"") && !string.IsNullOrWhiteSpace(artist))
                    json = await Http.GetStringAsync("https://lrclib.net/api/search?q=" + Uri.EscapeDataString(CleanArtist(artist) + " " + title));
                Journal.Debug("lyrics", "поиск за " + sw.ElapsedMilliseconds + " мс, " + json.Length / 1024 + " КБ");
                using var doc = JsonDocument.Parse(json);
                JsonElement? best = null;
                double bestScore = double.MaxValue;
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (!item.TryGetProperty("syncedLyrics", out var s) || s.ValueKind != JsonValueKind.String) continue;
                    double dur = item.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0;
                    double diff = duration > TimeSpan.Zero ? Math.Abs(dur - duration.TotalSeconds) : 0;
                    // текст с временем каждого слова — в приоритете, если длительность совпадает (±3 с)
                    bool words = item.TryGetProperty("hasWordSync", out var w) && w.ValueKind == JsonValueKind.True
                                 && item.TryGetProperty("lyricsfile", out var lf) && lf.ValueKind == JsonValueKind.String;
                    double score = diff - (words && diff <= 3 ? 1000 : 0);
                    if (score < bestScore) { bestScore = score; best = item; }
                }
                if (best.HasValue)
                {
                    var b = best.Value;
                    if (b.TryGetProperty("lyricsfile", out var lf) && lf.ValueKind == JsonValueKind.String
                        && b.TryGetProperty("hasWordSync", out var w) && w.ValueKind == JsonValueKind.True)
                        result = ParseLyricsFile(lf.GetString());
                    if (result == null || result.Count == 0) result = Parse(b.GetProperty("syncedLyrics").GetString());
                    Journal.Info("lyrics", "«" + title + "»: " + (result?.Count ?? 0) + " строк, по словам: " + (result != null && result.Exists(l => l.Words != null) ? "да" : "нет"));
                }
            }
            catch (Exception ex)
            {
                // сеть или сервис упали — не запоминаем «нет текста», LyricsState попробует ещё раз
                Journal.Warn("lyrics", "текст не загрузился: " + (ex is TaskCanceledException ? "lrclib не ответил вовремя" : ex.Message));
                LastFailed = true;
                return null;
            }
            LastFailed = false;
            if (result == null) Journal.Info("lyrics", "«" + title + "»: текста в lrclib нет");
            Cache[key] = result;
            return result;
        }

        public static List<LyricLine> Parse(string lrc)
        {
            var list = new List<LyricLine>();
            foreach (var raw in lrc.Split('\n'))
            {
                var m = LineRx.Match(raw.Trim());
                if (!m.Success) continue;
                var at = TimeSpan.FromMinutes(int.Parse(m.Groups[1].Value)) +
                         TimeSpan.FromSeconds(double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
                list.Add(new LyricLine { At = at, Text = m.Groups[3].Value.Trim() });
            }
            return list;
        }

        /// <summary>
        /// Формат lrclib «lyricsfile» (YAML): строки с start_ms/end_ms и словами со start_ms. Читаем только эти поля,
        /// без библиотеки YAML: структура плоская и всегда одинаковая.
        /// </summary>
        public static List<LyricLine> ParseLyricsFile(string yaml)
        {
            try
            {
                var list = new List<LyricLine>();
                bool inLines = false;
                LyricLine line = null; LyricWord word = null;
                foreach (var raw in yaml.Replace("\r", "").Split('\n'))
                {
                    if (raw.Length == 0) continue;
                    int indent = raw.Length - raw.TrimStart(' ').Length;
                    var t = raw.Trim();
                    if (indent == 0 && !t.StartsWith("-")) { inLines = t == "lines:"; continue; }
                    if (!inLines) continue;
                    bool item = t.StartsWith("- ");
                    if (item) t = t.Substring(2);
                    int c = t.IndexOf(':');
                    if (c <= 0) continue;
                    string key = t.Substring(0, c).Trim(), val = t.Substring(c + 1).Trim();
                    if (indent == 0 && item) { line = new LyricLine { Text = "" }; word = null; list.Add(line); }
                    else if (indent >= 2 && item && line != null) { word = new LyricWord { Text = "" }; (line.Words ??= new List<LyricWord>()).Add(word); }
                    if (line == null) continue;
                    bool onWord = word != null && indent >= 2;
                    switch (key)
                    {
                        case "text": if (onWord) word.Text = Scalar(val); else line.Text = Scalar(val).Trim(); break;
                        case "start_ms": if (long.TryParse(val, out var ms)) { if (onWord) word.At = TimeSpan.FromMilliseconds(ms); else line.At = TimeSpan.FromMilliseconds(ms); } break;
                        case "end_ms": if (!onWord && long.TryParse(val, out var me)) line.End = TimeSpan.FromMilliseconds(me); break;
                    }
                }
                foreach (var l in list) if (l.Words != null && l.Words.Count == 0) l.Words = null;
                return list;
            }
            catch { return null; }
        }

        private static string Scalar(string v)
        {
            if (v.Length >= 2 && v[0] == '\'' && v[^1] == '\'') return v.Substring(1, v.Length - 2).Replace("''", "'");
            if (v.Length >= 2 && v[0] == '"' && v[^1] == '"')
            {
                try { return JsonSerializer.Deserialize<string>(v); } catch { return v.Substring(1, v.Length - 2); }
            }
            return v;
        }

        private static string CleanTitle(string t)
        {
            t = Regex.Replace(t, @"\s*[\(\[](official|lyrics?|audio|video|mv|clip|hd|4k)[^\)\]]*[\)\]]", "", RegexOptions.IgnoreCase);
            return t.Trim();
        }

        private static string CleanArtist(string a)
        {
            a = Regex.Replace(a, @"\s*-\s*Topic$", "", RegexOptions.IgnoreCase);
            return a.Trim();
        }
    }
}

namespace Isle.Services
{
    /// <summary>Текст текущей песни — один на всё приложение (плеер на «Главной» и пилюля «С текстом»).</summary>
    public class LyricsState
    {
        public List<LyricLine> Lines { get; private set; }
        public string Key { get; private set; } = "";
        public bool Loading { get; private set; }
        /// <summary>Откуда текст: «Spicy Lyrics» или «LRCLIB» (пусто — текста нет). Для подписи на «Главной».</summary>
        public string Source { get; private set; } = "";
        public const string SourceSpicy = "Spicy Lyrics", SourceLrclib = "LRCLIB";
        public event Action Changed;

        public LyricsState(MediaService media)
        {
            media.TrackChanged += m => _ = LoadAsync(m);
        }

        public async Task LoadAsync(MediaInfo m)
        {
            if (!AppSettings.Current.LyricsEnabled || m == null || string.IsNullOrEmpty(m.Title)) { Set(null, ""); return; }
            var key = m.Artist + "|" + m.Title;
            if (key == Key && (Lines != null || Loading)) return;
            Key = key;
            Lines = null;
            Loading = true;
            Changed?.Invoke();

            // привязан Spotify и играет именно он — сначала Spicy Lyrics (по слогам); любая неудача — LRCLIB как раньше
            SpicyLyrics.Result spicy = null;
            if (SpotifyAuth.IsLinked && SpotifyAuth.IsSpotifyApp(m.SourceAppId))
            {
                try { spicy = await SpicyLyrics.TryGetAsync(m); } catch { spicy = null; }
                if (key != Key) return;
                if (spicy?.Outcome == SpicyLyrics.Outcome.Ok && spicy.Lines != null) { Set(spicy.Lines, key, SourceSpicy); return; }
            }

            var lyr = await LyricsService.GetAsync(m.Artist, m.Title, m.Duration);
            // сеть подвела — ещё две попытки с паузой, пока играет тот же трек
            for (int attempt = 0; lyr == null && LyricsService.LastFailed && attempt < 2 && key == Key; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(6 * (attempt + 1)));
                if (key != Key) return;
                lyr = await LyricsService.GetAsync(m.Artist, m.Title, m.Duration);
            }
            if (key != Key) return;
            Loading = false;
            Set(lyr, key, SourceLrclib);
            if (spicy?.Outcome == SpicyLyrics.Outcome.Queued) _ = RetryQueuedAsync(m, key);
        }

        /// <summary>
        /// Spicy Lyrics поставил трек в очередь (503): пока показан LRCLIB, через 20 с спрашиваем ещё один раз.
        /// Пришёл текст и трек тот же — подменяем.
        /// </summary>
        private async Task RetryQueuedAsync(MediaInfo m, string key)
        {
            await Task.Delay(TimeSpan.FromSeconds(20));
            if (key != Key || Loading) return;
            SpicyLyrics.Result r;
            try { r = await SpicyLyrics.TryGetAsync(m, retryQueued: true); } catch { return; }
            if (key != Key || Loading) return;
            if (r?.Outcome == SpicyLyrics.Outcome.Ok && r.Lines != null) Set(r.Lines, key, SourceSpicy);
        }

        private void Set(List<LyricLine> lines, string key, string source = "")
        {
            Lines = lines;
            Source = lines == null || lines.Count == 0 ? "" : source;
            Key = key;
            Loading = false;
            Changed?.Invoke();
        }

        public int IndexAt(TimeSpan pos) => Lines == null ? -1 : Lines.FindLastIndex(l => l.At <= pos + TimeSpan.FromMilliseconds(250));

        public string LineAt(TimeSpan pos)
        {
            int i = IndexAt(pos);
            if (i < 0 || Lines == null) return "";
            var t = Lines[i].Text;
            return t.Length == 0 ? "♪" : t;
        }
    }
}
