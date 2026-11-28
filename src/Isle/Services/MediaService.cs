using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinMedia = Windows.Media.Control;

namespace Isle.Services
{
    public class MediaInfo
    {
        public string Title = "";
        public string Artist = "";
        public string Album = "";
        public string SourceApp = "";
        /// <summary>Системный id плеера (AUMID): по нему ищем его громкость в микшере Windows.</summary>
        public string SourceAppId = "";
        public bool IsPlaying;
        public TimeSpan Position;
        public TimeSpan Duration;
        public DateTime PositionStamp = DateTime.Now;
        public BitmapImage Cover;
        public bool HasSession;

        public TimeSpan LivePosition
        {
            get
            {
                if (!IsPlaying) return Position;
                var p = Position + (DateTime.Now - PositionStamp);
                return Duration > TimeSpan.Zero && p > Duration ? Duration : p;
            }
        }
    }

    /// <summary>Музыка из любого плеера, который отдаёт данные в системные медиа-кнопки Windows.</summary>
    public class MediaService
    {
        private WinMedia.GlobalSystemMediaTransportControlsSessionManager _mgr;
        private WinMedia.GlobalSystemMediaTransportControlsSession _session;
        private readonly Dispatcher _ui;
        private string _lastKey = "";

        public MediaInfo Current { get; private set; } = new MediaInfo();
        /// <summary>Выбранный вручную источник (AUMID приложения) или null — «автоматически».</summary>
        public string PreferredSource { get; private set; }

        public class Source { public string Id; public string Name; public string Title; public bool Playing; }

        /// <summary>Все приложения, которые сейчас отдают музыку в Windows.</summary>
        public async Task<System.Collections.Generic.List<Source>> SourcesAsync()
        {
            var list = new System.Collections.Generic.List<Source>();
            if (_mgr == null) return list;
            foreach (var ses in _mgr.GetSessions())
            {
                var src = new Source { Id = ses.SourceAppUserModelId, Name = PrettyApp(ses.SourceAppUserModelId) };
                try
                {
                    var props = await ses.TryGetMediaPropertiesAsync();
                    src.Title = props == null ? "" : (props.Artist?.Length > 0 ? props.Title + " — " + props.Artist : props.Title);
                    src.Playing = ses.GetPlaybackInfo()?.PlaybackStatus == WinMedia.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                }
                catch { }
                list.Add(src);
            }
            return list;
        }

        public int SourceCount { get { try { return _mgr?.GetSessions().Count ?? 0; } catch { return 0; } } }

        public void ChooseSource(string id)
        {
            PreferredSource = id;
            Attach();
        }

        /// <summary>Автовыбор: выбранный вручную → играющий сейчас → системный текущий.</summary>
        private WinMedia.GlobalSystemMediaTransportControlsSession PickSession()
        {
            var sessions = _mgr.GetSessions();
            if (PreferredSource != null)
            {
                var chosen = sessions.FirstOrDefault(x => x.SourceAppUserModelId == PreferredSource);
                if (chosen != null) return chosen;
            }
            var cur = _mgr.GetCurrentSession();
            try
            {
                if (cur != null && cur.GetPlaybackInfo()?.PlaybackStatus == WinMedia.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) return cur;
                var playing = sessions.FirstOrDefault(x => x.GetPlaybackInfo()?.PlaybackStatus == WinMedia.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
                if (playing != null) return playing;
            }
            catch { }
            return cur;
        }
        public event Action Changed;
        public event Action<MediaInfo> TrackChanged;

        public MediaService(Dispatcher ui) { _ui = ui; }

        public async Task InitAsync()
        {
            try
            {
                _mgr = await WinMedia.GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                _mgr.CurrentSessionChanged += (_, __) => Attach();
                _mgr.SessionsChanged += (_, __) => Attach();
                Attach();
            }
            catch { }
        }

        private readonly object _attachLock = new();

        private void Attach()
        {
            // События сессий Windows приходят из разных потоков одновременно (по журналу — NullReference в Attach
            // при двух вызовах сразу): переключение сессии и подписки делаем по одному
            lock (_attachLock)
            try
            {
                var s = _mgr == null ? null : PickSession();
                if (!ReferenceEquals(s, _session))
                {
                    if (_session != null)
                    {
                        _session.MediaPropertiesChanged -= OnProps;
                        _session.PlaybackInfoChanged -= OnPlayback;
                        _session.TimelinePropertiesChanged -= OnTimeline;
                    }
                    _session = s;
                    if (_session != null)
                    {
                        _session.MediaPropertiesChanged += OnProps;
                        _session.PlaybackInfoChanged += OnPlayback;
                        _session.TimelinePropertiesChanged += OnTimeline;
                    }
                }
                _ = RefreshAsync();
            }
            catch { }
        }

        private void OnProps(WinMedia.GlobalSystemMediaTransportControlsSession s, WinMedia.MediaPropertiesChangedEventArgs e) => _ = RefreshAsync();
        private void OnPlayback(WinMedia.GlobalSystemMediaTransportControlsSession s, WinMedia.PlaybackInfoChangedEventArgs e) { if (PreferredSource == null) Attach(); else _ = RefreshAsync(); }
        private void OnTimeline(WinMedia.GlobalSystemMediaTransportControlsSession s, WinMedia.TimelinePropertiesChangedEventArgs e) => _ = RefreshAsync();

        public async Task RefreshAsync()
        {
            var info = new MediaInfo();
            var s = _session;
            Windows.Storage.Streams.IRandomAccessStreamReference thumb = null;
            if (s != null)
            {
                try
                {
                    info.HasSession = true;
                    info.SourceApp = PrettyApp(s.SourceAppUserModelId);
                    info.SourceAppId = s.SourceAppUserModelId ?? "";
                    var props = await s.TryGetMediaPropertiesAsync();
                    if (props != null)
                    {
                        info.Title = props.Title ?? "";
                        info.Artist = props.Artist ?? "";
                        info.Album = props.AlbumTitle ?? "";
                        var key = info.Title + "|" + info.Artist;
                        if (key == _lastKey && Current.Cover != null) info.Cover = Current.Cover;
                        else thumb = props.Thumbnail;
                    }
                    var pb = s.GetPlaybackInfo();
                    info.IsPlaying = pb?.PlaybackStatus == WinMedia.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    var tl = s.GetTimelineProperties();
                    if (tl != null)
                    {
                        info.Duration = tl.EndTime - tl.StartTime;
                        info.Position = tl.Position;
                        info.PositionStamp = tl.LastUpdatedTime.LocalDateTime;
                        if (info.PositionStamp > DateTime.Now || info.PositionStamp < DateTime.Now.AddHours(-12))
                            info.PositionStamp = DateTime.Now;
                    }
                }
                catch { }
            }

            // 1) сразу показываем новое название и время — не ждём обложку
            bool changedTrack = false;
            await _ui.InvokeAsync(() =>
            {
                var newKey = info.Title + "|" + info.Artist;
                changedTrack = newKey != _lastKey && !string.IsNullOrEmpty(info.Title);
                if (changedTrack && info.Position > TimeSpan.FromSeconds(3) && Current.Title != info.Title)
                {
                    // плееры иногда присылают позицию прошлого трека — новый начинается с нуля
                    info.Position = TimeSpan.Zero;
                    info.PositionStamp = DateTime.Now;
                }
                _lastKey = newKey;
                ApplyHold(info, changedTrack);
                Current = info;
                Changed?.Invoke();
                if (changedTrack)
                {
                    Journal.Info("media", "трек: " + Journal.Short(info.Artist + " — " + info.Title, 120) + ", плеер " + info.SourceApp + (info.IsPlaying ? ", играет" : ", пауза"));
                    TrackChanged?.Invoke(info);
                }
            });

            // 2) догружаем обложку и показываем ещё раз
            if (thumb != null)
            {
                var cover = await LoadThumb(thumb);
                if (cover != null)
                    await _ui.InvokeAsync(() =>
                    {
                        if (Current.Title != info.Title || Current.Artist != info.Artist) return;
                        Current.Cover = cover;
                        Changed?.Invoke();
                        TrackChanged?.Invoke(Current);
                    });
            }

            // 3) некоторые плееры (Spotify) дописывают обложку и время с задержкой — перепроверяем
            if (changedTrack)
            {
                await Task.Delay(800);
                if (ReferenceEquals(_session, s) && (Current.Cover == null || Current.Duration == TimeSpan.Zero)) _ = RefreshAsync();
            }
        }

        private static async Task<BitmapImage> LoadThumb(Windows.Storage.Streams.IRandomAccessStreamReference r)
        {
            try
            {
                using var ras = await r.OpenReadAsync();
                using var net = ras.AsStreamForRead();
                var ms = new MemoryStream();
                await net.CopyToAsync(ms);
                ms.Position = 0;
                BitmapImage bmp = null;
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 200;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                });
                return bmp;
            }
            catch { return null; }
        }

        private static string PrettyApp(string aumid)
        {
            if (string.IsNullOrEmpty(aumid)) return "";
            var a = aumid.ToLowerInvariant();
            if (a.Contains("spotify")) return "Spotify";
            if (a.Contains("chrome")) return "Google Chrome";
            if (a.Contains("msedge")) return "Microsoft Edge";
            if (a.Contains("firefox")) return "Firefox";
            if (a.Contains("yandex")) return "Яндекс";
            if (a.Contains("opera")) return "Opera";
            if (a.Contains("telegram")) return "Telegram";
            if (a.Contains("zunemusic") || a.Contains("media player")) return "Медиаплеер";
            if (a.Contains("vlc")) return "VLC";
            if (a.Contains("aimp")) return "AIMP";
            var name = Path.GetFileNameWithoutExtension(aumid);
            var bang = name.IndexOf('!');
            return bang > 0 ? name.Substring(0, bang) : name;
        }

        // ---------- управление ----------
        // Привязан Spotify с правом управления и играет он — команды идут в Spotify Web API (SpotifyControl),
        // интерфейс меняется сразу (оптимистично), потом сверяется с Windows и GET /me/player.
        // Любой отказ Web API — то же самое через системные медиа-кнопки Windows, как раньше.

        private bool ViaSpotify => SpotifyControl.Handles(Current);

        // оптимистичное состояние: события Windows от Spotify приходят с опозданием и на миг вернули бы старое
        private DateTime _holdUntil;
        private bool? _holdPlaying;
        private TimeSpan? _holdPosition;
        private DateTime _holdStamp;

        private void ApplyHold(MediaInfo info, bool changedTrack)
        {
            if (DateTime.Now > _holdUntil || changedTrack) { _holdPlaying = null; _holdPosition = null; return; }
            if (_holdPlaying.HasValue)
            {
                if (info.IsPlaying == _holdPlaying.Value) _holdPlaying = null; // Windows догнал
                else { info.IsPlaying = _holdPlaying.Value; info.Position = Current.LivePosition; info.PositionStamp = DateTime.Now; }
            }
            if (_holdPosition.HasValue)
            {
                var want = _holdPosition.Value + (info.IsPlaying ? DateTime.Now - _holdStamp : TimeSpan.Zero);
                if ((info.LivePosition - want).Duration() < TimeSpan.FromSeconds(1.5)) _holdPosition = null;
                else { info.Position = want; info.PositionStamp = DateTime.Now; }
            }
        }

        /// <summary>Сразу показать ожидаемое состояние (до ответа плеера).</summary>
        private void Optimistic(bool? playing = null, TimeSpan? position = null)
        {
            void Do()
            {
                var c = Current;
                if (!c.HasSession) return;
                c.Position = position ?? c.LivePosition;
                c.PositionStamp = DateTime.Now;
                if (playing.HasValue) c.IsPlaying = playing.Value;
                _holdUntil = DateTime.Now.AddSeconds(1.5);
                if (playing.HasValue) _holdPlaying = playing;
                if (position.HasValue) { _holdPosition = position; _holdStamp = DateTime.Now; }
                Changed?.Invoke();
            }
            if (_ui.CheckAccess()) Do(); else _ui.BeginInvoke(new Action(Do));
        }

        /// <summary>После команды через Web API: сверить с Windows и Spotify.</summary>
        private async void Reconcile()
        {
            try
            {
                await Task.Delay(700);
                await RefreshAsync();
                await SpotifyControl.RefreshStateAsync(force: true);
            }
            catch { }
        }

        public async void PlayPause()
        {
            if (ViaSpotify)
            {
                bool was = Current.IsPlaying;
                Optimistic(playing: !was);
                bool ok = await SpotifyControl.SendAsync(System.Net.Http.HttpMethod.Put, was ? "pause" : "play", was ? "пауза" : "играть");
                Reconcile();
                if (ok) return;
            }
            try { if (_session != null) await _session.TryTogglePlayPauseAsync(); } catch { }
        }

        public async void Play()
        {
            if (ViaSpotify)
            {
                Optimistic(playing: true);
                bool ok = await SpotifyControl.SendAsync(System.Net.Http.HttpMethod.Put, "play", "играть");
                Reconcile();
                if (ok) return;
            }
            try { if (_session != null) await _session.TryPlayAsync(); } catch { }
        }

        public async void Pause()
        {
            if (ViaSpotify)
            {
                Optimistic(playing: false);
                bool ok = await SpotifyControl.SendAsync(System.Net.Http.HttpMethod.Put, "pause", "пауза");
                Reconcile();
                if (ok) return;
            }
            try { if (_session != null) await _session.TryPauseAsync(); } catch { }
        }

        public enum RepeatMode { Off, All, One }

        /// <summary>Повтор у текущего плеера: (можно ли переключать снаружи, режим).</summary>
        public (bool supported, RepeatMode mode) RepeatInfo
        {
            get
            {
                if (ViaSpotify && SpotifyControl.RepeatState != null)
                    return (true, SpotifyControl.RepeatState == "track" ? RepeatMode.One : SpotifyControl.RepeatState == "context" ? RepeatMode.All : RepeatMode.Off);
                try
                {
                    var pb = _session?.GetPlaybackInfo();
                    if (pb == null) return (ViaSpotify, RepeatMode.Off);
                    var mode = pb.AutoRepeatMode;
                    var m = mode == Windows.Media.MediaPlaybackAutoRepeatMode.Track ? RepeatMode.One
                          : mode == Windows.Media.MediaPlaybackAutoRepeatMode.List ? RepeatMode.All : RepeatMode.Off;
                    return (ViaSpotify || pb.Controls.IsRepeatEnabled, m);
                }
                catch { return (ViaSpotify, RepeatMode.Off); }
            }
        }

        /// <summary>Повтор у текущего плеера: (умеет ли плеер его переключать снаружи, включён ли).</summary>
        public (bool supported, bool on) Repeat
        {
            get { var (sup, mode) = RepeatInfo; return (sup, mode != RepeatMode.Off); }
        }

        /// <summary>
        /// Spotify через Web API: выкл → плейлист → трек → выкл. Остальные плееры: выкл → повтор трека → выкл.
        /// false — плеер не даёт переключать повтор снаружи.
        /// </summary>
        public async Task<bool> ToggleRepeatAsync()
        {
            if (ViaSpotify)
            {
                if (SpotifyControl.RepeatState == null) await SpotifyControl.RefreshStateAsync(force: true);
                if (ViaSpotify && await SpotifyControl.CycleRepeatAsync()) { Reconcile(); return true; }
            }
            try
            {
                if (_session == null) return false;
                var pb = _session.GetPlaybackInfo();
                if (pb == null || !pb.Controls.IsRepeatEnabled) return false;
                bool on = pb.AutoRepeatMode != Windows.Media.MediaPlaybackAutoRepeatMode.None;
                var next = on ? Windows.Media.MediaPlaybackAutoRepeatMode.None : Windows.Media.MediaPlaybackAutoRepeatMode.Track;
                bool ok = await _session.TryChangeAutoRepeatModeAsync(next);
                Journal.Info("media", "повтор: " + (on ? "выкл" : "трек") + (ok ? "" : " — плеер отказал"));
                return ok;
            }
            catch (Exception ex) { Journal.Debug("media", "повтор не переключился: " + ex.Message); return false; }
        }

        /// <summary>Перемешивание: (можно ли переключать снаружи, включено ли).</summary>
        public (bool supported, bool on) ShuffleInfo
        {
            get
            {
                if (ViaSpotify && SpotifyControl.Shuffle.HasValue) return (true, SpotifyControl.Shuffle.Value);
                try
                {
                    var pb = _session?.GetPlaybackInfo();
                    if (pb == null) return (ViaSpotify, false);
                    return (ViaSpotify || pb.Controls.IsShuffleEnabled, pb.IsShuffleActive == true);
                }
                catch { return (ViaSpotify, false); }
            }
        }

        /// <summary>Переключить перемешивание. false — плеер не даёт переключать его снаружи.</summary>
        public async Task<bool> ToggleShuffleAsync()
        {
            if (ViaSpotify)
            {
                if (!SpotifyControl.Shuffle.HasValue) await SpotifyControl.RefreshStateAsync(force: true);
                if (ViaSpotify && await SpotifyControl.ToggleShuffleAsync(ShuffleInfo.on)) { Reconcile(); return true; }
            }
            try
            {
                if (_session == null) return false;
                var pb = _session.GetPlaybackInfo();
                if (pb == null || !pb.Controls.IsShuffleEnabled) return false;
                bool on = pb.IsShuffleActive == true;
                bool ok = await _session.TryChangeShuffleActiveAsync(!on);
                Journal.Info("media", "перемешивание: " + (on ? "выкл" : "вкл") + (ok ? "" : " — плеер отказал"));
                return ok;
            }
            catch (Exception ex) { Journal.Debug("media", "перемешивание не переключилось: " + ex.Message); return false; }
        }

        public async void Next()
        {
            if (ViaSpotify)
            {
                bool ok = await SpotifyControl.SendAsync(System.Net.Http.HttpMethod.Post, "next", "следующий");
                Reconcile();
                if (ok) return;
            }
            try { if (_session != null) await _session.TrySkipNextAsync(); } catch { }
        }

        public async void Previous()
        {
            if (ViaSpotify)
            {
                bool ok = await SpotifyControl.SendAsync(System.Net.Http.HttpMethod.Post, "previous", "предыдущий");
                Reconcile();
                if (ok) return;
            }
            try { if (_session != null) await _session.TrySkipPreviousAsync(); } catch { }
        }

        public async void Seek(TimeSpan pos)
        {
            if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
            if (ViaSpotify)
            {
                Optimistic(position: pos);
                bool ok = await SpotifyControl.SendAsync(System.Net.Http.HttpMethod.Put, "seek?position_ms=" + (long)pos.TotalMilliseconds, "перемотка");
                Reconcile();
                if (ok) return;
            }
            try { if (_session != null) await _session.TryChangePlaybackPositionAsync(pos.Ticks); } catch { }
        }
    }
}
