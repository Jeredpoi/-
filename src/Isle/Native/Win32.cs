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
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
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
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
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

        /// <summary>Переключает окно на следующую установленную раскладку. Возвращает код языка (EN, RU…).</summary>
        public static string SwitchToNextLayout(IntPtr target)
        {
            try
            {
                if (target == IntPtr.Zero) return null;
                int n = GetKeyboardLayoutList(0, null);
                if (n <= 1) return null;
                var list = new IntPtr[n];
                GetKeyboardLayoutList(n, list);
                uint tid = GetWindowThreadProcessId(target, out _);
                var cur = GetKeyboardLayout(tid);
                int i = Array.IndexOf(list, cur);
                var next = list[(i + 1 + n) % n];
                PostMessage(target, 0x0050, IntPtr.Zero, next); // WM_INPUTLANGCHANGEREQUEST
                int lang = (int)((long)next & 0xFFFF);
                return System.Globalization.CultureInfo.GetCultureInfo(lang).TwoLetterISOLanguageName.ToUpperInvariant();
            }
            catch { return null; }
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
        public static System.Windows.Media.ImageSource ShellIcon(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (IconCache.TryGetValue(path, out var cached)) return cached;
            System.Windows.Media.ImageSource result = null;
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
