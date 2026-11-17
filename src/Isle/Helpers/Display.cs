using System;
using System.Runtime.InteropServices;

namespace Isle.Helpers
{
    /// <summary>Частота обновления монитора (Гц) — для журнала и сверки плавности анимаций.</summary>
    public static class Display
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public int l, t, r, b, wl, wt, wr, wb;
            public int dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

        /// <summary>Гц монитора, на котором точка (пиксели экрана); 0 — не удалось узнать.</summary>
        public static int RefreshHz(int x, int y)
        {
            try
            {
                var mon = MonitorFromPoint(new POINT { X = x, Y = y }, 2 /* NEAREST */);
                var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                string dev = GetMonitorInfo(mon, ref mi) ? mi.szDevice : null;
                var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                return EnumDisplaySettings(dev, -1 /* ENUM_CURRENT_SETTINGS */, ref dm) && dm.dmDisplayFrequency > 1 ? dm.dmDisplayFrequency : 0;
            }
            catch { return 0; }
        }
    }
}
