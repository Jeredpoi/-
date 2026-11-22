using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Isle.Services
{
    /// <summary>Состояние плеера Spotify из Web API.</summary>
    public sealed class SpotifyPlayer
    {
        public string Repeat = "off";   // off | context | track
        public bool Shuffle;
        public int? Volume;
        public bool Playing;
        public string DeviceName = "";
    }

    /// <summary>
    /// Привязка Spotify к Isle: вход через браузер по OAuth 2.0 с PKCE (без секрета приложения — пароль Spotify Isle
    /// не видит), ключ обновления хранится зашифрованным Windows (DPAPI), токены в журнал не пишутся.
    /// Нужны Spotify Premium и своё приложение на developer.spotify.com (Client ID; адрес возврата — RedirectUri).
    /// Даёт то, чего нет у Windows: повтор (трек / плейлист), перемешивание.
    /// </summary>
    public static class SpotifyService
    {
        public const int Port = 43871;
        public static readonly string RedirectUri = "http://127.0.0.1:" + Port + "/callback";
        private const string Scopes = "user-read-playback-state user-modify-playback-state user-read-currently-playing";
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

        private static string _access;
        private static DateTime _accessUntil;
        private static readonly SemaphoreSlim Gate = new(1, 1);

        public static bool Connected => !string.IsNullOrEmpty(AppSettings.Current.SpotifyClientId) && !string.IsNullOrEmpty(RefreshToken);
        public static event Action Changed;

        private static string RefreshToken
        {
            get
            {
                var p = AppSettings.Current.SpotifyTokenProtected;
                if (string.IsNullOrEmpty(p)) return "";
                try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(p), null, DataProtectionScope.CurrentUser)); }
                catch { return ""; }
            }
            set
            {
                AppSettings.Current.SpotifyTokenProtected = string.IsNullOrEmpty(value) ? ""
                    : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
                AppSettings.Current.Save();
            }
        }

        private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        /// <summary>Вход: открывает браузер, ждёт возврата на 127.0.0.1 и получает ключи. null — успех, иначе текст ошибки.</summary>
        public static async Task<string> LoginAsync()
        {
            var clientId = AppSettings.Current.SpotifyClientId?.Trim();
            if (string.IsNullOrEmpty(clientId)) return "Сначала вставь Client ID";
            var verifier = B64Url(RandomNumberGenerator.GetBytes(48));
            var challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var state = B64Url(RandomNumberGenerator.GetBytes(12));
            TcpListener listener;
            try { listener = new TcpListener(IPAddress.Loopback, Port); listener.Start(); }
            catch (Exception ex) { Journal.Warn("spotify", "порт для входа занят: " + ex.Message); return "Порт " + Port + " занят другой программой"; }
            try
            {
                var url = "https://accounts.spotify.com/authorize?response_type=code&client_id=" + Uri.EscapeDataString(clientId)
                    + "&scope=" + Uri.EscapeDataString(Scopes) + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri)
                    + "&code_challenge_method=S256&code_challenge=" + challenge + "&state=" + state;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                Journal.Info("spotify", "вход: открыт браузер, жду возврата");

                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
                string code = null, error = null;
                while (code == null && error == null)
                {
                    using var client = await listener.AcceptTcpClientAsync(cts.Token);
                    var stream = client.GetStream();
                    var buf = new byte[8192];
                    int n = await stream.ReadAsync(buf, cts.Token);
                    var head = Encoding.ASCII.GetString(buf, 0, n);
                    var first = head.Split("\r\n")[0].Split(' ');
                    if (first.Length < 2 || !first[1].StartsWith("/callback")) { await Reply(stream, 404, "Not found"); continue; }
                    var q = first[1].Contains('?') ? first[1][(first[1].IndexOf('?') + 1)..] : "";
                    var args = new Dictionary<string, string>();
                    foreach (var kv in q.Split('&')) { int e = kv.IndexOf('='); if (e > 0) args[kv[..e]] = Uri.UnescapeDataString(kv[(e + 1)..]); }
                    if (!args.TryGetValue("state", out var st) || st != state) { await Reply(stream, 400, Page("Что-то пошло не так", "Попробуй войти ещё раз из настроек Isle.")); error = "Ответ Spotify не совпал — попробуй ещё раз"; break; }
                    if (args.TryGetValue("error", out var err)) { await Reply(stream, 200, Page("Вход отменён", "Можно закрыть вкладку.")); error = err == "access_denied" ? "Вход отменён" : "Spotify: " + err; break; }
                    args.TryGetValue("code", out code);
                    await Reply(stream, 200, Page("Spotify подключён к Isle", "Можно закрыть эту вкладку и вернуться к Isle."));
                }
                if (error != null) { Journal.Info("spotify", "вход не удался: " + error); return error; }

                var tokenErr = await TokenAsync(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = RedirectUri,
                    ["client_id"] = clientId, ["code_verifier"] = verifier,
                });
                if (tokenErr != null) return tokenErr;
                Journal.Info("spotify", "подключён");
                Changed?.Invoke();
                return null;
            }
            catch (OperationCanceledException) { return "Не дождался входа — попробуй ещё раз"; }
            catch (Exception ex) { Journal.Warn("spotify", "вход: " + ex.Message); return "Не получилось: " + ex.Message; }
            finally { try { listener.Stop(); } catch { } }
        }

        public static void Logout()
        {
            _access = null; RefreshToken = "";
            Journal.Info("spotify", "отключён");
            Changed?.Invoke();
        }

        private static string Page(string title, string text) =>
            "<!doctype html><meta charset=utf-8><title>Isle</title><body style=\"background:#000;color:#ECECF1;font:16px system-ui;display:grid;place-items:center;height:100vh;margin:0\">"
            + "<div style=\"text-align:center\"><h2>" + title + "</h2><p style=\"color:#8B8B95\">" + text + "</p></div>";

        private static async Task Reply(NetworkStream s, int code, string html)
        {
            var body = Encoding.UTF8.GetBytes(html);
            var head = "HTTP/1.1 " + code + " OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n";
            await s.WriteAsync(Encoding.ASCII.GetBytes(head));
            await s.WriteAsync(body);
        }

        /// <summary>Запрос ключей (по коду входа или по ключу обновления). null — успех.</summary>
        private static async Task<string> TokenAsync(Dictionary<string, string> form)
        {
            try
            {
                using var resp = await Http.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(form));
                var json = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    string why = "";
                    try { using var d = JsonDocument.Parse(json); why = d.RootElement.TryGetProperty("error_description", out var e) ? e.GetString() : d.RootElement.GetProperty("error").GetString(); } catch { }
                    Journal.Warn("spotify", "ключи не получены: " + (int)resp.StatusCode + " " + why);
                    if (form["grant_type"] == "refresh_token" && (int)resp.StatusCode == 400) { RefreshToken = ""; Changed?.Invoke(); }
                    return "Spotify отказал: " + (string.IsNullOrEmpty(why) ? ((int)resp.StatusCode).ToString() : why);
                }
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                _access = r.GetProperty("access_token").GetString();
                _accessUntil = DateTime.UtcNow.AddSeconds(r.GetProperty("expires_in").GetInt32() - 60);
                if (r.TryGetProperty("refresh_token", out var rt) && !string.IsNullOrEmpty(rt.GetString())) RefreshToken = rt.GetString(); // Spotify может выдать новый
                return null;
            }
            catch (Exception ex) { Journal.Warn("spotify", "ключи: " + ex.Message); return "Нет связи со Spotify"; }
        }

        private static async Task<string> AccessAsync()
        {
            if (!Connected) return null;
            if (_access != null && DateTime.UtcNow < _accessUntil) return _access;
            await Gate.WaitAsync();
            try
            {
                if (_access != null && DateTime.UtcNow < _accessUntil) return _access;
                var err = await TokenAsync(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token", ["refresh_token"] = RefreshToken, ["client_id"] = AppSettings.Current.SpotifyClientId.Trim(),
                });
                return err == null ? _access : null;
            }
            finally { Gate.Release(); }
        }

        private static async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path)
        {
            var token = await AccessAsync();
            if (token == null) return null;
            var req = new HttpRequestMessage(method, "https://api.spotify.com/v1" + path);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            if (method == HttpMethod.Put) req.Content = new StringContent("");
            try { return await Http.SendAsync(req); }
            catch (Exception ex) { Journal.Debug("spotify", path + ": " + ex.Message); return null; }
        }

        /// <summary>Что сейчас в плеере Spotify. null — не подключён, нет активного устройства или ошибка.</summary>
        public static async Task<SpotifyPlayer> PlayerAsync()
        {
            using var resp = await SendAsync(HttpMethod.Get, "/me/player");
            if (resp == null || resp.StatusCode == HttpStatusCode.NoContent || !resp.IsSuccessStatusCode)
            {
                if (resp != null && resp.StatusCode != HttpStatusCode.NoContent) Journal.Debug("spotify", "состояние плеера: " + (int)resp.StatusCode);
                return null;
            }
            try
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var r = doc.RootElement;
                var p = new SpotifyPlayer
                {
                    Repeat = r.TryGetProperty("repeat_state", out var rs) ? rs.GetString() : "off",
                    Shuffle = r.TryGetProperty("shuffle_state", out var ss) && ss.GetBoolean(),
                    Playing = r.TryGetProperty("is_playing", out var ip) && ip.GetBoolean(),
                };
                if (r.TryGetProperty("device", out var dev))
                {
                    if (dev.TryGetProperty("volume_percent", out var v) && v.ValueKind == JsonValueKind.Number) p.Volume = v.GetInt32();
                    if (dev.TryGetProperty("name", out var nm)) p.DeviceName = nm.GetString() ?? "";
                }
                return p;
            }
            catch { return null; }
        }

        /// <summary>Повтор: off → track → context → off. Возвращает новое состояние или null, если не вышло.</summary>
        public static async Task<string> CycleRepeatAsync(string current)
        {
            var next = current switch { "off" => "track", "track" => "context", _ => "off" };
            using var resp = await SendAsync(HttpMethod.Put, "/me/player/repeat?state=" + next);
            if (resp == null || !resp.IsSuccessStatusCode) { Journal.Info("spotify", "повтор не переключился: " + (resp == null ? "нет связи" : ((int)resp.StatusCode).ToString())); return null; }
            Journal.Info("spotify", "повтор: " + next);
            return next;
        }

        public static async Task<bool?> SetShuffleAsync(bool on)
        {
            using var resp = await SendAsync(HttpMethod.Put, "/me/player/shuffle?state=" + (on ? "true" : "false"));
            if (resp == null || !resp.IsSuccessStatusCode) { Journal.Info("spotify", "перемешивание не переключилось: " + (resp == null ? "нет связи" : ((int)resp.StatusCode).ToString())); return null; }
            Journal.Info("spotify", "перемешивание: " + (on ? "вкл" : "выкл"));
            return on;
        }
    }
}
