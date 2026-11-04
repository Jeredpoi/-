using System.Linq;
using System;
using Isle.Helpers;
using Isle.Services;
using Isle.Views;
using Xunit;

namespace Isle.Tests
{
    public class ReminderParserTests
    {
        // среда, 4 ноября 2026, 14:00
        private static readonly DateTime Now = new(2026, 11, 4, 14, 0, 0);

        [Fact]
        public void TomorrowAtHour()
        {
            var r = ReminderService.Parse("позвонить маме завтра в 9", Now);
            Assert.NotNull(r);
            Assert.Equal(new DateTime(2026, 11, 5, 9, 0, 0), r.When);
            Assert.Equal("Позвонить маме", r.Text);
            Assert.False(r.Daily);
        }

        [Fact]
        public void InMinutes()
        {
            var r = ReminderService.Parse("через 20 минут выключить плиту", Now);
            Assert.Equal(Now.AddMinutes(20), r.When);
            Assert.Equal("Выключить плиту", r.Text);
        }

        [Fact]
        public void InHalfHourAndHour()
        {
            Assert.Equal(Now.AddMinutes(30), ReminderService.Parse("через полчаса чай", Now).When);
            Assert.Equal(Now.AddHours(1), ReminderService.Parse("через час встреча", Now).When);
            Assert.Equal(Now.AddHours(2), ReminderService.Parse("через 2 часа встреча", Now).When);
        }

        [Fact]
        public void DailyAt()
        {
            var r = ReminderService.Parse("таблетки каждый день в 23:00", Now);
            Assert.True(r.Daily);
            Assert.Equal(new DateTime(2026, 11, 4, 23, 0, 0), r.When);
            Assert.Equal("Таблетки", r.Text);
        }

        [Fact]
        public void PastTimeTodayMovesToTomorrow()
        {
            var r = ReminderService.Parse("в 9 зарядка", Now);
            Assert.Equal(new DateTime(2026, 11, 5, 9, 0, 0), r.When);
        }

        [Fact]
        public void EveningSuffix()
        {
            var r = ReminderService.Parse("сегодня в 7 вечера кино", Now);
            Assert.Equal(new DateTime(2026, 11, 4, 19, 0, 0), r.When);
            Assert.Equal("Кино", r.Text);
        }

        [Fact]
        public void Weekday()
        {
            var r = ReminderService.Parse("в пятницу в 18:30 футбол", Now);
            Assert.Equal(new DateTime(2026, 11, 6, 18, 30, 0), r.When);
        }

        [Fact]
        public void TodayPastHourMeansEvening()
        {
            // 14:00, «сегодня в 9» — это 21:00, а не звонок прямо сейчас
            Assert.Equal(new DateTime(2026, 11, 4, 21, 0, 0), ReminderService.Parse("сегодня в 9 созвон", Now).When);
            // «сегодня в 13:30» уже прошло и вечером не бывает — завтра
            Assert.Equal(new DateTime(2026, 11, 5, 13, 30, 0), ReminderService.Parse("сегодня в 13:30 обед", Now).When);
        }

        [Theory]
        [InlineData("Привет! Как дела? У меня всё хорошо, спасибо. Давай встретимся завтра в девять, хорошо?", true)]
        [InlineData("Продолжение следует...", true)]
        [InlineData("Субтитры сделал DimaTorzok", true)]
        [InlineData("", true)]
        [InlineData("Привет!", false)]
        [InlineData("Напомни купить молоко", false)]
        public void WhisperHallucinations(string text, bool junk) =>
            Assert.Equal(junk, VoiceService.IsHallucination(text, "Привет! Как дела? У меня всё хорошо, спасибо. Давай встретимся завтра в девять, хорошо?"));

        [Fact]
        public void NoTimeReturnsNull()
        {
            Assert.Null(ReminderService.Parse("купить хлеб", Now));
        }
    }

    public class CalcTests
    {
        [Theory]
        [InlineData("2+2*3", "8")]
        [InlineData("7/2", "3.5")]
        [InlineData("(1+2)*4", "12")]
        [InlineData("200*15%", "30")]
        public void Calculates(string expr, string expected)
        {
            var prev = System.Globalization.CultureInfo.CurrentCulture;
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            try
            {
                Assert.True(HomeView.TryCalc(expr, out var r));
                Assert.Equal(expected, r);
            }
            finally { System.Globalization.CultureInfo.CurrentCulture = prev; }
        }

        [Theory]
        [InlineData("notepad")]
        [InlineData("2024")]
        [InlineData("https://ya.ru")]
        public void NotMath(string expr) => Assert.False(HomeView.TryCalc(expr, out _));
    }

    public class TextToolsTests
    {
        [Fact] public void LayoutToRu() => Assert.Equal("привет", TextTools.SwitchLayout("ghbdtn"));
        [Fact] public void LayoutToEn() => Assert.Equal("hello", TextTools.SwitchLayout("руддщ"));
        [Fact] public void Translit() => Assert.Equal("Shchuka i yozh", TextTools.Translit("Щука и ёж"));
        [Fact] public void Base64RoundTrip() => Assert.Equal("Привет!", TextTools.FromBase64(TextTools.ToBase64("Привет!")));
        [Fact] public void Sentence() => Assert.Equal("Привет. Как дела?", TextTools.SentenceCase("пРИВЕТ. как ДЕЛА?"));
        [Fact] public void Spaces() => Assert.Equal("a b\nc", TextTools.CollapseSpaces("  a   b \n  c  "));
        [Fact] public void Plural() { Assert.Equal("слово", TextTools.Plural(21, "слово", "слова", "слов")); Assert.Equal("слов", TextTools.Plural(11, "слово", "слова", "слов")); }

        [Fact]
        public void JsonPretty() => Assert.Equal("{\n  \"a\": 1\n}", TextTools.JsonPretty("{\"a\":1}").Replace("\r\n", "\n"));
    }

    public class LyricsTests
    {
        [Fact]
        public void ParsesLrc()
        {
            var l = LyricsService.Parse("[00:01.50] one\n[01:02.00]two\nnoise");
            Assert.Equal(2, l.Count);
            Assert.Equal(TimeSpan.FromSeconds(1.5), l[0].At);
            Assert.Equal("two", l[1].Text);
            Assert.Equal(TimeSpan.FromSeconds(62), l[1].At);
        }
    }
}

namespace Isle.Tests
{
    public class IconTests
    {
        [Fact]
        public void AllIconsParse()
        {
            foreach (var f in typeof(Icons).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            {
                var data = (string)f.GetValue(null);
                var g = System.Windows.Media.Geometry.Parse(data);
                Assert.False(g.Bounds.IsEmpty, f.Name);
                Assert.True(g.Bounds.Right <= 24.5 && g.Bounds.Bottom <= 24.5 && g.Bounds.Left >= -0.5 && g.Bounds.Top >= -0.5, f.Name + " выходит за сетку 24×24: " + g.Bounds);
            }
        }
    }
}

namespace Isle.Tests
{
    /// <summary>Создаёт настоящее окно и все вкладки — ловит падения при запуске (как то, из-за которого остров не появлялся).</summary>
    public class StartupSmokeTests
    {
        [Fact]
        public void MainWindowAndAllTabsCanBeCreated()
        {
            Exception error = null;
            var t = new System.Threading.Thread(() =>
            {
                try
                {
                    if (System.Windows.Application.Current == null)
                    {
                        var app = new App();
                        app.InitializeComponent();
                    }
                    var w = new MainWindow();
                    AppHost.Window = w;
                    foreach (var tab in new[] { "home", "clip", "shelf", "text", "chat", "notes", "weather", "controls" })
                    {
                        w.SelectTab(tab);
                        Assert.NotNull(w.GetView(tab));
                    }
                    // Показываем окно по-настоящему: проверяем OnSourceInitialized и Loaded (горячие клавиши, буфер, модули).
                    w.Show();
                    var frame = new System.Windows.Threading.DispatcherFrame();
                    var stop = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                    stop.Tick += (_, __) => { stop.Stop(); frame.Continue = false; };
                    stop.Start();
                    System.Windows.Threading.Dispatcher.PushFrame(frame);
                    Assert.True(w.IsVisible, "окно острова не видно после Show()");
                    var sw = new Views.SettingsWindow();
                    foreach (var sec in new[] { "general", "look", "music", "ai", "voice", "record", "weather", "modules", "keys", "updates" }) sw.ShowSection(sec);
                    sw.Close();
                    w.Close();
                }
                catch (Exception ex) { error = ex; }
            });
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.Start();
            t.Join();
            Assert.True(error == null, error?.ToString());
        }
    }
}

namespace Isle.Tests
{
    public class AiActionsTests
    {
        [Fact]
        public void ExtractsCommandsAndCleansText()
        {
            var (text, actions) = AiActions.Extract("Открываю блокнот.\n[[open: notepad]]\n[[timer: 10m]]\n[[screenshot]]\n[[cmd: ipconfig]]");
            Assert.Equal("Открываю блокнот.", text);
            Assert.Equal(new[] { "open", "timer", "screenshot", "cmd" }, actions.Select(a => a.Name).ToArray());
            Assert.Equal("notepad", actions[0].Arg);
            Assert.True(actions[3].NeedsConfirm);
            Assert.False(actions[0].NeedsConfirm);
        }

        [Fact]
        public void LeavesUnknownBrackets() => Assert.Contains("[[foo: bar]]", AiActions.Extract("x [[foo: bar]]").text);

        [Fact]
        public void TempoFromBeats()
        {
            Assert.Equal(120, SpectrumService.BpmFromIntervals(new[] { 0.5, 0.5, 0.49, 0.51, 0.5 }), 0);
            Assert.Equal(90, SpectrumService.BpmFromIntervals(new[] { 1.33, 1.34, 1.33, 1.32 }), 0); // 45 → удваиваем до 90
        }

        [Fact]
        public void DanceMatchesTempo()
        {
            var rnd = new Random(1);
            for (int i = 0; i < 200; i++)
            {
                Assert.Contains(Isle.Helpers.Character.PickDance(170, 0.8, -1, rnd), new[] { 4, 2, 8, 0 }); // быстро и плотно
                Assert.Contains(Isle.Helpers.Character.PickDance(80, 0.3, -1, rnd), new[] { 1, 7, 5, 0, 6 }); // медленно
                Assert.NotEqual(3, Isle.Helpers.Character.PickDance(110, 0.4, 3, rnd));                      // не повторяет текущий
            }
        }

        [Fact]
        public void HolidayHats()
        {
            Assert.Equal("ny", Isle.Helpers.Character.Holiday(new DateTime(2026, 12, 31)));
            Assert.Equal("ny", Isle.Helpers.Character.Holiday(new DateTime(2027, 1, 5)));
            Assert.Equal("halloween", Isle.Helpers.Character.Holiday(new DateTime(2026, 10, 31)));
            Assert.Null(Isle.Helpers.Character.Holiday(new DateTime(2026, 11, 4)));
        }

        [Fact]
        public void ThinkingIsRemovedEvenIfCut()
        {
            Assert.Equal("Ответ", AiService.StripThinking("<think>думаю…</think>Ответ"));
            Assert.Equal("Начало", AiService.StripThinking("Начало <think>рассуждение без конца"));
        }

        [Fact]
        public void HistoryAlternatesAndStartsWithUser()
        {
            var h = Isle.Views.ChatView.BuildHistory(new[]
            {
                new ChatMessage { Role = "assistant", Text = "привет" },
                new ChatMessage { Role = "user", Text = "a" },
                new ChatMessage { Role = "user", Text = "b" },
                new ChatMessage { Role = "assistant", Text = "c" },
            });
            Assert.Equal(new[] { "user", "assistant" }, h.Select(m => m.Role).ToArray());
            Assert.Equal("a\n\nb", h[0].Text);
        }

        [Theory]
        [InlineData(@"C:\Users\me\Downloads\run.bat", true)]
        [InlineData(@"C:\tools\x.exe", true)]
        [InlineData("telegram", false)]
        [InlineData("https://youtube.com", false)]
        [InlineData(@"C:\Users\me\Documents", false)]
        public void OpenOfScriptNeedsConfirm(string arg, bool confirm) =>
            Assert.Equal(confirm, new AiAction { Name = "open", Arg = arg }.NeedsConfirm);
    }
}

namespace Isle.Tests
{
    public class TimerParseTests
    {
        [Theory]
        [InlineData("5", 300)]
        [InlineData("30с", 30)]
        [InlineData("1 сек", 1)]
        [InlineData("90 sec", 90)]
        [InlineData("1:30", 90)]
        [InlineData("2м", 120)]
        [InlineData("1ч 20м", 4800)]
        [InlineData("1.5ч", 5400)]
        public void Parses(string text, int seconds) => Assert.Equal(TimeSpan.FromSeconds(seconds), TimerService.ParseDuration(text));

        [Theory]
        [InlineData("")]
        [InlineData("abc")]
        [InlineData("0")]
        public void Rejects(string text) => Assert.Null(TimerService.ParseDuration(text));
    }
}
