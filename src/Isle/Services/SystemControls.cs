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

        /// <summary>Сеть Wi-Fi рядом: имя, сигнал, защита (как её показывает netsh).</summary>
        public class WifiNet
        {
            public string Ssid;
            public int Signal;
            public bool Secured;
            /// <summary>«WPA2-Personal», «WPA3-Personal», «WPA2-Enterprise», «Open»…</summary>
            public string Auth = "";
            /// <summary>«CCMP», «TKIP», «GCMP»…</summary>
            public string Cipher = "";
            /// <summary>Пароль можно ввести прямо в чёлке (WPA/WPA2/WPA3-Personal). Корпоративные сети — только через Windows.</summary>
            public bool PasswordSupported => !Secured || ProfileAuth(Auth) != null;
        }

        /// <summary>Сети Wi-Fi рядом, сильные — первыми. error — понятная причина, если список не получить.</summary>
        public static Task<(System.Collections.Generic.List<WifiNet> nets, string error)> WifiNetworksAsync() => Task.Run(() =>
        {
            var nets = new System.Collections.Generic.List<WifiNet>();
            try
            {
                var text = RunNetsh("wlan show networks mode=bssid", out _);
                if (text.IndexOf("location", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("расположен", StringComparison.OrdinalIgnoreCase) >= 0)
                    return (nets, "Windows не даёт список сетей без доступа к расположению — включи его в Параметрах → Конфиденциальность");
                return (ParseNetworks(text), null);
            }
            catch (Exception ex) { return (nets, "Не удалось получить список сетей: " + ex.Message); }
        });

        /// <summary>Разбор вывода «netsh wlan show networks mode=bssid» (английская и русская Windows).</summary>
        public static System.Collections.Generic.List<WifiNet> ParseNetworks(string text)
        {
            var nets = new System.Collections.Generic.List<WifiNet>();
            WifiNet cur = null;
            void Flush()
            {
                if (cur == null || string.IsNullOrWhiteSpace(cur.Ssid)) return;
                var same = nets.Find(n => n.Ssid == cur.Ssid);
                if (same == null) nets.Add(cur);
                else if (cur.Signal > same.Signal) same.Signal = cur.Signal; // несколько точек доступа — берём лучшую
            }
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                var colon = line.IndexOf(':');
                if (colon < 0) continue;
                var name = line.Substring(0, colon).Trim();
                var value = line.Substring(colon + 1).Trim();
                if (name.StartsWith("SSID", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("BSSID", StringComparison.OrdinalIgnoreCase))
                { Flush(); cur = new WifiNet { Ssid = value, Signal = -1 }; }
                else if (cur == null) continue;
                else if (name.StartsWith("BSSID", StringComparison.OrdinalIgnoreCase)) { }
                else if (value.EndsWith("%") && int.TryParse(value.TrimEnd('%').Trim(), out var sg)) cur.Signal = Math.Max(cur.Signal, sg);
                else if (name.IndexOf("Authentication", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Проверка подлинности", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    cur.Auth = value;
                    cur.Secured = !value.StartsWith("Open", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("Открыт", StringComparison.OrdinalIgnoreCase);
                }
                else if (name.IndexOf("Encryption", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Шифрование", StringComparison.OrdinalIgnoreCase) >= 0)
                    cur.Cipher = value;
            }
            Flush();
            foreach (var n in nets) n.Signal = Math.Max(0, n.Signal);
            nets.Sort((a, b) => b.Signal.CompareTo(a.Signal));
            return nets;
        }

        /// <summary>Подключиться к сети, которую Windows уже знает. false — нужен пароль/настройки.</summary>
        public static Task<bool> ConnectWifiAsync(string ssid) => Task.Run(() =>
        {
            try
            {
                if (!HasProfile(ssid)) return false;
                var safe = ssid.Replace("\"", "");
                RunNetsh("wlan connect name=\"" + safe + "\" ssid=\"" + safe + "\"", out int code);
                return code == 0 && WaitConnected(ssid, 12);
            }
            catch { return false; }
        });

        /// <summary>
        /// Подключиться к новой сети с паролем: создаём профиль Windows (как при вводе пароля в системном меню) и подключаемся.
        /// Пароль не сохраняется у нас — только в профиле Windows. Не подключилось — профиль удаляем, чтобы не остался неверный пароль.
        /// </summary>
        public static Task<string> ConnectWifiWithPasswordAsync(WifiNet net, string password) => Task.Run(() =>
        {
            string file = null;
            bool hadProfile = HasProfile(net.Ssid);
            try
            {
                if (net.Secured && (password == null || password.Length < 8 || password.Length > 63))
                    return "Пароль Wi-Fi — от 8 до 63 символов";
                var xml = BuildProfileXml(net, password);
                if (xml == null) return "Эту сеть можно подключить только через Параметры Windows";
                file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "isle-wifi-" + Guid.NewGuid().ToString("N") + ".xml");
                System.IO.File.WriteAllText(file, xml, new System.Text.UTF8Encoding(false));
                RunNetsh("wlan add profile filename=\"" + file + "\" user=current", out int addCode);
                if (addCode != 0) return "Windows не принял настройки сети";
                var safe = net.Ssid.Replace("\"", "");
                RunNetsh("wlan connect name=\"" + safe + "\" ssid=\"" + safe + "\"", out _);
                if (WaitConnected(net.Ssid, 15)) return null;
                if (!hadProfile) RunNetsh("wlan delete profile name=\"" + safe + "\"", out _);
                return net.Secured ? "Не подключилось — проверь пароль" : "Не удалось подключиться";
            }
            catch (Exception ex) { return "Ошибка: " + ex.Message; }
            finally { try { if (file != null) System.IO.File.Delete(file); } catch { } }
        });

        private static bool HasProfile(string ssid)
        {
            try
            {
                var safe = ssid.Replace("\"", "");
                RunNetsh("wlan show profile name=\"" + safe + "\"", out int code);
                return code == 0;
            }
            catch { return false; }
        }

        private static bool WaitConnected(string ssid, int seconds)
        {
            for (int i = 0; i < seconds * 2; i++)
            {
                System.Threading.Thread.Sleep(500);
                if (string.Equals(WifiName(), ssid, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>Тип защиты для профиля WLAN по тому, что показал netsh. null — не поддерживаем (корпоративная сеть).</summary>
        public static string ProfileAuth(string netshAuth)
        {
            var a = (netshAuth ?? "").ToUpperInvariant();
            if (a.Contains("ENTERPRISE") || a.Contains("802.1X")) return null;
            if (a.StartsWith("WPA3")) return "WPA3SAE";
            if (a.StartsWith("WPA2")) return "WPA2PSK";
            if (a.StartsWith("WPA")) return "WPAPSK";
            if (a.StartsWith("OPEN") || a.StartsWith("ОТКРЫТ") || a.Length == 0) return "open";
            return null;
        }

        /// <summary>XML профиля WLAN (схема Microsoft WLANProfile v1) для netsh wlan add profile.</summary>
        public static string BuildProfileXml(WifiNet net, string password)
        {
            var auth = net.Secured ? ProfileAuth(net.Auth) : "open";
            if (auth == null) return null;
            string enc = auth == "open" ? "none" : (net.Cipher ?? "").ToUpperInvariant().Contains("TKIP") && auth == "WPAPSK" ? "TKIP" : "AES";
            string esc(string v) => System.Security.SecurityElement.Escape(v);
            var hex = Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(net.Ssid));
            var key = auth == "open" ? "" :
                "<sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>" + esc(password) + "</keyMaterial></sharedKey>";
            return "<?xml version=\"1.0\"?>" +
                "<WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\">" +
                "<name>" + esc(net.Ssid) + "</name>" +
                "<SSIDConfig><SSID><hex>" + hex + "</hex><name>" + esc(net.Ssid) + "</name></SSID></SSIDConfig>" +
                "<connectionType>ESS</connectionType><connectionMode>auto</connectionMode>" +
                "<MSM><security><authEncryption><authentication>" + auth + "</authentication><encryption>" + enc + "</encryption>" +
                "<useOneX>false</useOneX></authEncryption>" + key + "</security></MSM>" +
                "</WLANProfile>";
        }

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
