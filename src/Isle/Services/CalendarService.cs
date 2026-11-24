using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Isle.Services
{
    /// <summary>Повтор события: каждые Interval дней/недель/месяцев/лет, до Until или Count раз.</summary>
    public class CalRule
    {
        public string Freq { get; set; }            // DAILY, WEEKLY, MONTHLY, YEARLY
        public int Interval { get; set; } = 1;
        public int? Count { get; set; }
        public DateTime? Until { get; set; }
        public List<DayOfWeek> ByDay { get; set; }  // для WEEKLY: в какие дни недели
    }

    /// <summary>Событие (или «мастер» повторяющегося события).</summary>
    public class CalEvent
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Uid { get; set; }
        public string Title { get; set; } = "";
        public string Location { get; set; }
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        public bool AllDay { get; set; }
        /// <summary>За сколько минут напомнить (null — не напоминать).</summary>
        public int? Reminder { get; set; }
        public CalRule Rule { get; set; }
        public List<DateTime> ExDates { get; set; }
        /// <summary>Для изменённого повтора: какое вхождение он заменяет.</summary>
        public DateTime? RecurrenceId { get; set; }
        /// <summary>«local» — своё событие, иначе id подключённого календаря.</summary>
        public string Source { get; set; } = "local";
        /// <summary>Цвет события (#RRGGBB). Нет — подберётся сам (старые события получают цвет при первом запуске).</summary>
        public string Color { get; set; }
    }

    /// <summary>Конкретное появление события в календаре (повторы уже развёрнуты).</summary>
    public class CalOccurrence
    {
        public CalEvent Event { get; init; }
        public DateTime Start { get; init; }
        public DateTime End { get; init; }
        public string Color { get; init; }
        public string Key => Event.Id + "|" + Start.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
        public string TimeText => Event.AllDay ? "Весь день" : Start.ToString("HH:mm") + "–" + End.ToString("HH:mm");
    }

    /// <summary>Подключённый календарь: ссылка iCal (Google, Outlook, Apple iCloud) — обновляется сама.</summary>
    public class CalFeed
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public string Color { get; set; } = "#60A5FA";
        public DateTime? LastSync { get; set; }
        public string Error { get; set; }
        public int Count { get; set; }
    }

    /// <summary>
    /// Календарь Isle: свои события плюс подключённые календари по ссылке iCal (без входа в аккаунты и ключей) и
    /// импорт файла .ics. Напоминания приходят плашкой в чёлку.
    /// </summary>
    public static class CalendarService
    {
        public const string LocalColor = "#4ADE80";
        /// <summary>Цвета событий: читаются и на чёрной теме, и на «Закате».</summary>
        public static readonly string[] Palette = { "#4ADE80", "#60A5FA", "#F472B6", "#FBBF24", "#A78BFA", "#2DD4BF", "#FB923C", "#F87171" };
        private static readonly string[] FeedColors = { "#60A5FA", "#F472B6", "#FBBF24", "#A78BFA", "#2DD4BF", "#FB923C" };

        public static readonly List<CalEvent> Local = new();
        public static readonly List<CalFeed> Feeds = new();
        private static readonly Dictionary<string, List<CalEvent>> FeedEvents = new();
        public static event Action Changed;

        private static readonly HttpClient Http = Helpers.Net.Client(TimeSpan.FromSeconds(20));
        private static string Dir => Path.Combine(Storage.Dir, "calendar");
        private static string LocalPath => Path.Combine(Dir, "events.json");
        private static string FeedsPath => Path.Combine(Dir, "feeds.json");
        private static string FeedFile(string id) => Path.Combine(Dir, "feed-" + id + ".ics");
        private static bool _loaded;

        // ---------- загрузка и сохранение ----------
        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (File.Exists(LocalPath))
                {
                    try { Local.AddRange(JsonSerializer.Deserialize<List<CalEvent>>(File.ReadAllText(LocalPath)) ?? new()); }
                    catch (Exception ex)
                    {
                        // файл не читается — не затираем его пустым списком: откладываем в сторону, события можно будет вернуть
                        var bad = Path.Combine(Dir, "events.broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
                        try { File.Copy(LocalPath, bad, true); } catch { }
                        Journal.Warn("calendar", "свои события не прочитались (" + ex.Message + "), файл сохранён как " + Path.GetFileName(bad));
                    }
                }
                AssignMissingColors();
                if (File.Exists(FeedsPath)) Feeds.AddRange(JsonSerializer.Deserialize<List<CalFeed>>(File.ReadAllText(FeedsPath)) ?? new());
                foreach (var f in Feeds)
                    if (File.Exists(FeedFile(f.Id))) FeedEvents[f.Id] = Ics.Parse(File.ReadAllText(FeedFile(f.Id)), f.Id);
                Journal.Info("calendar", "своих событий " + Local.Count + ", подключённых календарей " + Feeds.Count);
            }
            catch (Exception ex) { Journal.Warn("calendar", "не прочитался: " + ex.Message); }
        }

        /// <summary>
        /// События из прошлых версий были без цвета (все зелёные). Раздаём цвета так, чтобы в одном дне они различались;
        /// перед этим — копия файла, на всякий случай.
        /// </summary>
        private static void AssignMissingColors()
        {
            var bare = Local.Where(e => string.IsNullOrEmpty(e.Color)).ToList();
            if (bare.Count == 0) return;
            try { File.Copy(LocalPath, Path.Combine(Dir, "events.backup-" + DateTime.Now.ToString("yyyyMMdd") + ".json"), true); } catch { }
            foreach (var e in bare.OrderBy(e => e.Start)) e.Color = PickColor(e.Start.Date, e);
            SaveLocal();
            Journal.Info("calendar", "событиям без цвета раздали цвета: " + bare.Count);
        }

        /// <summary>Цвет для события в этот день: первый, которого в дне ещё нет (иначе — самый редкий).</summary>
        public static string PickColor(DateTime day, CalEvent except = null)
        {
            var used = Local.Where(e => e != except && !string.IsNullOrEmpty(e.Color) && e.Start.Date <= day && e.End >= day)
                            .GroupBy(e => e.Color).ToDictionary(g => g.Key, g => g.Count());
            return Palette.OrderBy(c => used.TryGetValue(c, out var n) ? n : 0).ThenBy(c => Array.IndexOf(Palette, c)).First();
        }

        private static void SaveLocal() => Write(LocalPath, JsonSerializer.Serialize(Local));
        private static void SaveFeeds() => Write(FeedsPath, JsonSerializer.Serialize(Feeds));

        private static void Write(string path, string text)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(path + ".tmp", text);
                File.Move(path + ".tmp", path, true);
            }
            catch (Exception ex) { Journal.Warn("calendar", "не сохранился: " + ex.Message); }
        }

        // ---------- свои события ----------
        public static void Save(CalEvent e)
        {
            if (string.IsNullOrEmpty(e.Color)) e.Color = PickColor(e.Start.Date, e);
            if (!Local.Contains(e)) Local.Add(e);
            SaveLocal();
            Changed?.Invoke();
        }

        public static void Delete(CalEvent e)
        {
            if (!Local.Remove(e)) return;
            SaveLocal();
            Changed?.Invoke();
        }

        /// <summary>Импорт файла .ics (выгрузка из Google, Outlook, Apple): события становятся своими, повторы не дублируются.</summary>
        public static int ImportFile(string path)
        {
            var list = Ics.Parse(File.ReadAllText(path), "local");
            int added = 0;
            foreach (var e in list)
            {
                if (e.Uid != null && Local.Any(x => x.Uid == e.Uid && x.RecurrenceId == e.RecurrenceId)) continue;
                e.Color = PickColor(e.Start.Date);
                Local.Add(e); added++;
            }
            SaveLocal();
            Journal.Info("calendar", "импорт файла: добавлено " + added + " из " + list.Count);
            Changed?.Invoke();
            return added;
        }

        // ---------- подключённые календари ----------
        public static async Task<CalFeed> AddFeedAsync(string url, string name)
        {
            url = NormalizeUrl(url);
            var f = new CalFeed { Url = url, Name = string.IsNullOrWhiteSpace(name) ? GuessName(url) : name.Trim(), Color = FeedColors[Feeds.Count % FeedColors.Length] };
            await SyncAsync(f, throwOnError: true);
            Feeds.Add(f);
            SaveFeeds();
            Changed?.Invoke();
            return f;
        }

        public static void RemoveFeed(CalFeed f)
        {
            Feeds.Remove(f);
            FeedEvents.Remove(f.Id);
            try { File.Delete(FeedFile(f.Id)); } catch { }
            SaveFeeds();
            Changed?.Invoke();
        }

        /// <summary>webcal:// (так дают ссылку Apple) — это обычный https.</summary>
        public static string NormalizeUrl(string url)
        {
            url = (url ?? "").Trim();
            if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url[9..];
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
            return url;
        }

        private static string GuessName(string url)
        {
            var u = url.ToLowerInvariant();
            return u.Contains("google.com") ? "Google" : u.Contains("outlook") || u.Contains("office365") || u.Contains("live.com") ? "Outlook"
                : u.Contains("icloud.com") ? "Apple" : u.Contains("yandex") ? "Яндекс" : "Календарь";
        }

        private static async Task SyncAsync(CalFeed f, bool throwOnError = false)
        {
            try
            {
                var text = await Http.GetStringAsync(f.Url);
                if (!text.Contains("BEGIN:VCALENDAR")) throw new InvalidDataException("по ссылке не календарь — нужна ссылка iCal (.ics)");
                var list = Ics.Parse(text, f.Id);
                FeedEvents[f.Id] = list;
                Directory.CreateDirectory(Dir);
                await File.WriteAllTextAsync(FeedFile(f.Id), text);
                f.LastSync = DateTime.Now; f.Error = null; f.Count = list.Count;
                Journal.Info("calendar", "«" + f.Name + "» обновлён: " + list.Count + " событий");
            }
            catch (Exception ex)
            {
                f.Error = ex is HttpRequestException ? "нет связи или ссылка закрыта" : ex.Message;
                Journal.Warn("calendar", "«" + f.Name + "» не обновился: " + ex.Message);
                if (throwOnError) throw;
            }
        }

        public static async Task RefreshAllAsync()
        {
            foreach (var f in Feeds.ToList()) await SyncAsync(f);
            SaveFeeds();
            Changed?.Invoke();
        }

        // ---------- выборка ----------
        /// <summary>Цвет события: свой, а у событий из подключённых календарей — по названию (разные дела — разные цвета).</summary>
        public static string ColorOf(CalEvent e)
        {
            if (!string.IsNullOrEmpty(e.Color)) return e.Color;
            if (e.Source == "local") return LocalColor;
            uint h = 2166136261;
            foreach (var ch in e.Uid ?? e.Title ?? "") h = (h ^ ch) * 16777619;
            return Palette[h % (uint)Palette.Length];
        }

        /// <summary>Все появления событий, пересекающие [from, to), по времени начала.</summary>
        public static List<CalOccurrence> Between(DateTime from, DateTime to)
        {
            var all = Local.Concat(FeedEvents.Values.SelectMany(x => x));
            var res = new List<CalOccurrence>();
            foreach (var group in all.GroupBy(e => (e.Source, e.Uid ?? e.Id)))
            {
                var overrides = group.Where(e => e.RecurrenceId.HasValue).ToList();
                foreach (var e in group)
                {
                    if (e.RecurrenceId.HasValue) { Add(e, e.Start, e.End); continue; }
                    foreach (var (s, en) in Expand(e, from, to))
                        if (!overrides.Any(o => o.RecurrenceId.Value == s)) Add(e, s, en);
                }
            }
            return res.OrderBy(o => o.Start).ThenBy(o => o.Event.AllDay ? 0 : 1).ToList();

            void Add(CalEvent e, DateTime s, DateTime en)
            {
                if (s < to && (en > from || (en == s && s >= from))) res.Add(new CalOccurrence { Event = e, Start = s, End = en, Color = ColorOf(e) });
            }
        }

        public static List<CalOccurrence> On(DateTime day) => Between(day.Date, day.Date.AddDays(1));

        /// <summary>Повторы события в окне [from, to). Без правила — само событие.</summary>
        public static IEnumerable<(DateTime start, DateTime end)> Expand(CalEvent e, DateTime from, DateTime to)
        {
            var len = e.End - e.Start;
            if (len < TimeSpan.Zero) len = TimeSpan.Zero;
            if (e.Rule == null || string.IsNullOrEmpty(e.Rule.Freq)) { yield return (e.Start, e.Start + len); yield break; }
            var r = e.Rule;
            int interval = Math.Max(1, r.Interval);
            int made = 0;
            var ex = e.ExDates ?? new List<DateTime>();
            // недели: в каждую «свою» неделю — все дни из BYDAY
            if (r.Freq == "WEEKLY" && r.ByDay is { Count: > 0 })
            {
                var weekStart = e.Start.Date.AddDays(-(((int)e.Start.DayOfWeek + 6) % 7));
                for (int w = 0; w < 5000; w++)
                {
                    var ws = weekStart.AddDays(7 * interval * w);
                    if (ws > to || (r.Until.HasValue && ws > r.Until.Value)) yield break;
                    for (int d = 0; d < 7; d++)
                    {
                        var day = ws.AddDays(d);
                        if (!r.ByDay.Contains(day.DayOfWeek)) continue;
                        var s = day + e.Start.TimeOfDay;
                        if (s < e.Start) continue;
                        if (r.Until.HasValue && s > r.Until.Value) yield break;
                        if (r.Count.HasValue && made >= r.Count.Value) yield break;
                        made++;
                        if (ex.Contains(s)) continue;
                        if (s + len >= from && s < to) yield return (s, s + len);
                    }
                }
                yield break;
            }
            // без счётчика повторов можно сразу перескочить к окну (ежедневное событие с 2015 года — не 4000 шагов)
            int first = 0;
            if (!r.Count.HasValue && from > e.Start)
            {
                double step = r.Freq switch { "DAILY" => 1, "WEEKLY" => 7, "MONTHLY" => 28, "YEARLY" => 365, _ => 0 } * interval;
                if (step > 0) first = Math.Max(0, (int)((from - e.Start - len).TotalDays / step) - 2);
                if (r.Freq is "MONTHLY" or "YEARLY") first = Math.Max(0, first - 2);
            }
            for (int i = first; i < first + 20000; i++)
            {
                DateTime s;
                try
                {
                    s = r.Freq switch
                    {
                        "DAILY" => e.Start.AddDays((double)i * interval),
                        "WEEKLY" => e.Start.AddDays(7.0 * i * interval),
                        "MONTHLY" => e.Start.AddMonths(i * interval),
                        "YEARLY" => e.Start.AddYears(i * interval),
                        _ => i == 0 ? e.Start : DateTime.MaxValue,
                    };
                }
                catch (ArgumentOutOfRangeException) { yield break; }
                if (s == DateTime.MaxValue || s > to) yield break;
                if (r.Until.HasValue && s > r.Until.Value) yield break;
                if (r.Count.HasValue && made >= r.Count.Value) yield break;
                made++;
                // 31-е число в коротком месяце: AddMonths даёт 30-е — такие пропускаем, как делают календари
                if (r.Freq == "MONTHLY" && s.Day != e.Start.Day) continue;
                if (ex.Contains(s)) continue;
                if (s + len >= from) yield return (s, s + len);
            }
        }

        // ---------- напоминания ----------
        private static readonly DispatcherTimer Ticker = new() { Interval = TimeSpan.FromSeconds(20) };
        private static readonly DispatcherTimer FeedTimer = new() { Interval = TimeSpan.FromMinutes(30) };
        private static readonly HashSet<string> Fired = new();

        /// <summary>Запуск при старте Isle: загрузка, напоминания, обновление календарей раз в полчаса.</summary>
        public static void Start()
        {
            Load();
            Ticker.Tick += (_, __) => CheckReminders();
            Ticker.Start();
            FeedTimer.Tick += async (_, __) => await RefreshAllAsync();
            FeedTimer.Start();
            if (Feeds.Count > 0) _ = RefreshAllAsync();
        }

        private static void CheckReminders()
        {
            if (!AppSettings.Current.CalendarReminders) return;
            var now = DateTime.Now;
            foreach (var o in Between(now.AddMinutes(-1), now.AddHours(25)))
            {
                if (o.Event.AllDay)
                {
                    // событие на весь день — одна плашка утром (в 9:00 или сразу, если Isle запустили позже)
                    if (o.Start.Date != now.Date || now.Hour < 9) continue;
                    Fire(o, "day", "Сегодня", now);
                    continue;
                }
                if (o.Event.Reminder is int m && m > 0)
                {
                    var at = o.Start.AddMinutes(-m);
                    if (now >= at && now < o.Start) Fire(o, "before", "Через " + Minutes((int)Math.Ceiling((o.Start - now).TotalMinutes)), now);
                }
                if (o.Event.Reminder.HasValue && now >= o.Start && now < o.Start.AddMinutes(2)) Fire(o, "start", "Начинается сейчас", now);
            }
        }

        private static void Fire(CalOccurrence o, string kind, string when, DateTime now)
        {
            var key = o.Key + "|" + kind;
            if (!Fired.Add(key)) return;
            Journal.Info("calendar", "напоминание (" + kind + ")"); // название события в журнал не пишем
            ToastBus.Show(new Toast
            {
                Kind = ToastKind.Info, Title = o.Event.Title, Private = true,
                Subtitle = when + " · " + o.TimeText + (string.IsNullOrWhiteSpace(o.Event.Location) ? "" : " · " + o.Event.Location),
                Icon = Icons.Calendar, Important = true, Duration = TimeSpan.FromSeconds(kind == "start" ? 6 : 5),
            });
        }

        public static string Minutes(int m) => m >= 60 && m % 60 == 0 ? (m / 60) + " ч" : m >= 60 ? (m / 60) + " ч " + (m % 60) + " мин" : Math.Max(1, m) + " мин";
    }

    /// <summary>
    /// Чтение iCalendar (.ics, RFC 5545): VEVENT с датами, часовыми поясами, повторами (RRULE), исключениями (EXDATE),
    /// изменёнными повторами (RECURRENCE-ID) и напоминанием (VALARM). Только то, что нужно для показа и напоминаний.
    /// </summary>
    public static class Ics
    {
        public static List<CalEvent> Parse(string text, string source)
        {
            var list = new List<CalEvent>();
            CalEvent cur = null;
            bool inAlarm = false;
            DateTime? end = null;
            TimeSpan? duration = null;
            foreach (var (name, prms, value) in Lines(text))
            {
                if (name == "BEGIN" && value == "VEVENT") { cur = new CalEvent { Source = source }; end = null; duration = null; continue; }
                if (cur == null) continue;
                if (name == "BEGIN" && value == "VALARM") { inAlarm = true; continue; }
                if (name == "END" && value == "VALARM") { inAlarm = false; continue; }
                if (name == "END" && value == "VEVENT")
                {
                    if (cur.Start != default)
                    {
                        cur.End = end ?? (duration.HasValue ? cur.Start + duration.Value : cur.AllDay ? cur.Start.AddDays(1) : cur.Start);
                        if (cur.End < cur.Start) cur.End = cur.Start;
                        // напоминание по умолчанию для событий со временем — 10 минут (если в календаре своего нет)
                        if (!cur.AllDay && cur.Reminder == null) cur.Reminder = 10;
                        list.Add(cur);
                    }
                    cur = null; inAlarm = false;
                    continue;
                }
                if (inAlarm)
                {
                    if (name == "TRIGGER" && !prms.ContainsKey("VALUE") && TryDuration(value, out var tr) && tr <= TimeSpan.Zero)
                        cur.Reminder = Math.Max(cur.Reminder ?? 0, (int)Math.Round(-tr.TotalMinutes));
                    continue;
                }
                switch (name)
                {
                    case "UID": cur.Uid = value; break;
                    case "SUMMARY": cur.Title = Unescape(value); break;
                    case "LOCATION": cur.Location = Unescape(value); break;
                    case "DTSTART":
                        if (TryDate(value, prms, out var s, out bool allDay)) { cur.Start = s; cur.AllDay = allDay; }
                        break;
                    case "DTEND":
                        if (TryDate(value, prms, out var e, out _)) end = e;
                        break;
                    case "DURATION":
                        if (TryDuration(value, out var d)) duration = d;
                        break;
                    case "RRULE": cur.Rule = ParseRule(value, prms); break;
                    case "EXDATE":
                        foreach (var part in value.Split(','))
                            if (TryDate(part, prms, out var x, out _)) (cur.ExDates ??= new()).Add(x);
                        break;
                    case "RECURRENCE-ID":
                        if (TryDate(value, prms, out var rid, out _)) cur.RecurrenceId = rid;
                        break;
                    case "STATUS":
                        if (value == "CANCELLED") cur.Title = "✕ " + cur.Title;
                        break;
                }
            }
            // отменённые повторы приходят отдельным VEVENT с RECURRENCE-ID и STATUS:CANCELLED — просто исключаем
            foreach (var c in list.Where(x => x.RecurrenceId.HasValue && x.Title.StartsWith("✕ ")).ToList())
            {
                var master = list.FirstOrDefault(m => m.Uid == c.Uid && !m.RecurrenceId.HasValue);
                if (master != null) { (master.ExDates ??= new()).Add(c.RecurrenceId.Value); list.Remove(c); }
            }
            return list;
        }

        /// <summary>Строки с учётом переноса (продолжение начинается с пробела или табуляции): имя, параметры, значение.</summary>
        private static IEnumerable<(string name, Dictionary<string, string> prms, string value)> Lines(string text)
        {
            var sb = new StringBuilder();
            var raw = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var joined = new List<string>();
            foreach (var l in raw)
            {
                if ((l.StartsWith(" ") || l.StartsWith("\t")) && joined.Count > 0) joined[^1] += l[1..];
                else joined.Add(l);
            }
            foreach (var line in joined)
            {
                int colon = FindColon(line);
                if (colon <= 0) continue;
                var head = line[..colon].Split(';');
                var prms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in head.Skip(1))
                {
                    int eq = p.IndexOf('=');
                    if (eq > 0) prms[p[..eq].ToUpperInvariant()] = p[(eq + 1)..].Trim('"');
                }
                yield return (head[0].ToUpperInvariant(), prms, line[(colon + 1)..]);
            }
        }

        /// <summary>Двоеточие, отделяющее значение (в кавычках параметров двоеточие бывает: TZID="…").</summary>
        private static int FindColon(string line)
        {
            bool q = false;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '"') q = !q;
                else if (line[i] == ':' && !q) return i;
            }
            return -1;
        }

        private static string Unescape(string v) => v.Replace("\\n", "\n").Replace("\\N", "\n").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\").Trim();

        /// <summary>Дата/время в местное время компьютера: 20261108 (весь день), …T180000Z (UTC), TZID=… или «плавающее» местное.</summary>
        public static bool TryDate(string v, Dictionary<string, string> prms, out DateTime dt, out bool allDay)
        {
            dt = default; allDay = false;
            v = v.Trim();
            if ((prms.TryGetValue("VALUE", out var vt) && vt == "DATE") || v.Length == 8)
            {
                if (!DateTime.TryParseExact(v[..Math.Min(8, v.Length)], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) return false;
                allDay = true;
                return true;
            }
            bool utc = v.EndsWith("Z");
            if (!DateTime.TryParseExact(v.TrimEnd('Z'), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return false;
            if (utc) { dt = DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime(); dt = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified); return true; }
            if (prms.TryGetValue("TZID", out var tz) && FindZone(tz) is TimeZoneInfo zone)
            {
                try { dt = DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(t, DateTimeKind.Unspecified), zone, TimeZoneInfo.Local), DateTimeKind.Unspecified); return true; }
                catch { }
            }
            dt = t; // «плавающее» время — как есть, по часам компьютера
            return true;
        }

        private static readonly Dictionary<string, TimeZoneInfo> Zones = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Пояс по имени: IANA (Europe/Moscow) или Windows (Russian Standard Time). Неизвестный — null (местное время).</summary>
        private static TimeZoneInfo FindZone(string id)
        {
            if (Zones.TryGetValue(id, out var z)) return z;
            try { z = TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch
            {
                try { z = TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var win) ? TimeZoneInfo.FindSystemTimeZoneById(win) : null; } catch { z = null; }
            }
            return Zones[id] = z;
        }

        /// <summary>Длительность: P1D, PT1H30M, -PT15M, P1W.</summary>
        public static bool TryDuration(string v, out TimeSpan d)
        {
            d = TimeSpan.Zero;
            v = v.Trim();
            if (v.Length < 2) return false;
            bool neg = v.StartsWith("-");
            v = v.TrimStart('+', '-');
            if (!v.StartsWith("P")) return false;
            bool time = false; int num = 0; bool any = false;
            foreach (var c in v[1..])
            {
                if (char.IsDigit(c)) { num = num * 10 + (c - '0'); any = true; continue; }
                switch (c)
                {
                    case 'T': time = true; break;
                    case 'W': d += TimeSpan.FromDays(7 * num); break;
                    case 'D': d += TimeSpan.FromDays(num); break;
                    case 'H': d += TimeSpan.FromHours(num); break;
                    case 'M': d += time ? TimeSpan.FromMinutes(num) : TimeSpan.Zero; break;
                    case 'S': d += TimeSpan.FromSeconds(num); break;
                    default: return false;
                }
                num = 0;
            }
            if (neg) d = -d;
            return any;
        }

        private static CalRule ParseRule(string v, Dictionary<string, string> prms)
        {
            var r = new CalRule();
            foreach (var part in v.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string k = part[..eq].ToUpperInvariant(), val = part[(eq + 1)..];
                switch (k)
                {
                    case "FREQ": r.Freq = val.ToUpperInvariant(); break;
                    case "INTERVAL": if (int.TryParse(val, out var i)) r.Interval = Math.Max(1, i); break;
                    case "COUNT": if (int.TryParse(val, out var c)) r.Count = c; break;
                    case "UNTIL": if (TryDate(val, new Dictionary<string, string>(), out var u, out bool ad)) r.Until = ad ? u.AddDays(1).AddSeconds(-1) : u; break;
                    case "BYDAY":
                        var days = new List<DayOfWeek>();
                        foreach (var dd in val.Split(','))
                        {
                            var code = dd.Length >= 2 ? dd[^2..].ToUpperInvariant() : dd;
                            int idx = Array.IndexOf(new[] { "SU", "MO", "TU", "WE", "TH", "FR", "SA" }, code);
                            // «2MO» (второй понедельник месяца) — редкий случай: показываем как ежемесячное по дате
                            if (idx >= 0 && dd.Length == 2) days.Add((DayOfWeek)idx);
                        }
                        if (days.Count > 0) r.ByDay = days;
                        break;
                }
            }
            return r.Freq == null ? null : r;
        }
    }
}
