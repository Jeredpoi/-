using System.Linq;
using System;
using System.IO;
using System.Text.Json;
using Isle.Helpers;
using Isle.Services;
using Isle.Views;
using Xunit;

namespace Isle.Tests
{
    public class WeatherForecastParsingTests
    {
        [Fact]
        public void MetNorwaySymbolsAndForecastDetailsAreMapped()
        {
            var now = DateTimeOffset.Now;
            string stamp1 = now.AddMinutes(-5).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            string stamp2 = now.AddHours(2).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            string json = "{\"properties\":{\"timeseries\":[" +
                "{\"time\":\"" + stamp1 + "\",\"data\":{\"instant\":{\"details\":{\"air_temperature\":4,\"relative_humidity\":88,\"wind_speed\":3.2,\"wind_from_direction\":270}},\"next_1_hours\":{\"summary\":{\"symbol_code\":\"lightrain_day\"},\"details\":{\"precipitation_amount\":0.4,\"probability_of_precipitation\":70}}}}," +
                "{\"time\":\"" + stamp2 + "\",\"data\":{\"instant\":{\"details\":{\"air_temperature\":1,\"relative_humidity\":90,\"wind_speed\":2,\"wind_from_direction\":280}},\"next_1_hours\":{\"summary\":{\"symbol_code\":\"snow_day\"},\"details\":{\"precipitation_amount\":0.8,\"probability_of_precipitation\":80}}}}]}}";
            using var doc = JsonDocument.Parse(json);

            var weather = WeatherService.ParseLocationForecast(doc.RootElement, "Москва");

            Assert.Equal("Москва", weather.City);
            Assert.Equal(61, weather.Code);
            Assert.Equal(4, weather.Temp);
            Assert.Equal(3.2, weather.Wind);
            Assert.Equal(70, weather.Hours[0].PrecipitationProbability);
            Assert.True(weather.NextSnowAt.HasValue);
            Assert.Contains(weather.Days, d => d.Precip > 0);
        }
    }

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

        [Theory]
        [InlineData("Напомни купить молоко. Спасибо за субтитры Алексею Дубровскому!", "Напомни купить молоко.")]
        [InlineData("Спасибо за субтитры Алексею Дубровскому", "")]
        [InlineData("Открой телеграм", "Открой телеграм")]
        public void WhisperSubtitleTailIsRemoved(string text, string expected) =>
            Assert.Equal(expected, VoiceService.StripJunk(text));

        [Theory]
        [InlineData("Сделай этот текст более официальным", true)]
        [InlineData("исправь ошибки в сообщении", true)]
        [InlineData("переведи текст на английский", true)]
        [InlineData("преобразуй то, что я написал, в официальную форму", true)]
        [InlineData("чтобы текст был преобразован в официальной форме", true)]
        [InlineData("открой телеграм", false)]
        [InlineData("сделай таймер на 5 минут", false)]
        public void VoiceEditRequests(string command, bool edit) =>
            Assert.Equal(edit, TextEditor.IsEditRequest(command));

        [Fact]
        public void ChatContextKeepsWholeShortSessionAndTrimsLongOne()
        {
            var shortChat = new System.Collections.Generic.List<ChatMessage>();
            for (int i = 0; i < 30; i++) shortChat.Add(new ChatMessage { Role = i % 2 == 0 ? "user" : "assistant", Text = "реплика " + i });
            Assert.Equal(30, AiService.Fit(shortChat, 14000).Count); // раньше обрезалось до 16

            var longChat = new System.Collections.Generic.List<ChatMessage>();
            for (int i = 0; i < 10; i++) longChat.Add(new ChatMessage { Role = i % 2 == 0 ? "user" : "assistant", Text = new string('a', 3000) });
            var fit = AiService.Fit(longChat, 14000);
            Assert.True(fit.Count < 10);
            Assert.Equal("user", fit[0].Role);
        }

        [Theory]
        [InlineData("{\"id\":\"openai/whisper-large-v3\",\"supported_endpoints\":[\"/v1/audio/transcriptions\"]}", true)]
        [InlineData("{\"id\":\"openai/gpt-5.4-nano\",\"supported_endpoints\":[\"/v1/chat/completions\"],\"input_modalities\":[\"text\",\"audio\"]}", false)]
        [InlineData("{\"id\":\"elevenlabs/tts\",\"supported_endpoints\":[\"/v1/audio/speech\"]}", false)]
        [InlineData("{\"id\":\"whisper-large-v3\"}", true)]
        [InlineData("{\"id\":\"llama-3.3-70b-versatile\"}", false)]
        public void OnlySpeechToTextModels(string json, bool stt)
        {
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(stt, VoiceService.IsSpeechToText(doc.RootElement, doc.RootElement.GetProperty("id").GetString()));
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
    public class SteamDownloadTrackingTests
    {
        private static readonly DateTime PausedAt = new(2026, 11, 6, 16, 45, 0, DateTimeKind.Local);

        [Fact]
        public void ProgressTextUsesSteamStyleIntegerPercentage()
        {
            Assert.Equal("10%", new SteamDownload { Progress = 0.109 }.ProgressText);
        }

        [Fact]
        public void PausedUpdateIsNotKeptAsAnActiveDownloadPill()
        {
            var appId = 900000 + Random.Shared.Next(1, 99999);
            var root = CreateSteamRoot(appId);
            try
            {
                File.WriteAllText(Path.Combine(root, "logs", "content_log.txt"), PausedLog(appId));
                var download = SteamDownloadService.ReadCurrent(root, PausedAt);

                Assert.Null(download);
                Assert.Null(SteamDownloadService.TakePaused());
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void PausingAnObservedDownloadEmitsOneNotification()
        {
            var appId = 900000 + Random.Shared.Next(1, 99999);
            var root = CreateSteamRoot(appId);
            try
            {
                var logPath = Path.Combine(root, "logs", "content_log.txt");
                File.WriteAllText(logPath, ActiveLog(appId));
                Assert.NotNull(SteamDownloadService.ReadCurrent(root, new DateTime(2026, 11, 6, 16, 43, 40, DateTimeKind.Local)));

                File.AppendAllText(logPath,
                    $"[2026-11-06 16:44:05] AppID {appId} update canceled : Disabled (Suspended)\n" +
                    $"[2026-11-06 16:44:05] AppID {appId} App update changed : Running Update,Stopping,\n" +
                    $"[2026-11-06 16:44:05] AppID {appId} App update changed : None\n" +
                    $"[2026-11-06 16:44:06] AppID {appId} state changed : Update Required,Fully Installed,Update Paused,Update Started,\n");

                Assert.Null(SteamDownloadService.ReadCurrent(root, PausedAt));
                var notification = SteamDownloadService.TakePaused();
                Assert.NotNull(notification);
                Assert.True(notification.IsPaused);
                Assert.Equal("16%", notification.ProgressText);
                Assert.Null(SteamDownloadService.TakePaused());
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void ActiveUpdateShowsFileUpdatePercentAndLoadsSteamArtworkAndFolder()
        {
            var appId = 900000 + Random.Shared.Next(1, 99999);
            var root = CreateSteamRoot(appId);
            try
            {
                var installFolder = Path.Combine(root, "steamapps", "common", "Test Game Folder");
                Directory.CreateDirectory(installFolder);
                var artFolder = Path.Combine(root, "appcache", "librarycache", appId.ToString());
                Directory.CreateDirectory(artFolder);
                File.WriteAllBytes(Path.Combine(artFolder, "library_header.jpg"), Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j2ioAAAAASUVORK5CYII="));
                var manifestPath = Path.Combine(root, "steamapps", $"appmanifest_{appId}.acf");
                File.AppendAllText(manifestPath, "");
                var manifest = File.ReadAllText(manifestPath).Replace("\"name\" \"Test Game\"", "\"name\" \"Test Game\"\n\t\"installdir\" \"Test Game Folder\"");
                File.WriteAllText(manifestPath, manifest);
                File.WriteAllText(Path.Combine(root, "logs", "content_log.txt"), ActiveLog(appId));

                var download = SteamDownloadService.ReadCurrent(root, new DateTime(2026, 11, 6, 16, 43, 36, DateTimeKind.Local));

                Assert.NotNull(download);
                Assert.Equal("16%", download.ProgressText);
                Assert.NotEmpty(download.SpeedText);
                Assert.NotNull(download.Remaining);
                Assert.NotNull(download.Icon);
                Assert.True(download.Icon.IsFrozen); // иначе показ обложки в окне падает с «другим потоком»
                Assert.Equal(installFolder, download.FolderPath);
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void FreshInstallShowsDownloadedDataNotInstalledFiles()
        {
            var appId = 900000 + Random.Shared.Next(1, 99999);
            var root = CreateSteamRoot(appId);
            try
            {
                // новая установка: в логе нет «Fully Installed» — главное «Загрузка данных» (220 из 1000), а не установка (160)
                File.WriteAllText(Path.Combine(root, "logs", "content_log.txt"), ActiveLog(appId).Replace("Update Required,Fully Installed,", "Update Required,"));
                var download = SteamDownloadService.ReadCurrent(root, new DateTime(2026, 11, 6, 16, 43, 36, DateTimeKind.Local));
                Assert.NotNull(download);
                Assert.Equal("22%", download.ProgressText);
                Assert.Equal(1000, download.Total);
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void LongRunningActiveDownloadDoesNotExpireAfterTenMinutes()
        {
            var appId = 900000 + Random.Shared.Next(1, 99999);
            var root = CreateSteamRoot(appId);
            try
            {
                File.WriteAllText(Path.Combine(root, "logs", "content_log.txt"), ActiveLog(appId));
                var download = SteamDownloadService.ReadCurrent(root, new DateTime(2026, 11, 6, 17, 0, 0, DateTimeKind.Local));
                Assert.NotNull(download);
                Assert.Equal("16%", download.ProgressText);
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void UserCancelledUpdateIsNoLongerShown()
        {
            var appId = 900000 + Random.Shared.Next(1, 99999);
            var root = CreateSteamRoot(appId);
            try
            {
                var log = ActiveLog(appId) +
                    $"[2026-11-06 16:44:05] AppID {appId} update canceled : User canceled\n" +
                    $"[2026-11-06 16:44:05] AppID {appId} App update changed : None\n" +
                    $"[2026-11-06 16:44:06] AppID {appId} state changed : Update Required,Fully Installed,Update Queued,Update Started,\n";
                File.WriteAllText(Path.Combine(root, "logs", "content_log.txt"), log);

                Assert.Null(SteamDownloadService.ReadCurrent(root, PausedAt));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void FinishedDownloadEmitsOneCompletionEvent()
        {
            var appId = 900000 + Random.Shared.Next(1, 99999);
            var root = CreateSteamRoot(appId);
            try
            {
                var logPath = Path.Combine(root, "logs", "content_log.txt");
                File.WriteAllText(logPath, ActiveLog(appId));
                Assert.NotNull(SteamDownloadService.ReadCurrent(root, new DateTime(2026, 11, 6, 16, 43, 40, DateTimeKind.Local)));

                File.AppendAllText(logPath,
                    $"[2026-11-06 16:44:10] AppID {appId} update finished : No Error\n" +
                    $"[2026-11-06 16:44:10] AppID {appId} state changed : Fully Installed,\n");
                Assert.Null(SteamDownloadService.ReadCurrent(root, new DateTime(2026, 11, 6, 16, 44, 11, DateTimeKind.Local)));
                Assert.Equal("Test Game", SteamDownloadService.TakeCompleted()?.Name);
                Assert.Null(SteamDownloadService.TakeCompleted());
            }
            finally { Directory.Delete(root, true); }
        }

        private static string CreateSteamRoot(int appId)
        {
            var root = Path.Combine(Path.GetTempPath(), "IsleSteamTest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "logs"));
            Directory.CreateDirectory(Path.Combine(root, "steamapps"));
            File.WriteAllText(Path.Combine(root, "steamapps", $"appmanifest_{appId}.acf"),
                $"\"AppState\" {{\n\t\"appid\" \"{appId}\"\n\t\"name\" \"Test Game\"\n" +
                "\t\"BytesToDownload\" \"1000\"\n\t\"BytesDownloaded\" \"220\"\n" +
                "\t\"BytesToStage\" \"1000\"\n\t\"BytesStaged\" \"160\"\n}\n");
            return root;
        }

        private static string ActiveLog(int appId) =>
            $"[2026-11-06 16:43:34] AppID {appId} update started : download 0/1958392864, store 0/0, reuse 0/34740569739, delta 0/1043595302, stage 0/38428989932\n" +
            $"[2026-11-06 16:43:34] AppID {appId} state changed : Update Required,Fully Installed,Update Queued,Update Running,Update Started,\n" +
            $"[2026-11-06 16:43:34] AppID {appId} App update changed : Running Update,Downloading,Staging,\n" +
            "[2026-11-06 16:43:35] Current download rate: 100.525 Mbps\n";

        private static string PausedLog(int appId) => ActiveLog(appId) +
            $"[2026-11-06 16:44:05] AppID {appId} update canceled : Disabled (Suspended)\n" +
            $"[2026-11-06 16:44:05] AppID {appId} App update changed : Running Update,Stopping,\n" +
            $"[2026-11-06 16:44:05] AppID {appId} App update changed : None\n" +
            $"[2026-11-06 16:44:52] AppID {appId} state changed : Update Required,Fully Installed,Update Queued,Update Started, (Suspended)\n" +
            $"[2026-11-06 16:44:52] AppID {appId} state changed : Update Required,Fully Installed,Update Paused,Update Started,\n";
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
                Assert.Contains(Isle.Helpers.Character.PickDance(170, 0.8, -1, rnd), new[] { 4, 2, 8, 0, 11, 10 }); // быстро и плотно
                Assert.Contains(Isle.Helpers.Character.PickDance(80, 0.3, -1, rnd), new[] { 1, 7, 5, 0, 6, 9 }); // медленно
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
        [InlineData("90:00", 5400)]
        [InlineData("2м", 120)]
        [InlineData("1ч 20м", 4800)]
        [InlineData("1.5ч", 5400)]
        [InlineData("4h40m", 16800)] // так присылает ИИ: [[timer: 4h40m]]
        public void Parses(string text, int seconds) => Assert.Equal(TimeSpan.FromSeconds(seconds), TimerService.ParseDuration(text));

        [Theory]
        [InlineData("")]
        [InlineData("abc")]
        [InlineData("0")]
        [InlineData("1:90")]
        [InlineData("1:30:90")]
        [InlineData("24:00:00")]
        [InlineData("999999999999999999999:00")]
        [InlineData("5 bananas")]
        public void Rejects(string text) => Assert.Null(TimerService.ParseDuration(text));
    }

    public class BrowserDownloadCompletionTests
    {
        [Fact]
        public void QuickDownloadThatFinishedBetweenPollsStillNotifies() =>
            Assert.True(BrowserDownloadService.ShouldNotifyCompletion(false,
                new DateTime(2026, 11, 6, 12, 0, 2), new DateTime(2026, 11, 6, 12, 0, 0)));

        [Fact]
        public void OldHistoryDoesNotLookLikeANewCompletion() =>
            Assert.False(BrowserDownloadService.ShouldNotifyCompletion(false,
                new DateTime(2026, 11, 6, 11, 59, 59), new DateTime(2026, 11, 6, 12, 0, 0)));

        [Fact]
        public void PreviouslyObservedDownloadNotifiesEvenWithoutEndTimestamp() =>
            Assert.True(BrowserDownloadService.ShouldNotifyCompletion(true, DateTime.MinValue,
                new DateTime(2026, 11, 6, 12, 0, 0)));
    }
}

namespace Isle.Tests
{
    /// <summary>Разбор живой сводки Steam — на настоящей сводке из журнала пользователя (загрузка «112 Operator»).</summary>
    public class SteamOverviewTests
    {
        private const string Real = @"{""remote_client_id"":""0"",""update_appid"":793460,""update_is_upload"":false,""update_network_bytes_per_second"":13705941,""update_peak_network_bytes_per_second"":21849331,""update_disc_bytes_per_second"":13218838,""update_state"":""Downloading"",""update_state_flags"":97,""progress"":[{""bytes_in_progress"":0,""bytes_total"":0,""estimated_time_remaining_sec"":0},{""bytes_in_progress"":3747178867,""bytes_total"":3747178867,""estimated_time_remaining_sec"":0},{""bytes_in_progress"":1032054096,""bytes_total"":1502001728,""estimated_time_remaining_sec"":37},{""bytes_in_progress"":1619544710,""bytes_total"":3747178867,""estimated_time_remaining_sec"":169},{""bytes_in_progress"":0,""bytes_total"":0,""estimated_time_remaining_sec"":0},{""bytes_in_progress"":0,""bytes_total"":0,""estimated_time_remaining_sec"":0},{""bytes_in_progress"":0,""bytes_total"":3747178867,""estimated_time_remaining_sec"":0}],""overall_percent_complete"":43,""overall_estimated_time_remaining_sec"":169,""update_start_time"":1794151722,""update_is_install"":true,""update_is_workshop"":false,""update_is_shader"":false,""update_is_prefetch_estimate"":false,""paused"":false,""throttling_suspended"":false,""lan_peer_hostname"":"""",""download_throttle_rate"":0,""restrict_auto_updates"":false,""restrict_auto_updates_start"":0,""restrict_auto_updates_end"":0}";

        [Fact]
        public void ParsesRealSteamOverview()
        {
            var o = SteamClientApi.Parse(Real);
            Assert.NotNull(o);
            Assert.Equal(793460, o.AppId);
            Assert.Equal("Downloading", o.State);
            Assert.False(o.Paused);
            Assert.True(o.IsInstall);
            Assert.Equal(1032054096, o.Downloaded);   // progress[2] — загрузка данных
            Assert.Equal(1502001728, o.ToDownload);
            Assert.Equal(1619544710, o.Written);      // progress[3] — запись на диск
            Assert.Equal(3747178867, o.ToWrite);
            Assert.Equal(43, o.OverallPercent);       // общий процент Steam
            Assert.Equal(169, o.SecondsRemaining);    // общий остаток Steam
            Assert.Equal(13705941, o.NetworkBytesPerSecond);
        }

        [Fact]
        public void SkipsDownloadsOfAnotherComputer()
        {
            var remote = Real.Replace("\"remote_client_id\":\"0\"", "\"remote_client_id\":\"76561198000000001\"");
            Assert.NotEqual(Real, remote);
            Assert.Null(SteamClientApi.Parse(remote));
        }

        [Fact]
        public void IdleOverviewHasNoApp()
        {
            var o = SteamClientApi.Parse("{\"remote_client_id\":\"0\",\"update_state\":\"None\",\"update_appid\":0,\"paused\":true,\"overall_percent_complete\":0,\"overall_estimated_time_remaining_sec\":-1}");
            Assert.NotNull(o);
            Assert.Equal(0, o.AppId);
            Assert.True(o.Paused);
        }
    }
}

namespace Isle.Tests
{
    public class WifiTests
    {
        private const string NetshRu = @"
Имя интерфейса : Беспроводная сеть
В данный момент видно 3 сети.

SSID 1 : HomeNet
    Тип сети                : Инфраструктура
    Проверка подлинности    : WPA2-Personal
    Шифрование              : CCMP
    BSSID 1                 : aa:bb:cc:dd:ee:01
         Сигнал             : 62%
    BSSID 2                 : aa:bb:cc:dd:ee:02
         Сигнал             : 91%

SSID 2 : Кафе & <Гости>
    Тип сети                : Инфраструктура
    Проверка подлинности    : Открытая
    Шифрование              : Нет
    BSSID 1                 : aa:bb:cc:dd:ee:03
         Сигнал             : 40%

SSID 3 : Office
    Проверка подлинности    : WPA2-Enterprise
    Шифрование              : CCMP
    BSSID 1                 : aa:bb:cc:dd:ee:04
         Сигнал             : 20%
";

        [Fact]
        public void NetworksParsedWithBestSignalAndSecurity()
        {
            var nets = SystemControls.ParseNetworks(NetshRu);
            Assert.Equal(3, nets.Count);
            Assert.Equal("HomeNet", nets[0].Ssid);
            Assert.Equal(91, nets[0].Signal);
            Assert.True(nets[0].Secured);
            Assert.Equal("WPA2-Personal", nets[0].Auth);
            Assert.True(nets[0].PasswordSupported);
            var cafe = nets.Single(n => n.Ssid == "Кафе & <Гости>");
            Assert.False(cafe.Secured);
            var office = nets.Single(n => n.Ssid == "Office");
            Assert.False(office.PasswordSupported);
        }

        [Fact]
        public void ProfileXmlEscapesNameAndPassword()
        {
            var net = new SystemControls.WifiNet { Ssid = "A&B <x>", Secured = true, Auth = "WPA2-Personal", Cipher = "CCMP" };
            var xml = SystemControls.BuildProfileXml(net, "p&ss<word>");
            var doc = System.Xml.Linq.XDocument.Parse(xml); // корректный XML
            System.Xml.Linq.XNamespace ns = "http://www.microsoft.com/networking/WLAN/profile/v1";
            Assert.Equal("A&B <x>", doc.Root.Element(ns + "name").Value);
            Assert.Equal("WPA2PSK", doc.Descendants(ns + "authentication").Single().Value);
            Assert.Equal("AES", doc.Descendants(ns + "encryption").Single().Value);
            Assert.Equal("p&ss<word>", doc.Descendants(ns + "keyMaterial").Single().Value);
            Assert.Equal(Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes("A&B <x>")), doc.Descendants(ns + "hex").Single().Value);
            Assert.Equal("WPA3SAE", SystemControls.ProfileAuth("WPA3-Personal"));
            Assert.Null(SystemControls.ProfileAuth("WPA2-Enterprise"));
            Assert.Null(SystemControls.BuildProfileXml(new SystemControls.WifiNet { Ssid = "x", Secured = true, Auth = "WPA2-Enterprise" }, "12345678"));
        }
    }
}

namespace Isle.Tests
{
    public class LiveMosaicTests
    {
        [Fact]
        public void MovingFrameRestoresPixelsOutsideAndCancelRestoresAll()
        {
            int w = 64, h = 32;
            var px = new byte[w * h * 4];
            for (int i = 0; i < px.Length; i += 4) { px[i] = (byte)(i % 251); px[i + 1] = (byte)(i % 199); px[i + 2] = (byte)(i % 97); px[i + 3] = 255; }
            var bmp = new System.Windows.Media.Imaging.WriteableBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null);
            bmp.WritePixels(new System.Windows.Int32Rect(0, 0, w, h), px, w * 4, 0);
            byte[] Now() { var b = new byte[w * h * 4]; bmp.CopyPixels(b, w * 4, 0); return b; }

            var live = new LiveMosaic(bmp);
            live.Show(live.Clamp(0, 0, 40, 20));
            Assert.NotEqual(px, Now());
            // рамка уехала вправо — левая часть снова исходная
            live.Show(live.Clamp(40, 0, 24, 32));
            var now = Now();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < 40; x++)
                    Assert.Equal(px[(y * w + x) * 4], now[(y * w + x) * 4]);
            live.Cancel();
            Assert.Equal(px, Now());
        }
    }
}
