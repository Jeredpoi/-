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
    /// Текст песен с разметкой по слогам из Spicy Lyrics через developer API (developers.spicylyrics.org):
    /// GET /v1/lyrics/{id} с ключом пользователя (Authorization: Bearer sl_sk_…), поиск — GET /v1/tracks/search.
    /// Ключ — свой у каждого пользователя, хранится зашифрованным (DPAPI). Один запрос на трек, ответы кэшируем,
    /// на 429/503 и сбои — пауза. Любая неудача — LyricsState берёт текст из LRCLIB. В журнал — только статусы и коды ошибок.
    /// Подпись (обязательна по условиям сервиса): провайдер всегда, у синхронизации от сообщества — ещё автор и составитель.
    /// </summary>
    public static class SpicyLyrics
    {
        public const string SiteUrl = "https://spicylyrics.org";
        public const string DevUrl = "https://developers.spicylyrics.org";
        private const string Api = "https://api.spicylyrics.org/v1";

        /// <summary>NoMatch — трек не нашёлся или Spotify показал не тот трек, что Windows (часто временно).</summary>
        public enum Outcome { Ok, None, Queued, Failed, Skipped, NoMatch }

        /// <summary>Человек из подписи синхронизации: имя и ссылка на профиль.</summary>
        public sealed class Person { public string Name, Url; }

        /// <summary>Чья синхронизация: провайдер (Spicy Lyrics / Apple Music / Spotify) и, у сообщества, кто загрузил и кто сделал.</summary>
        public sealed class Credit
        {
            public string Provider;
            /// <summary>source == "spicy_lyrics": синхронизация от сообщества.</summary>
            public bool Community;
            public Person Uploader, Maker;
            /// <summary>Авторы песни (SongWriters) — «Written by …» в конце текста; может быть пусто.</summary>
            public List<string> Writers = new();
        }

        public sealed class Result
        {
            public Outcome Outcome;
            public List<LyricLine> Lines;
            public string TrackId;
            public Credit Credit;
        }

        /// <summary>Последняя ошибка ключа (401/403) — для настроек; null — всё в порядке или ещё не спрашивали.</summary>
        public static string KeyProblem { get; private set; }
        public static event Action KeyProblemChanged;

        private static readonly HttpClient Http = CreateClient();
        /// <summary>id трека Spotify → текст (null — у сервиса текста нет). Живёт до выхода из программы.</summary>
        private static readonly Dictionary<string, (List<LyricLine> Lines, Credit Credit)> Cache = new();
        /// <summary>«Исполнитель|Название» → id трека Spotify (null — не Spotify или не совпало).</summary>
        private static readonly Dictionary<string, string> Ids = new();
        private static DateTime _pausedUntil = DateTime.MinValue;
        private static TimeSpan _backoff = TimeSpan.FromMinutes(1);

        private static HttpClient CreateClient()
        {
            var c = Helpers.Net.Client(TimeSpan.FromSeconds(10));
            var v = typeof(SpicyLyrics).Assembly.GetName().Version;
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Isle/" + (v == null ? "0" : v.Major + "." + v.Minor + "." + Math.Max(0, v.Build)) + " (desktop island app)");
            return c;
        }

        /// <summary>
        /// Публичный ключ Isle (sl_pk_…, «Client access» с разрешённым «No origin header» — для нативных клиентов).
        /// Он публичный по задумке сервиса: зашит в программу, у каждого пользователя свой лимит по IP.
        /// Пусто — встроенного ключа нет, Spicy работает только со своим ключом пользователя.
        /// </summary>
        private const string BuiltInKey = "";

        /// <summary>Свой ключ пользователя (если вставил), иначе встроенный.</summary>
        private static string Key
        {
            get
            {
                var own = AppSettings.Current.SpicyKey?.Trim();
                return string.IsNullOrEmpty(own) ? BuiltInKey : own;
            }
        }

        private static bool OwnKey => !string.IsNullOrEmpty(AppSettings.Current.SpicyKey?.Trim());

        /// <summary>Остаток лимита из последнего ответа: «осталось 980 из 1000, сброс через 12 мин»; null — ещё не знаем.</summary>
        public static string LimitText { get; private set; }
        public static event Action LimitChanged;

        /// <summary>Ключ сменили — забываем паузу и ошибку, кэш текста оставляем.</summary>
        public static void KeyChanged()
        {
            _pausedUntil = DateTime.MinValue;
            _backoff = TimeSpan.FromMinutes(1);
            SetKeyProblem(null);
        }

        private static void SetKeyProblem(string p)
        {
            if (KeyProblem == p) return;
            KeyProblem = p;
            KeyProblemChanged?.Invoke();
        }

        /// <summary>Отвязали Spotify — забываем найденные id (текст в кэше не секретный, его оставляем).</summary>
        public static void Reset()
        {
            lock (Ids) Ids.Clear();
        }

        /// <summary>
        /// Текст для трека, который играет в Spotify. Вся попытка (id трека + запрос) — не дольше 10 с.
        /// </summary>
        /// <summary>Spicy Lyrics подключён: пользователь вставил свой ключ developer API.</summary>
        public static bool Available => !string.IsNullOrEmpty(Key);

        /// <summary>
        /// Текст для играющего трека. Spotify привязан и играет он — берём точный id трека из Spotify; иначе (или если
        /// Spotify ещё не переключился) ищем по названию, исполнителю и длительности и берём только точное совпадение.
        /// Вся попытка — не дольше 12 с.
        /// </summary>
        public static async Task<Result> TryGetAsync(MediaInfo m, bool retryQueued = false)
        {
            var res = new Result { Outcome = Outcome.Skipped };
            if (!Available || m == null || string.IsNullOrWhiteSpace(m.Title)) return res;
            if (DateTime.UtcNow < _pausedUntil) { Journal.Debug("spicy", "пауза после отказа сервиса — сразу LRCLIB"); return res; }
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            try
            {
                var key = m.Artist + "|" + m.Title;
                string id;
                bool known;
                lock (Ids) known = Ids.TryGetValue(key, out id);
                bool spotify = SpotifyAuth.IsLinked && SpotifyAuth.IsSpotifyApp(m.SourceAppId);
                if (!known)
                {
                    if (spotify) id = await SpotifyAuth.CurrentTrackIdAsync(m.Title, cts.Token);
                    if (id == null)
                    {
                        var (st, found) = await SearchAsync(m, cts.Token);
                        if (st != 200 && st != 0) return Fail(res, st, "поиск");
                        id = found;
                    }
                    // запоминаем только найденный id: «не нашли» бывает временным (Spotify ещё не переключил трек)
                    if (id != null) lock (Ids) Ids[key] = id;
                }
                if (id == null) { res.Outcome = spotify ? Outcome.NoMatch : Outcome.None; return res; }
                res.TrackId = id;
                lock (Cache)
                    if (Cache.TryGetValue(id, out var cached))
                    {
                        res.Outcome = cached.Lines == null ? Outcome.None : Outcome.Ok;
                        res.Lines = cached.Lines;
                        res.Credit = cached.Credit;
                        return res;
                    }

                var (status, body, error) = await GetAsync("lyrics/" + Uri.EscapeDataString(id), cts.Token);
                Journal.Info("spicy", "запрос текста: " + status + (error != null ? " " + error : ""));
                switch (status)
                {
                    case 200:
                        SetKeyProblem(null);
                        var lines = Map(body);
                        if (lines == null || lines.Count == 0)
                        {
                            // Static (без времени) или пусто — нам не подходит, запоминаем и берём LRCLIB
                            Journal.Info("spicy", "текст без разметки по времени — беру LRCLIB");
                            lock (Cache) Cache[id] = (null, null);
                            res.Outcome = Outcome.None;
                            return res;
                        }
                        var credit = ReadCredit(body);
                        lock (Cache) Cache[id] = (lines, credit);
                        _backoff = TimeSpan.FromMinutes(1);
                        res.Outcome = Outcome.Ok;
                        res.Lines = lines;
                        res.Credit = credit;
                        Journal.Info("spicy", lines.Count + " строк, по словам: " + (lines.Exists(l => l.Words != null) ? "да" : "нет") + ", источник " + credit.Provider);
                        return res;
                    case 404:
                        lock (Cache) Cache[id] = (null, null);
                        res.Outcome = Outcome.None;
                        return res;
                    case 503 when !retryQueued:
                        res.Outcome = Outcome.Queued; // временно — LyricsState спросит ещё раз позже
                        return res;
                    default:
                        return Fail(res, status, error);
                }
            }
            catch (Exception ex)
            {
                Journal.Warn("spicy", "сбой: " + (ex is OperationCanceledException ? "не ответил за 12 с" : ex.GetType().Name));
                Pause(null, 0);
                res.Outcome = Outcome.Failed;
                return res;
            }
        }

        private static Result Fail(Result res, int status, string error)
        {
            if (status == 401 || status == 403)
            {
                // ключ не принят (неверный, отозван, приложение на паузе) — до смены ключа не спрашиваем
                if (OwnKey) SetKeyProblem(status == 401 ? "Ключ не принят" + (error != null ? " (" + error + ")" : "") : "Доступ закрыт" + (error != null ? " (" + error + ")" : ""));
                Pause(TimeSpan.FromHours(6), status);
            }
            else Pause(status == 400 ? TimeSpan.FromMinutes(30) : (TimeSpan?)null, status);
            res.Outcome = Outcome.Failed;
            return res;
        }

        /// <summary>Поиск трека: только tier «exact» (другая версия песни — другие тайминги).</summary>
        private static async Task<(int, string)> SearchAsync(MediaInfo m, CancellationToken ct)
        {
            var q = "tracks/search?title=" + Uri.EscapeDataString(m.Title.Trim()) + "&limit=3";
            if (!string.IsNullOrWhiteSpace(m.Artist)) q += "&artist=" + Uri.EscapeDataString(m.Artist.Trim());
            if (m.Duration > TimeSpan.FromSeconds(5)) q += "&duration=" + m.Duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
            var (status, body, error) = await GetAsync(q, ct);
            if (status != 200) { Journal.Info("spicy", "поиск: " + status + (error != null ? " " + error : "")); return (status, null); }
            if (body?["results"] is JsonArray rs && rs.Count > 0 && rs[0] is JsonObject first)
            {
                var tier = Str(first["tier"]);
                Journal.Info("spicy", "поиск: первый результат «" + tier + "»");
                if (tier == "exact") return (200, Str(first["id"]));
            }
            else Journal.Info("spicy", "поиск: ничего");
            return (200, null);
        }

        /// <summary>GET к developer API. Возвращает HTTP-статус, Body из конверта и код ошибки (Body.error), если он есть.</summary>
        private static async Task<(int, JsonNode, string)> GetAsync(string path, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Api + "/" + path);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Key);
            using var resp = await Http.SendAsync(req, ct);
            int status = (int)resp.StatusCode;
            ReadLimit(resp);
            if ((status == 429 || status == 503) && resp.Headers.RetryAfter?.Delta is TimeSpan ra && ra > _backoff) _backoff = ra;
            JsonNode body = null;
            string error = null;
            try
            {
                var root = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
                body = root?["Body"];
                if (status != 200) error = Str(body?["error"]);
            }
            catch { }
            return (status, body, error);
        }

        private static void ReadLimit(HttpResponseMessage resp)
        {
            static int? H(HttpResponseMessage r, string n) =>
                r.Headers.TryGetValues(n, out var v) && int.TryParse(v.FirstOrDefault(), out var x) ? x : null;
            int? lim = H(resp, "RateLimit-Limit"), rem = H(resp, "RateLimit-Remaining"), reset = H(resp, "RateLimit-Reset");
            if (lim == null || rem == null) return;
            var t = "Осталось " + rem + " из " + lim + " запросов" + (reset is int r && r > 0 ? ", сброс через " + (r >= 120 ? (r / 60) + " мин" : r + " с") : "") + ".";
            if (t == LimitText) return;
            LimitText = t;
            Journal.Debug("spicy", "лимит: " + rem + "/" + lim);
            LimitChanged?.Invoke();
        }

        private static Credit ReadCredit(JsonNode body)
        {
            var src = Str(body?["source"]);
            var c = new Credit
            {
                Provider = src switch { "apple_music" => "Apple Music", "spotify" => "Spotify", _ => "Spicy Lyrics" },
            };
            c.Community = src == "spicy_lyrics";
            if (body?["SongWriters"] is JsonArray ws)
                foreach (var w in ws) { var t = Str(w)?.Trim(); if (!string.IsNullOrEmpty(t)) c.Writers.Add(t); }
            if (src == "spicy_lyrics" && body?["UploadAttribution"] is JsonObject at)
            {
                c.Uploader = ReadPerson(at["Uploader"]);
                c.Maker = ReadPerson(at["Maker"]);
            }
            return c;
        }

        private static Person ReadPerson(JsonNode n)
        {
            var name = Str(n?["username"]);
            if (string.IsNullOrWhiteSpace(name)) return null;
            var url = Str(n["url"]);
            // ссылку открываем только на профиль сервиса
            if (url == null || !url.StartsWith("https://spicylyrics.org/", StringComparison.OrdinalIgnoreCase)) url = null;
            return new Person { Name = name, Url = url };
        }

        private static void Pause(TimeSpan? fixedFor, int status)
        {
            var d = fixedFor ?? _backoff;
            if (fixedFor == null) _backoff = TimeSpan.FromTicks(Math.Min(TimeSpan.FromMinutes(15).Ticks, _backoff.Ticks * 2));
            _pausedUntil = DateTime.UtcNow + d;
            Journal.Info("spicy", "пауза " + (int)d.TotalMinutes + " мин" + (status > 0 ? " (статус " + status + ")" : ""));
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
                        OppositeAligned = vg["OppositeAligned"] is JsonValue oa && oa.GetValueKind() == JsonValueKind.True,
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
                    var line = new LyricLine { At = TimeSpan.FromSeconds(st), Text = t, OppositeAligned = vg["OppositeAligned"] is JsonValue oa && oa.GetValueKind() == JsonValueKind.True };
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
