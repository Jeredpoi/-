using System;
using System.IO;
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
                var s = _mgr?.GetCurrentSession();
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
        private void OnPlayback(WinMedia.GlobalSystemMediaTransportControlsSession s, WinMedia.PlaybackInfoChangedEventArgs e) => _ = RefreshAsync();
        private void OnTimeline(WinMedia.GlobalSystemMediaTransportControlsSession s, WinMedia.TimelinePropertiesChangedEventArgs e) => _ = RefreshAsync();

        public async Task RefreshAsync()
        {
            var info = new MediaInfo();
            var s = _session;
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
                        else if (props.Thumbnail != null) info.Cover = await LoadThumb(props.Thumbnail);
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

            _ui.InvokeAsync(() =>
            {
                var newKey = info.Title + "|" + info.Artist;
                bool trackChanged = newKey != _lastKey && !string.IsNullOrEmpty(info.Title);
                _lastKey = newKey;
                Current = info;
                Changed?.Invoke();
                if (trackChanged) TrackChanged?.Invoke(info);
            });
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
