using System;
using System.Globalization;
using System.Linq;
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
        private IntPtr _lastFg;

        private static readonly string[] ShellClasses = { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "Windows.UI.Core.CoreWindow",
            // консоль отдаёт раскладку по умолчанию, а не ту, что включена — из-за неё значок врал «EN»
            "ConsoleWindowClass" };

        private static IntPtr _skipHwnd;
        private static bool _skipResult;

        /// <summary>Окно, чья раскладка не настоящая (рабочий стол, обои, сам остров). Кэшируем по окну — проверка дорогая.</summary>
        private static bool IsShellOrOwn(IntPtr hwnd)
        {
            if (hwnd == _skipHwnd) return _skipResult;
            _skipResult = IsShellOrOwnUncached(hwnd);
            _skipHwnd = hwnd;
            return _skipResult;
        }

        private static bool IsShellOrOwnUncached(IntPtr hwnd)
        {
            try
            {
                var sb = new StringBuilder(64);
                Win32.GetClassName(hwnd, sb, sb.Capacity);
                if (ShellClasses.Contains(sb.ToString())) return true;
                Win32.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == (uint)Environment.ProcessId) return true; // сам Isle (поле ввода в острове)
                using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                var name = p.ProcessName.ToLowerInvariant();
                return name.StartsWith("wallpaper") || name == "lively" || name == "explorer" && sb.ToString() != "CabinetWClass";
            }
            catch { return false; }
        }
        private bool? _lastCaps;
        private bool? _lastCharging;
        private int _lastBattery = -1;
        private bool _warnedLow;
        private bool _lastFullscreen;

        // до первого «настоящего» окна берём раскладку ввода по умолчанию, а не всегда «EN»
        /// <summary>Последнее «настоящее» окно (не рабочий стол, не обои, не сам остров) — туда переключаем раскладку с кнопки.</summary>
        public IntPtr LastRealWindow => _lastFg;

        /// <summary>Раскладку переключил сам Isle — сразу показываем новую, не дожидаясь, пока окно её применит.</summary>
        public void ForceLanguage(int langId)
        {
            if (langId == 0 || langId == _lastLang) return;
            _lastLang = langId;
            LanguageCode = LangCode(langId);
            LanguageChanged?.Invoke();
        }

        public string LanguageCode { get; private set; } = LangCode((int)((long)Win32.GetKeyboardLayout(0) & 0xFFFF));
        public int BatteryPercent { get; private set; } = -1;
        public bool Charging { get; private set; }
        public bool HasBattery { get; private set; }

        public event Action<bool> FullscreenChanged;
        /// <summary>Сейчас на переднем плане полноэкранное приложение (игра, видео).</summary>
        public bool IsFullscreen => _lastFullscreen;
        public event Action LanguageChanged;

        public void Start()
        {
            _fast.Tick += (_, __) => Fast();
            _slow.Tick += (_, __) => Slow();
            _fast.Start();
            _slow.Start();
            Slow();
        }

        /// <summary>Перечитать раскладку сейчас (после нажатия кнопки раскладки в острове).</summary>
        public void RefreshLanguage() => ReadLanguage(AppSettings.Current.LayoutToasts);

        private void ReadLanguage(bool toasts)
        {
            // Раскладка — того поля, где стоит курсор ввода, а не просто «окна»
            try
            {
                var fg = Win32.GetForegroundWindow();
                // Рабочий стол, панель задач, обои (Wallpaper Engine) и сам остров «говорят» английскую раскладку по умолчанию —
                // из-за них раньше выскакивала ложная плашка «EN», хотя раскладку никто не менял. Их пропускаем.
                if (fg == IntPtr.Zero || IsShellOrOwn(fg)) return;
                var hkl = Win32.LayoutOf(Win32.FocusWindowOf(fg));
                int langId = (int)((long)hkl & 0xFFFF);
                bool sameWindow = fg == _lastFg;
                _lastFg = fg;
                if (langId != 0 && langId != _lastLang)
                {
                    bool first = _lastLang == -1;
                    _lastLang = langId;
                    LanguageCode = LangCode(langId);
                    LanguageChanged?.Invoke();
                    // плашку показываем только если раскладка сменилась в том же окне (её переключили),
                    // а не потому, что перешли в другое окно со своей раскладкой
                    if (!first && sameWindow && toasts)
                        ToastBus.Show(new Toast
                        {
                            Kind = ToastKind.Layout, Title = LangName(langId), Badge = LanguageCode, Icon = Icons.Keyboard,
                            ReplaceKey = "layout", Duration = TimeSpan.FromSeconds(1.2)
                        });
                }
            }
            catch { }

        }

        private void Fast()
        {
            var s = AppSettings.Current;

            ReadLanguage(s.LayoutToasts);

            // Caps Lock — страховка для случаев без перехвата клавиатуры (кнопка на экранной клавиатуре, программа).
            // GetKeyState у фоновой программы отстаёт: сразу после нажатия ему не верим, состояние уже знает OnCapsKey.
            bool caps = (Win32.GetKeyState(Win32.VK_CAPITAL) & 1) != 0;
            if (DateTime.Now - _capsKeyAt < TimeSpan.FromMilliseconds(700)) return;
            // расхождение должно продержаться 3 опроса подряд (~0,6 с) — иначе это просто запаздывание GetKeyState
            if (_lastCaps.HasValue && caps != _lastCaps.Value && ++_capsMismatch < 3) return;
            _capsMismatch = 0;
            if (_lastCaps.HasValue && caps != _lastCaps.Value)
            {
                Journal.Info("caps", "опрос: Caps Lock " + (caps ? "вкл" : "выкл") + (DateTime.Now - _capsKeyAt < TimeSpan.FromSeconds(3) ? " (поправка после нажатия)" : ""));
                ShowCaps(caps);
            }
            _lastCaps = caps;
        }

        private DateTime _capsKeyAt = DateTime.MinValue;
        private int _capsMismatch;

        /// <summary>
        /// Нажали Caps Lock (из перехвата клавиатуры): переключаем плашку сразу, без ожидания опроса.
        /// Если состояние на самом деле не сменилось (клавиша переназначена), опрос через 0,7 с поправит.
        /// </summary>
        public void OnCapsKey()
        {
            if (!_lastCaps.HasValue) _lastCaps = (Win32.GetKeyState(Win32.VK_CAPITAL) & 1) != 0;
            bool caps = !_lastCaps.Value;
            _lastCaps = caps;
            _capsKeyAt = DateTime.Now;
            _capsMismatch = 0;
            Journal.Info("caps", "нажатие: Caps Lock " + (caps ? "вкл" : "выкл"));
            ShowCaps(caps);
        }

        private static void ShowCaps(bool caps)
        {
            if (!AppSettings.Current.CapsToasts) return;
            ToastBus.Show(new Toast
            {
                Kind = ToastKind.Caps, Title = caps ? "Caps Lock включён" : "Caps Lock выключен",
                Icon = Icons.Caps, On = caps, ReplaceKey = "caps", Duration = TimeSpan.FromSeconds(1.4)
            });
        }

        private void Slow()
        {
            var s = AppSettings.Current;
            // Батарея
            if (GetSystemPowerStatus(out var ps))
            {
                HasBattery = ps.BatteryFlag != 128 && ps.BatteryFlag != 255 && ps.BatteryLifePercent != 255; // 128 — батареи нет (обычный ПК), 255 — Windows не знает: тоже не показываем
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
                            Icon = Charging ? Icons.Bolt : Icons.Battery, On = Charging,
                            Accent = System.Windows.Media.Color.FromRgb(0x4A, 0xDE, 0x80),
                            Progress = BatteryPercent / 100.0
                        });
                    if (!Charging && BatteryPercent >= 0 && BatteryPercent <= 15 && !_warnedLow)
                    {
                        _warnedLow = true;
                        ToastBus.Show(new Toast
                        {
                            Kind = ToastKind.Error, Title = "Низкий заряд", Subtitle = BatteryPercent + "% · подключи зарядку",
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

        /// <summary>
        /// Процессы рабочего стола, которые растягивают окно на весь экран, но играми/видео не являются
        /// (Fences, обои, виджеты). Из-за них остров раньше ошибочно прятался.
        /// </summary>
        private static readonly string[] DesktopTools =
        {
            "explorer", "fences", "fencessrv", "fencesmenu64", "desktopfences", "stardock", "objectdock", "start11",
            "rainmeter", "wallpaper32", "wallpaper64", "wallpaperengine", "lively", "livelywpf", "deskscapes",
            "nimi places", "tidytabs", "groupy", "displayfusion", "translucenttb", "roundedtb", "isle"
        };

        private static bool IsForegroundFullscreen()
        {
            try
            {
                if (Win32.SHQueryUserNotificationState(out int state) == 0)
                {
                    // 3 = QUNS_RUNNING_D3D_FULL_SCREEN (игры), 4 = QUNS_PRESENTATION_MODE
                    if (state == 3 || state == 4) return true;
                }
                var fg = Win32.GetForegroundWindow();
                if (fg == IntPtr.Zero || fg == Win32.GetDesktopWindow() || fg == Win32.GetShellWindow()) return false;
                // окна самого Isle (слой снимка экрана на весь экран, настройки) — не «полноэкранная игра»
                Win32.GetWindowThreadProcessId(fg, out uint fgPid);
                if (fgPid == (uint)Environment.ProcessId) return false;
                var cls = new StringBuilder(64);
                Win32.GetClassName(fg, cls, cls.Capacity);
                var c = cls.ToString();
                if (c == "WorkerW" || c == "Progman" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd") return false;

                var proc = ProcessOf(fg);
                if (DesktopTools.Any(t => proc.StartsWith(t))) return false;

                if (!Win32.GetWindowRect(fg, out var r)) return false;
                var mon = Win32.MonitorFromWindow(fg, 2);
                var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
                if (!Win32.GetMonitorInfo(mon, ref mi)) return false;
                return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top &&
                       r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
            }
            catch { return false; }
        }

        private static string ProcessOf(IntPtr hwnd)
        {
            try
            {
                Win32.GetWindowThreadProcessId(hwnd, out uint pid);
                using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                return p.ProcessName.ToLowerInvariant();
            }
            catch { return ""; }
        }

        public static string LangName(int langId)
        {
            try
            {
                var n = CultureInfo.GetCultureInfo(langId).Parent.NativeName;
                return n.Length > 0 ? char.ToUpper(n[0]) + n.Substring(1) : n;
            }
            catch { return ""; }
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
