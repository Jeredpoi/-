using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace Isle.Services
{
    public class Reminder
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Text { get; set; } = "";
        public DateTime When { get; set; }
        public bool Daily { get; set; }
        public string WhenText => (Daily ? "каждый день в " + When.ToString("HH:mm") : Pretty(When));

        private static string Pretty(DateTime t)
        {
            var today = DateTime.Today;
            if (t.Date == today) return "сегодня в " + t.ToString("HH:mm");
            if (t.Date == today.AddDays(1)) return "завтра в " + t.ToString("HH:mm");
            return t.ToString("dd.MM в HH:mm");
        }
    }

    public class RemindersData { public List<Reminder> Items { get; set; } = new(); }

    /// <summary>Напоминания на естественном языке: «позвонить маме завтра в 9», «через 20 минут выключить плиту».</summary>
    public class ReminderService
    {
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
        public ObservableCollection<Reminder> Items { get; } = new();

        public void Start()
        {
            foreach (var r in Storage.Load<RemindersData>("reminders.json").Items.OrderBy(x => x.When)) Items.Add(r);
            _timer.Tick += (_, __) => Check();
            _timer.Start();
        }

        public Reminder Add(string input)
        {
            var r = Parse(input, DateTime.Now);
            if (r == null) return null;
            Items.Add(r);
            Resort();
            Save();
            return r;
        }

        public void Remove(Reminder r) { Items.Remove(r); Save(); }

        private void Resort()
        {
            var sorted = Items.OrderBy(x => x.When).ToList();
            Items.Clear();
            foreach (var r in sorted) Items.Add(r);
        }

        private void Save() => Storage.Save("reminders.json", new RemindersData { Items = Items.ToList() });

        private void Check()
        {
            var now = DateTime.Now;
            bool changed = false;
            foreach (var r in Items.Where(x => x.When <= now).ToList())
            {
                ToastBus.Show(new Toast
                {
                    Kind = ToastKind.Reminder, Title = "Напоминание", Subtitle = r.Text, Icon = Icons.Bell,
                    Accent = System.Windows.Media.Color.FromRgb(0xFF, 0xB5, 0x47),
                    Duration = TimeSpan.FromSeconds(8), Important = true
                });
                if (r.Daily) { while (r.When <= now) r.When = r.When.AddDays(1); }
                else Items.Remove(r);
                changed = true;
            }
            if (changed) { Resort(); Save(); }
        }

        // ---------- Разбор фразы ----------

        private static readonly Regex InRx = new(@"через\s+(?:(\d+(?:[.,]\d+)?)\s*)?(полчаса|сек(?:унд[уы]?)?|с\b|мин(?:ут[уы]?)?|м\b|час(?:а|ов)?|ч\b|д(?:ень|ня|ней)\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DailyRx = new(@"(каждый\s+день|ежедневно)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DayRx = new(@"\b(послезавтра|завтра|сегодня)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex AtRx = new(@"\bв\s+(\d{1,2})(?:[:.](\d{2}))?(?:\s*(утра|дня|вечера|ночи))?(?!\d)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex WeekRx = new(@"\bв(?:о)?\s+(понедельник|вторник|среду|четверг|пятницу|субботу|воскресенье)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static Reminder Parse(string input, DateTime now)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            string text = " " + input.Trim() + " ";
            DateTime? when = null;
            bool daily = false;

            var mIn = InRx.Match(text);
            if (mIn.Success)
            {
                double n = 1;
                if (mIn.Groups[1].Success) double.TryParse(mIn.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out n);
                var unit = mIn.Groups[2].Value.ToLowerInvariant();
                TimeSpan span = unit.StartsWith("пол") ? TimeSpan.FromMinutes(30)
                    : unit.StartsWith("с") ? TimeSpan.FromSeconds(n)
                    : unit.StartsWith("м") ? TimeSpan.FromMinutes(n)
                    : unit.StartsWith("ч") ? TimeSpan.FromHours(n)
                    : TimeSpan.FromDays(n);
                when = now + span;
                text = text.Remove(mIn.Index, mIn.Length).Insert(mIn.Index, " ");
            }

            if (DailyRx.IsMatch(text)) { daily = true; text = DailyRx.Replace(text, " "); }

            DateTime baseDay = now.Date;
            bool explicitDay = false;
            var mDay = DayRx.Match(text);
            if (mDay.Success)
            {
                var d = mDay.Groups[1].Value.ToLowerInvariant();
                baseDay = d == "завтра" ? now.Date.AddDays(1) : d == "послезавтра" ? now.Date.AddDays(2) : now.Date;
                explicitDay = true;
                text = text.Remove(mDay.Index, mDay.Length).Insert(mDay.Index, " ");
            }

            var mWeek = WeekRx.Match(text);
            if (mWeek.Success)
            {
                var target = mWeek.Groups[1].Value.ToLowerInvariant() switch
                {
                    "понедельник" => DayOfWeek.Monday, "вторник" => DayOfWeek.Tuesday, "среду" => DayOfWeek.Wednesday,
                    "четверг" => DayOfWeek.Thursday, "пятницу" => DayOfWeek.Friday, "субботу" => DayOfWeek.Saturday,
                    _ => DayOfWeek.Sunday
                };
                int add = ((int)target - (int)now.DayOfWeek + 7) % 7;
                if (add == 0) add = 7;
                baseDay = now.Date.AddDays(add);
                explicitDay = true;
                text = text.Remove(mWeek.Index, mWeek.Length).Insert(mWeek.Index, " ");
            }

            var mAt = AtRx.Match(text);
            if (mAt.Success && when == null)
            {
                int h = int.Parse(mAt.Groups[1].Value);
                int min = mAt.Groups[2].Success ? int.Parse(mAt.Groups[2].Value) : 0;
                var part = mAt.Groups[3].Success ? mAt.Groups[3].Value.ToLowerInvariant() : "";
                if ((part == "дня" || part == "вечера") && h < 12) h += 12;
                if (part == "ночи" && h == 12) h = 0;
                if (h > 23 || min > 59) return null;
                var t = baseDay.AddHours(h).AddMinutes(min);
                if (!explicitDay && t <= now) t = t.AddDays(1);
                when = t;
                text = text.Remove(mAt.Index, mAt.Length).Insert(mAt.Index, " ");
            }
            else if (when == null && explicitDay)
            {
                when = baseDay.AddHours(9);
                if (when <= now) when = now.AddMinutes(1);
            }

            if (when == null) return null;
            var task = Regex.Replace(text, @"\s+", " ").Trim(' ', ',', '.', '-', '—');
            if (task.Length == 0) task = "Напоминание";
            task = char.ToUpper(task[0]) + task.Substring(1);
            return new Reminder { Text = task, When = when.Value, Daily = daily };
        }
    }
}
