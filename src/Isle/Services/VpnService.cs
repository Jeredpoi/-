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
        public event Action Changed;

        public void Start()
        {
            _timer.Tick += async (_, __) => await Check();
            _timer.Start();
            _ = Check();
        }

        private async Task Check()
        {
            if (!AppSettings.Current.VpnStatus) { if (Active) { Active = false; Changed?.Invoke(); } return; }
            var nic = Find();
            bool active = nic != null;
            if (active == _last) return;
            bool first = _last == null;
            _last = active;
            Active = active;
            Name = nic?.Name ?? "";
            Country = "";
            Changed?.Invoke();
            if (active) Country = await LookupCountry();
            else if (!first) Country = await LookupCountry();
            Changed?.Invoke();
            if (!first)
                ToastBus.Show(new Toast
                {
                    Kind = ToastKind.Info,
                    Title = active ? "VPN подключён" + (Country.Length > 0 ? " · " + Country : "") : "VPN отключён",
                    Subtitle = active ? Name : (Country.Length > 0 ? "Сейчас вы в " + Country : ""),
                    Icon = Icons.Shield,
                    Accent = active ? System.Windows.Media.Color.FromRgb(0x4A, 0xDE, 0x80) : System.Windows.Media.Color.FromRgb(0xF8, 0x71, 0x71)
                });
        }

        private static NetworkInterface Find()
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                    n.OperationalStatus == OperationalStatus.Up &&
                    (n.NetworkInterfaceType == NetworkInterfaceType.Ppp ||
                     Markers.Any(m => (n.Description + " " + n.Name).ToLowerInvariant().Contains(m))) &&
                    !n.Description.ToLowerInvariant().Contains("hyper-v") &&
                    !n.Description.ToLowerInvariant().Contains("teredo"));
            }
            catch { return null; }
        }

        private static async Task<string> LookupCountry()
        {
            try
            {
                await Task.Delay(1500); // даём туннелю подняться
                using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://ipwho.is/?fields=country_code"));
                return doc.RootElement.TryGetProperty("country_code", out var c) ? c.GetString() ?? "" : "";
            }
            catch { return ""; }
        }
    }
}
