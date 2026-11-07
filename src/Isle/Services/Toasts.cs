using System;
using System.Windows.Media;

namespace Isle.Services
{
    public enum ToastKind { Info, Layout, Caps, Volume, Battery, Notification, Download, Timer, Reminder, Success, Alarm }

    public class Toast
    {
        public ToastKind Kind { get; set; }
        public string Title { get; set; } = "";
        public string Subtitle { get; set; } = "";
        public string SpeedText { get; set; } = "";
        public string EtaText { get; set; } = "";
        /// <summary>Путь-геометрия иконки (24x24, stroke).</summary>
        public string Icon { get; set; } = Icons.Info;
        public Color Accent { get; set; } = Color.FromRgb(0xEC, 0xEC, 0xF1);
        public ImageSource Image { get; set; }
        public string FolderPath { get; set; }
        /// <summary>Картинка — обложка игры: показываем круглой миниатюрой, как в пилюле загрузки.</summary>
        public bool RoundImage { get; set; }
        /// <summary>Анимированный значок погоды вместо иконки (вид: sun, rain, thunder, snow… — см. WeatherGlyph).</summary>
        public string WeatherKind { get; set; }
        public double? Progress { get; set; }
        public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(2.2);
        /// <summary>Тосты с ключом заменяют друг друга без мигания (громкость, раскладка).</summary>
        public string ReplaceKey { get; set; }
        public bool Important { get; set; }
    }

    public static class ToastBus
    {
        public static event Action<Toast> Raised;

        public static void Show(Toast t)
        {
            if (AppSettings.Current.DoNotDisturb && !t.Important) return;
            Raised?.Invoke(t);
        }
    }

    public static class Icons
    {
        public const string Info = "M12 3.5a8.5 8.5 0 1 0 0 17a8.5 8.5 0 1 0 0-17M12 11v5M12 8h.01";
        public const string Check = "M5 12.5l4.5 4.5L19 7.5";
        public const string Keyboard = "M4.5 6h15A1.5 1.5 0 0 1 21 7.5v9a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 16.5v-9A1.5 1.5 0 0 1 4.5 6zM7 10h.01M10 10h.01M13 10h.01M16 10h.01M8 14.5h8";
        public const string Caps = "M12 4.5L19 12h-3.5v4h-7v-4H5zM8.5 19.5h7";
        public const string Volume = "M4 9.5h3l4.5-4v13L7 14.5H4zM15 9a4 4 0 0 1 0 6M17.8 6.5a7.5 7.5 0 0 1 0 11";
        public const string Mute = "M4 9.5h3l4.5-4v13L7 14.5H4zM16 10l4 4M20 10l-4 4";
        public const string Bolt = "M13 3L5.5 13.5H11L10 21l8-10.5h-5.5z";
        public const string Battery = "M4.5 7.5h13A1.5 1.5 0 0 1 19 9v6a1.5 1.5 0 0 1-1.5 1.5h-13A1.5 1.5 0 0 1 3 15V9a1.5 1.5 0 0 1 1.5-1.5zM21 10.5v3M6.5 10.5v3";
        public const string Download = "M12 4v11M7.5 10.5L12 15l4.5-4.5M5 19.5h14";
        public const string Bell = "M6 16.5V11a6 6 0 0 1 12 0v5.5l1.5 1.5h-15zM10 20.5h4";
        public const string Snow = "M12 3v18M4.2 7.5l15.6 9M4.2 16.5l15.6-9M8.5 5l3.5 3 3.5-3M8.5 19l3.5-3 3.5 3M3.5 11l4.5-.5-.5-4.5M20.5 13l-4.5.5.5 4.5M3.5 13l4.5.5-.5 4.5M20.5 11l-4.5-.5.5-4.5";
        public const string Timer = "M12 7.5a6.5 6.5 0 1 0 0 13a6.5 6.5 0 1 0 0-13M12 11v3l2 1.5M10 3.5h4M18.5 7l1-1";
        public const string Home = "M4 10.5L12 4l8 6.5V19a1 1 0 0 1-1 1h-4.5v-5.5h-5V20H5a1 1 0 0 1-1-1z";
        public const string Clipboard = "M9 3.5h6a1 1 0 0 1 1 1V6H8V4.5a1 1 0 0 1 1-1zM8 5H6.5A1.5 1.5 0 0 0 5 6.5v12A1.5 1.5 0 0 0 6.5 20h11a1.5 1.5 0 0 0 1.5-1.5v-12A1.5 1.5 0 0 0 17.5 5H16M9 11h6M9 15h4";
        public const string Tray = "M4 13.5l2.2-7.2a1.5 1.5 0 0 1 1.4-1.1h8.8a1.5 1.5 0 0 1 1.4 1.1L20 13.5V18a1.5 1.5 0 0 1-1.5 1.5h-13A1.5 1.5 0 0 1 4 18zM4 13.5h4.5l1.2 2h4.6l1.2-2H20";
        public const string Text = "M5 7.5V5h14v2.5M12 5v14M9.5 19h5";
        public const string Chat = "M5 5.5h14a1.5 1.5 0 0 1 1.5 1.5v8.5a1.5 1.5 0 0 1-1.5 1.5h-7l-4.5 3.5V17H5a1.5 1.5 0 0 1-1.5-1.5V7A1.5 1.5 0 0 1 5 5.5zM8.5 11.2h.01M12 11.2h.01M15.5 11.2h.01";
        public const string Note = "M14.5 4.5l5 5L9 20H4v-5zM12.5 6.5l5 5";
        public const string Sun = "M8 3v1.2M3.2 8h1.2M4.6 4.6l.9.9M11.4 4.6l-.9.9M5.4 10.6a3 3 0 1 1 5.4-2.8M8.5 20h8a3.5 3.5 0 0 0 .4-7a5 5 0 0 0-9.4 1.3A2.9 2.9 0 0 0 8.5 20z";
        public const string Sliders = "M4.5 7h8.5M17 7h2.5M4.5 17h2.5M11 17h8.5M15 4.5v5M9 14.5v5";
        public const string Gear = "M12 8.5a3.5 3.5 0 1 0 0 7a3.5 3.5 0 1 0 0-7M12 3v2.2M12 18.8V21M3 12h2.2M18.8 12H21M5.6 5.6l1.6 1.6M16.8 16.8l1.6 1.6M5.6 18.4l1.6-1.6M16.8 7.2l1.6-1.6";
        public const string Pin = "M9 4h6l-1 5 3 3v1.5H7V12l3-3zM12 13.5V20";
        public const string Moon = "M19.5 14.5A8 8 0 0 1 9.5 4.5a8 8 0 1 0 10 10z";
        public const string Wave = "M4 10v4M8 7v10M12 4.5v15M16 8v8M20 10.5v3";
        public const string Prev = "M18 6v12l-8.5-6zM6 6v12";
        public const string Next = "M6 6v12l8.5-6zM18 6v12";
        public const string Play = "M8 5.5v13l10-6.5z";
        public const string Pause = "M8.5 5.5v13M15.5 5.5v13";
        public const string Crop = "M4 9V5.5A1.5 1.5 0 0 1 5.5 4H9M15 4h3.5A1.5 1.5 0 0 1 20 5.5V9M20 15v3.5a1.5 1.5 0 0 1-1.5 1.5H15M9 20H5.5A1.5 1.5 0 0 1 4 18.5V15M9.5 12h5M12 9.5v5";
        public const string Monitor = "M4.5 4.5h15A1.5 1.5 0 0 1 21 6v9a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 15V6a1.5 1.5 0 0 1 1.5-1.5zM9 20h6M12 16.5V20";
        public const string Scan = "M4 8V5.5A1.5 1.5 0 0 1 5.5 4H8M16 4h2.5A1.5 1.5 0 0 1 20 5.5V8M20 16v2.5a1.5 1.5 0 0 1-1.5 1.5H16M8 20H5.5A1.5 1.5 0 0 1 4 18.5V16M8.5 9h7M12 9v7";
        public const string Qr = "M4 4h6v6H4zM14 4h6v6h-6zM4 14h6v6H4zM14 14h2.5v2.5H14zM17.5 17.5H20V20h-2.5zM7 7h.01M17 7h.01M7 17h.01";
        public const string Pipette = "M16.5 3.5a2.1 2.1 0 0 1 3 3L17 9l-3-3zM14 6l4 4M13.5 7.5L5 16v3h3l8.5-8.5";
        public const string Eraser = "M9 19.5h10.5M5 13.5l8-8a1.5 1.5 0 0 1 2.1 0l3.4 3.4a1.5 1.5 0 0 1 0 2.1L11 19.5H9l-4-4a1.4 1.4 0 0 1 0-2zM9 9.5l5 5";
        public const string Record = "M4.5 6.5h9A1.5 1.5 0 0 1 15 8v8a1.5 1.5 0 0 1-1.5 1.5h-9A1.5 1.5 0 0 1 3 16V8a1.5 1.5 0 0 1 1.5-1.5zM15 10.5l6-3.5v10l-6-3.5";
        public const string Search = "M10.5 4a6.5 6.5 0 1 0 0 13a6.5 6.5 0 1 0 0-13M15.5 15.5L20 20";
        public const string Trash = "M4.5 7h15M9.5 7V5a1 1 0 0 1 1-1h3a1 1 0 0 1 1 1v2M6.5 7l.8 11.6a1.5 1.5 0 0 0 1.5 1.4h6.4a1.5 1.5 0 0 0 1.5-1.4L17.5 7M10 11v5M14 11v5";
        public const string Plus = "M12 5v14M5 12h14";
        public const string Send = "M12 19V5M6 11l6-6 6 6";
        public const string Lock = "M6.5 10.5h11A1.5 1.5 0 0 1 19 12v6.5a1.5 1.5 0 0 1-1.5 1.5h-11A1.5 1.5 0 0 1 5 18.5V12a1.5 1.5 0 0 1 1.5-1.5zM8 10.5V8a4 4 0 0 1 8 0v2.5M12 14.5v2";
        public const string Restart = "M4.5 12a7.5 7.5 0 1 0 2.2-5.3L4.5 9M4.5 4.5V9H9";
        public const string Power = "M12 3.5V11M7 6.2a7.5 7.5 0 1 0 10 0";
        public const string Wifi = "M3.5 9.5a12 12 0 0 1 17 0M6.5 12.8a7.5 7.5 0 0 1 11 0M9.5 16a3 3 0 0 1 5 0M12 19.5h.01";
        public const string Bluetooth = "M7 7.5l10 9-5 4v-17l5 4-10 9";
        public const string Contrast = "M12 3.5a8.5 8.5 0 1 0 0 17a8.5 8.5 0 1 0 0-17M12 3.5v17M12 8h4.5M12 12h6.5M12 16h4.5";
        public const string Copy = "M9.5 8.5h9A1.5 1.5 0 0 1 20 10v9a1.5 1.5 0 0 1-1.5 1.5h-9A1.5 1.5 0 0 1 8 19v-9a1.5 1.5 0 0 1 1.5-1.5zM16 8.5v-3A1.5 1.5 0 0 0 14.5 4h-9A1.5 1.5 0 0 0 4 5.5v9A1.5 1.5 0 0 0 5.5 16H8";
        public const string Close = "M6.5 6.5l11 11M17.5 6.5l-11 11";
        public const string Terminal = "M4.5 4.5h15A1.5 1.5 0 0 1 21 6v12a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 18V6a1.5 1.5 0 0 1 1.5-1.5zM7 9.5l2.5 2.5L7 14.5M12 15h5";
        public const string Folder = "M3.5 7A1.5 1.5 0 0 1 5 5.5h4l2 2h8A1.5 1.5 0 0 1 20.5 9v9a1.5 1.5 0 0 1-1.5 1.5H5A1.5 1.5 0 0 1 3.5 18z";
        public const string Shield = "M12 3.5L19 6v5.5c0 4.4-3 7.6-7 9c-4-1.4-7-4.6-7-9V6zM9 12l2 2 4-4";
        public const string Bold = "M7 4.5h6a3.75 3.75 0 0 1 0 7.5H7zM7 12h7a3.75 3.75 0 0 1 0 7.5H7z";
        public const string Italic = "M10 4.5h8M6 19.5h8M14 4.5l-4 15";
        public const string Strike = "M16.5 6.5A4 4 0 0 0 13 4.5h-2a3.5 3.5 0 0 0-1.5 6.7M4.5 12h15M7.5 17a4 4 0 0 0 4 2.5H13a3.5 3.5 0 0 0 3.2-5";
        public const string ListBullet = "M9 6.5h11M9 12h11M9 17.5h11M4.5 6.5h.01M4.5 12h.01M4.5 17.5h.01";
        public const string ListNumber = "M10 6.5h10M10 12h10M10 17.5h10M4 5.5l1.5-1v4M4 10.5h2.5L4 14h2.5";
        public const string ListCheck = "M3.5 6.5L5 8l3-3M3.5 13.5L5 15l3-3M11 7h9.5M11 14h9.5M11 19.5h9.5";
        public const string Battery2 = Battery;
        public const string MapPin = "M12 21s6.5-5.8 6.5-11a6.5 6.5 0 0 0-13 0c0 5.2 6.5 11 6.5 11zM12 7.5a2.5 2.5 0 1 0 0 5a2.5 2.5 0 1 0 0-5";
        public const string Mic = "M12 3.5a3 3 0 0 0-3 3V12a3 3 0 0 0 6 0V6.5a3 3 0 0 0-3-3M18 11.5a6 6 0 0 1-12 0M12 17.5v3";
    }
}
