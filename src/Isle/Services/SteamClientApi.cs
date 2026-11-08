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
        public sealed record Overview(
            int AppId, string State, bool Paused, bool IsInstall,
            long Downloaded, long ToDownload, long Staged, long ToStage,
            double NetworkBytesPerSecond, double DiskBytesPerSecond, double SecondsRemaining);

        private const string FlagFile = ".cef-enable-remote-debugging";
        private const string DebugHost = "http://127.0.0.1:8080";

        // Обещание, которое отдаёт одну свежую сводку и сразу отписывается.
        private const string OverviewJs =
            "(()=>new Promise(r=>{let h=null,done=false;" +
            "const fin=v=>{if(done)return;done=true;try{h&&h.unregister()}catch(e){}r(v)};" +
            "const t=setTimeout(()=>fin(null),1500);" +
            "try{h=SteamClient.Downloads.RegisterForDownloadOverview(o=>{clearTimeout(t);fin(JSON.stringify(o))});}catch(e){fin(null)}" +
            "if(done){try{h&&h.unregister()}catch(e){}}}))()";

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(1.5) };
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static ClientWebSocket _ws;
        private static int _id;
        private static DateTime _retryAt = DateTime.MinValue;
        private static bool _restartLoop;

        /// <summary>Последняя сводка (null — недоступно: выключено, Steam закрыт или ещё не перезапущен).</summary>
        public static Overview Last { get; private set; }
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
            if (root == null) return;
            var flag = Path.Combine(root, FlagFile);
            try
            {
                if (!enabled)
                {
                    if (File.Exists(flag)) File.Delete(flag);
                    PatchAutostart(false);
                    PatchShortcuts(false);
                    CloseSocket();
                    Last = null;
                    return;
                }
                if (!File.Exists(flag)) File.WriteAllText(flag, ""); // Linux/Steam Deck; на Windows нужен ключ запуска (ниже)
                PatchAutostart(true);
                PatchShortcuts(true); // ярлыки Steam (рабочий стол, «Пуск», панель задач) — тоже с ключом
            }
            catch (Exception ex) { App.Trace("точная скорость Steam: не удалось записать файл: " + ex.Message); return; }
            if (!_restartLoop) { _restartLoop = true; _ = Task.Run(() => RestartWhenSafeAsync(root)); }
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
            void Note(string text) { if (text != lastNote) { lastNote = text; App.Trace("точная скорость Steam: " + text); } }
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
            catch (Exception ex) { App.Trace("точная скорость Steam: перезапуск не удался: " + ex.Message); }
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
                            bool has = args.Contains(DebugArg, StringComparison.OrdinalIgnoreCase);
                            if (add == has) continue;
                            sc.Arguments = add ? (args + " " + DebugArg).Trim() : args.Replace(DebugArg, "").Trim();
                            sc.Save();
                            App.Trace("точная скорость Steam: ярлык " + (add ? "дополнен" : "возвращён") + ": " + link);
                        }
                        catch { } // общий ярлык без прав администратора — пропускаем
                    }
                }
            }
            catch (Exception ex) { App.Trace("точная скорость Steam: ярлыки: " + ex.Message); }
            finally { if (shell != null) try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); } catch { } }
        }

        /// <summary>Автозапуск Steam вместе с Windows — с ключом, чтобы данные загрузок были доступны сразу.</summary>
        private static void PatchAutostart(bool add)
        {
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (run?.GetValue("Steam") is not string cmd || cmd.Length == 0) return;
                bool has = cmd.Contains(DebugArg, StringComparison.OrdinalIgnoreCase);
                if (add && !has) run.SetValue("Steam", cmd.TrimEnd() + " " + DebugArg);
                else if (!add && has) run.SetValue("Steam", cmd.Replace(" " + DebugArg, "").Replace(DebugArg, "").TrimEnd());
            }
            catch (Exception ex) { App.Trace("точная скорость Steam: автозапуск: " + ex.Message); }
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
            App.Trace("точная скорость Steam: перезапускаю Steam");
            Notify(DownloadActive ? "Steam перезапустится на минуту — загрузка продолжится сама" : "Steam перезапустится на минуту, свёрнутым в трей");
            Process.Start(new ProcessStartInfo(exe, "-shutdown") { UseShellExecute = false })?.Dispose();
            for (int i = 0; i < 60 && SteamRunning(); i++) await Task.Delay(1000); // Steam закрывается до минуты
            if (SteamRunning()) { App.Trace("точная скорость Steam: Steam не закрылся за минуту — не трогаю"); return; }
            await Task.Delay(1500);
            Process.Start(new ProcessStartInfo(exe, "-silent " + DebugArg) { UseShellExecute = false })?.Dispose(); // в трей, без окна
            for (int i = 0; i < 90; i++) // ждём, пока Steam поднимется
            {
                await Task.Delay(2000);
                if (await DebuggerUpAsync()) { App.Trace("точная скорость Steam: включена"); Notify("Точная скорость Steam включена"); return; }
            }
            App.Trace("точная скорость Steam: Steam запустился, но отладчик на 127.0.0.1:8080 не отвечает");
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
                Process.Start(new ProcessStartInfo(exe, "-silent " + DebugArg) { UseShellExecute = false })?.Dispose();
                for (int i = 0; i < 60; i++)
                {
                    await Task.Delay(2000);
                    if (await DebuggerUpAsync())
                    {
                        App.Trace("точная скорость Steam: включена кнопкой");
                        report.Report("Готово — скорость, объём и время теперь берутся прямо из Steam");
                        return true;
                    }
                }
                report.Report("Steam запустился, но данные не отвечают. Пришли файл isle-trace.log");
                App.Trace("точная скорость Steam: после запуска с ключом отладчик на 127.0.0.1:8080 не отвечает");
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
                if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe, "-silent " + DebugArg) { UseShellExecute = false })?.Dispose();
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
            // отвечает именно встроенный браузер Steam, а не что-то другое на порту 8080
            try
            {
                var text = await Http.GetStringAsync(DebugHost + "/json/version");
                return text.Contains("Steam", StringComparison.OrdinalIgnoreCase) || text.Contains("Valve", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // ===================== чтение =====================

        /// <summary>Свежая сводка загрузки из самого Steam или null.</summary>
        public static async Task<Overview> ReadAsync()
        {
            if (!AppSettings.Current.SteamExactSpeed || DateTime.Now < _retryAt) return null;
            if (!await Gate.WaitAsync(0)) return Last;
            try
            {
                if (_ws == null || _ws.State != WebSocketState.Open)
                {
                    CloseSocket();
                    var url = await FindContextAsync();
                    if (url == null) { _retryAt = DateTime.Now.AddSeconds(15); return null; }
                    var ws = new ClientWebSocket();
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await ws.ConnectAsync(new Uri(url), cts.Token);
                    _ws = ws;
                }
                var json = await EvaluateAsync(OverviewJs);
                if (json == null) return null;
                using var doc = JsonDocument.Parse(json);
                var o = doc.RootElement;
                Last = new Overview(
                    Int(o, "update_appid"), Str(o, "update_state"), Bool(o, "paused"), Bool(o, "update_is_install"),
                    Long(o, "update_bytes_downloaded"), Long(o, "update_bytes_to_download"),
                    Long(o, "update_bytes_staged"), Long(o, "update_bytes_to_stage"),
                    Num(o, "update_network_bytes_per_second"), Num(o, "update_disc_bytes_per_second"), Num(o, "update_seconds_remaining"));
                LastAt = DateTime.Now;
                return Last;
            }
            catch (Exception ex)
            {
                App.Trace("точная скорость Steam: " + ex.Message);
                CloseSocket();
                _retryAt = DateTime.Now.AddSeconds(10);
                return null;
            }
            finally { Gate.Release(); }
        }

        /// <summary>Страница «SharedJSContext» встроенного браузера Steam — там доступен объект SteamClient.</summary>
        private static async Task<string> FindContextAsync()
        {
            try
            {
                var text = await Http.GetStringAsync(DebugHost + "/json");
                using var doc = JsonDocument.Parse(text);
                foreach (var t in doc.RootElement.EnumerateArray())
                {
                    var title = t.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "";
                    if ((title.Contains("SharedJSContext", StringComparison.OrdinalIgnoreCase) || title.Contains("Shared Context", StringComparison.OrdinalIgnoreCase))
                        && t.TryGetProperty("webSocketDebuggerUrl", out var ws))
                        return ws.GetString();
                }
            }
            catch { }
            return null;
        }

        private static async Task<string> EvaluateAsync(string expression)
        {
            int id = Interlocked.Increment(ref _id);
            var payload = JsonSerializer.Serialize(new
            {
                id, method = "Runtime.evaluate",
                @params = new { expression, awaitPromise = true, returnByValue = true }
            });
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _ws.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, cts.Token);
            var buf = new byte[64 * 1024];
            for (int i = 0; i < 50; i++)
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(buf, cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) throw new Exception("Steam закрыл подключение");
                    sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                } while (!r.EndOfMessage);
                using var msg = JsonDocument.Parse(sb.ToString());
                if (!msg.RootElement.TryGetProperty("id", out var mid) || mid.GetInt32() != id) continue; // чужие события
                if (msg.RootElement.TryGetProperty("result", out var res) && res.TryGetProperty("result", out var val)
                    && val.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
                return null;
            }
            return null;
        }

        private static void CloseSocket()
        {
            try { _ws?.Abort(); _ws?.Dispose(); } catch { }
            _ws = null;
        }

        private static long Long(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (long)v.GetDouble() : 0;
        private static double Num(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        private static int Int(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : 0;
        private static bool Bool(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        private static string Str(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
    }
}
