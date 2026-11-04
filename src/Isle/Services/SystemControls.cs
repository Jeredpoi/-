using System;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using Microsoft.Win32;
using Isle.Native;
using Radios = Windows.Devices.Radios;

namespace Isle.Services
{
    public static class SystemControls
    {
        private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        public static bool IsDarkTheme
        {
            get
            {
                using var k = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                return (k?.GetValue("AppsUseLightTheme") as int? ?? 1) == 0;
            }
        }

        public static void SetDarkTheme(bool dark)
        {
            using var k = Registry.CurrentUser.CreateSubKey(PersonalizeKey);
            k.SetValue("AppsUseLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
            k.SetValue("SystemUsesLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
            BroadcastThemeChange();
        }

        private static void BroadcastThemeChange()
        {
            try { SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "ImmersiveColorSet", 0x0002, 200, out _); } catch { }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

        public static (string name, string status, bool up) Network()
        {
            try
            {
                var nics = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                                n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                                n.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                                !n.Description.ToLowerInvariant().Contains("virtual") &&
                                !n.Description.ToLowerInvariant().Contains("hyper-v"))
                    .ToList();
                var wifi = nics.FirstOrDefault(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
                if (wifi != null) return ("Wi-Fi", "Подключено", true);
                var eth = nics.FirstOrDefault(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet);
                if (eth != null) return ("Ethernet", "Подключено", true);
                return ("Сеть", "Нет подключения", false);
            }
            catch { return ("Сеть", "—", false); }
        }

        public static async Task<bool?> GetRadio(Radios.RadioKind kind)
        {
            try
            {
                var radios = await Radios.Radio.GetRadiosAsync();
                var r = radios.FirstOrDefault(x => x.Kind == kind);
                if (r == null) return null;
                return r.State == Radios.RadioState.On;
            }
            catch { return null; }
        }

        public static async Task<bool?> ToggleRadio(Radios.RadioKind kind)
        {
            try
            {
                var access = await Radios.Radio.RequestAccessAsync();
                if (access != Radios.RadioAccessStatus.Allowed) return null;
                var radios = await Radios.Radio.GetRadiosAsync();
                var r = radios.FirstOrDefault(x => x.Kind == kind);
                if (r == null) return null;
                var on = r.State != Radios.RadioState.On;
                await r.SetStateAsync(on ? Radios.RadioState.On : Radios.RadioState.Off);
                return on;
            }
            catch { return null; }
        }

        public static void Lock() => Win32.LockWorkStation();
        public static void Sleep() => Win32.SetSuspendState(false, false, false);
        public static void Restart() => Run("shutdown", "/r /t 0");
        public static void Shutdown() => Run("shutdown", "/s /t 0");
        public static void OpenSettings(string uri = "ms-settings:") => Shell(uri);
        public static void TaskManager() => Run("taskmgr", "");

        public static void Shell(string target, string args = "")
        {
            try { Process.Start(new ProcessStartInfo(target, args) { UseShellExecute = true }); } catch { }
        }

        private static void Run(string exe, string args)
        {
            try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true }); } catch { }
        }

        public static bool RunCommand(string line)
        {
            line = line.Trim();
            if (line.Length == 0) return false;
            try
            {
                if (Uri.TryCreate(line, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https" || u.Scheme.StartsWith("ms-")))
                {
                    Shell(line);
                    return true;
                }
                if (!line.Contains(' ') && line.Contains('.') && !line.Contains('\\'))
                {
                    Shell("https://" + line);
                    return true;
                }
                if (line.StartsWith(">"))
                {
                    Process.Start(new ProcessStartInfo("cmd.exe", "/k " + line.Substring(1).Trim()) { UseShellExecute = true });
                    return true;
                }
                if (line.StartsWith("?"))
                {
                    Shell("https://www.google.com/search?q=" + Uri.EscapeDataString(line.Substring(1).Trim()));
                    return true;
                }
                int sp = line.IndexOf(' ');
                if (sp > 0) Shell(line.Substring(0, sp), line.Substring(sp + 1));
                else Shell(line);
                return true;
            }
            catch { return false; }
        }

        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool Autostart
        {
            get
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                return k?.GetValue("Isle") != null;
            }
            set
            {
                using var k = Registry.CurrentUser.CreateSubKey(RunKey);
                if (value) k.SetValue("Isle", "\"" + Environment.ProcessPath + "\"");
                else k.DeleteValue("Isle", false);
            }
        }
    }
}
