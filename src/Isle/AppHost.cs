using System;
using Isle.Services;

namespace Isle
{
    /// <summary>Общие сервисы приложения (создаются один раз при запуске).</summary>
    public static class AppHost
    {
        public static MediaService Media;
        public static AudioService Audio;
        public static ClipboardService Clipboard;
        public static ReminderService Reminders;
        public static TimerService Timer;
        public static ShelfService Shelf;
        public static SystemWatchers Watchers;
        public static NotificationService Notifications;
        public static VpnService Vpn;
        public static VoiceService Voice;
        public static SpectrumService Spectrum;
        public static LyricsState Lyrics;
        public static WeatherNow Weather;
        public static event Action WeatherChanged;
        public static MainWindow Window;

        public static void SetWeather(WeatherNow w)
        {
            Weather = w;
            WeatherChanged?.Invoke();
        }
    }
}
