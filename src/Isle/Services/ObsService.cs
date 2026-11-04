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
        public static bool IsObsRunning() =>
            Process.GetProcessesByName("obs64").Any() || Process.GetProcessesByName("obs32").Any() || Process.GetProcessesByName("obs").Any();

        /// <summary>Включает/выключает запись. Возвращает true, если запись теперь идёт.</summary>
        public static async Task<bool> ToggleRecordAsync()
        {
            var d = await RequestAsync("ToggleRecord");
            return d.TryGetProperty("outputActive", out var oa) && oa.GetBoolean();
        }

        /// <summary>Проверка подключения: возвращает версию OBS.</summary>
        public static async Task<string> TestAsync()
        {
            try
            {
                var d = await RequestAsync("GetVersion");
                return "Подключено: OBS " + (d.TryGetProperty("obsVersion", out var v) ? v.GetString() : "");
            }
            catch (Exception ex) { return "Ошибка: " + ex.Message; }
        }

        private static async Task<JsonElement> RequestAsync(string requestType)
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
            await SendAsync(ws, new { op = 6, d = new { requestType, requestId = "isle-1" } }, cts.Token);
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
