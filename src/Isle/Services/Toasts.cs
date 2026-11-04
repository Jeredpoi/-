using System;
using System.Windows.Media;

namespace Isle.Services
{
    public enum ToastKind { Info, Layout, Caps, Volume, Battery, Notification, Timer, Reminder, Success }

    public class Toast
    {
        public ToastKind Kind { get; set; }
        public string Title { get; set; } = "";
        public string Subtitle { get; set; } = "";
        /// <summary>Путь-геометрия иконки (24x24, stroke).</summary>
        public string Icon { get; set; } = Icons.Info;
        public Color Accent { get; set; } = Color.FromRgb(0xEC, 0xEC, 0xF1);
        public ImageSource Image { get; set; }
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
        public const string Info = "M12 2a10 10 0 1 0 0 20a10 10 0 1 0 0-20M12 16v-4M12 8h.01";
        public const string Check = "M5 12l5 5L20 7";
        public const string Keyboard = "M2 5h20v14H2zM6 9h.01M10 9h.01M14 9h.01M18 9h.01M7 15h10";
        public const string Caps = "M12 4l8 8h-4v6H8v-6H4z";
        public const string Volume = "M11 5L6 9H2v6h4l5 4zM15.5 8.5a5 5 0 0 1 0 7M19 5a10 10 0 0 1 0 14";
        public const string Mute = "M11 5L6 9H2v6h4l5 4zM22 9l-6 6M16 9l6 6";
        public const string Bolt = "M13 2L4 14h7l-1 8 9-12h-7z";
        public const string Battery = "M2 7h16v10H2zM22 11v2";
        public const string Bell = "M18 8a6 6 0 0 0-12 0c0 7-3 9-3 9h18s-3-2-3-9M13.7 21a2 2 0 0 1-3.4 0";
        public const string Timer = "M12 13m-8 0a8 8 0 1 0 16 0a8 8 0 1 0-16 0M12 9v4l2 2M9 2h6";
        public const string Home = "M3 11l9-8 9 8v10h-6v-6H9v6H3z";
        public const string Clipboard = "M8 4h8v4H8zM6 6H4v16h16V6h-2";
        public const string Tray = "M22 12h-6l-2 3h-4l-2-3H2M5.5 5h13L22 12v7H2v-7z";
        public const string Text = "M4 7V4h16v3M9 20h6M12 4v16";
        public const string Chat = "M21 12a8 8 0 0 1-11.6 7.1L3 21l1.9-6.4A8 8 0 1 1 21 12z";
        public const string Note = "M12 20h9M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4z";
        public const string Sun = "M12 8a4 4 0 1 0 0 8a4 4 0 1 0 0-8M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4";
        public const string Sliders = "M4 21v-7M4 10V3M12 21v-9M12 8V3M20 21v-5M20 12V3M1 14h6M9 8h6M17 16h6";
        public const string Gear = "M12 9a3 3 0 1 0 0 6a3 3 0 1 0 0-6M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-2.8 1.2V21a2 2 0 1 1-4 0v-.1A1.7 1.7 0 0 0 7.3 19.7l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1A1.7 1.7 0 0 0 3.3 14H3a2 2 0 1 1 0-4h.1A1.7 1.7 0 0 0 4.3 7.3l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1A1.7 1.7 0 0 0 10 3.3V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 2.8 1.2l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1A1.7 1.7 0 0 0 20.7 10H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z";
        public const string Pin = "M12 17v5M8 3h8l-1 7 3 3H6l3-3z";
        public const string Moon = "M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z";
        public const string Wave = "M2 12h2M6 8v8M10 5v14M14 8v8M18 10v4M22 12h-2";
        public const string Prev = "M19 20L9 12l10-8zM5 19V5";
        public const string Next = "M5 4l10 8-10 8zM19 5v14";
        public const string Play = "M7 4l13 8-13 8z";
        public const string Pause = "M6 4h4v16H6zM14 4h4v16h-4z";
        public const string Crop = "M6 2v14h14M2 6h14v14";
        public const string Monitor = "M2 3h20v14H2zM8 21h8M12 17v4";
        public const string Scan = "M3 7V3h4M17 3h4v4M21 17v4h-4M7 21H3v-4M7 12h10";
        public const string Qr = "M3 3h7v7H3zM14 3h7v7h-7zM3 14h7v7H3zM14 14h3v3h-3zM20 14v.01M14 20h.01M17 17h4v4";
        public const string Pipette = "M2 22l1-4L14 7l3 3L6 21zM14 7l3-3a2.1 2.1 0 0 1 3 3l-3 3";
        public const string Eraser = "M20 20H9L3.5 14.5a2 2 0 0 1 0-2.8L13 2.2a2 2 0 0 1 2.8 0l5 5a2 2 0 0 1 0 2.8L11 20";
        public const string Record = "M2 6h14v12H2zM16 10l6-4v12l-6-4";
        public const string Search = "M11 4a7 7 0 1 0 0 14a7 7 0 1 0 0-14M21 21l-4.3-4.3";
        public const string Trash = "M3 6h18M8 6V4h8v2M6 6l1 14h10l1-14";
        public const string Plus = "M12 5v14M5 12h14";
        public const string Send = "M5 12h14M13 6l6 6-6 6";
        public const string Lock = "M5 11h14v10H5zM8 11V7a4 4 0 0 1 8 0v4";
        public const string Restart = "M3 12a9 9 0 1 0 3-6.7L3 8M3 3v5h5";
        public const string Power = "M12 2v10M18.4 6.6a9 9 0 1 1-12.8 0";
        public const string Wifi = "M5 12.6a10 10 0 0 1 14 0M8.5 16a5 5 0 0 1 7 0M2 8.8a15 15 0 0 1 20 0M12 20h.01";
        public const string Bluetooth = "M7 7l10 10-5 5V2l5 5L7 17";
        public const string Contrast = "M12 2a10 10 0 1 0 0 20a10 10 0 1 0 0-20M12 2v20";
        public const string Copy = "M9 9h12v12H9zM5 15H3V3h12v2";
        public const string Close = "M18 6L6 18M6 6l12 12";
        public const string Terminal = "M4 17l6-6-6-6M12 19h8";
        public const string Folder = "M3 6h6l2 2h10v11H3z";
        public const string Shield = "M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10";
        public const string Bold = "M6 4h8a4 4 0 0 1 0 8H6zM6 12h9a4 4 0 0 1 0 8H6z";
        public const string Italic = "M19 4h-9M14 20H5M15 4L9 20";
        public const string Strike = "M16 4H9a3 3 0 0 0-2.8 4M14 12a4 4 0 0 1 0 8H6M4 12h16";
        public const string ListBullet = "M8 6h13M8 12h13M8 18h13M3 6h.01M3 12h.01M3 18h.01";
        public const string ListNumber = "M10 6h11M10 12h11M10 18h11M4 6h1v4M4 10h2M6 18H4c0-1 2-2 2-3s-1-1.5-2-1";
        public const string ListCheck = "M3 6l2 2 3-3M3 13l2 2 3-3M11 7h10M11 14h10M11 20h10";
        public const string Battery2 = "M2 7h16v10H2zM22 11v2";
        public const string MapPin = "M12 22s7-6.3 7-12a7 7 0 1 0-14 0c0 5.7 7 12 7 12M12 7a3 3 0 1 0 0 6a3 3 0 1 0 0-6";
        public const string Mic = "M12 2a3 3 0 0 0-3 3v7a3 3 0 0 0 6 0V5a3 3 0 0 0-3-3M19 10v2a7 7 0 0 1-14 0v-2M12 19v3";
    }
}
