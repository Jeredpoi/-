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
