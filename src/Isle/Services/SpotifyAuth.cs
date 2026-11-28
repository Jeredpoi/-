using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Isle.Services
{
    /// <summary>
    /// Необязательная привязка Spotify (OAuth PKCE, без секрета): id играющего трека и токен для Spicy Lyrics,
    /// а с правом user-modify-playback-state — ещё и управление музыкой через Web API (SpotifyControl).
    /// Токены лежат в настройках зашифрованными (DPAPI), в журнал не пишутся.
    /// </summary>
    public static class SpotifyAuth
    {
        public const int Port = 47533;
        public const string RedirectUri = "http://127.0.0.1:47533/spotify-callback";
        private const string Scopes = "user-read-currently-playing user-read-playback-state " + ControlScope;
        /// <summary>Право управлять воспроизведением (пауза, перемотка, громкость…). У старых привязок его нет.</summary>
        public const string ControlScope = "user-modify-playback-state";

        private static readonly HttpClient Http = Helpers.Net.Client(TimeSpan.FromSeconds(15));
        private static readonly SemaphoreSlim RefreshLock = new(1, 1);
        private static CancellationTokenSource _linkCts;

        /// <summary>Привязка изменилась (привязали, отвязали, токен отозван).</summary>
        public static event Action Changed;

        public static bool IsLinked
        {
            get
            {
                var s = AppSettings.Current;
                return !string.IsNullOrWhiteSpace(s.SpotifyClientId) && !string.IsNullOrEmpty(s.SpotifyRefreshProtected);
            }
        }

        public static bool IsLinking => _linkCts != null;

        /// <summary>
        /// Токен выдан с правом управлять Spotify. Привязки, сделанные до появления этого права, его не имеют —
        /// для них кнопки работают по-старому (через Windows), а в настройках просим перепривязать.
        /// </summary>
        public static bool HasControlScope
        {
            get
            {
                if (!IsLinked) return false;
                var sc = AppSettings.Current.SpotifyScopes ?? "";
                return Array.IndexOf(sc.Split(' ', StringSplitOptions.RemoveEmptyEntries), ControlScope) >= 0;
            }
        }

        /// <summary>Музыка играет в приложении Spotify (а не в браузере или другом плеере).</summary>
        public static bool IsSpotifyApp(string appId) => !string.IsNullOrEmpty(appId) && appId.IndexOf("Spotify", StringComparison.OrdinalIgnoreCase) >= 0;

        // ---------- привязка ----------

        /// <summary>
        /// Открыть вход Spotify в браузере и дождаться ответа на 127.0.0.1:47533. Возвращает null при успехе
        /// или текст ошибки для пользователя.
        /// </summary>
        public static async Task<string> LinkAsync(string clientId)
        {
            clientId = (clientId ?? "").Trim();
            if (!Regex.IsMatch(clientId, "^[0-9a-zA-Z]{20,64}$")) return "Вставь Client ID из своего приложения на developer.spotify.com";
            CancelLink();
            var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            _linkCts = cts;
            TcpListener listener = null;
            try
            {
                var verifier = B64Url(RandomNumberGenerator.GetBytes(64));
                var challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
                var state = B64Url(RandomNumberGenerator.GetBytes(16));

                // обычный TCP вместо HttpListener: HttpListener на 127.0.0.1 без прав администратора не стартует
                listener = new TcpListener(IPAddress.Loopback, Port);
                try { listener.Start(); }
                catch (SocketException) { return "Порт " + Port + " занят другой программой — закрой её и попробуй ещё раз"; }

                var url = "https://accounts.spotify.com/authorize?response_type=code"
                          + "&client_id=" + Uri.EscapeDataString(clientId)
                          + "&scope=" + Uri.EscapeDataString(Scopes)
                          + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri)
                          + "&code_challenge_method=S256&code_challenge=" + challenge
                          + "&state=" + state;
                Journal.Info("spotify", "привязка: открываю вход Spotify в браузере");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });

                var (code, error) = await WaitForCodeAsync(listener, state, cts.Token);
                if (code == null)
                {
                    Journal.Warn("spotify", "привязка не удалась: " + (error ?? "нет кода"));
                    return error == "access_denied" ? "Вход в Spotify отменён" : "Spotify не вернул код входа (" + (error ?? "нет ответа") + ")";
                }

                var form = new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = RedirectUri,
                    ["client_id"] = clientId,
                    ["code_verifier"] = verifier,
                };
                using var resp = await Http.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(form), cts.Token);
                Journal.Info("spotify", "обмен кода на токен: " + (int)resp.StatusCode);
                if (!resp.IsSuccessStatusCode) return "Spotify не выдал токен (" + (int)resp.StatusCode + "). Проверь Client ID и Redirect URI";
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
                var s = AppSettings.Current;
                s.SpotifyClientId = clientId;
                SaveTokens(doc.RootElement);
                s.SpotifyUserName = await FetchNameAsync(s.SpotifyAccessToken) ?? "аккаунт Spotify";
                s.Save();
                Journal.Info("spotify", "привязано");
                Changed?.Invoke();
                return null;
            }
            catch (OperationCanceledException) { return cts.IsCancellationRequested ? "Не дождался ответа из браузера — попробуй ещё раз" : "Spotify не ответил вовремя"; }
            catch (Exception ex)
            {
                Journal.Warn("spotify", "привязка: " + ex.GetType().Name);
                return "Не получилось привязать: " + ex.Message;
            }
            finally
            {
                try { listener?.Stop(); } catch { }
                if (ReferenceEquals(_linkCts, cts)) _linkCts = null;
                cts.Dispose();
            }
        }

        public static void CancelLink()
        {
            try { _linkCts?.Cancel(); } catch { }
        }

        /// <summary>«Отвязать»: стираем токены и имя. Client ID оставляем — чтобы привязать заново в один клик.</summary>
        public static void Unlink()
        {
            CancelLink();
            var s = AppSettings.Current;
            s.SpotifyRefreshProtected = "";
            s.SpotifyAccessProtected = "";
            s.SpotifyTokenExpiry = default;
            s.SpotifyUserName = "";
            s.SpotifyScopes = "";
            s.Save();
            SpicyLyrics.Reset();
            Journal.Info("spotify", "отвязано");
            Changed?.Invoke();
        }

        /// <summary>Ждём, пока браузер вернётся на 127.0.0.1:47533/spotify-callback?code=…&amp;state=…</summary>
        private static async Task<(string code, string error)> WaitForCodeAsync(TcpListener listener, string state, CancellationToken ct)
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(ct);
                client.ReceiveTimeout = 5000;
                using var stream = client.GetStream();
                var buf = new byte[8192];
                int n = 0;
                // читаем только строку запроса и заголовки. Браузер открывает и запасные соединения, в которые ничего
                // не шлёт: на каждое — не больше 5 с, иначе настоящий ответ ждал бы в очереди до конца ожидания
                using (var perConn = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    perConn.CancelAfter(TimeSpan.FromSeconds(5));
                    try
                    {
                        while (n < buf.Length)
                        {
                            int r = await stream.ReadAsync(buf.AsMemory(n), perConn.Token);
                            if (r <= 0) break;
                            n += r;
                            if (Encoding.ASCII.GetString(buf, 0, n).Contains("\r\n\r\n")) break;
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { continue; } // молчащее соединение — к следующему
                }
                var head = Encoding.ASCII.GetString(buf, 0, n);
                var first = head.Split("\r\n")[0].Split(' ');
                string path = first.Length >= 2 ? first[1] : "";
                if (!path.StartsWith("/spotify-callback", StringComparison.Ordinal))
                {
                    await Reply(stream, "404 Not Found", "");
                    continue; // favicon и прочее
                }
                var q = ParseQuery(path);
                q.TryGetValue("code", out var code);
                q.TryGetValue("error", out var error);
                q.TryGetValue("state", out var st);
                if (st != state) { await Reply(stream, "400 Bad Request", Page("Ссылка устарела", "Вернись в Isle и нажми «Привязать Spotify» ещё раз.")); return (null, "state"); }
                if (string.IsNullOrEmpty(code)) { await Reply(stream, "200 OK", Page("Не получилось", "Spotify не дал доступ. Можно закрыть вкладку.")); return (null, error ?? "нет кода"); }
                await Reply(stream, "200 OK", Page("Готово, можно закрыть вкладку", "Spotify привязан к Isle."));
                return (code, null);
            }
        }

        private static async Task Reply(NetworkStream s, string status, string html)
        {
            var body = Encoding.UTF8.GetBytes(html);
            var head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
            try { await s.WriteAsync(head); await s.WriteAsync(body); await s.FlushAsync(); } catch { }
        }

        private static string Page(string title, string text) =>
            "<!doctype html><html lang=\"ru\"><meta charset=\"utf-8\"><title>Isle</title>"
            + "<body style=\"background:#000;color:#EDEDF2;font-family:Segoe UI,sans-serif;display:grid;place-items:center;height:100vh;margin:0\">"
            + "<div style=\"text-align:center\"><h2 style=\"font-weight:600\">" + WebUtility.HtmlEncode(title) + "</h2>"
            + "<p style=\"color:#8A8A96\">" + WebUtility.HtmlEncode(text) + "</p></div></body></html>";

        private static Dictionary<string, string> ParseQuery(string path)
        {
            var d = new Dictionary<string, string>();
            int i = path.IndexOf('?');
            if (i < 0) return d;
            foreach (var part in path.Substring(i + 1).Split('&'))
            {
                int e = part.IndexOf('=');
                if (e <= 0) continue;
                d[Uri.UnescapeDataString(part.Substring(0, e))] = Uri.UnescapeDataString(part.Substring(e + 1).Replace('+', ' ')); // «+» — пробел, до раскодирования %2B
            }
            return d;
        }

        // ---------- токены ----------

        private static void SaveTokens(JsonElement root)
        {
            var s = AppSettings.Current;
            s.SpotifyAccessToken = root.GetProperty("access_token").GetString();
            // при обновлении Spotify может не прислать новый refresh_token — тогда остаётся прежний
            if (root.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String && rt.GetString().Length > 0)
                s.SpotifyRefreshToken = rt.GetString();
            int sec = root.TryGetProperty("expires_in", out var ex) && ex.ValueKind == JsonValueKind.Number ? ex.GetInt32() : 3600;
            s.SpotifyTokenExpiry = DateTime.UtcNow.AddSeconds(Math.Max(60, sec - 60));
            // какие права реально выданы (при обновлении токена Spotify тоже присылает scope)
            if (root.TryGetProperty("scope", out var sc) && sc.ValueKind == JsonValueKind.String)
            {
                bool had = HasControlScope;
                s.SpotifyScopes = sc.GetString() ?? "";
                if (had != HasControlScope) Changed?.Invoke();
            }
        }

        /// <summary>Действующий токен доступа (обновляется сам, когда истёк или после 401). null — не привязан или не вышло.</summary>
        public static async Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken ct = default)
        {
            if (!IsLinked) return null;
            var s = AppSettings.Current;
            if (!forceRefresh && DateTime.UtcNow < s.SpotifyTokenExpiry)
            {
                var t = s.SpotifyAccessToken;
                if (!string.IsNullOrEmpty(t)) return t;
            }
            var stale = s.SpotifyAccessProtected;
            await RefreshLock.WaitAsync(ct);
            try
            {
                // пока ждали, токен уже обновил другой запрос
                if (s.SpotifyAccessProtected != stale && DateTime.UtcNow < s.SpotifyTokenExpiry) return s.SpotifyAccessToken;
                var refresh = s.SpotifyRefreshToken;
                if (string.IsNullOrEmpty(refresh)) return null;
                var form = new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refresh,
                    ["client_id"] = s.SpotifyClientId.Trim(),
                };
                using var resp = await Http.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(form), ct);
                Journal.Info("spotify", "обновление токена: " + (int)resp.StatusCode);
                if (resp.StatusCode == HttpStatusCode.BadRequest || resp.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // доступ отозван (или приложение удалено) — отвязываемся, дальше работает LRCLIB
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    if (body.Contains("invalid_grant") || body.Contains("invalid_client")) { Unlink(); return null; }
                }
                if (!resp.IsSuccessStatusCode) return null;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                SaveTokens(doc.RootElement);
                s.Save();
                return s.SpotifyAccessToken;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Journal.Warn("spotify", "обновление токена: " + ex.GetType().Name);
                return null;
            }
            finally { RefreshLock.Release(); }
        }

        private static async Task<string> FetchNameAsync(string token)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me");
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                using var resp = await Http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) return null;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var r = doc.RootElement;
                if (r.TryGetProperty("display_name", out var dn) && dn.ValueKind == JsonValueKind.String && dn.GetString().Length > 0) return dn.GetString();
                return r.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
            }
            catch { return null; }
        }

        // ---------- текущий трек ----------

        /// <summary>
        /// id трека, который сейчас играет в Spotify, — только если его название совпадает с тем, что прислал
        /// Windows (title). Иначе null: лучше LRCLIB, чем текст чужой песни.
        /// </summary>
        public static async Task<string> CurrentTrackIdAsync(string title, CancellationToken ct)
        {
            // Spotify Web API узнаёт о смене трека чуть позже Windows — одна повторная попытка через 1,5 с
            string lastName = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (attempt > 0) await Task.Delay(1500, ct);
                var (status, id, name) = await CurrentlyPlayingAsync(ct);
                if (status == 401)
                {
                    if (await GetAccessTokenAsync(true, ct) == null) return null;
                    (status, id, name) = await CurrentlyPlayingAsync(ct);
                }
                if (status == 429 || (status != 200 && status != 204)) { Journal.Info("spotify", "сейчас играет: " + status); return null; }
                if (status == 200 && id != null && LooseMatch(name, title)) return id;
                if (status == 200) lastName = name;
            }
            // названия песен в журнал писать можно — по ним видно, почему не совпало
            Journal.Info("spotify", "сейчас играет: трек Spotify не совпал с плеером Windows: Spotify «" + Journal.Short(lastName ?? "(ничего)", 100)
                                    + "», Windows «" + Journal.Short(title ?? "", 100) + "»");
            return null;
        }

        private static async Task<(int status, string id, string name)> CurrentlyPlayingAsync(CancellationToken ct)
        {
            var token = await GetAccessTokenAsync(false, ct);
            if (token == null) return (0, null, null);
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me/player/currently-playing");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var resp = await Http.SendAsync(req, ct);
            int code = (int)resp.StatusCode;
            if (code != 200) return (code, null, null);
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(json)) return (204, null, null);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) return (204, null, null);
            if (item.TryGetProperty("type", out var tp) && tp.GetString() != "track") return (204, null, null);
            if (item.TryGetProperty("is_local", out var loc) && loc.ValueKind == JsonValueKind.True) return (204, null, null);
            string id = item.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
            string name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : "";
            return (200, id, name);
        }

        /// <summary>
        /// Нестрогое сравнение названий: без регистра, диакритики (NFKD), скобок, «feat.», «- Remastered 2011»
        /// и прочих хвостов через дефис, знаков. Совпадает и когда одно название содержится в другом.
        /// </summary>
        public static bool LooseMatch(string a, string b)
        {
            string na = Norm(a), nb = Norm(b);
            if (na.Length == 0 || nb.Length == 0)
            {
                // название целиком в скобках или из одних знаков — сравниваем без чистки хвостов
                na = Letters(a); nb = Letters(b);
                return na.Length > 0 && na == nb;
            }
            if (na == nb) return true;
            // «содержит» — только если короткое название не совсем крошечное, иначе «Ты» совпало бы с чем угодно
            string shortS = na.Length <= nb.Length ? na : nb, longS = na.Length <= nb.Length ? nb : na;
            if (shortS.Length >= 4 || shortS.Length * 2 >= longS.Length)
                if (longS.Contains(shortS, StringComparison.Ordinal)) return true;
            // хвосты через дефис уже сняты; сравним ещё и полные строки без чистки (Windows мог отрезать скобки сам)
            string fa = Letters(a), fb = Letters(b);
            return fa.Length > 0 && fb.Length > 0 && (fa == fb || (Math.Min(fa.Length, fb.Length) >= 4 && (fa.Contains(fb, StringComparison.Ordinal) || fb.Contains(fa, StringComparison.Ordinal))));
        }

        private static readonly Regex FeatRx = new(@"\s(feat\.?|ft\.?|featuring|при\s+уч\.?|при\s+участии)\s.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex BracketsRx = new(@"[\(\[\{（【].*?[\)\]\}）】]", RegexOptions.Compiled);
        private static readonly Regex DashTailRx = new(@"\s[-–—]\s.*$", RegexOptions.Compiled);
        private static readonly Regex RemasterRx = new(@"\b(\d{4}\s+)?(remaster(ed)?|ремастер(инг)?|remix|ремикс|version|версия|live|mono|stereo|radio\s+edit|edit)(\s+\d{4})?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static string Norm(string s)
        {
            s = Fold(s);
            s = BracketsRx.Replace(s, " ");
            s = DashTailRx.Replace(s, " ");
            s = FeatRx.Replace(s, " ");
            s = RemasterRx.Replace(s, " ");
            return Letters(s, folded: true);
        }

        /// <summary>Нижний регистр, NFKD без диакритики, ё → е.</summary>
        private static string Fold(string s)
        {
            s = (s ?? "").Normalize(NormalizationForm.FormKD).ToLowerInvariant();
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
                if (cat == System.Globalization.UnicodeCategory.NonSpacingMark || cat == System.Globalization.UnicodeCategory.EnclosingMark) continue;
                sb.Append(c);
            }
            // NFKD раскладывает «ё» → «е» + знак, «й» → «и» + знак; знаки сняты — одинаково для обеих сторон
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        private static string Letters(string s, bool folded = false)
        {
            if (!folded) s = Fold(s);
            var sb = new StringBuilder();
            foreach (var c in s) if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
