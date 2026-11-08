using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Isle.Services
{
    public class LyricLine { public TimeSpan At; public string Text; }

    /// <summary>Синхронизированный текст песен с lrclib.net (бесплатно, без ключа).</summary>
    public static class LyricsService
    {
        private static readonly HttpClient Http = CreateClient();
        private static readonly Dictionary<string, List<LyricLine>> Cache = new();
        private static readonly Regex LineRx = new(@"^\[(\d+):(\d+(?:\.\d+)?)\](.*)$", RegexOptions.Compiled);

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Isle/0.1 (desktop island app)");
            return c;
        }

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
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
                JsonElement? best = null;
                double bestDiff = double.MaxValue;
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (!item.TryGetProperty("syncedLyrics", out var s) || s.ValueKind != JsonValueKind.String) continue;
                    double dur = item.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0;
                    double diff = duration > TimeSpan.Zero ? Math.Abs(dur - duration.TotalSeconds) : 0;
                    if (diff < bestDiff) { bestDiff = diff; best = item; }
                }
                if (best.HasValue) result = Parse(best.Value.GetProperty("syncedLyrics").GetString());
            }
            catch { return null; } // сеть или сервис упали — не запоминаем «нет текста», попробуем в следующий раз
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
            var lyr = await LyricsService.GetAsync(m.Artist, m.Title, m.Duration);
            if (key != Key) return;
            Loading = false;
            Set(lyr, key);
        }

        private void Set(List<LyricLine> lines, string key)
        {
            Lines = lines;
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
