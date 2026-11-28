using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Isle.Services
{
    /// <summary>
    /// Текст песен с разметкой по слогам из Spicy Lyrics (github.com/Spikerko/spicy-lyrics). Сервис не документирован
    /// для сторонних программ, поэтому бережно: один запрос на трек, ответы кэшируем, на 429/503 и сбои — пауза.
    /// Любая неудача — null, и LyricsState берёт текст из LRCLIB как раньше. В журнал — только статусы.
    /// </summary>
    public static class SpicyLyrics
    {
        public const string Credit = "Текст: Spicy Lyrics · Spikerko";
        public const string RepoUrl = "https://github.com/Spikerko/spicy-lyrics";
        private const string Api = "https://api.spicylyrics.org/query";

        public enum Outcome { Ok, None, Queued, Failed, Skipped }

        public sealed class Result
        {
            public Outcome Outcome;
            public List<LyricLine> Lines;
            public string TrackId;
        }

        private static readonly HttpClient Http = CreateClient();
        /// <summary>id трека Spotify → текст (null — у сервиса текста нет). Живёт до выхода из программы.</summary>
        private static readonly Dictionary<string, List<LyricLine>> Cache = new();
        /// <summary>«Исполнитель|Название» → id трека Spotify (null — не Spotify или не совпало).</summary>
        private static readonly Dictionary<string, string> Ids = new();
        private static DateTime _pausedUntil = DateTime.MinValue;
        private static TimeSpan _backoff = TimeSpan.FromMinutes(1);

        private static string Version
        {
            get
            {
                var v = typeof(SpicyLyrics).Assembly.GetName().Version;
                return "Isle-" + (v == null ? "0.0.0" : v.Major + "." + v.Minor + "." + Math.Max(0, v.Build));
            }
        }

        private static HttpClient CreateClient()
        {
            var c = Helpers.Net.Client(TimeSpan.FromSeconds(10));
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Isle/" + Version.Substring(5) + " (desktop island app)");
            return c;
        }

        /// <summary>Отвязали Spotify — забываем найденные id (текст в кэше не секретный, его оставляем).</summary>
        public static void Reset()
        {
            lock (Ids) Ids.Clear();
        }

        /// <summary>
        /// Текст для трека, который играет в Spotify. Вся попытка (id трека + запрос) — не дольше 10 с.
        /// </summary>
        public static async Task<Result> TryGetAsync(MediaInfo m, bool retryQueued = false)
        {
            var res = new Result { Outcome = Outcome.Skipped };
            if (m == null || !SpotifyAuth.IsLinked || !SpotifyAuth.IsSpotifyApp(m.SourceAppId)) return res;
            if (DateTime.UtcNow < _pausedUntil) { Journal.Debug("spicy", "пауза после отказа сервиса — сразу LRCLIB"); return res; }
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                var key = m.Artist + "|" + m.Title;
                string id;
                bool known;
                lock (Ids) known = Ids.TryGetValue(key, out id);
                if (!known)
                {
                    id = await SpotifyAuth.CurrentTrackIdAsync(m.Title, cts.Token);
                    // запоминаем только найденный id: null бывает и временным (429, сбой сети, Spotify ещё не переключил
                    // трек) — раньше он запоминался, и для этой песни Spicy больше не спрашивался до перезапуска
                    if (id != null) lock (Ids) Ids[key] = id;
                }
                if (id == null) return res;
                res.TrackId = id;
                lock (Cache)
                    if (Cache.TryGetValue(id, out var cached))
                    {
                        res.Outcome = cached == null ? Outcome.None : Outcome.Ok;
                        res.Lines = cached;
                        return res;
                    }

                var token = await SpotifyAuth.GetAccessTokenAsync(false, cts.Token);
                if (token == null) { res.Outcome = Outcome.Failed; return res; }
                var (status, data) = await QueryAsync(id, token, cts.Token);
                if (status == 401)
                {
                    // как в самом Spicy Lyrics: токен уже не принимают — обновляем и пробуем ровно один раз
                    var fresh = await SpotifyAuth.GetAccessTokenAsync(true, cts.Token);
                    if (fresh != null && fresh != token) (status, data) = await QueryAsync(id, fresh, cts.Token);
                }
                Journal.Info("spicy", "запрос текста: " + status);
                switch (status)
                {
                    case 200:
                        var lines = Map(Unpack(data));
                        if (lines == null || lines.Count == 0)
                        {
                            // Static (без времени) или пусто — нам не подходит, запоминаем и берём LRCLIB
                            Journal.Info("spicy", "текст без разметки по времени — беру LRCLIB");
                            lock (Cache) Cache[id] = null;
                            res.Outcome = Outcome.None;
                            return res;
                        }
                        lock (Cache) Cache[id] = lines;
                        _backoff = TimeSpan.FromMinutes(1);
                        res.Outcome = Outcome.Ok;
                        res.Lines = lines;
                        Journal.Info("spicy", lines.Count + " строк, по словам: " + (lines.Exists(l => l.Words != null) ? "да" : "нет"));
                        return res;
                    case 404:
                        lock (Cache) Cache[id] = null;
                        res.Outcome = Outcome.None;
                        return res;
                    case 503:
                        // сервер поставил трек в очередь — повторим один раз позже (LyricsState), пока покажем LRCLIB
                        if (retryQueued) lock (Cache) Cache[id] = null;
                        res.Outcome = Outcome.Queued;
                        return res;
                    default:
                        // 429, неизвестная версия клиента, 5xx и т. п. — не долбим сервис, пауза с ростом
                        Pause(status == 400 || status == 403 || status == 426 ? TimeSpan.FromMinutes(30) : (TimeSpan?)null, status);
                        res.Outcome = Outcome.Failed;
                        return res;
                }
            }
            catch (Exception ex)
            {
                Journal.Warn("spicy", "сбой: " + (ex is OperationCanceledException ? "не ответил за 10 с" : ex.GetType().Name));
                Pause(null, 0);
                res.Outcome = Outcome.Failed;
                return res;
            }
        }

        private static void Pause(TimeSpan? fixedFor, int status)
        {
            var d = fixedFor ?? _backoff;
            if (fixedFor == null) _backoff = TimeSpan.FromTicks(Math.Min(TimeSpan.FromMinutes(15).Ticks, _backoff.Ticks * 2));
            _pausedUntil = DateTime.UtcNow + d;
            Journal.Info("spicy", "пауза " + (int)d.TotalMinutes + " мин" + (status > 0 ? " (статус " + status + ")" : ""));
        }

        /// <summary>
        /// Запрос как у самого Spicy Lyrics (src/utils/API/Query.ts + fetchLyrics.ts). Возвращает httpStatus из
        /// конверта ответа (или транспортный статус, если до конверта не дошло) и поле data.
        /// </summary>
        private static async Task<(int, JsonNode)> QueryAsync(string trackId, string token, CancellationToken ct)
        {
            var ver = Version;
            var body = new JsonObject
            {
                ["queries"] = new JsonArray(new JsonObject
                {
                    ["operation"] = "lyrics",
                    ["variables"] = new JsonObject { ["id"] = trackId, ["auth"] = "SpicyLyrics-WebAuth" },
                }),
                ["client"] = new JsonObject { ["version"] = ver },
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, Api)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("X-mode", "2");
            req.Headers.TryAddWithoutValidation("SpicyLyrics-Version", ver);
            req.Headers.TryAddWithoutValidation("SpicyLyrics-WebAuth", "Bearer " + token);
            using var resp = await Http.SendAsync(req, ct);
            int transport = (int)resp.StatusCode;
            if (!resp.IsSuccessStatusCode)
            {
                if (transport == 429 && resp.Headers.RetryAfter?.Delta is TimeSpan ra && ra > _backoff) _backoff = ra;
                return (transport, null);
            }
            var root = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
            // ответ: { queries: [ { operation, operationId: "0", result: { httpStatus, data, format } } ] }
            if (root?["queries"] is not JsonArray qs) return (0, null);
            foreach (var q in qs)
            {
                var opId = q?["operationId"]?.ToString();
                if (opId != null && opId != "0") continue;
                var result = q?["result"];
                if (result == null) continue;
                int st = result["httpStatus"] is JsonValue hv && hv.TryGetValue<int>(out var s) ? s : 0;
                return (st, result["data"]);
            }
            return (0, null);
        }

        // ---------- распаковка SLObjPack (src/utils/objpack.ts) ----------

        /// <summary>
        /// data в ответе упакован SLObjPack: [valuesList, stream]. valuesList — уникальные примитивы (строки, числа,
        /// true/false/null), stream — числа: ≥0 — ссылка на примитив, отрицательные — коды: −1 объект
        /// (кол-во ключей, ссылки на ключи, затем значения), −2 массив (длина, элементы), −3 массив одинаковых объектов
        /// (длина, кол-во ключей, ключи один раз, затем значения по порядку), −4 [], −5 массив из одного элемента, −6 {}.
        /// Если data пришёл неупакованным (объект или строка JSON) — берём как есть.
        /// </summary>
        public static JsonNode Unpack(JsonNode data)
        {
            if (data == null) return null;
            if (data is JsonValue v && v.TryGetValue<string>(out var str))
            {
                if (string.IsNullOrWhiteSpace(str)) return null;
                try { data = JsonNode.Parse(str); } catch { return null; }
                if (data == null) return null;
            }
            if (data is JsonObject) return data;
            if (data is not JsonArray packed || packed.Count != 2 || packed[0] is not JsonArray values || packed[1] is not JsonArray streamArr)
                throw new FormatException("SLObjPack: неверная структура");

            const int MaxDepth = 512, MaxArray = 1 << 20, MaxKeys = 1 << 16, MaxStream = 1 << 24, MaxValues = 1 << 22, MaxOps = 1 << 22;
            if (values.Count > MaxValues || streamArr.Count > MaxStream) throw new FormatException("SLObjPack: слишком большой");

            // примитивы: строка, конечное число, true/false, null
            var vals = new JsonNode[values.Count];
            var isString = new bool[values.Count];
            for (int i = 0; i < values.Count; i++)
            {
                var x = values[i];
                if (x == null) continue;
                if (x is not JsonValue jv) throw new FormatException("SLObjPack: неверный примитив " + i);
                var kind = jv.GetValueKind();
                if (kind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
                    throw new FormatException("SLObjPack: неверный примитив " + i);
                vals[i] = x;
                isString[i] = kind == JsonValueKind.String;
            }
            var stream = new long[streamArr.Count];
            for (int i = 0; i < stream.Length; i++)
            {
                if (streamArr[i] is not JsonValue sv || sv.GetValueKind() != JsonValueKind.Number) throw new FormatException("SLObjPack: неверный код");
                double d = AsDouble(sv);
                if (d != Math.Floor(d) || double.IsInfinity(d)) throw new FormatException("SLObjPack: неверный код");
                stream[i] = (long)d;
            }

            int cursor = 0;
            long Read()
            {
                if (cursor >= stream.Length) throw new FormatException("SLObjPack: поток кончился раньше времени");
                return stream[cursor++];
            }
            JsonNode Ptr(long p)
            {
                if (p < 0 || p >= vals.Length) throw new FormatException("SLObjPack: неверная ссылка " + p);
                return vals[p]?.DeepClone();
            }
            string Key()
            {
                long p = Read();
                if (p < 0 || p >= vals.Length || !isString[p]) throw new FormatException("SLObjPack: ключ не строка");
                var k = vals[p].GetValue<string>();
                if (k is "__proto__" or "constructor" or "prototype") throw new FormatException("SLObjPack: запрещённый ключ");
                return k;
            }
            int Count(long n, int max)
            {
                if (n < 0 || n > max) throw new FormatException("SLObjPack: неверное количество " + n);
                return (int)n;
            }
            void Need(long min)
            {
                if (min > stream.Length - cursor) throw new FormatException("SLObjPack: данных меньше, чем объявлено");
            }
            JsonNode Decode(int depth)
            {
                if (depth > MaxDepth) throw new FormatException("SLObjPack: слишком глубоко");
                long op = Read();
                if (op >= 0) return Ptr(op);
                switch (op)
                {
                    case -1:
                    {
                        int nk = Count(Read(), MaxKeys);
                        Need((long)nk * 2);
                        var keys = new string[nk];
                        for (int i = 0; i < nk; i++) keys[i] = Key();
                        var o = new JsonObject();
                        for (int i = 0; i < nk; i++) o[keys[i]] = Decode(depth + 1); // повторный ключ — последний выигрывает, как в JS
                        return o;
                    }
                    case -2:
                    {
                        int n = Count(Read(), MaxArray);
                        Need(n);
                        var a = new JsonArray();
                        for (int i = 0; i < n; i++) a.Add(Decode(depth + 1));
                        return a;
                    }
                    case -3:
                    {
                        int n = Count(Read(), MaxArray);
                        int nk = Count(Read(), MaxKeys);
                        if ((long)n * nk > MaxOps) throw new FormatException("SLObjPack: слишком много значений");
                        Need(nk + (long)n * nk);
                        var keys = new string[nk];
                        for (int i = 0; i < nk; i++) keys[i] = Key();
                        var a = new JsonArray();
                        for (int i = 0; i < n; i++)
                        {
                            var o = new JsonObject();
                            for (int k = 0; k < nk; k++) o[keys[k]] = Decode(depth + 1);
                            a.Add(o);
                        }
                        return a;
                    }
                    case -4: return new JsonArray();
                    case -5: return new JsonArray(Decode(depth + 1));
                    case -6: return new JsonObject();
                    default: throw new FormatException("SLObjPack: неизвестный код " + op);
                }
            }

            var result = Decode(0);
            if (cursor != stream.Length) throw new FormatException("SLObjPack: лишние данные в конце");
            return result;
        }

        // ---------- в наш формат ----------

        /// <summary>
        /// Syllable → строки со словами по слогам (у каждого слога время начала и конца, слоги одного слова идут
        /// подряд без пробела); Line → строки со временем начала и конца; Static → null (берём LRCLIB).
        /// Время у Spicy Lyrics в секундах. Фоновые голоса (Background) не показываем — только основной.
        /// </summary>
        public static List<LyricLine> Map(JsonNode lyrics)
        {
            if (lyrics is not JsonObject o) return null;
            var type = Str(o["Type"]);
            if (o["Content"] is not JsonArray content) return null;
            var list = new List<LyricLine>();
            if (type == "Syllable")
            {
                foreach (var g in content)
                {
                    if (g is not JsonObject vg || Str(vg["Type"]) != "Vocal") continue;
                    if (vg["Lead"]?["Syllables"] is not JsonArray syl || syl.Count == 0) continue;
                    var words = new List<LyricWord>();
                    var text = new StringBuilder();
                    for (int i = 0; i < syl.Count; i++)
                    {
                        var s = syl[i];
                        var t = Str(s?["Text"]) ?? "";
                        if (t.Trim().Length == 0) continue;
                        double st = Num(s["StartTime"]), en = Num(s["EndTime"]);
                        if (double.IsNaN(st)) continue;
                        bool partOfWord = s["IsPartOfWord"] is JsonValue pv && pv.GetValueKind() == JsonValueKind.True;
                        words.Add(new LyricWord
                        {
                            At = TimeSpan.FromSeconds(st),
                            End = double.IsNaN(en) || en <= st ? null : TimeSpan.FromSeconds(en),
                            Text = t,
                        });
                        text.Append(t);
                        // как spacedText в ProcessLyrics.ts: пробел после слога, который заканчивает слово
                        if (!partOfWord) text.Append(' ');
                    }
                    if (words.Count == 0) continue;
                    double ls = Num(vg["Lead"]["StartTime"]), le = Num(vg["Lead"]["EndTime"]);
                    var line = new LyricLine
                    {
                        At = double.IsNaN(ls) ? words[0].At : TimeSpan.FromSeconds(Math.Min(ls, words[0].At.TotalSeconds)),
                        Text = System.Text.RegularExpressions.Regex.Replace(text.ToString(), @"\s+", " ").Trim(),
                        Words = words,
                    };
                    if (!double.IsNaN(le)) line.End = TimeSpan.FromSeconds(le);
                    list.Add(line);
                }
            }
            else if (type == "Line")
            {
                foreach (var g in content)
                {
                    if (g is not JsonObject vg || Str(vg["Type"]) != "Vocal") continue;
                    var t = (Str(vg["Text"]) ?? "").Trim();
                    double st = Num(vg["StartTime"]), en = Num(vg["EndTime"]);
                    if (t.Length == 0 || double.IsNaN(st)) continue;
                    var line = new LyricLine { At = TimeSpan.FromSeconds(st), Text = t };
                    if (!double.IsNaN(en) && en > st) line.End = TimeSpan.FromSeconds(en);
                    list.Add(line);
                }
            }
            else return null; // Static и неизвестное — без времени нам не нужно

            if (list.Count == 0) return null;
            list.Sort((a, b) => a.At.CompareTo(b.At));
            // проигрыш между строками (как «♪» у LRCLIB): иначе прошлая строка горит весь проигрыш
            var withGaps = new List<LyricLine>(list.Count + 8);
            for (int i = 0; i < list.Count; i++)
            {
                withGaps.Add(list[i]);
                var end = list[i].End;
                if (end.HasValue && i + 1 < list.Count && list[i + 1].At - end.Value >= TimeSpan.FromSeconds(4))
                    withGaps.Add(new LyricLine { At = end.Value + TimeSpan.FromMilliseconds(300), Text = "" });
            }
            return withGaps;
        }

        private static string Str(JsonNode n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

        private static double Num(JsonNode n)
        {
            if (n is not JsonValue v) return double.NaN;
            if (v.GetValueKind() == JsonValueKind.Number) return AsDouble(v);
            if (v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            return double.NaN;
        }

        /// <summary>Число из JsonValue независимо от того, как оно хранится внутри (int, long, double, JsonElement).</summary>
        private static double AsDouble(JsonValue v) =>
            double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
    }
}
