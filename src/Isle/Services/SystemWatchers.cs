using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Isle.Native;

namespace Isle.Services
{
    /// <summary>Следит за раскладкой, Caps Lock, батареей и полноэкранными приложениями.</summary>
    public class SystemWatchers
    {
        private readonly DispatcherTimer _fast = new() { Interval = TimeSpan.FromMilliseconds(200) };
        private readonly DispatcherTimer _slow = new() { Interval = TimeSpan.FromSeconds(5) };
        private int _lastLang = -1;
        private bool? _lastCaps;
        private bool? _lastCharging;
        private int _lastBattery = -1;
        private bool _warnedLow;
        private bool _lastFullscreen;

        public string LanguageCode { get; private set; } = "EN";
        public int BatteryPercent { get; private set; } = -1;
        public bool Charging { get; private set; }
        public bool HasBattery { get; private set; }

        public event Action<bool> FullscreenChanged;
        public event Action LanguageChanged;

        public void Start()
        {
            _fast.Tick += (_, __) => Fast();
            _slow.Tick += (_, __) => Slow();
            _fast.Start();
            _slow.Start();
            Slow();
        }

        private void Fast()
        {
            var s = AppSettings.Current;

            // Раскладка
            try
            {
                var fg = Win32.GetForegroundWindow();
                uint tid = Win32.GetWindowThreadProcessId(fg, out _);
                var hkl = Win32.GetKeyboardLayout(tid);
                int langId = (int)((long)hkl & 0xFFFF);
                if (langId != 0 && langId != _lastLang)
                {
                    bool first = _lastLang == -1;
                    _lastLang = langId;
                    LanguageCode = LangCode(langId);
                    LanguageChanged?.Invoke();
                    if (!first && s.LayoutToasts)
                        ToastBus.Show(new Toast
                        {
                            Kind = ToastKind.Layout, Title = LanguageCode, Icon = Icons.Keyboard,
                            ReplaceKey = "layout", Duration = TimeSpan.FromSeconds(1.2)
                        });
                }
            }
            catch { }

            // Caps Lock
            bool caps = (Win32.GetKeyState(Win32.VK_CAPITAL) & 1) != 0;
            if (_lastCaps.HasValue && caps != _lastCaps.Value && s.CapsToasts)
                ToastBus.Show(new Toast
                {
                    Kind = ToastKind.Caps, Title = caps ? "Caps Lock включён" : "Caps Lock выключен",
                    Icon = Icons.Caps, ReplaceKey = "caps", Duration = TimeSpan.FromSeconds(1.4)
                });
            _lastCaps = caps;
        }

        private void Slow()
        {
            var s = AppSettings.Current;
            // Батарея
            if (GetSystemPowerStatus(out var ps))
            {
                HasBattery = ps.BatteryFlag != 128 && ps.BatteryLifePercent != 255;
                Charging = ps.ACLineStatus == 1;
                BatteryPercent = ps.BatteryLifePercent == 255 ? -1 : ps.BatteryLifePercent;
                if (HasBattery && s.BatteryToasts)
                {
                    if (_lastCharging.HasValue && Charging != _lastCharging.Value)
                        ToastBus.Show(new Toast
                        {
                            Kind = ToastKind.Battery,
                            Title = Charging ? "Зарядка" : "От батареи",
                            Subtitle = BatteryPercent + "%",
                            Icon = Charging ? Icons.Bolt : Icons.Battery,
                            Accent = System.Windows.Media.Color.FromRgb(0x4A, 0xDE, 0x80),
                            Progress = BatteryPercent / 100.0
                        });
                    if (!Charging && BatteryPercent >= 0 && BatteryPercent <= 15 && !_warnedLow)
                    {
                        _warnedLow = true;
                        ToastBus.Show(new Toast
                        {
                            Kind = ToastKind.Battery, Title = "Низкий заряд", Subtitle = BatteryPercent + "%",
                            Icon = Icons.Battery, Accent = System.Windows.Media.Color.FromRgb(0xF8, 0x71, 0x71),
                            Important = true, Duration = TimeSpan.FromSeconds(4)
                        });
                    }
                    if (Charging || BatteryPercent > 20) _warnedLow = false;
                }
                _lastCharging = Charging;
                _lastBattery = BatteryPercent;
            }

            // Полноэкранные приложения (игры, видео, презентации)
            bool fs = IsForegroundFullscreen();
            if (fs != _lastFullscreen)
            {
                _lastFullscreen = fs;
                FullscreenChanged?.Invoke(fs);
            }
        }

        public void CheckFullscreenNow() => Slow();

        private static bool IsForegroundFullscreen()
        {
            try
            {
                if (Win32.SHQueryUserNotificationState(out int state) == 0)
                {
                    // 3 = QUNS_RUNNING_D3D_FULL_SCREEN, 4 = QUNS_PRESENTATION_MODE
                    if (state == 3 || state == 4) return true;
                }
                var fg = Win32.GetForegroundWindow();
                if (fg == IntPtr.Zero || fg == Win32.GetDesktopWindow() || fg == Win32.GetShellWindow()) return false;
                var cls = new StringBuilder(64);
                Win32.GetClassName(fg, cls, cls.Capacity);
                var c = cls.ToString();
                if (c == "WorkerW" || c == "Progman" || c == "Shell_TrayWnd") return false;
                if (!Win32.GetWindowRect(fg, out var r)) return false;
                var mon = Win32.MonitorFromWindow(fg, 2);
                var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
                if (!Win32.GetMonitorInfo(mon, ref mi)) return false;
                return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top &&
                       r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
            }
            catch { return false; }
        }

        private static string LangCode(int langId)
        {
            try
            {
                var ci = CultureInfo.GetCultureInfo(langId);
                return ci.TwoLetterISOLanguageName.ToUpperInvariant();
            }
            catch { return "??"; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
            public int BatteryLifeTime, BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll")]
        private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
    }
}
