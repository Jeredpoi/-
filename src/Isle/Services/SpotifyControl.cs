using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Isle.Services
{
    /// <summary>
    /// Управление музыкой через Spotify Web API — когда Spotify привязан с правом user-modify-playback-state
    /// и играет именно приложение Spotify. Так работают повтор, перемешивание и собственная громкость Spotify,
    /// которые Windows снаружи переключать не даёт.
    /// Любой отказ (403 PREMIUM_REQUIRED у бесплатного аккаунта, нет устройства, сбой сети) — молча возвращаемся
    /// к способу через Windows и до перезапуска (или новой привязки) Web API для управления больше не трогаем.
    /// В журнал — только статусы, без токенов.
    /// </summary>
    public static class SpotifyControl
    {
        private const string Api = "https://api.spotify.com/v1/me/player";
        private static readonly HttpClient Http = Helpers.Net.Client(TimeSpan.FromSeconds(6));

        /// <summary>Почему управление через Web API выключено на эту сессию (null — работает).</summary>
        private static string _offReason;

        /// <summary>Состояние плеера Spotify изменилось (повтор, перемешивание, громкость) — приходит в потоке интерфейса.</summary>
        public static event Action StateChanged;

        /// <summary>Повтор в Spotify: off | context | track; null — ещё не знаем.</summary>
        public static string RepeatState { get; private set; }
        public static bool? Shuffle { get; private set; }
        /// <summary>Громкость самого Spotify 0..100; null — неизвестна или устройство её не меняет.</summary>
        public static int? VolumePercent { get; private set; }

        private static DateTime _stateAt, _volumeSetAt, _modeSetAt;
        private static int _stateBusy;

        static SpotifyControl()
        {
            // перепривязали (может, уже с Premium или с нужным правом) — пробуем Web API заново
            SpotifyAuth.Changed += () =>
            {
                _offReason = null;
                RepeatState = null; Shuffle = null; VolumePercent = null;
                _stateAt = default;
                Raise();
            };
        }

        /// <summary>Можно управлять через Web API (привязка с нужным правом и без отказов в этой сессии).</summary>
        public static bool Available => _offReason == null && SpotifyAuth.HasControlScope;

        /// <summary>Управлять этим плеером через Spotify Web API.</summary>
        public static bool Handles(MediaInfo m) => m != null && m.HasSession && Available && SpotifyAuth.IsSpotifyApp(m.SourceAppId);

        /// <summary>
        /// Команда плееру: PUT/POST на /v1/me/player/{path}. true — Spotify принял (2xx). false — не вышло,
        /// вызывающий делает то же самое через Windows.
        /// </summary>
        public static async Task<bool> SendAsync(HttpMethod method, string path, string what, bool quiet = false)
        {
            if (!Available) return false;
            try
            {
                var token = await SpotifyAuth.GetAccessTokenAsync();
                if (token == null) { Off("нет токена"); return false; }
                var (code, reason) = await RawAsync(method, path, token);
                if (code == 401)
                {
                    var fresh = await SpotifyAuth.GetAccessTokenAsync(true);
                    if (fresh != null) (code, reason) = await RawAsync(method, path, fresh);
                }
                bool ok = code >= 200 && code < 300;
                var line = "управление, " + what + ": " + code + (string.IsNullOrEmpty(reason) ? "" : " " + reason);
                if (ok && quiet) Journal.Debug("spotify", line); else Journal.Info("spotify", line);
                if (ok) return true;
                Off(reason == "PREMIUM_REQUIRED" ? "нужен Spotify Premium" : "статус " + code + (string.IsNullOrEmpty(reason) ? "" : " " + reason));
                return false;
            }
            catch (Exception ex)
            {
                Journal.Info("spotify", "управление, " + what + ": " + (ex is OperationCanceledException ? "нет ответа" : ex.GetType().Name));
                Off(ex is OperationCanceledException ? "нет ответа" : ex.GetType().Name);
                return false;
            }
        }

        private static async Task<(int code, string reason)> RawAsync(HttpMethod method, string path, string token)
        {
            using var req = new HttpRequestMessage(method, Api + "/" + path);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            // PUT/POST без тела: Content-Length: 0 обязателен, иначе Spotify отвечает 411
            req.Content = new ByteArrayContent(Array.Empty<byte>());
            using var resp = await Http.SendAsync(req);
            int code = (int)resp.StatusCode;
            string reason = null;
            if (code >= 400)
            {
                try
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object
                        && err.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String)
                        reason = r.GetString();
                }
                catch { }
            }
            return (code, reason);
        }

        private static void Off(string reason)
        {
            if (_offReason != null) return;
            _offReason = reason ?? "?";
            Journal.Info("spotify", "управление через Web API выключено до перезапуска (" + _offReason + ") — кнопки работают через Windows");
            Raise();
        }

        // ---------- состояние плеера: GET /v1/me/player ----------

        /// <summary>
        /// Перечитать повтор, перемешивание и громкость Spotify. Не чаще раза в 2 с (force — сразу).
        /// Читать можно и без права управления, но нужно оно только при управлении — без него не спрашиваем.
        /// </summary>
        public static async Task RefreshStateAsync(bool force = false)
        {
            if (!Available) return;
            if (!force && DateTime.UtcNow - _stateAt < TimeSpan.FromSeconds(2)) return;
            if (Interlocked.Exchange(ref _stateBusy, 1) == 1) return;
            _stateAt = DateTime.UtcNow;
            try
            {
                var token = await SpotifyAuth.GetAccessTokenAsync();
                if (token == null) return;
                using var req = new HttpRequestMessage(HttpMethod.Get, Api);
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                using var resp = await Http.SendAsync(req);
                int code = (int)resp.StatusCode;
                if (code != 200) { Journal.Debug("spotify", "состояние плеера: " + code); return; }
                var json = await resp.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(json)) return;
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                // только что переключили сами — Spotify мог ещё не отразить, не перебиваем свой оптимистичный вид
                bool modeFresh = DateTime.UtcNow - _modeSetAt < TimeSpan.FromSeconds(1.5);
                if (!modeFresh && r.TryGetProperty("repeat_state", out var rp) && rp.ValueKind == JsonValueKind.String) RepeatState = rp.GetString();
                if (!modeFresh && r.TryGetProperty("shuffle_state", out var sh) && (sh.ValueKind == JsonValueKind.True || sh.ValueKind == JsonValueKind.False)) Shuffle = sh.GetBoolean();
                if (DateTime.UtcNow - _volumeSetAt > TimeSpan.FromSeconds(1.5) && r.TryGetProperty("device", out var dev) && dev.ValueKind == JsonValueKind.Object)
                {
                    bool supports = !dev.TryGetProperty("supports_volume", out var sv) || sv.ValueKind != JsonValueKind.False;
                    VolumePercent = supports && dev.TryGetProperty("volume_percent", out var vp) && vp.ValueKind == JsonValueKind.Number ? vp.GetInt32() : null;
                }
                Raise();
            }
            catch (Exception ex) { Journal.Debug("spotify", "состояние плеера: " + ex.GetType().Name); }
            finally { Interlocked.Exchange(ref _stateBusy, 0); }
        }

        private static void Raise()
        {
            var d = System.Windows.Application.Current?.Dispatcher;
            if (d == null) return;
            if (d.CheckAccess()) StateChanged?.Invoke();
            else d.BeginInvoke(new Action(() => StateChanged?.Invoke()));
        }

        // ---------- повтор и перемешивание ----------

        /// <summary>Повтор по кругу, как в самом Spotify: выкл → весь плейлист → трек → выкл.</summary>
        public static async Task<bool> CycleRepeatAsync()
        {
            var prev = RepeatState;
            var next = prev == "context" ? "track" : prev == "track" ? "off" : "context";
            RepeatState = next; _modeSetAt = DateTime.UtcNow; Raise(); // сразу показываем
            if (await SendAsync(HttpMethod.Put, "repeat?state=" + next, "повтор " + next)) return true;
            RepeatState = prev; Raise();
            return false;
        }

        public static async Task<bool> ToggleShuffleAsync(bool current)
        {
            var prev = Shuffle;
            bool next = !current;
            Shuffle = next; _modeSetAt = DateTime.UtcNow; Raise();
            if (await SendAsync(HttpMethod.Put, "shuffle?state=" + (next ? "true" : "false"), "перемешивание " + (next ? "вкл" : "выкл"))) return true;
            Shuffle = prev; Raise();
            return false;
        }

        // ---------- громкость: не чаще 5 запросов в секунду ----------

        private static int? _volWant;
        private static bool _volBusy;
        private static DateTime _volLast;
        private static Action<double> _volFallback;

        /// <summary>
        /// Громкость Spotify (0..1) с крутилки. Запросы прореживаются (≤ 5/с, последнее значение всегда уходит).
        /// Не вышло — fallback с тем же значением (громкость приложения в микшере Windows).
        /// </summary>
        public static void SetVolume(double v01, Action<double> fallback)
        {
            int pct = (int)Math.Round(Math.Clamp(v01, 0, 1) * 100);
            VolumePercent = pct;
            _volumeSetAt = DateTime.UtcNow;
            _volWant = pct;
            _volFallback = fallback;
            if (!_volBusy) _ = VolumeLoopAsync();
        }

        private static async Task VolumeLoopAsync()
        {
            _volBusy = true;
            try
            {
                while (_volWant.HasValue)
                {
                    var wait = TimeSpan.FromMilliseconds(200) - (DateTime.UtcNow - _volLast);
                    if (wait > TimeSpan.Zero) await Task.Delay(wait);
                    int want = _volWant.Value;
                    _volWant = null;
                    _volLast = DateTime.UtcNow;
                    if (!await SendAsync(HttpMethod.Put, "volume?volume_percent=" + want, "громкость", quiet: true))
                    {
                        var fb = _volFallback;
                        int last = _volWant ?? want;
                        _volWant = null;
                        VolumePercent = null;
                        fb?.Invoke(last / 100.0);
                        break;
                    }
                    _volumeSetAt = DateTime.UtcNow;
                }
            }
            finally { _volBusy = false; }
        }
    }
}
