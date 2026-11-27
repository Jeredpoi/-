using System;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Isle.Services
{
    /// <summary>Определяет, включён ли VPN, и показывает страну выхода (через ipwho.is).</summary>
    public class VpnService
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
        private static readonly string[] Markers =
        {
            "vpn", "wireguard", "openvpn", "tap-windows", "tap-win", "wintun", "outline", "amnezia", "proton",
            "nord", "mullvad", "windscribe", "surfshark", "expressvpn", "hotspot shield", "cloudflare warp", "warp",
            "tunnel", "sing-box", "v2ray", "hiddify", "clash", "zerotier", "tailscale"
        };

        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(10) };
        private bool? _last;

        public bool Active { get; private set; }
        public string Name { get; private set; } = "";
        public string Country { get; private set; } = "";
        public string CountryName { get; private set; } = "";
        public string Ip { get; private set; } = "";
        private NetworkInterfaceType _type;

        public string Tooltip => !Active ? "VPN выключен" :
            "VPN подключён" + (CountryName.Length > 0 ? " · " + CountryName : "") + (Ip.Length > 0 ? " · " + Ip : "") + " · " + Name +
            "\nКлик — открыть VPN-клиент · Shift+клик — закрыть клиент (выключить VPN) · правый клик — проверить заново";

        /// <summary>Процессы VPN-клиентов: закрываем их, чтобы отключить туннель.</summary>
        private static readonly string[] Clients =
        {
            "happ", "v2rayn", "v2rayng", "xray", "v2ray", "sing-box", "hiddify", "hiddifynext", "nekoray", "nekobox", "throne",
            "clash-verge", "clash-verge-rev", "verge-mihomo", "mihomo", "clash", "amneziavpn", "amneziawg", "outline", "outlineclient",
            "protonvpn", "protonvpn.client", "nordvpn", "windscribe", "surfshark", "expressvpn", "hotspotshield", "mullvad vpn", "adguardvpn", "karing", "flclash"
        };

        public async Task<string> DisconnectAsync()
        {
            var name = Name;
            try
            {
                if (_type == NetworkInterfaceType.Ppp)
                {
                    await Run("rasdial", "/disconnect");
                }
                else if (name.ToLowerInvariant().Contains("warp") || name.ToLowerInvariant().Contains("cloudflare"))
                {
                    await Run("warp-cli", "disconnect");
                }
                else
                {
                    int killed = 0;
                    foreach (var p in System.Diagnostics.Process.GetProcesses())
                    {
                        try
                        {
                            var pn = p.ProcessName.ToLowerInvariant();
                            if (Clients.Any(c => pn == c || pn.StartsWith(c + "-") || pn.StartsWith(c + ".")))
                            {
                                try { p.CloseMainWindow(); } catch { }
                                if (!p.WaitForExit(1500)) p.Kill(true);
                                killed++;
                            }
                        }
                        catch { }
                        finally { p.Dispose(); }
                    }
                    if (killed == 0) return "Не нашёл программу VPN — отключи его в самом клиенте";
                }
            }
            catch (Exception ex) { return "Не получилось: " + ex.Message; }
            await Task.Delay(1200);
            _last = null;
            await Check();
            return Active ? "VPN всё ещё подключён — отключи его в самом клиенте" : "VPN отключён";
        }

        public void Recheck() { _last = null; _ = Check(); }

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);

        /// <summary>Открывает окно VPN-клиента (Happ, v2rayN, Hiddify…), не закрывая его.</summary>
        public string OpenClient()
        {
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                try
                {
                    var pn = p.ProcessName.ToLowerInvariant();
                    if (!Clients.Any(c => pn == c || pn.StartsWith(c + "-") || pn.StartsWith(c + "."))) continue;
                    if (p.MainWindowHandle != IntPtr.Zero)
                    {
                        ShowWindow(p.MainWindowHandle, 9); // SW_RESTORE
                        Native.Win32.ForceForeground(p.MainWindowHandle);
                        return null;
                    }
                    // окна нет (сидит в трее) — повторный запуск обычно показывает уже открытый клиент
                    var exe = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exe) && !pn.StartsWith("xray") && !pn.StartsWith("sing-box") && !pn.StartsWith("v2ray") && !pn.StartsWith("mihomo"))
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
                        return null;
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
            return "Не нашёл окно VPN-клиента — открой его из трея";
        }

        private static async Task Run(string exe, string args)
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false });
            if (p != null) await p.WaitForExitAsync();
        }
        public event Action Changed;

        public void Start()
        {
            _timer.Tick += async (_, __) => await Check();
            _timer.Start();
            // сеть поменялась (VPN включили или выключили) — проверяем сразу, а не ждём до 10 секунд
            var disp = Dispatcher.CurrentDispatcher;
            NetworkChange.NetworkAddressChanged += (_, __) => disp.InvokeAsync(async () =>
            {
                int v = ++_changeVersion;
                await Task.Delay(800); // адреса меняются пачкой — ждём, пока улягутся
                if (v == _changeVersion) await Check();
            });
            _ = Check();
        }

        private int _changeVersion;
        private bool _checking;

        private async Task Check()
        {
            if (!AppSettings.Current.VpnStatus) { _last = null; if (Active) { Active = false; Changed?.Invoke(); } return; } // включат снова — проверим заново
            // Список сетевых адаптеров Windows отдаёт медленно (до десятков мс) — не в потоке интерфейса, иначе раз в 10 с подлагивает анимация
            if (_checking) { _recheck = true; return; } // сеть поменялась, пока шла проверка — повторим сразу после неё
            _checking = true;
            try
            {
                var nic = await Task.Run(Find);
                bool active = nic != null;
                if (active == _last) return;
                // VPN-клиент при переподключении (смена сервера, обновление) на 2–5 с убирает адаптер — раньше это давало
                // плашки «VPN отключён» и сразу «подключён». Пропажу подтверждаем повторной проверкой через 6 с
                if (!active && _last == true)
                {
                    await Task.Delay(6000);
                    nic = await Task.Run(Find);
                    active = nic != null;
                    if (active) { Journal.Debug("vpn", "адаптер пропадал на пару секунд — это переподключение, не отключение"); return; }
                }
                bool first = _last == null;
                _last = active;
                Active = active;
                Name = nic?.Name ?? "";
                _type = nic?.NetworkInterfaceType ?? NetworkInterfaceType.Unknown;
                Country = ""; CountryName = ""; Ip = "";
                Changed?.Invoke();
                Journal.Info("vpn", (active ? "подключён: " + Name : "отключён") + (first ? " (при запуске)" : ""));
                // Плашку показываем сразу. Раньше она ждала страну по IP, а в момент отключения сеть
                // перестраивается: запрос висел до таймаута, и «VPN отключён» не появлялось вовсе.
                if (!first) ShowToast(active);
                if (active || !first)
                {
                    (Country, CountryName, Ip) = await LookupCountry();
                    if (Active != active) return; // пока узнавали страну, состояние снова поменялось
                    Changed?.Invoke();
                    if (!first && CountryName.Length > 0) ShowToast(active); // та же плашка, уже со страной
                }
            }
            finally
            {
                _checking = false;
                if (_recheck) { _recheck = false; _ = Check(); }
            }
        }

        private bool _recheck;

        private void ShowToast(bool active) => ToastBus.Show(new Toast
        {
            Kind = ToastKind.Info,
            Title = active ? "VPN подключён" + (Country.Length > 0 ? " · " + Country : "") : "VPN отключён",
            Subtitle = active ? (CountryName.Length > 0 ? CountryName + " · " : "") + Name : (CountryName.Length > 0 ? "Сейчас вы: " + CountryName : "Трафик идёт напрямую"),
            Icon = Icons.Shield, On = active, ReplaceKey = "vpn",
            Accent = active ? System.Windows.Media.Color.FromRgb(0x4A, 0xDE, 0x80) : System.Windows.Media.Color.FromRgb(0xA1, 0xA1, 0xAB)
        });

        private static NetworkInterface Find()
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                    n.OperationalStatus == OperationalStatus.Up && HasAddress(n) &&
                    (n.NetworkInterfaceType == NetworkInterfaceType.Ppp ||
                     Markers.Any(m => (n.Description + " " + n.Name).ToLowerInvariant().Contains(m))) &&
                    !n.Description.ToLowerInvariant().Contains("hyper-v") &&
                    !n.Description.ToLowerInvariant().Contains("teredo"));
            }
            catch { return null; }
        }

        /// <summary>
        /// Есть настоящий адрес. Некоторые клиенты при отключении оставляют свой адаптер «включённым», но без адреса —
        /// раньше такой считался работающим VPN, и «VPN отключён» не приходило.
        /// </summary>
        private static bool HasAddress(NetworkInterface n)
        {
            try
            {
                return n.GetIPProperties().UnicastAddresses.Any(a =>
                    a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254."));
            }
            catch { return true; }
        }

        private static async Task<(string code, string name, string ip)> LookupCountry()
        {
            try
            {
                await Task.Delay(1500); // даём туннелю подняться
                using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://ipwho.is/?lang=ru&fields=country_code,country,ip"));
                var r = doc.RootElement;
                string get(string k) => r.TryGetProperty(k, out var v) ? v.GetString() ?? "" : "";
                return (get("country_code"), get("country"), get("ip"));
            }
            catch { return ("", "", ""); }
        }
    }
}
