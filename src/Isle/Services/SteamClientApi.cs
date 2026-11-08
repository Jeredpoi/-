using System;
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
                    CloseSocket();
                    Last = null;
                    return;
                }
                if (!File.Exists(flag)) File.WriteAllText(flag, "");
            }
            catch (Exception ex) { App.Trace("точная скорость Steam: не удалось записать файл: " + ex.Message); return; }
            if (!_restartLoop) { _restartLoop = true; _ = Task.Run(() => RestartWhenSafeAsync(root)); }
        }

        /// <summary>Steam запущен, но отладчик не отвечает — значит, он стартовал до появления файла. Перезапускаем, когда можно.</summary>
        private static async Task RestartWhenSafeAsync(string root)
        {
            try
            {
                for (int i = 0; i < 720; i++) // до 6 часов, проверка раз в 30 с
                {
                    if (!AppSettings.Current.SteamExactSpeed) return;
                    if (!SteamRunning()) return;                 // Steam закрыт — при следующем запуске файл уже подействует
                    if (await DebuggerUpAsync()) return;         // уже работает
                    if (RunningAppId() == 0 && !DownloadActive)
                    {
                        await RestartSteamAsync(root);
                        return;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(30));
                }
            }
            catch (Exception ex) { App.Trace("точная скорость Steam: перезапуск не удался: " + ex.Message); }
            finally { _restartLoop = false; }
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
                return key?.GetValue("RunningAppID") is int id ? id : 0;
            }
            catch { return 0; }
        }

        private static async Task RestartSteamAsync(string root)
        {
            var exe = Path.Combine(root, "steam.exe");
            if (!File.Exists(exe)) return;
            App.Trace("точная скорость Steam: перезапускаю Steam");
            Process.Start(new ProcessStartInfo(exe, "-shutdown") { UseShellExecute = false })?.Dispose();
            for (int i = 0; i < 60 && SteamRunning(); i++) await Task.Delay(1000); // Steam закрывается до минуты
            if (SteamRunning()) return; // не закрылся — не трогаем
            await Task.Delay(1500);
            Process.Start(new ProcessStartInfo(exe, "-silent") { UseShellExecute = false })?.Dispose(); // в трей, без окна
        }

        private static async Task<bool> DebuggerUpAsync()
        {
            try { return (await Http.GetAsync(DebugHost + "/json/version")).IsSuccessStatusCode; }
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
