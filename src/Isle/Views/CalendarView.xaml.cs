using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Isle.Helpers;
using Isle.Services;

namespace Isle.Views
{
    /// <summary>
    /// Календарь: слева месяц, у дней — точки событий (по цвету календаря), справа — события выбранного дня,
    /// недели или почасовая лента дня. Свои события можно добавлять и менять, подключённые — только смотреть.
    /// </summary>
    public partial class CalendarView : UserControl, IIslandView
    {
        private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
        private DateTime _month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
        private DateTime _day = DateTime.Today;
        private string _mode = AppSettings.Current.CalendarMode ?? "month";
        private CalEvent _editing;   // null — новое событие
        private int? _remind = 10;
        private string _repeat;      // null, DAILY, WEEKLY, MONTHLY, YEARLY

        public CalendarView()
        {
            InitializeComponent();
            CalendarService.Load();
            CalendarService.Changed += () => Dispatcher.InvokeAsync(Render);
            foreach (var d in new[] { "Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс" })
                WeekDays.Children.Add(new TextBlock { Text = d, FontSize = 10.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 0, 0, 4) });
            (_mode == "day" ? ModeDay : _mode == "week" ? ModeWeek : ModeMonth).IsChecked = true;
            BuildChips();
            IsVisibleChanged += (_, __) => { if (IsVisible) { if (_day.Date != DateTime.Today && _shownToday != DateTime.Today) GoToday(); Render(); } };
            Render();
        }

        private DateTime _shownToday = DateTime.Today;

        public void OnShown() => Render();

        // ---------- отрисовка ----------
        private void Render()
        {
            MonthTitle.Text = Ru.DateTimeFormat.MonthNames[_month.Month - 1] is var mn && mn.Length > 0 ? char.ToUpper(mn[0]) + mn[1..] + " " + _month.Year : _month.ToString("yyyy"); // «Ноябрь 2026» — именительный падеж
            TodayBtn.Visibility = _day.Date == DateTime.Today && _month.Month == DateTime.Today.Month && _month.Year == DateTime.Today.Year ? Visibility.Hidden : Visibility.Visible;
            RenderMonth();
            if (ListPane.Visibility == Visibility.Visible) RenderList();
        }

        private DateTime GridStart => _month.AddDays(-(((int)_month.DayOfWeek + 6) % 7));

        private void RenderMonth()
        {
            Days.Children.Clear();
            var start = GridStart;
            var occ = CalendarService.Between(start, start.AddDays(42));
            var byDay = new Dictionary<DateTime, List<CalOccurrence>>();
            foreach (var o in occ)
            {
                // событие на несколько дней — точка в каждом его дне
                var last = o.End > o.Start ? o.End.AddTicks(-1).Date : o.Start.Date;
                for (var d = o.Start.Date; d <= last && d < start.AddDays(42); d = d.AddDays(1))
                {
                    if (d < start) continue;
                    if (!byDay.TryGetValue(d, out var l)) byDay[d] = l = new List<CalOccurrence>();
                    l.Add(o);
                }
            }
            var accent = (Brush)FindResource("Accent");
            for (int i = 0; i < 42; i++)
            {
                var d = start.AddDays(i);
                bool inMonth = d.Month == _month.Month, sel = d == _day.Date, today = d == DateTime.Today;
                var cell = new Border { CornerRadius = new CornerRadius(10), Margin = new Thickness(1.5), Cursor = Cursors.Hand, Background = Brushes.Transparent };
                if (sel) cell.SetResourceReference(Border.BackgroundProperty, "SelBg");
                var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var num = new TextBlock { Text = d.Day.ToString(), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center,
                    FontWeight = today || sel ? FontWeights.Bold : FontWeights.Medium, Opacity = inMonth ? 1 : 0.35 };
                if (today && !sel) num.Foreground = accent;
                sp.Children.Add(num);
                var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Height = 6, Margin = new Thickness(0, 2, 0, 0), Opacity = inMonth ? 1 : 0.4 };
                if (byDay.TryGetValue(d, out var list))
                    foreach (var c in list.Select(o => o.Color).Take(3))
                        dots.Children.Add(new Ellipse { Width = 4.5, Height = 4.5, Margin = new Thickness(1, 0, 1, 0), Fill = Brush(c) });
                sp.Children.Add(dots);
                cell.Child = sp;
                if (today) cell.ToolTip = "Сегодня";
                var day = d;
                cell.MouseEnter += (_, __) => { if (day != _day.Date) cell.SetResourceReference(Border.BackgroundProperty, "ChipHover"); };
                cell.MouseLeave += (_, __) => { if (day != _day.Date) cell.Background = Brushes.Transparent; };
                cell.MouseLeftButtonUp += (_, e) =>
                {
                    if (e.ClickCount >= 2) { _day = day; OpenForm(null); return; }
                    Select(day);
                };
                Days.Children.Add(cell);
            }
        }

        private void Select(DateTime day)
        {
            _day = day.Date;
            if (_day.Month != _month.Month || _day.Year != _month.Year) _month = new DateTime(_day.Year, _day.Month, 1);
            ShowPane(ListPane);
            Render();
        }

        private void RenderList()
        {
            List.Children.Clear();
            switch (_mode)
            {
                case "week":
                {
                    var ws = _day.AddDays(-(((int)_day.DayOfWeek + 6) % 7));
                    DayTitle.Text = ("Неделя · " + ws.ToString("d MMMM", Ru) + " – " + ws.AddDays(6).ToString("d MMMM", Ru)).ToUpper(Ru);
                    var occ = CalendarService.Between(ws, ws.AddDays(7));
                    for (int i = 0; i < 7; i++)
                    {
                        var d = ws.AddDays(i);
                        var dayOcc = occ.Where(o => o.Start.Date <= d && (o.End > d || o.Start.Date == d)).ToList();
                        var head = new TextBlock { Text = Cap(d.ToString("dddd, d MMMM", Ru)), FontSize = 12, FontWeight = FontWeights.SemiBold,
                            Margin = new Thickness(2, i == 0 ? 0 : 10, 0, 6), Cursor = Cursors.Hand };
                        if (d == DateTime.Today) head.Foreground = (Brush)FindResource("Accent");
                        else if (dayOcc.Count == 0) head.Foreground = (Brush)FindResource("Muted");
                        var day = d;
                        head.MouseLeftButtonUp += (_, __) => { _mode = "day"; ModeDay.IsChecked = true; Select(day); };
                        List.Children.Add(head);
                        foreach (var o in dayOcc) List.Children.Add(EventCard(o, compact: true));
                    }
                    break;
                }
                case "day":
                    DayTitle.Text = _day.ToString("dddd, d MMMM", Ru).ToUpper(Ru);
                    RenderTimeline();
                    break;
                default:
                {
                    DayTitle.Text = _day.ToString("dddd, d MMMM", Ru).ToUpper(Ru);
                    var occ = CalendarService.On(_day);
                    if (occ.Count == 0) { List.Children.Add(Empty()); break; }
                    foreach (var o in occ) List.Children.Add(EventCard(o, compact: false));
                    break;
                }
            }
        }

        /// <summary>Свободный день: подсказка и кнопка «Добавить».</summary>
        private FrameworkElement Empty()
        {
            var sp = new StackPanel { Margin = new Thickness(0, 30, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            sp.Children.Add(new Border { Width = 46, Height = 46, CornerRadius = new CornerRadius(14), HorizontalAlignment = HorizontalAlignment.Center,
                Child = new Icon { Kind = "Calendar", Width = 20, Height = 20, Foreground = (Brush)FindResource("Muted") } });
            ((Border)sp.Children[0]).SetResourceReference(Border.BackgroundProperty, "Plate");
            sp.Children.Add(new TextBlock { Text = _day == DateTime.Today ? "Сегодня ничего не запланировано" : "В этот день ничего нет", FontSize = 13, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 4) });
            sp.Children.Add(new TextBlock { Text = CalendarService.Feeds.Count == 0 ? "Добавь событие или подключи Google, Outlook, Apple — кнопка со звеньями сверху" : "Двойной клик по дню — новое событие",
                Style = (Style)FindResource("MutedText"), FontSize = 11, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 300 });
            var b = new Button { Style = (Style)FindResource("Flat"), Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            b.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { new Icon { Kind = "Plus", Width = 13, Height = 13 }, new TextBlock { Text = "Добавить событие", FontSize = 12, Margin = new Thickness(6, 0, 0, 0) } } };
            b.Click += (_, __) => OpenForm(null);
            sp.Children.Add(b);
            return sp;
        }

        /// <summary>Карточка события: цветная полоска календаря, время, название, место.</summary>
        private FrameworkElement EventCard(CalOccurrence o, bool compact)
        {
            var e = o.Event;
            var row = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(12, compact ? 6 : 9, 12, compact ? 6 : 9), Margin = new Thickness(0, 0, 0, 6) };
            row.SetResourceReference(Border.BackgroundProperty, "Plate");
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(new Border { Width = 3, CornerRadius = new CornerRadius(1.5), Background = Brush(o.Color), Margin = new Thickness(0, 1, 10, 1) });
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Text = o.TimeText + (e.Rule != null ? "  ↻" : "") + (e.Reminder.HasValue && !e.AllDay ? "" : ""), FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("Muted") });
            info.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(e.Title) ? "(без названия)" : e.Title, FontSize = compact ? 13 : 14, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
            if (!compact && !string.IsNullOrWhiteSpace(e.Location))
                info.Children.Add(new TextBlock { Text = e.Location, Style = (Style)FindResource("MutedText"), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(info, 1); g.Children.Add(info);
            row.Child = g;
            if (e.Source == "local")
            {
                row.Cursor = Cursors.Hand;
                row.ToolTip = "Клик — изменить";
                row.MouseLeftButtonUp += (_, __) => OpenForm(e);
            }
            else row.ToolTip = "Из календаря «" + (CalendarService.Feeds.FirstOrDefault(f => f.Id == e.Source)?.Name ?? "?") + "» — изменить можно там";
            return row;
        }

        /// <summary>День по часам: события — блоки на своём времени, «весь день» — сверху. Линия «сейчас» — красная.</summary>
        private void RenderTimeline()
        {
            var occ = CalendarService.On(_day);
            foreach (var o in occ.Where(x => x.Event.AllDay || x.Start.Date < _day)) List.Children.Add(EventCard(o, compact: true));
            const double H = 44;
            var canvas = new Canvas { Height = 24 * H, ClipToBounds = false };
            var muted = (Brush)FindResource("Muted");
            for (int h = 0; h < 24; h++)
            {
                canvas.Children.Add(Pos(new TextBlock { Text = h.ToString("00") + ":00", FontSize = 10, Foreground = muted }, 0, h * H - 6));
                var line = new Border { Height = 1, Opacity = 0.5 };
                line.SetResourceReference(Border.BackgroundProperty, "CardBorder");
                canvas.Children.Add(Pos(line, 40, h * H));
            }
            var timed = occ.Where(x => !x.Event.AllDay && x.Start.Date == _day).ToList();
            // пересекающиеся события — рядом, в своих колонках
            var cols = new List<DateTime>();
            var colOf = new Dictionary<CalOccurrence, int>();
            foreach (var o in timed)
            {
                int c = cols.FindIndex(end => end <= o.Start);
                if (c < 0) { cols.Add(o.End); c = cols.Count - 1; } else cols[c] = o.End;
                colOf[o] = c;
            }
            var blocks = new List<(Border b, CalOccurrence o)>();
            canvas.SizeChanged += (_, __) => Layout();
            foreach (var o in timed)
            {
                var b = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 4, 8, 4), Background = Brush(o.Color, 0x40), BorderBrush = Brush(o.Color), BorderThickness = new Thickness(2, 0, 0, 0) };
                b.Child = new StackPanel { Children = {
                    new TextBlock { Text = o.Event.Title, FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = o.TimeText, FontSize = 10, Foreground = muted } } };
                if (o.Event.Source == "local") { b.Cursor = Cursors.Hand; var ev = o.Event; b.MouseLeftButtonUp += (_, __) => OpenForm(ev); }
                canvas.Children.Add(b);
                blocks.Add((b, o));
            }
            if (_day == DateTime.Today)
            {
                var now = new Border { Height = 2, Background = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)), CornerRadius = new CornerRadius(1) };
                canvas.Children.Add(Pos(now, 40, DateTime.Now.TimeOfDay.TotalHours * H));
                blocks.Add((now, null));
            }
            void Layout()
            {
                double w = Math.Max(50, canvas.ActualWidth - 44);
                foreach (Border line in canvas.Children.OfType<Border>().Where(x => x.Height == 1)) line.Width = w;
                foreach (var (b, o) in blocks)
                {
                    if (o == null) { b.Width = w; continue; }
                    int n = Math.Max(1, cols.Count), c = colOf[o];
                    double top = o.Start.TimeOfDay.TotalHours * H, bottom = (o.End.Date > _day ? 24 : o.End.TimeOfDay.TotalHours) * H;
                    b.Width = Math.Max(20, w / n - 4);
                    b.Height = Math.Max(22, bottom - top - 2);
                    Canvas.SetLeft(b, 44 + c * (w / n));
                    Canvas.SetTop(b, top + 1);
                }
            }
            List.Children.Add(new Border { Child = canvas, Margin = new Thickness(0, 8, 0, 8) });
            // прокрутка: к ближайшему событию или на час раньше «сейчас»
            double target = (timed.Count > 0 ? timed[0].Start.TimeOfDay.TotalHours : _day == DateTime.Today ? DateTime.Now.TimeOfDay.TotalHours : 8) - 1;
            Dispatcher.InvokeAsync(() => ListScroll.ScrollToVerticalOffset(Math.Max(0, target * H)), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private static string Cap(string t) => string.IsNullOrEmpty(t) ? t : char.ToUpper(t[0]) + t[1..];

        private static UIElement Pos(UIElement el, double x, double y) { Canvas.SetLeft(el, x); Canvas.SetTop(el, y); return el; }

        private static Brush Brush(string hex, byte alpha = 255)
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(hex);
                var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)); b.Freeze(); return b;
            }
            catch { return Brushes.Gray; }
        }

        // ---------- навигация ----------
        private void OnPrev(object s, RoutedEventArgs e) => Shift(-1);
        private void OnNext(object s, RoutedEventArgs e) => Shift(1);

        /// <summary>Стрелки: в «Месяце» листают месяцы, в «Неделе» — недели, в «Дне» — дни.</summary>
        private void Shift(int dir)
        {
            if (_mode == "day") { Select(_day.AddDays(dir)); return; }
            if (_mode == "week") { Select(_day.AddDays(7 * dir)); return; }
            _month = _month.AddMonths(dir);
            _day = _month.Month == DateTime.Today.Month && _month.Year == DateTime.Today.Year ? DateTime.Today : _month;
            Render();
        }

        private void OnToday(object s, RoutedEventArgs e) => GoToday();

        /// <summary>
        /// Колёсико над месяцем — листать месяцы (вниз — следующий). Тачпад шлёт много мелких шагов —
        /// копим до одного «щелчка», и не чаще раза в 120 мс, чтобы месяцы не пролетали пачкой.
        /// </summary>
        private int _wheel;
        private DateTime _wheelAt;
        private void OnMonthWheel(object s, MouseWheelEventArgs e)
        {
            e.Handled = true;
            _wheel += e.Delta;
            if (Math.Abs(_wheel) < 120 || DateTime.Now - _wheelAt < TimeSpan.FromMilliseconds(120)) return;
            int dir = _wheel < 0 ? 1 : -1;
            _wheel = 0; _wheelAt = DateTime.Now;
            _month = _month.AddMonths(dir);
            // в «Месяце» выбранный день идёт за месяцем (сегодня — если вернулись в текущий), в «Дне»/«Неделе» остаётся
            if (_mode == "month") _day = _month.Month == DateTime.Today.Month && _month.Year == DateTime.Today.Year ? DateTime.Today : _month;
            Render();
        }

        private void GoToday()
        {
            _shownToday = DateTime.Today;
            _month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            Select(DateTime.Today);
        }

        private void OnMode(object s, RoutedEventArgs e)
        {
            _mode = (string)((FrameworkElement)s).Tag;
            AppSettings.Current.CalendarMode = _mode; AppSettings.Current.Save();
            ShowPane(ListPane);
            Render();
        }

        private void ShowPane(FrameworkElement pane)
        {
            ListPane.Visibility = pane == ListPane ? Visibility.Visible : Visibility.Collapsed;
            FormPane.Visibility = pane == FormPane ? Visibility.Visible : Visibility.Collapsed;
            FeedsPane.Visibility = pane == FeedsPane ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------- форма события ----------
        private static readonly (string text, int? min)[] RemindOptions = { ("Нет", null), ("5 мин", 5), ("10 мин", 10), ("30 мин", 30), ("1 ч", 60), ("За день", 1440) };
        private static readonly (string text, string freq)[] RepeatOptions = { ("Нет", null), ("Каждый день", "DAILY"), ("Каждую неделю", "WEEKLY"), ("Каждый месяц", "MONTHLY"), ("Каждый год", "YEARLY") };

        private void BuildChips()
        {
            FRemind.Children.Clear();
            foreach (var (text, min) in RemindOptions)
            {
                var t = new ToggleButton { Style = (Style)FindResource("Chip"), Content = text, Margin = new Thickness(0, 0, 6, 6), Tag = min };
                t.Click += (_, __) => { _remind = min; SyncChips(); };
                FRemind.Children.Add(t);
            }
            FRepeat.Children.Clear();
            foreach (var (text, freq) in RepeatOptions)
            {
                var t = new ToggleButton { Style = (Style)FindResource("Chip"), Content = text, Margin = new Thickness(0, 0, 6, 6), Tag = freq };
                t.Click += (_, __) => { _repeat = freq; SyncChips(); };
                FRepeat.Children.Add(t);
            }
        }

        private void SyncChips()
        {
            foreach (ToggleButton t in FRemind.Children) t.IsChecked = Equals(t.Tag as int?, _remind);
            foreach (ToggleButton t in FRepeat.Children) t.IsChecked = (string)t.Tag == _repeat;
        }

        private void OnAdd(object s, RoutedEventArgs e) => OpenForm(null);

        private void OpenForm(CalEvent ev)
        {
            _editing = ev;
            FError.Visibility = Visibility.Collapsed;
            if (ev == null)
            {
                // новое: на выбранный день, ближайший ровный час (сегодня) или 10:00
                var start = _day.Date == DateTime.Today ? DateTime.Today.AddHours(Math.Min(23, DateTime.Now.Hour + 1)) : _day.Date.AddHours(10);
                FTitle.Text = ""; FPlace.Text = "";
                FDate.Text = start.ToString("dd.MM.yyyy");
                FFrom.Text = start.ToString("HH:mm"); FTo.Text = start.AddHours(1).ToString("HH:mm");
                FAllDay.IsChecked = false; _remind = 10; _repeat = null;
            }
            else
            {
                FTitle.Text = ev.Title; FPlace.Text = ev.Location ?? "";
                FDate.Text = ev.Start.ToString("dd.MM.yyyy");
                FFrom.Text = ev.Start.ToString("HH:mm"); FTo.Text = ev.End.ToString("HH:mm");
                FAllDay.IsChecked = ev.AllDay; _remind = ev.Reminder; _repeat = ev.Rule?.Freq;
            }
            DeleteBtn.Visibility = ev != null ? Visibility.Visible : Visibility.Collapsed;
            OnAllDay(null, null);
            SyncChips();
            ShowPane(FormPane);
            Dispatcher.InvokeAsync(() => { FTitle.Focus(); FTitle.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
        }

        private void OnAllDay(object s, RoutedEventArgs e)
        {
            bool all = FAllDay.IsChecked == true;
            FFrom.IsEnabled = FTo.IsEnabled = !all;
            FFrom.Opacity = FTo.Opacity = all ? 0.4 : 1;
        }

        private void OnSave(object s, RoutedEventArgs e)
        {
            if (!DateTime.TryParseExact(FDate.Text.Trim(), new[] { "dd.MM.yyyy", "d.M.yyyy", "dd.MM.yy", "d.M.yy", "d.M" }, Ru, DateTimeStyles.None, out var date))
            { ShowError("Дата — в виде 24.11.2026"); return; }
            bool all = FAllDay.IsChecked == true;
            DateTime start = date.Date, end = date.Date.AddDays(1);
            if (!all)
            {
                if (!TryTime(FFrom.Text, out var a)) { ShowError("Время начала — в виде 18:00"); return; }
                if (!TryTime(FTo.Text, out var b)) b = a + TimeSpan.FromHours(1);
                start = date.Date + a;
                end = date.Date + b;
                if (end <= start) end = end.AddDays(1); // 23:00–01:00 — через полночь
            }
            var ev = _editing ?? new CalEvent();
            ev.Title = string.IsNullOrWhiteSpace(FTitle.Text) ? "Событие" : FTitle.Text.Trim();
            ev.Location = string.IsNullOrWhiteSpace(FPlace.Text) ? null : FPlace.Text.Trim();
            ev.Start = start; ev.End = end; ev.AllDay = all;
            ev.Reminder = all && _remind is < 1440 ? null : _remind;
            ev.Rule = _repeat == null ? null : new CalRule { Freq = _repeat, Interval = 1 };
            CalendarService.Save(ev);
            Journal.Info("calendar", (_editing == null ? "добавлено" : "изменено") + " событие" + (ev.Rule != null ? ", повтор " + ev.Rule.Freq : ""));
            _day = start.Date;
            _month = new DateTime(_day.Year, _day.Month, 1);
            ShowPane(ListPane);
            Render();
        }

        private static bool TryTime(string t, out TimeSpan ts)
        {
            t = (t ?? "").Trim().Replace('.', ':').Replace('-', ':');
            if (int.TryParse(t, out int h) && h is >= 0 and < 24) { ts = TimeSpan.FromHours(h); return true; }
            return TimeSpan.TryParseExact(t, new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out ts) && ts < TimeSpan.FromDays(1);
        }

        private void ShowError(string text) { FError.Text = text; FError.Visibility = Visibility.Visible; }

        private void OnDelete(object s, RoutedEventArgs e)
        {
            if (_editing != null) CalendarService.Delete(_editing);
            ShowPane(ListPane);
            Render();
        }

        private void OnCancel(object s, RoutedEventArgs e) { ShowPane(ListPane); Render(); }

        // ---------- календари ----------
        private void OnFeeds(object s, RoutedEventArgs e)
        {
            if (FeedsPane.Visibility == Visibility.Visible) { ShowPane(ListPane); Render(); return; }
            RenderFeeds();
            ShowPane(FeedsPane);
        }

        private void RenderFeeds()
        {
            FeedList.Children.Clear();
            RemindToggle.IsChecked = AppSettings.Current.CalendarReminders;
            var rows = new List<(string color, string name, string sub, Action remove)>
            {
                (CalendarService.LocalColor, "Мой календарь (Isle)", CalendarService.Local.Count + " событий", null)
            };
            foreach (var f in CalendarService.Feeds)
            {
                var feed = f;
                rows.Add((f.Color, f.Name, f.Error != null ? "не обновился: " + f.Error : f.Count + " событий · обновлён " + (f.LastSync?.ToString("HH:mm") ?? "—"), () => CalendarService.RemoveFeed(feed)));
            }
            foreach (var (color, name, sub, remove) in rows)
            {
                var row = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 7, 6, 7), Margin = new Thickness(0, 0, 0, 6) };
                row.SetResourceReference(Border.BackgroundProperty, "Plate");
                var dp = new DockPanel();
                if (remove != null)
                {
                    var x = new Button { Style = (Style)FindResource("Ghost"), Width = 28, Height = 28, Padding = new Thickness(0), ToolTip = "Отключить календарь", Content = new Icon { Kind = "Close", Width = 12, Height = 12 } };
                    x.Click += (_, __) => { remove(); RenderFeeds(); };
                    DockPanel.SetDock(x, Dock.Right); dp.Children.Add(x);
                }
                dp.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = Brush(color), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
                dp.Children.Add(new StackPanel { Children = {
                    new TextBlock { Text = name, FontSize = 13, FontWeight = FontWeights.SemiBold },
                    new TextBlock { Text = sub, FontSize = 10.5, Foreground = (Brush)FindResource("Muted"), TextTrimming = TextTrimming.CharacterEllipsis } } });
                row.Child = dp;
                FeedList.Children.Add(row);
            }
        }

        private void OnFeedKey(object s, KeyEventArgs e) { if (e.Key == Key.Enter) OnFeedAdd(s, e); }

        private async void OnFeedAdd(object s, RoutedEventArgs e)
        {
            var url = FeedUrl.Text.Trim();
            if (url.Length < 8) { Status("Вставь ссылку iCal — где её взять, написано ниже", true); return; }
            FeedAddBtn.IsEnabled = false;
            Status("Подключаю…", false);
            try
            {
                var f = await CalendarService.AddFeedAsync(url, null);
                FeedUrl.Text = "";
                Status("Подключено: «" + f.Name + "», событий: " + f.Count, false);
                RenderFeeds();
            }
            catch (Exception ex) { Status("Не получилось: " + (ex is System.Net.Http.HttpRequestException ? "ссылка не открылась (нет сети или календарь не опубликован)" : ex.Message), true); }
            finally { FeedAddBtn.IsEnabled = true; }
        }

        private void Status(string text, bool error)
        {
            FeedStatus.Text = text;
            FeedStatus.Foreground = error ? new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)) : (Brush)FindResource("Muted");
            FeedStatus.Visibility = Visibility.Visible;
        }

        private void OnImport(object s, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Календарь (*.ics)|*.ics|Все файлы|*.*", Title = "Импорт календаря" };
            if (AppHost.Window != null) AppHost.Window.SuppressAutoCollapse = true;
            try
            {
                if (dlg.ShowDialog() != true) return;
                int n = CalendarService.ImportFile(dlg.FileName);
                Status("Перенесено событий: " + n + " — они теперь в «Мой календарь»", false);
                RenderFeeds();
            }
            catch (Exception ex) { Status("Файл не прочитался: " + ex.Message, true); }
            finally { if (AppHost.Window != null) AppHost.Window.SuppressAutoCollapse = false; }
        }

        private void OnRemindToggle(object s, RoutedEventArgs e)
        {
            AppSettings.Current.CalendarReminders = RemindToggle.IsChecked == true;
            AppSettings.Current.Save();
        }
    }
}
