using System;
using System.Diagnostics;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Isle.Services
{
    /// <summary>
    /// Управление записью в OBS через встроенный obs-websocket (OBS 28+, протокол v5).
    /// В OBS: Инструменты → Настройки сервера WebSocket → «Включить сервер».
    /// </summary>
    public static class ObsService
    {
        public static bool IsObsRunning()
        {
            foreach (var name in new[] { "obs64", "obs32", "obs" })
            {
                var found = Process.GetProcessesByName(name);
                bool any = found.Length > 0;
                foreach (var p in found) p.Dispose(); // процессы — системные ресурсы, не оставляем их висеть
                if (any) return true;
            }
            return false;
        }

        /// <summary>Включает/выключает запись. Возвращает, идёт ли запись теперь, и путь к сохранённому файлу (при остановке).</summary>
        public static async Task<(bool on, string file)> ToggleRecordAsync()
        {
            var st = await RequestAsync("GetRecordStatus");
            bool active = st.TryGetProperty("outputActive", out var oa) && oa.GetBoolean();
            if (!active)
            {
                // папка из настроек Isle — OBS сам кладёт запись туда, куда указано у него (у кого-то это рабочий стол)
                var folder = AppSettings.Current.ObsRecordFolder;
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    try
                    {
                        System.IO.Directory.CreateDirectory(folder);
                        await RequestAsync("SetRecordDirectory", new { recordDirectory = folder });
                        Journal.Info("obs", "папка записей: " + folder);
                    }
                    catch (Exception ex) { Journal.Warn("obs", "не удалось задать папку записей: " + ex.Message); }
                }
                await RequestAsync("StartRecord");
                return (true, null);
            }
            // StopRecord отдаёт путь к готовому файлу — по нему кнопка «папка» откроет Проводник с выделенным видео
            var stop = await RequestAsync("StopRecord");
            string file = stop.TryGetProperty("outputPath", out var op) ? op.GetString() : null;
            return (false, file ?? await LastRecordingAsync());
        }

        /// <summary>Самый свежий файл в папке записей OBS.</summary>
        public static async Task<string> LastRecordingAsync()
        {
            try
            {
                var d = await RequestAsync("GetRecordDirectory");
                var dir = d.TryGetProperty("recordDirectory", out var rd) ? rd.GetString() : null;
                if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                    return new System.IO.DirectoryInfo(dir).GetFiles()
                        .Where(f => f.Extension.ToLowerInvariant() is ".mkv" or ".mp4" or ".mov" or ".flv" or ".ts" or ".m3u8" or ".fmp4" or ".hybrid_mp4" or ".webm")
                        .OrderByDescending(f => f.LastWriteTime).FirstOrDefault()?.FullName;
            }
            catch { }
            return null;
        }

        /// <summary>Всплывашка «Запись сохранена» с кнопкой папки (Проводник откроется с выделенным файлом).</summary>
        public static Toast SavedToast(string file) => new()
        {
            Kind = ToastKind.Success, Title = "Запись сохранена",
            Subtitle = file != null ? System.IO.Path.GetFileName(file) : "В папке записей OBS",
            // путь из OBS — с прямыми слешами; приводим к виду Windows, иначе Проводник открывал рабочий стол
            FilePath = file != null ? System.IO.Path.GetFullPath(file.Replace('/', '\\')) : null,
            FolderPath = file != null ? System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(file.Replace('/', '\\'))) : null,
            Icon = Icons.Record, ReplaceKey = "obs-saved", Important = true, Duration = TimeSpan.FromSeconds(7)
        };

        private static DateTime _savedShownAt;
        private static string _savedFile;
        public static void ShowSaved(string file)
        {
            // кнопка и слежение за OBS не дублируют друг друга; но точный путь важнее догадки — его показываем
            bool recent = DateTime.Now - _savedShownAt < TimeSpan.FromSeconds(10);
            if (recent && (file == null || file == _savedFile)) return;
            _savedShownAt = DateTime.Now;
            _savedFile = file;
            ToastBus.Show(SavedToast(file));
        }

        public static bool Recording { get; private set; }
        public static bool Streaming { get; private set; }
        public static event Action Changed;
        private static readonly System.Windows.Threading.DispatcherTimer Poll = new() { Interval = TimeSpan.FromSeconds(3) };
        private static bool _polling;

        /// <summary>Следит за OBS: горит REC/LIVE, а сохранённая запись приходит карточкой.</summary>
        public static void StartMonitor()
        {
            Poll.Tick += async (_, __) =>
            {
                if (_polling) return;
                _polling = true;
                try { await PollOnce(); } finally { _polling = false; }
            };
            Poll.Start();
        }

        private static async Task PollOnce()
        {
            bool rec = false, live = false, known = false;
            // список процессов — не в потоке интерфейса (раз в 3 с он подтормаживал анимацию)
            if (AppSettings.Current.ObsStatus && await Task.Run(IsObsRunning))
            {
                try
                {
                    var r = await RequestAsync("GetRecordStatus");
                    rec = r.TryGetProperty("outputActive", out var a) && a.GetBoolean();
                    var st = await RequestAsync("GetStreamStatus");
                    live = st.TryGetProperty("outputActive", out var b) && b.GetBoolean();
                    known = true;
                }
                catch { return; }
            }
            bool stoppedRec = Recording && !rec;
            if (rec == Recording && live == Streaming) return;
            Recording = rec; Streaming = live;
            Changed?.Invoke();
            // «Запись сохранена» — только если OBS сам сказал, что запись остановлена (а не упал / закрыт / слежение выключено)
            if (stoppedRec && known) ShowSaved(await LastRecordingAsync());
        }

        /// <summary>Проверка подключения: возвращает версию OBS.</summary>
        public static async Task<string> TestAsync()
        {
            try
            {
                var d = await RequestAsync("GetVersion");
                var text = "Подключено: OBS " + (d.TryGetProperty("obsVersion", out var v) ? v.GetString() : "");
                // куда лягут записи: своя папка из Настроек Isle (её Isle выставит в OBS при старте записи)
                // или та, что сейчас задана в самом OBS
                string dir = AppSettings.Current.ObsRecordFolder;
                if (string.IsNullOrWhiteSpace(dir))
                {
                    try
                    {
                        var rd = await RequestAsync("GetRecordDirectory");
                        dir = rd.TryGetProperty("recordDirectory", out var r) ? r.GetString() : null;
                    }
                    catch { }
                }
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    try { dir = System.IO.Path.GetFullPath(dir.Replace('/', '\\')); } catch { }
                    text += "\nЗаписи сохраняются в: " + dir;
                }
                return text;
            }
            catch (Exception ex) { return "Ошибка: " + ex.Message; }
        }

        private static async Task<JsonElement> RequestAsync(string requestType, object requestData = null)
        {
            var s = AppSettings.Current;
            using var ws = new ClientWebSocket();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            await ws.ConnectAsync(new Uri("ws://127.0.0.1:" + s.ObsPort), cts.Token);

            // 1. Hello (op 0) — может содержать вызов на пароль
            using var hello = JsonDocument.Parse(await ReceiveAsync(ws, cts.Token));
            var d = hello.RootElement.GetProperty("d");
            string auth = null;
            if (d.TryGetProperty("authentication", out var a))
            {
                var pass = s.ObsPassword;
                if (string.IsNullOrEmpty(pass)) throw new Exception("в OBS включён пароль — впиши его в Настройки → Запись экрана");
                var salt = a.GetProperty("salt").GetString();
                var challenge = a.GetProperty("challenge").GetString();
                var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(pass + salt)));
                auth = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
            }

            // 2. Identify (op 1) → Identified (op 2)
            await SendAsync(ws, auth == null
                ? new { op = 1, d = new { rpcVersion = 1 } }
                : (object)new { op = 1, d = new { rpcVersion = 1, authentication = auth } }, cts.Token);
            using (var identified = JsonDocument.Parse(await ReceiveAsync(ws, cts.Token)))
                if (identified.RootElement.GetProperty("op").GetInt32() != 2) throw new Exception("OBS не принял подключение (неверный пароль?)");

            // 3. ToggleRecord (op 6) → ответ (op 7)
            await SendAsync(ws, requestData == null
                ? new { op = 6, d = new { requestType, requestId = "isle-1" } }
                : (object)new { op = 6, d = new { requestType, requestId = "isle-1", requestData } }, cts.Token);
            for (int i = 0; i < 10; i++)
            {
                using var msg = JsonDocument.Parse(await ReceiveAsync(ws, cts.Token));
                if (msg.RootElement.GetProperty("op").GetInt32() != 7) continue;
                var rd = msg.RootElement.GetProperty("d");
                var status = rd.GetProperty("requestStatus");
                if (!status.GetProperty("result").GetBoolean())
                    throw new Exception(status.TryGetProperty("comment", out var c) ? c.GetString() : "OBS отказал");
                var result = rd.TryGetProperty("responseData", out var resp) ? resp.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
                return result;
            }
            throw new Exception("OBS не ответил");
        }

        private static async Task SendAsync(ClientWebSocket ws, object payload, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }

        private static async Task<string> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
        {
            var buf = new byte[16384];
            var sb = new StringBuilder();
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buf, ct);
                if (r.MessageType == WebSocketMessageType.Close) throw new Exception("OBS закрыл подключение");
                sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
            } while (!r.EndOfMessage);
            return sb.ToString();
        }
    }
}
