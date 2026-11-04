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

        private void Attach()
        {
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
                Current = info;
                Changed?.Invoke();
                if (changedTrack) TrackChanged?.Invoke(info);
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

        public async void PlayPause() { try { if (_session != null) await _session.TryTogglePlayPauseAsync(); } catch { } }
        public async void Next() { try { if (_session != null) await _session.TrySkipNextAsync(); } catch { } }
        public async void Previous() { try { if (_session != null) await _session.TrySkipPreviousAsync(); } catch { } }

        public async void Seek(TimeSpan pos)
        {
            try { if (_session != null) await _session.TryChangePlaybackPositionAsync(pos.Ticks); } catch { }
        }
    }
}
