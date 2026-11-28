using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Isle.Native
{
    internal static class Win32
    {
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WM_HOTKEY = 0x0312;
        public const int WM_CLIPBOARDUPDATE = 0x031D;
        public const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_WIN = 0x0008, MOD_NOREPEAT = 0x4000;
        public const int VK_CAPITAL = 0x14;
        public const int VK_CONTROL = 0x11;
        public const int VK_V = 0x56;
        public const int VK_LWIN = 0x5B;

        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(uint idThread);
        [DllImport("user32.dll")] public static extern short GetKeyState(int nVirtKey);
        [DllImport("user32.dll")] public static extern bool LockWorkStation();
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
        [DllImport("user32.dll")] public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("powrprof.dll", SetLastError = true)] public static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);
        [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int pquns);
        [DllImport("kernel32.dll")] public static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);

        /// <summary>0x11 = WDA_EXCLUDEFROMCAPTURE: окно не видно на скриншотах и в записи экрана (Win10 2004+).</summary>
        [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
        /// <summary>Дождаться, пока DWM выведет следующий кадр (окно действительно исчезло с экрана).</summary>
        [DllImport("dwmapi.dll")] public static extern int DwmFlush();
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [StructLayout(LayoutKind.Sequential)]
        public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
        [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        /// <summary>
        /// Надёжно делает окно активным (Windows часто запрещает это фоновым программам):
        /// подключаемся к потоку активного окна, ставим своё окно на передний план и отключаемся.
        /// </summary>
        public static void ForceForeground(IntPtr hwnd)
        {
            var fg = GetForegroundWindow();
            if (fg == hwnd) return;
            uint fgThread = GetWindowThreadProcessId(fg, out _);
            uint me = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
            try
            {
                // «Нажатие» Alt снимает с процесса запрет на смену переднего окна
                keybd_event(0x12, 0, 0, IntPtr.Zero);
                keybd_event(0x12, 0, 2, IntPtr.Zero);
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
                SetFocus(hwnd);
            }
            finally { if (attached) AttachThreadInput(me, fgThread, false); }
        }

        [DllImport("user32.dll")] public static extern int GetKeyboardLayoutList(int n, IntPtr[] list);

        [StructLayout(LayoutKind.Sequential)]
        private struct GUITHREADINFO
        {
            public int cbSize, flags;
            public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
            public int l, t, r, b;
        }
        [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO info);

        /// <summary>Окно с фокусом ввода внутри окна target (поле ввода в браузере, мессенджере и т. п.).</summary>
        public static IntPtr FocusWindowOf(IntPtr target)
        {
            uint tid = GetWindowThreadProcessId(target, out _);
            var gi = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            return GetGUIThreadInfo(tid, ref gi) && gi.hwndFocus != IntPtr.Zero ? gi.hwndFocus : target;
        }

        public static IntPtr LayoutOf(IntPtr hwnd) => GetKeyboardLayout(GetWindowThreadProcessId(hwnd, out _));

        public static string LangCode(IntPtr hkl)
        {
            try { return System.Globalization.CultureInfo.GetCultureInfo((int)((long)hkl & 0xFFFF)).TwoLetterISOLanguageName.ToUpperInvariant(); }
            catch { return null; }
        }

        /// <summary>
        /// Просит окно (точнее, его поле с фокусом) включить следующую установленную раскладку.
        /// Возвращает раскладку, которую попросили, или IntPtr.Zero.
        /// </summary>
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("imm32.dll")] private static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

        public static IntPtr RequestNextLayout(IntPtr target)
        {
            try
            {
                if (target == IntPtr.Zero) return IntPtr.Zero;
                int n = GetKeyboardLayoutList(0, null);
                if (n <= 1) return IntPtr.Zero;
                var list = new IntPtr[n];
                GetKeyboardLayoutList(n, list);
                var focus = FocusWindowOf(target);
                // сравниваем по языку (младшие 16 бит): у окон старшая часть HKL бывает другой, и полный код
                // в списке не находился — Isle «переключал» на ту же раскладку, и ничего не происходило
                int cur = (int)((long)LayoutOf(focus) & 0xFFFF);
                int i = Array.FindIndex(list, h => (int)((long)h & 0xFFFF) == cur);
                var next = list[(i + 1 + n) % n];
                // WM_INPUTLANGCHANGEREQUEST, INPUTLANGCHANGE_SYSCHARSET. Поле с фокусом, корневое окно и его IME-окно:
                // Chrome и другие окна с собственным вводом принимают просьбу не через то окно, где фокус.
                PostMessage(focus, 0x0050, new IntPtr(1), next);
                var root = GetAncestor(target, 2); // GA_ROOT
                if (root == IntPtr.Zero) root = target;
                if (root != focus) PostMessage(root, 0x0050, new IntPtr(1), next);
                var ime = ImmGetDefaultIMEWnd(root);
                if (ime != IntPtr.Zero) PostMessage(ime, 0x0050, new IntPtr(1), next);
                return next;
            }
            catch { return IntPtr.Zero; }
        }

        /// <summary>
        /// Нажать сочетание, которым у пользователя переключается раскладка (Alt+Shift, Ctrl+Shift или Ё),
        /// без клавиши Win: Win открывает системную плашку языка, и из-за неё мигают живые обои (Wallpaper Engine).
        /// Сочетание отключено в Windows — ничего не жмём (Win+Пробел в играх «залипал» и выкидывал на рабочий стол).
        /// </summary>
        /// <returns>false — сочетание отключено в Windows, ничего не нажато.</returns>
        public static bool PressLayoutHotkey()
        {
            switch (LayoutHotkeyValue())
            {
                case "2": SendKeyCombo(0xA2, 0xA0); return true; // Ctrl+Shift
                case "4": SendKeyCombo(0xC0); return true;       // Ё (`)
                case "3": return false;                           // сочетание отключено — Win+Пробел НЕ жмём (в играх «залипал» Win)
                default: SendKeyCombo(0xA4, 0xA0); return true;  // Alt+Shift (по умолчанию в Windows)
            }
        }

        private static string LayoutHotkeyValue()
        {
            string v = null;
            try
            {
                v = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Keyboard Layout\Toggle", "Language Hotkey", null) as string
                    ?? Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Keyboard Layout\Toggle", "Hotkey", null) as string;
            }
            catch { }
            return (v ?? "1").Trim();
        }

        /// <summary>Включено ли в Windows сочетание для смены раскладки (не «3» — отключено).</summary>
        public static bool LayoutHotkeyEnabled() => LayoutHotkeyValue() != "3";

        /// <summary>
        /// Страховка: если после нашего нажатия клавиша-модификатор всё ещё числится зажатой — отпускаем её.
        /// Иначе следующий «Пробел» в игре мог стать «Win+Пробел» и выкинуть из полноэкранного режима.
        /// </summary>
        public static void ReleaseModifiers()
        {
            foreach (ushort vk in new ushort[] { 0x5B, 0x5C, 0xA4, 0xA5, 0xA0, 0xA1, 0xA2, 0xA3 })
            {
                if ((GetAsyncKeyState(vk) & 0x8000) == 0) continue;
                var up = new INPUT[] { new() { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } } } };
                SendInput(1, up, Marshal.SizeOf(typeof(INPUT)));
            }
        }

        /// <summary>Переключить раскладку в активном окне на следующую.</summary>
        public static void NextKeyboardLayout(IntPtr target)
        {
            // WM_INPUTLANGCHANGEREQUEST, HKL_NEXT
            PostMessage(target == IntPtr.Zero ? GetForegroundWindow() : target, 0x0050, IntPtr.Zero, new IntPtr(1));
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SHGetFileInfo(string path, uint attrs, ref SHFILEINFO info, uint size, uint flags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        private static readonly System.Collections.Generic.Dictionary<string, System.Windows.Media.ImageSource> IconCache = new();

        /// <summary>Настоящая иконка файла/папки/ярлыка из оболочки Windows.</summary>
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHDefExtractIcon(string iconFile, int index, uint flags, out IntPtr large, out IntPtr small, uint size);

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")] private class ShellLinkCo { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int cch, IntPtr fd, uint flags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder dir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder args, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int cmd);
            void SetShowCmd(int cmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder iconPath, int cch, out int index);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int index);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string rel, uint reserved);
            void Resolve(IntPtr hwnd, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }

        /// <summary>
        /// Откуда брать значок ярлыка без стрелки: (файл со значком, номер) или файл, на который ярлык ведёт.
        /// .lnk — через IShellLink, .url — из IconFile/IconIndex (так у ярлыков Steam и сайтов).
        /// </summary>
        private static (string iconFile, int index, string target) ResolveShortcut(string path)
        {
            try
            {
                var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".url")
                {
                    string file = null; int idx = 0;
                    foreach (var line in System.IO.File.ReadLines(path))
                    {
                        if (line.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase)) file = line.Substring(9).Trim();
                        else if (line.StartsWith("IconIndex=", StringComparison.OrdinalIgnoreCase)) int.TryParse(line.Substring(10).Trim(), out idx);
                    }
                    return (file, idx, null);
                }
                if (ext != ".lnk") return (null, 0, null);
                var link = (IShellLinkW)new ShellLinkCo();
                ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Load(path, 0);
                var icon = new System.Text.StringBuilder(520);
                link.GetIconLocation(icon, icon.Capacity, out int index);
                var target = new System.Text.StringBuilder(520);
                link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
                Marshal.ReleaseComObject(link);
                return (icon.Length > 0 ? icon.ToString() : null, index, target.Length > 0 ? target.ToString() : null);
            }
            catch { return (null, 0, null); }
        }

        private static System.Windows.Media.ImageSource FromHIcon(IntPtr h)
        {
            if (h == IntPtr.Zero) return null;
            try
            {
                var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(h, System.Windows.Int32Rect.Empty,
                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally { DestroyIcon(h); }
        }

        /// <summary>Значок из файла (exe, dll, ico) размером 48 px — чётче, чем 32 px из SHGetFileInfo.</summary>
        private static System.Windows.Media.ImageSource ExtractIcon(string file, int index)
        {
            if (string.IsNullOrEmpty(file)) return null;
            file = Environment.ExpandEnvironmentVariables(file);
            if (!System.IO.File.Exists(file)) return null;
            if (SHDefExtractIcon(file, index, 0, out var large, out var small, 48 | (16 << 16)) != 0) return null;
            if (small != IntPtr.Zero) DestroyIcon(small);
            return FromHIcon(large);
        }

        private static System.Windows.Media.ImageSource FileInfoIcon(string path)
        {
            var info = new SHFILEINFO();
            SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), 0x100 | 0x0); // SHGFI_ICON | SHGFI_LARGEICON
            return FromHIcon(info.hIcon);
        }

        /// <summary>Значок файла, программы или ярлыка. У ярлыков — значок самой программы, без стрелки.</summary>
        public static System.Windows.Media.ImageSource ShellIcon(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (IconCache.TryGetValue(path, out var cached)) return cached;
            System.Windows.Media.ImageSource result = null;
            try
            {
                var (iconFile, index, target) = ResolveShortcut(path);
                result = ExtractIcon(iconFile, index);
                if (result == null && !string.IsNullOrEmpty(target))
                {
                    if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) result = ExtractIcon(target, 0);
                    if (result == null && (System.IO.File.Exists(target) || System.IO.Directory.Exists(target))) result = FileInfoIcon(target);
                }
                if (result == null && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) result = ExtractIcon(path, 0);
            }
            catch { }
            if (result != null) { IconCache[path] = result; return result; }
            try
            {
                var info = new SHFILEINFO();
                // SHGFI_ICON | SHGFI_LARGEICON
                SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), 0x100 | 0x0);
                if (info.hIcon != IntPtr.Zero)
                {
                    var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(info.hIcon, System.Windows.Int32Rect.Empty,
                        System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    DestroyIcon(info.hIcon);
                    result = src;
                }
            }
            catch { }
            IconCache[path] = result;
            return result;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk, wScan;
            public uint dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        public const uint INPUT_KEYBOARD = 1;
        public const uint KEYEVENTF_KEYUP = 0x0002;

        public static void SendKeyCombo(params ushort[] keys)
        {
            var inputs = new INPUT[keys.Length * 2];
            for (int i = 0; i < keys.Length; i++)
            {
                inputs[i] = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = keys[i] } } };
            }
            for (int i = 0; i < keys.Length; i++)
            {
                inputs[keys.Length + i] = new INPUT
                {
                    type = INPUT_KEYBOARD,
                    U = new InputUnion { ki = new KEYBDINPUT { wVk = keys[keys.Length - 1 - i], dwFlags = KEYEVENTF_KEYUP } }
                };
            }
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        [DllImport("user32.dll")] public static extern IntPtr GetClipboardOwner();

        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);

        /// <summary>Процесс, положивший данные в буфер (пусто — системная клавиша PrintScreen).</summary>
        public static string ClipboardOwnerProcess()
        {
            try
            {
                var h = GetClipboardOwner();
                if (h == IntPtr.Zero) return "";
                GetWindowThreadProcessId(h, out uint pid);
                using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                return p.ProcessName;
            }
            catch { return ""; }
        }

        public delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
        [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc proc, uint pid, uint tid, uint flags);
        public const uint EVENT_SYSTEM_FOREGROUND = 0x0003, WINEVENT_OUTOFCONTEXT = 0x0000;

        /// <summary>Имя процесса, которому принадлежит окно (пусто — не удалось).</summary>
        public static string ProcessNameOf(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return "";
                GetWindowThreadProcessId(hwnd, out uint pid);
                using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                return p.ProcessName;
            }
            catch { return ""; }
        }

        public static string ForegroundProcessName()
        {
            try
            {
                var h = GetForegroundWindow();
                GetWindowThreadProcessId(h, out uint pid);
                using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                return p.ProcessName;
            }
            catch { return ""; }
        }
    }
}
