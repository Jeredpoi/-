using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
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
                if (wifi != null) return ("Wi-Fi", WifiName() ?? "Подключено", true);
                var eth = nics.FirstOrDefault(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet);
                if (eth != null) return ("Ethernet", "Подключено", true);
                return ("Сеть", "Нет подключения", false);
            }
            catch { return ("Сеть", "—", false); }
        }

        /// <summary>Имя сети, к которой подключён компьютер (для Wi-Fi — её SSID).</summary>
        private static string WifiName()
        {
            try
            {
                var p = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
                if (p?.IsWlanConnectionProfile == true)
                {
                    var ssid = p.WlanConnectionProfileDetails?.GetConnectedSsid();
                    if (!string.IsNullOrWhiteSpace(ssid)) return ssid;
                }
                return string.IsNullOrWhiteSpace(p?.ProfileName) ? null : p.ProfileName;
            }
            catch { return null; }
        }

        private static string RunNetsh(string args, out int exitCode)
        {
            // netsh пишет в кодировке консоли (для русской Windows — 866)
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            var psi = new ProcessStartInfo("netsh", args)
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true,
                StandardOutputEncoding = System.Text.Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage)
            };
            using var p = Process.Start(psi);
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(8000);
            exitCode = p.HasExited ? p.ExitCode : -1;
            return text;
        }

        /// <summary>Сети Wi-Fi рядом (имя, сила сигнала, есть ли пароль), сильные — первыми. error — понятная причина, если список не получить.</summary>
        public static Task<(System.Collections.Generic.List<(string ssid, int signal, bool secured)> nets, string error)> WifiNetworksAsync() => Task.Run(() =>
        {
            var nets = new System.Collections.Generic.List<(string ssid, int signal, bool secured)>();
            try
            {
                var text = RunNetsh("wlan show networks mode=bssid", out _);
                if (text.IndexOf("location", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("расположен", StringComparison.OrdinalIgnoreCase) >= 0)
                    return (nets, "Windows не даёт список сетей без доступа к расположению — включи его в Параметрах → Конфиденциальность");
                string ssid = null; int signal = -1; bool secured = false;
                void Flush() { if (!string.IsNullOrWhiteSpace(ssid) && !nets.Exists(n => n.ssid == ssid)) nets.Add((ssid, Math.Max(0, signal), secured)); }
                foreach (var raw in text.Split('\n'))
                {
                    var line = raw.Trim();
                    var colon = line.IndexOf(':');
                    if (colon < 0) continue;
                    var name = line.Substring(0, colon).Trim();
                    var value = line.Substring(colon + 1).Trim();
                    if (name.StartsWith("SSID", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("BSSID", StringComparison.OrdinalIgnoreCase))
                    { Flush(); ssid = value; signal = -1; secured = false; }
                    else if (value.EndsWith("%") && signal < 0 && int.TryParse(value.TrimEnd('%').Trim(), out var sg)) signal = sg;
                    else if (name.IndexOf("Authentication", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Проверка подлинности", StringComparison.OrdinalIgnoreCase) >= 0)
                        secured = !value.StartsWith("Open", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("Открыт", StringComparison.OrdinalIgnoreCase);
                }
                Flush();
                nets.Sort((a, b) => b.signal.CompareTo(a.signal));
                return (nets, null);
            }
            catch (Exception ex) { return (nets, "Не удалось получить список сетей: " + ex.Message); }
        });

        /// <summary>Подключиться к сети, которую Windows уже знает. false — нужен пароль/настройки.</summary>
        public static Task<bool> ConnectWifiAsync(string ssid) => Task.Run(() =>
        {
            try
            {
                var safe = ssid.Replace("\"", "");
                RunNetsh("wlan connect name=\"" + safe + "\" ssid=\"" + safe + "\"", out int code);
                return code == 0;
            }
            catch { return false; }
        });

        /// <summary>Подключённые сейчас Bluetooth-устройства (наушники, мышь…): имена.</summary>
        public static async Task<System.Collections.Generic.List<string>> ConnectedBluetoothAsync()
        {
            var names = new System.Collections.Generic.List<string>();
            try
            {
                foreach (var selector in new[]
                {
                    Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromConnectionStatus(Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected),
                    Windows.Devices.Bluetooth.BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected)
                })
                {
                    foreach (var d in await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(selector))
                        if (!string.IsNullOrWhiteSpace(d.Name) && !names.Contains(d.Name)) names.Add(d.Name);
                }
            }
            catch { }
            return names;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        private static long _lastIdle, _lastTotal;

        /// <summary>Загрузка процессора (с прошлого вызова) и занятая память, в процентах.</summary>
        public static (int cpu, int ram) Load()
        {
            int cpu = 0, ram = 0;
            try
            {
                if (GetSystemTimes(out long idle, out long kernel, out long user))
                {
                    long total = kernel + user; // время ядра уже включает простой
                    long dTotal = total - _lastTotal, dIdle = idle - _lastIdle;
                    if (_lastTotal != 0 && dTotal > 0) cpu = (int)Math.Round(Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100));
                    _lastTotal = total; _lastIdle = idle;
                }
                var m = new MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ref m)) ram = (int)m.dwMemoryLoad;
            }
            catch { }
            return (cpu, ram);
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
                // «калькулятор», «obs», «телеграм», путь, сайт — ищем программу по-человечески
                if (AppLauncher.Find(line) != null || !line.Contains(' ')) return AppLauncher.Open(line);
                int sp = line.IndexOf(' ');
                if (AppLauncher.Find(line.Substring(0, sp)) != null) return AppLauncher.Launch(line.Substring(0, sp), line.Substring(sp + 1));
                return AppLauncher.Open(line);
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
                if (value)
                {
                    var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(exe)) throw new InvalidOperationException("Не удалось определить путь к Isle.exe");
                    // При запуске через dotnet run / dotnet Isle.dll в Run нужно записать DLL как аргумент,
                    // иначе Windows запустит только хост dotnet и окно Isle не появится.
                    var entry = Assembly.GetEntryAssembly()?.Location;
                    var command = string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(entry)
                        ? "\"" + exe + "\" \"" + entry + "\""
                        : "\"" + exe + "\"";
                    k.SetValue("Isle", command, RegistryValueKind.String);
                }
                else k.DeleteValue("Isle", false);
            }
        }

        /// <summary>Repairs an existing Run entry after the executable was moved or updated.</summary>
        public static void RepairAutostartPath()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (key?.GetValue("Isle") != null) Autostart = true;
        }
    }
}
