using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Isle.Native;
using Isle.Services;

namespace Isle.Helpers
{
    /// <summary>
    /// Режим выреза, как на MacBook: сверху экрана Windows резервирует чёрную полосу высотой с чёлку
    /// (так же, как место под панель задач) — развёрнутые окна, в том числе браузер, встают ниже неё,
    /// а чёлка сливается с полосой, как вырез в экране. «В браузере» — полоса есть, только пока впереди браузер.
    /// </summary>
    public static class NotchBar
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge; public Win32.RECT rc; public IntPtr lParam; }
        [DllImport("shell32.dll")] private static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hgt, bool repaint);
        private const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABE_TOP = 1, ABN_POSCHANGED = 1;

        private static readonly string[] Browsers =
        {
            "chrome", "msedge", "firefox", "opera", "opera_gx", "browser", "brave", "vivaldi", "comet", "arc", "zen",
            "librewolf", "waterfox", "thorium", "chromium", "iexplore", "floorp", "yandex", "atlas",
        };

        private static Window _bar;
        private static IntPtr _hwnd;
        private static uint _callback;
        private static bool _registered;
        /// <summary>Полоса сейчас зарезервирована (чёлка не «тянется» к курсору — под ней вкладки браузера).</summary>
        public static bool Active => _registered;
        private static Win32.WinEventProc _fgProc;
        private static bool _hooked;
        private static readonly System.Windows.Threading.DispatcherTimer Debounce = new() { Interval = TimeSpan.FromMilliseconds(500) };

        /// <summary>Высота полосы в пикселях экрана (чёлка в свёрнутом виде).</summary>
        public static Func<int> Height { get; set; } = () => 34;

        /// <summary>Пересчитать по настройке и тому, что сейчас впереди. Вызывается при смене настройки и переднего окна.</summary>
        public static void Update()
        {
            var mode = AppSettings.Current.NotchMode ?? "off";
            if (mode == "browser" && !_hooked)
            {
                _hooked = true;
                _fgProc = (_, __, hwnd, ___, ____, _____, ______) => { Debounce.Stop(); Debounce.Start(); };
                Win32.SetWinEventHook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _fgProc, 0, 0, Win32.WINEVENT_OUTOFCONTEXT);
                Debounce.Tick += (_, __) => { Debounce.Stop(); Update(); };
                // развернули браузер или вернули в окно — событие «другое окно впереди» не приходит, поэтому проверяем раз в 0,7 с
                ZoomWatch.Tick += (_, __) =>
                {
                    if ((AppSettings.Current.NotchMode ?? "off") != "browser") { ZoomWatch.Stop(); return; }
                    var fg = Win32.GetForegroundWindow();
                    bool zoomed = fg != IntPtr.Zero && Win32.IsZoomed(fg);
                    if (fg == _lastFg && zoomed == _lastZoomed) return;
                    _lastFg = fg; _lastZoomed = zoomed;
                    Update();
                };
            }
            if (mode == "browser") ZoomWatch.Start(); else ZoomWatch.Stop();
            bool want = mode == "always" || (mode == "browser" && BrowserInFront());
            if (want) Register(); else Unregister();
        }

        private static readonly System.Windows.Threading.DispatcherTimer ZoomWatch = new() { Interval = TimeSpan.FromMilliseconds(700) };
        private static IntPtr _lastFg;
        private static bool _lastZoomed;

        /// <summary>Впереди браузер, развёрнутый на весь экран. Браузер в обычном окне полосу не включает — там она только мешала бы.</summary>
        private static bool BrowserInFront()
        {
            var name = Win32.ForegroundProcessName().ToLowerInvariant();
            if (name == System.Diagnostics.Process.GetCurrentProcess().ProcessName.ToLowerInvariant()) return _registered; // своё окно впереди — не дёргаем
            if (!Browsers.Contains(name)) return false;
            var fg = Win32.GetForegroundWindow();
            return fg != IntPtr.Zero && Win32.IsZoomed(fg);
        }

        private static void Register()
        {
            if (_registered) { Place(); return; }
            if (_bar == null)
            {
                _bar = new Window
                {
                    WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false,
                    Background = Brushes.Black, Topmost = true, Width = 10, Height = 10, Left = 0, Top = -100, Title = "Isle notch",
                };
                _bar.SourceInitialized += (_, __) =>
                {
                    _hwnd = new WindowInteropHelper(_bar).Handle;
                    int ex = Win32.GetWindowLong(_hwnd, -20);
                    Win32.SetWindowLong(_hwnd, -20, ex | Win32.WS_EX_TOOLWINDOW | 0x08000000 /* WS_EX_NOACTIVATE */);
                    HwndSource.FromHwnd(_hwnd)?.AddHook(Hook);
                };
                _bar.Show();
            }
            _callback = _callback == 0 ? RegisterWindowMessage("IsleNotchBar") : _callback;
            var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd, uCallbackMessage = _callback };
            SHAppBarMessage(ABM_NEW, ref abd);
            _registered = true;
            _bar.Visibility = Visibility.Visible;
            Place();
            Journal.Info("notch", "режим выреза: полоса " + Height() + " px сверху");
        }

        /// <summary>Попросить у Windows полосу сверху основного экрана и поставить туда окно.</summary>
        private static void Place()
        {
            if (!_registered || _hwnd == IntPtr.Zero) return;
            int h = Math.Max(16, Height());
            var mon = Win32.MonitorFromWindow(_hwnd, 1 /* PRIMARY */);
            var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
            Win32.GetMonitorInfo(mon, ref mi);
            var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd, uEdge = ABE_TOP,
                rc = new Win32.RECT { Left = mi.rcMonitor.Left, Top = mi.rcMonitor.Top, Right = mi.rcMonitor.Right, Bottom = mi.rcMonitor.Top + h } };
            SHAppBarMessage(ABM_QUERYPOS, ref abd);
            abd.rc.Bottom = abd.rc.Top + h;
            SHAppBarMessage(ABM_SETPOS, ref abd);
            MoveWindow(_hwnd, abd.rc.Left, abd.rc.Top, abd.rc.Right - abd.rc.Left, h, true);
            // чёлка всегда над полосой
            AppHost.Window?.KeepIslandOnTop();
        }

        public static void Unregister()
        {
            if (!_registered) return;
            var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };
            SHAppBarMessage(ABM_REMOVE, ref abd);
            _registered = false;
            if (_bar != null) _bar.Visibility = Visibility.Hidden;
            Journal.Info("notch", "режим выреза: полоса убрана");
        }

        private static IntPtr Hook(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            // Windows просит пересчитать место (сменилось разрешение, панель задач, другая полоса)
            if (_callback != 0 && msg == _callback && w.ToInt32() == (int)ABN_POSCHANGED) Place();
            return IntPtr.Zero;
        }
    }
}
