using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Isle.Services
{
    /// <summary>
    /// «Точная скорость Steam»: окно Steam — встроенный браузер, и в нём у Steam есть функция
    /// SteamClient.Downloads.RegisterForDownloadOverview — та самая сводка, из которой Steam рисует свою
    /// страницу загрузок (скорость сети, скачано / всего, остаток). Чтобы до неё достучаться, Steam
    /// должен быть запущен с файлом .cef-enable-remote-debugging в своей папке — тогда на 127.0.0.1:8080
    /// открывается отладчик встроенного браузера. Isle создаёт этот файл сам и сам перезапускает Steam,
    /// когда он ничего не качает и игра не запущена. Если что-то не так — Isle молча считает по-старому.
    /// </summary>
    public static class SteamClientApi
    {
        /// <summary>
        /// Сводка загрузки Steam — поля как их на самом деле присылает Steam (проверено по журналу):
        /// progress[2] — загрузка данных (сжатые, «X из Y» в Steam), progress[3] — запись на диск,
        /// progress[6] — установка; overall_percent_complete и overall_estimated_time_remaining_sec — общий
        /// процент и остаток, которые Steam показывает у себя.
        /// </summary>
        public sealed record Overview(
            int AppId, string State, bool Paused, bool IsInstall,
            long Downloaded, long ToDownload,        // загрузка данных
            long Written, long ToWrite,              // запись на диск
            long Installed, long ToInstall,          // установка
            double NetworkBytesPerSecond, double DiskBytesPerSecond,
            double OverallPercent, double SecondsRemaining);

        private const string FlagFile = ".cef-enable-remote-debugging";
        /// <summary>
        /// Свой порт отладчика, чтобы не мешать программам на 8080. Ключ -devtools-port у Steam не документирован:
        /// если Steam его не понимает, он открывает отладчик на 8080 — Isle проверяет оба порта и берёт тот, где отвечает Steam.
        /// </summary>
        private const int OwnPort = 47852;
        private static readonly int[] Ports = { OwnPort, 8080 };
        private static string _host = "http://127.0.0.1:" + OwnPort;
        private static string DebugHost => _host;

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(1.5) };
        private static ClientWebSocket _ws;
        private static int _id;
        private static Task _live;
        private static CancellationTokenSource _liveCts;
        private static bool _restartLoop;

        /// <summary>Последняя сводка (null — недоступно: выключено, Steam закрыт или ещё не перезапущен).</summary>
        public static Overview Last { get; private set; }
        /// <summary>Новая сводка от Steam (null — связь со Steam потеряна). Приходит сразу, как Steam её прислал.</summary>
        public static event Action<Overview> Updated;
        /// <summary>Isle сейчас показывает загрузку Steam — перезапускать Steam нельзя (ставит MainWindow).</summary>
        public static volatile bool DownloadActive;
        public static DateTime LastAt { get; private set; }

        // ===================== включение =====================

        /// <summary>
        /// Включает или выключает. Включение: создать файл в папке Steam и, если Steam уже запущен
        /// без отладчика, тихо перезапустить его, как только это безопасно (не идёт игра и загрузка).
        /// </summary>
        public static void Apply(bool enabled)
        {
            var root = SteamDownloadService.SteamRoot();
            Diag.Write("Isle " + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version + ": точная скорость Steam " + (enabled ? "включена" : "выключена")
                + ", папка Steam: " + (root ?? "не найдена"));
            if (root == null) return;
            var flag = Path.Combine(root, FlagFile);
            try
            {
                if (!enabled)
                {
                    if (File.Exists(flag)) File.Delete(flag);
                    PatchAutostart(false);
                    PatchShortcuts(false);
                    StopLive();
                    return;
                }
                if (!File.Exists(flag)) File.WriteAllText(flag, ""); // Linux/Steam Deck; на Windows нужен ключ запуска (ниже)
                PatchAutostart(true);
                PatchShortcuts(true); // ярлыки Steam (рабочий стол, «Пуск», панель задач) — тоже с ключом
            }
            catch (Exception ex) { Log("точная скорость Steam: не удалось записать файл: " + ex.Message); return; }
            if (!_restartLoop) { _restartLoop = true; _ = Task.Run(() => RestartWhenSafeAsync(root)); }
            StartLive();
        }

        /// <summary>
        /// На Windows доступ к данным загрузок Steam включается только ключом запуска -cef-enable-debugging.
        /// Пока Isle работает, он следит раз в 30 с: Steam запущен без ключа (с ярлыка, вручную) — перезапускает его
        /// с ключом. При старте Isle — сразу, позже — только когда началась загрузка (чтобы не дёргать Steam зря),
        /// и не чаще одного раза на один запуск Steam. Пока идёт игра — никогда.
        /// </summary>
        private static async Task RestartWhenSafeAsync(string root)
        {
            bool first = true;
            DateTime handledStart = DateTime.MinValue;
            string lastNote = null;
            void Note(string text) { if (text != lastNote) { lastNote = text; Log("точная скорость Steam: " + text); } }
            try
            {
                while (AppSettings.Current.SteamExactSpeed)
                {
                    var started = SteamStartTime();
                    if (started == null) Note("Steam закрыт");
                    else if (await DebuggerUpAsync()) Note("работает");
                    else if (started.Value == handledStart) Note("Steam уже перезапускался, но данные недоступны");
                    else if (RunningAppId() is int game && game != 0) Note("идёт игра " + game + " — перезапущу Steam после неё");
                    else if (first || DownloadActive)
                    {
                        handledStart = started.Value;
                        await RestartSteamAsync(root);
                        handledStart = SteamStartTime() ?? handledStart; // новый процесс Steam тоже не перезапускаем повторно, если не помогло
                    }
                    else Note("Steam запущен без доступа к загрузкам — перезапущу, когда начнётся загрузка");
                    first = false;
                    await Task.Delay(TimeSpan.FromSeconds(30));
                }
            }
            catch (Exception ex) { Log("точная скорость Steam: перезапуск не удался: " + ex.Message); }
            finally { _restartLoop = false; }
        }

        /// <summary>Время запуска процесса Steam (null — Steam не запущен).</summary>
        private static DateTime? SteamStartTime()
        {
            var list = Process.GetProcessesByName("steam");
            try { return list.Length == 0 ? null : list.Min(p => { try { return p.StartTime; } catch { return DateTime.MinValue; } }); }
            finally { foreach (var p in list) p.Dispose(); }
        }

        private const string DebugArg = "-cef-enable-debugging";
        private static readonly string LaunchArgs = DebugArg + " -devtools-port " + OwnPort;
        private static readonly System.Text.RegularExpressions.Regex OurArgs =
            new(@"\s*-cef-enable-debugging|\s*-devtools-port\s+\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>Аргументы ярлыка/автозапуска: наши ключи убираются и (если add) добавляются заново — без дублей и старых портов.</summary>
        private static string WithOurArgs(string args, bool add)
        {
            var clean = OurArgs.Replace(args ?? "", "").Trim();
            return add ? (clean + " " + LaunchArgs).Trim() : clean;
        }

        /// <summary>
        /// Ярлыки Steam на рабочем столе, в меню «Пуск» и на панели задач: дописываем ключ в «Объект» ярлыка,
        /// чтобы Steam, запущенный как обычно, сразу отдавал данные загрузок и перезапускать его не приходилось.
        /// Общие ярлыки (для всех пользователей) без прав администратора не изменить — их просто пропускаем.
        /// </summary>
        private static void PatchShortcuts(bool add)
        {
            var app = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dirs = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                Path.Combine(app, @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"),
                Path.Combine(app, @"Microsoft\Internet Explorer\Quick Launch\User Pinned\StartMenu"),
            };
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            dynamic shell = null;
            try
            {
                shell = Activator.CreateInstance(shellType);
                foreach (var dir in dirs)
                {
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                    IEnumerable<string> links;
                    try { links = Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories).ToList(); }
                    catch { continue; }
                    foreach (var link in links)
                    {
                        try
                        {
                            dynamic sc = shell.CreateShortcut(link);
                            string target = sc.TargetPath ?? "";
                            if (!Path.GetFileName(target).Equals("steam.exe", StringComparison.OrdinalIgnoreCase)) continue;
                            string args = sc.Arguments ?? "";
                            string updated = WithOurArgs(args, add);
                            if (updated == args.Trim()) continue;
                            sc.Arguments = updated;
                            sc.Save();
                            Log("точная скорость Steam: ярлык " + (add ? "дополнен" : "возвращён") + ": " + link);
                        }
                        catch { } // общий ярлык без прав администратора — пропускаем
                    }
                }
            }
            catch (Exception ex) { Log("точная скорость Steam: ярлыки: " + ex.Message); }
            finally { if (shell != null) try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); } catch { } }
        }

        /// <summary>Автозапуск Steam вместе с Windows — с ключом, чтобы данные загрузок были доступны сразу.</summary>
        private static void PatchAutostart(bool add)
        {
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (run?.GetValue("Steam") is not string cmd || cmd.Length == 0) return;
                string updated = WithOurArgs(cmd, add);
                if (updated != cmd.Trim()) run.SetValue("Steam", updated);
            }
            catch (Exception ex) { Log("точная скорость Steam: автозапуск: " + ex.Message); }
        }

        private static bool SteamRunning()
        {
            var list = Process.GetProcessesByName("steam");
            bool any = list.Length > 0;
            foreach (var p in list) p.Dispose();
            return any;
        }

        /// <summary>Идёт ли игра (Steam пишет её номер в реестр, 0 — ничего не запущено).</summary>
        private static int RunningAppId()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (key?.GetValue("RunningAppID") is not int id || id == 0) return 0;
                // RunningAppID Steam иногда не стирает после выхода из игры — проверяем флаг Running у самой игры
                using var app = key.OpenSubKey(@"Apps\" + id);
                return app?.GetValue("Running") is int running && running != 0 ? id : 0;
            }
            catch { return 0; }
        }

        private static readonly SemaphoreSlim RestartGate = new(1, 1);

        private static async Task RestartSteamAsync(string root)
        {
            if (!await RestartGate.WaitAsync(0)) return; // уже перезапускаем
            try { await RestartSteamCoreAsync(root); }
            finally { RestartGate.Release(); }
        }

        private static async Task RestartSteamCoreAsync(string root)
        {
            var exe = Path.Combine(root, "steam.exe");
            if (!File.Exists(exe)) return;
            Log("точная скорость Steam: перезапускаю Steam");
            Notify(DownloadActive ? "Steam перезапустится на минуту — загрузка продолжится сама" : "Steam перезапустится на минуту, свёрнутым в трей");
            Process.Start(new ProcessStartInfo(exe, "-shutdown") { UseShellExecute = false })?.Dispose();
            for (int i = 0; i < 60 && SteamRunning(); i++) await Task.Delay(1000); // Steam закрывается до минуты
            if (SteamRunning()) { Log("точная скорость Steam: Steam не закрылся за минуту — не трогаю"); return; }
            await Task.Delay(1500);
            Process.Start(new ProcessStartInfo(exe, "-silent " + LaunchArgs) { UseShellExecute = false })?.Dispose(); // в трей, без окна
            for (int i = 0; i < 90; i++) // ждём, пока Steam поднимется
            {
                await Task.Delay(2000);
                if (await DebuggerUpAsync()) { Log("точная скорость Steam: включена (" + DebugHost + ")"); return; }
            }
            Log("точная скорость Steam: Steam запустился, но не отвечает ни на порту " + OwnPort + ", ни на 8080");
        }

        /// <summary>Отвечает ли встроенный браузер Steam (точная скорость работает).</summary>
        public static Task<bool> IsWorkingAsync() => DebuggerUpAsync();

        /// <summary>
        /// Кнопка «Включить точную скорость»: ключ в ярлыки и автозапуск, Steam закрывается полностью
        /// (вместе с играми и программами из Steam; не закрылся за 20 с — процессы завершаются),
        /// запускается с ключом, и Isle ждёт, пока данные пойдут.
        /// </summary>
        public static async Task<bool> ActivateAsync(IProgress<string> report)
        {
            var root = SteamDownloadService.SteamRoot();
            var exe = root == null ? null : Path.Combine(root, "steam.exe");
            if (exe == null || !File.Exists(exe)) { report.Report("Steam не найден"); return false; }
            AppSettings.Current.SteamExactSpeed = true;
            await Task.Run(() => Apply(true));
            if (await DebuggerUpAsync()) { report.Report("Готово — точная скорость уже работает"); return true; }

            if (!await RestartGate.WaitAsync(0)) { report.Report("Steam уже перезапускается — подожди минуту"); return false; }
            try
            {
                if (SteamRunning())
                {
                    report.Report("Закрываю Steam…");
                    Process.Start(new ProcessStartInfo(exe, "-shutdown") { UseShellExecute = false })?.Dispose();
                    for (int i = 0; i < 20 && SteamRunning(); i++) await Task.Delay(1000);
                    if (SteamRunning())
                    {
                        report.Report("Steam не закрылся сам — завершаю его процессы…");
                        await Task.Run(KillSteam);
                        for (int i = 0; i < 10 && SteamRunning(); i++) await Task.Delay(1000);
                    }
                    if (SteamRunning()) { report.Report("Не получилось закрыть Steam — закрой его вручную (трей → «Выход») и нажми ещё раз"); return false; }
                    await Task.Delay(1500);
                }
                report.Report("Запускаю Steam с точной скоростью…");
                Process.Start(new ProcessStartInfo(exe, "-silent " + LaunchArgs) { UseShellExecute = false })?.Dispose();
                for (int i = 0; i < 60; i++)
                {
                    await Task.Delay(2000);
                    if (await DebuggerUpAsync())
                    {
                        Log("точная скорость Steam: включена кнопкой");
                        report.Report("Готово — скорость, объём и время теперь берутся прямо из Steam");
                        return true;
                    }
                }
                report.Report("Steam запустился, но данные не отвечают. Нажми «Показать журнал» и пришли файл steam-download.log");
                Log("точная скорость Steam: после запуска с ключом Steam не отвечает ни на порту " + OwnPort + ", ни на 8080");
                return false;
            }
            catch (Exception ex) { report.Report("Ошибка: " + ex.Message); return false; }
            finally { RestartGate.Release(); }
        }

        /// <summary>Завершить все процессы Steam (если сам не закрылся).</summary>
        private static void KillSteam()
        {
            foreach (var name in new[] { "steam", "steamwebhelper", "steamservice", "gameoverlayui" })
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try { p.Kill(true); } catch { }
                    finally { p.Dispose(); }
                }
        }

        /// <summary>Перезапустить Steam сейчас (кнопка в Настройках) — если не идёт игра.</summary>
        public static async Task<string> RestartNowAsync()
        {
            var root = SteamDownloadService.SteamRoot();
            if (root == null) return "Steam не найден";
            try { var flag = Path.Combine(root, FlagFile); if (!File.Exists(flag)) File.WriteAllText(flag, ""); }
            catch (Exception ex) { return "Не удалось записать в папку Steam: " + ex.Message; }
            if (await DebuggerUpAsync()) return "Уже работает";
            if (RunningAppId() != 0) return "Сначала закрой игру";
            if (!SteamRunning())
            {
                var exe = Path.Combine(root, "steam.exe");
                if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe, "-silent " + LaunchArgs) { UseShellExecute = false })?.Dispose();
                return "Запускаю Steam…";
            }
            _ = Task.Run(() => RestartSteamAsync(root));
            return "Перезапускаю Steam…";
        }

        private static void Notify(string title)
        {
            try
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                    ToastBus.Show(new Toast { Title = title, Subtitle = "Точная скорость загрузок", Icon = Icons.Download, ReplaceKey = "steam-exact", Duration = TimeSpan.FromSeconds(3) })));
            }
            catch { }
        }

        private static async Task<bool> DebuggerUpAsync()
        {
            // свой порт или 8080 — где отвечает именно встроенный браузер Steam, а не другая программа
            foreach (var port in Ports)
            {
                try
                {
                    var host = "http://127.0.0.1:" + port;
                    var text = await Http.GetStringAsync(host + "/json/version");
                    if (text.Contains("Steam", StringComparison.OrdinalIgnoreCase) || text.Contains("Valve", StringComparison.OrdinalIgnoreCase))
                    {
                        _host = host;
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        // ===================== живая сводка =====================

        // Подписка внутри Steam: каждую новую сводку загрузки Steam сам передаёт в Isle через «привязку» isleDl.
        private const string SubscribeJs =
            "(()=>{try{if(window.__isleDl&&window.__isleDl.unregister)window.__isleDl.unregister();}catch(e){}" +
            "window.__isleDl=SteamClient.Downloads.RegisterForDownloadOverview(o=>{try{isleDl(JSON.stringify(o))}catch(e){}});" +
            "return 'подписка оформлена';})()";
        private const string ProbeJs =
            "(typeof SteamClient!=='undefined'&&!!SteamClient.Downloads&&typeof SteamClient.Downloads.RegisterForDownloadOverview==='function')";

        public static void StartLive()
        {
            // уже слушаем (и не остановлены) — второй раз не запускаем; остановленный цикл доработает сам, а мы стартуем новый
            if (_live != null && !_live.IsCompleted && _liveCts != null && !_liveCts.IsCancellationRequested) return;
            _liveCts = new CancellationTokenSource();
            var token = _liveCts.Token;
            _live = Task.Run(() => LiveLoopAsync(token));
        }

        private static void StopLive()
        {
            try { _liveCts?.Cancel(); } catch { }
            CloseSocket();
            Last = null;
            Updated?.Invoke(null);
        }

        private static async Task LiveLoopAsync(CancellationToken token)
        {
            Diag.Write("=== запуск живой сводки Steam ===");
            string lastProblem = null;
            while (!token.IsCancellationRequested && AppSettings.Current.SteamExactSpeed)
            {
                string problem;
                try { problem = await ConnectAndListenAsync(token); }
                catch (Exception ex) { problem = "обрыв: " + ex.GetType().Name + ": " + ex.Message; }
                CloseSocket();
                if (Last != null) { Last = null; Updated?.Invoke(null); }
                if (problem != lastProblem) { Diag.Write(problem); lastProblem = problem; } // одну и ту же причину не повторяем каждые 5 с
                try { await Task.Delay(5000, token); } catch { }
            }
            Diag.Write("=== живая сводка остановлена ===");
        }

        /// <summary>Подключиться к Steam и слушать сводку. Возвращает причину, по которой связь закончилась.</summary>
        private static async Task<string> ConnectAndListenAsync(CancellationToken token)
        {
            // 1) отвечает ли встроенный браузер Steam
            if (!await DebuggerUpAsync()) return "Steam не отвечает ни на порту " + OwnPort + ", ни на 8080 — Steam закрыт или запущен без ключа " + DebugArg;
            string version = await Http.GetStringAsync(DebugHost + "/json/version", token);
            Diag.Write("отладчик Steam отвечает на " + DebugHost + ": " + OneLine(version, 300));

            // 2) список страниц
            string listText = await Http.GetStringAsync(DebugHost + "/json", token);
            var pages = new System.Collections.Generic.List<(string title, string url, string ws)>();
            using (var doc = JsonDocument.Parse(listText))
                foreach (var t in doc.RootElement.EnumerateArray())
                {
                    string title = t.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "";
                    string url = t.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                    string ws = t.TryGetProperty("webSocketDebuggerUrl", out var w) ? w.GetString() : null;
                    pages.Add((title, url, ws));
                }
            Diag.Write("страниц в Steam: " + pages.Count + " — " + string.Join(" | ", pages.Select(p => "«" + p.title + "» " + OneLine(p.url, 80) + (p.ws == null ? " (без отладки)" : ""))));
            var ordered = pages.Where(p => p.ws != null)
                .OrderByDescending(p => p.title.Contains("SharedJSContext", StringComparison.OrdinalIgnoreCase) || p.title.Contains("Shared Context", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (ordered.Count == 0) return "у Steam нет страниц с отладкой";

            // 3) страница, где есть SteamClient.Downloads
            foreach (var page in ordered)
            {
                token.ThrowIfCancellationRequested();
                CloseSocket();
                try
                {
                    var ws = new ClientWebSocket();
                    using (var cts = CancellationTokenSource.CreateLinkedTokenSource(token)) { cts.CancelAfter(3000); await ws.ConnectAsync(new Uri(page.ws), cts.Token); }
                    _ws = ws;
                    var probe = await CallAsync("Runtime.evaluate", new { expression = ProbeJs, returnByValue = true }, token);
                    bool ok = probe.TryGetProperty("result", out var r1) && r1.TryGetProperty("value", out var v1) && v1.ValueKind == JsonValueKind.True;
                    Diag.Write("страница «" + page.title + "»: SteamClient.Downloads " + (ok ? "есть" : "нет"));
                    if (!ok) continue;

                    // 4) подписка
                    await CallAsync("Runtime.enable", new { }, token);
                    await CallAsync("Runtime.addBinding", new { name = "isleDl" }, token);
                    var sub = await CallAsync("Runtime.evaluate", new { expression = SubscribeJs, returnByValue = true }, token);
                    Diag.Write("подписка на сводку загрузки: " + OneLine(sub.ToString(), 300));

                    // 5) слушаем; тишина 30 минут или обрыв — просто переподключаемся
                    try
                    {
                        while (!token.IsCancellationRequested)
                        {
                            using var msg = await ReceiveAsync(token, TimeSpan.FromMinutes(30));
                            HandleEvent(msg.RootElement);
                        }
                        return "остановлено";
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { return "остановлено"; }
                    catch (OperationCanceledException) { return "Steam долго молчал — переподключаюсь"; }
                    catch (Exception ex) { return "связь со Steam оборвалась (" + ex.Message + ") — переподключаюсь"; }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return "остановлено"; }
                catch (Exception ex) { Diag.Write("страница «" + page.title + "»: ошибка " + ex.GetType().Name + ": " + ex.Message); }
            }
            return "ни на одной странице Steam нет SteamClient.Downloads (или подписка оборвалась)";
        }

        private static DateTime _lastRawLog;
        private static string _lastRaw;

        /// <summary>Событие от Steam: если это наша сводка — разбираем и сразу отдаём окну.</summary>
        private static void HandleEvent(JsonElement root)
        {
            if (!root.TryGetProperty("method", out var m) || m.GetString() != "Runtime.bindingCalled") return;
            var p = root.GetProperty("params");
            if (p.GetProperty("name").GetString() != "isleDl") return;
            var json = p.GetProperty("payload").GetString();
            var parsed = Parse(json);
            if (parsed == null) return; // загрузка на другом компьютере
            Last = parsed;
            LastAt = DateTime.Now;
            // в журнал: короткая выжимка + сырая сводка без «history» (история скорости — 18 КБ каждый раз)
            if (json != _lastRaw && DateTime.Now - _lastRawLog >= TimeSpan.FromSeconds(1))
            {
                var l = Last;
                Diag.Write($"Steam: app {l.AppId} «{l.State}» paused={l.Paused} install={l.IsInstall} {l.OverallPercent}% осталось {l.SecondsRemaining} с | " +
                           $"сеть {l.NetworkBytesPerSecond / 1048576:0.0} МБ/с, диск {l.DiskBytesPerSecond / 1048576:0.0} МБ/с | " +
                           $"загрузка {l.Downloaded / 1048576} / {l.ToDownload / 1048576} МБ, запись {l.Written / 1048576} / {l.ToWrite / 1048576} МБ, установка {l.Installed / 1048576} / {l.ToInstall / 1048576} МБ");
                Diag.Write("Steam (сырая): " + WithoutHistory(json));
                _lastRaw = json;
                _lastRawLog = DateTime.Now;
            }
            Updated?.Invoke(Last);
        }

        /// <summary>Запрос к странице Steam; пока ждём ответ, события сводки тоже обрабатываются.</summary>
        private static async Task<JsonElement> CallAsync(string method, object prms, CancellationToken token)
        {
            int id = Interlocked.Increment(ref _id);
            var payload = JsonSerializer.Serialize(new { id, method, @params = prms });
            await _ws.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, token);
            for (int i = 0; i < 500; i++)
            {
                using var msg = await ReceiveAsync(token, TimeSpan.FromSeconds(5));
                var root = msg.RootElement;
                if (root.TryGetProperty("id", out var mid) && mid.GetInt32() == id)
                {
                    if (root.TryGetProperty("error", out var err)) throw new Exception(method + ": " + err.ToString());
                    var result = root.TryGetProperty("result", out var res) ? res.Clone() : default;
                    if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("exceptionDetails", out var ex))
                        throw new Exception(method + ": исключение в Steam: " + OneLine(ex.ToString(), 400));
                    return result;
                }
                HandleEvent(root);
            }
            throw new Exception(method + ": нет ответа");
        }

        private static async Task<JsonDocument> ReceiveAsync(CancellationToken token, TimeSpan timeout)
        {
            var buf = new byte[64 * 1024];
            var sb = new StringBuilder();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(timeout);
            WebSocketReceiveResult r;
            do
            {
                r = await _ws.ReceiveAsync(buf, cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) throw new Exception("Steam закрыл подключение");
                sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
            } while (!r.EndOfMessage);
            return JsonDocument.Parse(sb.ToString());
        }

        /// <summary>Строка и в общий журнал запуска, и в подробный журнал загрузок.</summary>
        private static void Log(string text) { App.Trace(text); Diag.Write(text); }

        private static string OneLine(string s, int max)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ");
            return s.Length > max ? s.Substring(0, max) + "…" : s;
        }

        private static void CloseSocket()
        {
            try { _ws?.Abort(); _ws?.Dispose(); } catch { }
            _ws = null;
        }

        /// <summary>Разбор сводки Steam (null — сводка о загрузке на другом компьютере).</summary>
        internal static Overview Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var o = doc.RootElement;
            // сводка о загрузке на ДРУГОМ компьютере (удалённое управление загрузками Steam) — не наша
            if (o.TryGetProperty("remote_client_id", out var rc) && rc.ToString() != "0" && rc.ToString() != "") return null;
            var dl = Prog(o, 2); var wr = Prog(o, 3); var inst = Prog(o, 6);
            // старые версии Steam присылали отдельные поля update_bytes_* — берём их, если массива progress нет
            if (dl.total == 0 && Long(o, "update_bytes_to_download") > 0) dl = (Long(o, "update_bytes_downloaded"), Long(o, "update_bytes_to_download"), 0);
            if (wr.total == 0 && Long(o, "update_bytes_to_stage") > 0) wr = (Long(o, "update_bytes_staged"), Long(o, "update_bytes_to_stage"), 0);
            double eta = o.TryGetProperty("overall_estimated_time_remaining_sec", out _) ? Num(o, "overall_estimated_time_remaining_sec") : Num(o, "update_seconds_remaining");
            return new Overview(
                Int(o, "update_appid"), Str(o, "update_state"), Bool(o, "paused"), Bool(o, "update_is_install"),
                dl.got, dl.total, wr.got, wr.total, inst.got, inst.total,
                Num(o, "update_network_bytes_per_second"), Num(o, "update_disc_bytes_per_second"),
                Num(o, "overall_percent_complete"), eta);
        }

        /// <summary>Этап из массива progress: сколько сделано, сколько всего, сколько секунд осталось.</summary>
        private static (long got, long total, double eta) Prog(JsonElement o, int index)
        {
            if (!o.TryGetProperty("progress", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() <= index) return (0, 0, 0);
            var e = arr[index];
            return (Long(e, "bytes_in_progress"), Long(e, "bytes_total"), Num(e, "estimated_time_remaining_sec"));
        }

        private static string WithoutHistory(string json)
        {
            try
            {
                if (System.Text.Json.Nodes.JsonNode.Parse(json) is System.Text.Json.Nodes.JsonObject obj)
                {
                    obj.Remove("history");
                    return obj.ToJsonString();
                }
            }
            catch { }
            return json;
        }

        private static long Long(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (long)v.GetDouble() : 0;
        private static double Num(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        private static int Int(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : 0;
        private static bool Bool(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        private static string Str(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
    }
}
