using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Isle.Services
{
    /// <summary>Что пришло с телефона.</summary>
    public class PhoneItem
    {
        public string Name { get; init; } = "";
        public string Path { get; init; }       // файл (фото, видео, документ)
        public string Text { get; init; }       // или текст
        public long Size { get; init; }
        public DateTime Time { get; init; } = DateTime.Now;
        public bool IsPhoto => Path != null && PhotoExt.Contains(System.IO.Path.GetExtension(Path).ToLowerInvariant());
        public static readonly HashSet<string> PhotoExt = new() { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".heic", ".heif" };
    }

    /// <summary>Уведомление с телефона (через «Связь с телефоном»).</summary>
    public class PhoneNotice
    {
        public string App { get; init; } = "";
        public string Title { get; init; } = "";
        public string Body { get; init; } = "";
        public System.Windows.Media.ImageSource Logo { get; init; }
        public DateTime Time { get; init; } = DateTime.Now;
    }

    /// <summary>Файл, который компьютер предлагает телефону скачать.</summary>
    public class OutgoingFile
    {
        public string Id { get; init; } = "";
        public string Path { get; init; } = "";
        public string Name => System.IO.Path.GetFileName(Path);
        public long Size => File.Exists(Path) ? new FileInfo(Path).Length : 0;
    }

    /// <summary>
    /// Связь с телефоном без приложений: маленький веб-сервер в локальной сети. Телефон сканирует QR-код,
    /// открывает страничку в браузере и отправляет фото, файлы и текст — или скачивает то, что предложил компьютер.
    /// Работает и когда телефон раздаёт интернет на компьютер (Wi-Fi-точка или USB): это тоже общая сеть,
    /// интернет при этом не нужен. В адресе — случайный ключ: без него сервер ничего не отдаёт и не принимает.
    /// </summary>
    public static class PhoneLink
    {
        private static TcpListener _listener;
        private static CancellationTokenSource _cts;
        private static string _token;
        private static DateTime _lastRequest = DateTime.MinValue;
        private static readonly System.Windows.Threading.DispatcherTimer IdleStop = new() { Interval = TimeSpan.FromMinutes(1) };

        public static bool Running => _listener != null;
        public static int Port { get; private set; }
        /// <summary>Телефон обращался за последние 15 секунд (страничка открыта и опрашивает).</summary>
        public static bool PhoneOnline => DateTime.Now - _lastRequest < TimeSpan.FromSeconds(15);
        public static DateTime LastRequest => _lastRequest;
        public static bool KeepAlive { get; set; } // вкладка открыта — не выключать по простою

        public static readonly List<PhoneItem> Received = new();
        public static readonly List<OutgoingFile> Outgoing = new();
        public static string OutgoingText { get; private set; } = "";

        public static event Action<PhoneItem> ItemReceived;
        public static event Action Changed;
        /// <summary>Телефон открыл страничку (отсканировал QR-код).</summary>
        public static event Action PageOpened;

        /// <summary>Уведомления с телефона — их присылает «Связь с телефоном» Windows (телефон по Bluetooth).</summary>
        public static readonly List<PhoneNotice> Notices = new();
        public static event Action NoticesChanged;
        public static void AddNotice(PhoneNotice n)
        {
            Notices.Insert(0, n);
            if (Notices.Count > 100) Notices.RemoveAt(Notices.Count - 1);
            NoticesChanged?.Invoke();
        }

        public static string SaveDir => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Isle с телефона");

        // ---------- запуск ----------
        public static void Start()
        {
            if (Running) return;
            _token ??= Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
            var s = AppSettings.Current;
            if (s.PhonePort is < 1024 or > 65000) { s.PhonePort = 47000 + RandomNumberGenerator.GetInt32(2000); s.Save(); }
            foreach (var port in new[] { s.PhonePort, 0 })
            {
                try
                {
                    var l = new TcpListener(IPAddress.Any, port);
                    l.Start();
                    _listener = l;
                    Port = ((IPEndPoint)l.LocalEndpoint).Port;
                    break;
                }
                catch (SocketException ex) { Journal.Warn("phone", "порт " + port + " занят: " + ex.Message); }
            }
            if (_listener == null) return;
            _cts = new CancellationTokenSource();
            _ = AcceptLoop(_listener, _cts.Token);
            _lastRequest = DateTime.MinValue;
            IdleStop.Tick -= OnIdleTick; IdleStop.Tick += OnIdleTick; IdleStop.Start();
            _idleSince = DateTime.Now;
            Journal.Info("phone", "сервер для телефона запущен, порт " + Port + ", адреса: " + string.Join(", ", Addresses().Select(a => a.ip + " (" + a.name + ")")));
            Changed?.Invoke();
        }

        public static void Stop()
        {
            if (!Running) return;
            try { _cts?.Cancel(); _listener?.Stop(); } catch { }
            _listener = null;
            IdleStop.Stop();
            Journal.Info("phone", "сервер для телефона остановлен");
            Changed?.Invoke();
        }

        private static DateTime _idleSince;
        private static void OnIdleTick(object s, EventArgs e)
        {
            if (KeepAlive || DateTime.Now - _lastRequest < TimeSpan.FromMinutes(15)) { _idleSince = DateTime.Now; return; }
            if (DateTime.Now - _idleSince > TimeSpan.FromMinutes(15)) Stop();
        }

        /// <summary>Адрес для QR: http://IP:порт/ключ/</summary>
        public static string Url(string ip) => "http://" + ip + ":" + Port + "/" + _token + "/";

        /// <summary>
        /// Адреса компьютера в локальных сетях: Wi-Fi, кабель, точка доступа телефона, USB-модем.
        /// VPN-туннели и виртуальные адаптеры не подходят — телефон до них не достучится.
        /// </summary>
        public static List<(string ip, string name)> Addresses()
        {
            var list = new List<(string ip, string name, int rank)>();
            string[] skip = { "vpn", "tap", "tun", "wintun", "wireguard", "xray", "v2ray", "sing", "clash", "hiddify", "happ", "outline", "zerotier", "tailscale", "default switch", "virtualbox", "vmware", "loopback", "bluetooth", "teredo", "vethernet", "wsl" };
            try
            {
                foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp) continue;
                    var desc = (n.Description + " " + n.Name).ToLowerInvariant();
                    if (skip.Any(desc.Contains)) continue;
                    var props = n.GetIPProperties();
                    bool gateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                    foreach (var a in props.UnicastAddresses)
                    {
                        if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        var b = a.Address.GetAddressBytes();
                        bool priv = b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
                        if (!priv) continue;
                        int rank = (gateway ? 0 : 10) + (n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : 1);
                        string kind = n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi"
                            : desc.Contains("ndis") || desc.Contains("usb") ? "USB-модем телефона" : "кабель";
                        list.Add((a.Address.ToString(), kind + " · " + n.Name, rank));
                    }
                }
            }
            catch { }
            return list.OrderBy(x => x.rank).Select(x => (x.ip, x.name)).Distinct().ToList();
        }

        // ---------- что компьютер отдаёт телефону ----------
        public static void Offer(IEnumerable<string> paths)
        {
            foreach (var p in paths.Where(File.Exists))
                if (Outgoing.All(o => !string.Equals(o.Path, p, StringComparison.OrdinalIgnoreCase)))
                    Outgoing.Insert(0, new OutgoingFile { Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant(), Path = p });
            Changed?.Invoke();
        }

        public static void Unoffer(OutgoingFile f) { Outgoing.Remove(f); Changed?.Invoke(); }

        public static void OfferText(string text) { OutgoingText = text ?? ""; Changed?.Invoke(); }

        // ---------- HTTP ----------
        private static async Task AcceptLoop(TcpListener l, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await l.AcceptTcpClientAsync(ct); }
                catch { break; }
                _ = Task.Run(() => Handle(c));
            }
        }

        private static async Task Handle(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 30000;
                    var stream = client.GetStream();
                    var (method, target, headers, leftover) = await ReadHead(stream);
                    if (method == null) return;
                    string path = target, query = "";
                    int qi = target.IndexOf('?');
                    if (qi >= 0) { path = target[..qi]; query = target[(qi + 1)..]; }
                    var parts = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                    // без ключа — как будто здесь ничего нет
                    if (parts.Length == 0 || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(parts[0]), Encoding.ASCII.GetBytes(_token ?? "-")))
                    { await Send(stream, 404, "text/plain", Encoding.UTF8.GetBytes("Not found")); return; }
                    _lastRequest = DateTime.Now;
                    string route = parts.Length > 1 ? parts[1] : "";
                    if (method == "GET" && route == "")
                    {
                        await Send(stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(Page));
                        Application.Current?.Dispatcher.InvokeAsync(() => PageOpened?.Invoke());
                    }
                    else if (method == "GET" && route == "list") await Send(stream, 200, "application/json", JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        files = Outgoing.ToList().Select(o => new { id = o.Id, name = o.Name, size = o.Size, img = ImageType(o.Name) != null }),
                        text = OutgoingText,
                    }));
                    else if (method == "GET" && route == "f" && parts.Length > 2) await SendFile(stream, Outgoing.FirstOrDefault(o => o.Id == parts[2]), Query(query, "view") == "1");
                    else if (method == "POST" && route == "up") await Receive(stream, headers, leftover, Query(query, "name"));
                    else if (method == "POST" && route == "text") await ReceiveText(stream, headers, leftover);
                    else await Send(stream, 404, "text/plain", Encoding.UTF8.GetBytes("Not found"));
                }
                catch (Exception ex) { Journal.Debug("phone", "запрос не удался: " + ex.Message); }
            }
        }

        private static string Query(string q, string key)
        {
            foreach (var kv in q.Split('&'))
            {
                int e = kv.IndexOf('=');
                if (e > 0 && kv[..e] == key) return Uri.UnescapeDataString(kv[(e + 1)..].Replace('+', ' '));
            }
            return null;
        }

        private static async Task<(string method, string target, Dictionary<string, string> headers, byte[] leftover)> ReadHead(NetworkStream s)
        {
            var buf = new byte[16384];
            int len = 0;
            while (len < buf.Length)
            {
                int n = await s.ReadAsync(buf.AsMemory(len, buf.Length - len));
                if (n <= 0) return (null, null, null, null);
                len += n;
                int end = IndexOf(buf, len, "\r\n\r\n"u8);
                if (end < 0) continue;
                var head = Encoding.ASCII.GetString(buf, 0, end).Split("\r\n");
                var first = head[0].Split(' ');
                if (first.Length < 2) return (null, null, null, null);
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var h in head.Skip(1)) { int c = h.IndexOf(':'); if (c > 0) headers[h[..c].Trim()] = h[(c + 1)..].Trim(); }
                return (first[0], first[1], headers, buf[(end + 4)..len]);
            }
            return (null, null, null, null);
        }

        private static int IndexOf(byte[] b, int len, ReadOnlySpan<byte> pat)
        {
            for (int i = 0; i + pat.Length <= len; i++) if (b.AsSpan(i, pat.Length).SequenceEqual(pat)) return i;
            return -1;
        }

        private static async Task Send(NetworkStream s, int code, string type, byte[] body)
        {
            var head = "HTTP/1.1 " + code + (code == 200 ? " OK" : " Error") + "\r\nContent-Type: " + type + "\r\nContent-Length: " + body.Length
                + "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
            await s.WriteAsync(Encoding.ASCII.GetBytes(head));
            await s.WriteAsync(body);
        }

        private static string ImageType(string name) => System.IO.Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".gif" => "image/gif", ".webp" => "image/webp", ".bmp" => "image/bmp", _ => null,
        };

        /// <summary>view — показать картинку прямо на страничке (превью), иначе — скачать.</summary>
        private static async Task SendFile(NetworkStream s, OutgoingFile f, bool view = false)
        {
            if (f == null || !File.Exists(f.Path)) { await Send(s, 404, "text/plain", Encoding.UTF8.GetBytes("Файл больше не доступен")); return; }
            var img = ImageType(f.Name);
            if (view && img == null) { await Send(s, 404, "text/plain", Array.Empty<byte>()); return; }
            using var fs = File.OpenRead(f.Path);
            var head = view
                ? "HTTP/1.1 200 OK\r\nContent-Type: " + img + "\r\nContent-Length: " + fs.Length + "\r\nCache-Control: max-age=3600\r\nConnection: close\r\n\r\n"
                : "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: " + fs.Length
                + "\r\nContent-Disposition: attachment; filename*=UTF-8''" + Uri.EscapeDataString(f.Name) + "\r\nConnection: close\r\n\r\n";
            await s.WriteAsync(Encoding.ASCII.GetBytes(head));
            await fs.CopyToAsync(s);
            if (!view) Journal.Info("phone", "телефон скачал файл (" + fs.Length / 1024 + " КБ)");
        }

        private static async Task Receive(NetworkStream s, Dictionary<string, string> h, byte[] leftover, string name)
        {
            if (!h.TryGetValue("Content-Length", out var cl) || !long.TryParse(cl, out long total) || total < 0) { await Send(s, 411, "text/plain", Array.Empty<byte>()); return; }
            name = SafeName(string.IsNullOrWhiteSpace(name) ? "файл с телефона" : name);
            Directory.CreateDirectory(SaveDir);
            var path = System.IO.Path.Combine(SaveDir, name);
            for (int i = 2; File.Exists(path); i++)
                path = System.IO.Path.Combine(SaveDir, System.IO.Path.GetFileNameWithoutExtension(name) + " (" + i + ")" + System.IO.Path.GetExtension(name));
            long got = 0;
            using (var fs = File.Create(path))
            {
                int take = (int)Math.Min(leftover.Length, total);
                await fs.WriteAsync(leftover.AsMemory(0, take)); got += take;
                var buf = new byte[81920];
                while (got < total)
                {
                    int n = await s.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, total - got)));
                    if (n <= 0) break;
                    await fs.WriteAsync(buf.AsMemory(0, n)); got += n;
                    _lastRequest = DateTime.Now;
                }
            }
            if (got < total) { try { File.Delete(path); } catch { } Journal.Warn("phone", "передача файла оборвалась"); return; }
            await Send(s, 200, "application/json", "{\"ok\":true}"u8.ToArray());
            Journal.Info("phone", "с телефона: файл " + System.IO.Path.GetExtension(path) + ", " + total / 1024 + " КБ");
            Deliver(new PhoneItem { Name = System.IO.Path.GetFileName(path), Path = path, Size = total });
        }

        private static async Task ReceiveText(NetworkStream s, Dictionary<string, string> h, byte[] leftover)
        {
            if (!h.TryGetValue("Content-Length", out var cl) || !int.TryParse(cl, out int total) || total < 0 || total > 1_000_000) { await Send(s, 411, "text/plain", Array.Empty<byte>()); return; }
            var buf = new byte[total];
            int got = Math.Min(leftover.Length, total);
            Array.Copy(leftover, buf, got);
            while (got < total) { int n = await s.ReadAsync(buf.AsMemory(got, total - got)); if (n <= 0) break; got += n; }
            var text = Encoding.UTF8.GetString(buf, 0, got);
            await Send(s, 200, "application/json", "{\"ok\":true}"u8.ToArray());
            if (string.IsNullOrWhiteSpace(text)) return;
            Journal.Info("phone", "с телефона: текст, " + text.Length + " симв.");
            Deliver(new PhoneItem { Name = "Текст", Text = text, Size = text.Length });
        }

        private static string SafeName(string n)
        {
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
            n = n.Trim().TrimStart('.');
            return n.Length == 0 ? "файл" : n.Length > 120 ? n[..120] : n;
        }

        private static void Deliver(PhoneItem item)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                Received.Insert(0, item);
                if (Received.Count > 200) Received.RemoveAt(Received.Count - 1);
                ItemReceived?.Invoke(item);
                Changed?.Invoke();
            });
        }

        // ---------- страничка для телефона ----------
        private const string Page = """
<!doctype html><html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
<meta name="theme-color" content="#000"><title>Isle</title><style>
:root{color-scheme:dark;--bg:#000;--card:#0E0E10;--line:#1C1C20;--fg:#ECECF1;--mut:#8B8B95;--ok:#4ADE80;--bad:#F87171;--ease:cubic-bezier(.2,.9,.25,1)}
*{box-sizing:border-box;-webkit-tap-highlight-color:transparent}
body{margin:0;font:16px/1.4 -apple-system,system-ui,"Segoe UI",Roboto,sans-serif;background:var(--bg);color:var(--fg);padding:calc(env(safe-area-inset-top) + 74px) 16px calc(env(safe-area-inset-bottom) + 32px);min-height:100vh}
/* чёлка сверху — как в Isle: раскрывается, когда что-то произошло */
#isl{position:fixed;left:50%;top:calc(env(safe-area-inset-top) + 10px);transform:translateX(-50%);z-index:9;height:38px;min-width:118px;max-width:calc(100vw - 32px);padding:0 16px;border-radius:19px;background:#000;border:1px solid #232328;box-shadow:0 8px 30px rgba(0,0,0,.6);display:flex;align-items:center;justify-content:center;gap:9px;font:600 14px system-ui;white-space:nowrap;overflow:hidden;transition:all .45s var(--ease)}
#isl.big{height:52px;border-radius:26px;padding:0 20px;font-size:15px}
#isl .d{width:8px;height:8px;border-radius:50%;background:var(--ok);flex:none;box-shadow:0 0 0 0 rgba(74,222,128,.6);animation:pulse 2s infinite}
#isl.off .d{background:var(--bad);animation:none}
#isl .t{overflow:hidden;text-overflow:ellipsis}
#isl svg{flex:none}
@keyframes pulse{70%{box-shadow:0 0 0 8px rgba(74,222,128,0)}100%{box-shadow:0 0 0 0 rgba(74,222,128,0)}}
h1{font-size:28px;letter-spacing:-.02em;margin:0 0 4px}p.s{color:var(--mut);margin:0 0 20px;font-size:14px}
.in{opacity:0;transform:translateY(14px);animation:up .6s var(--ease) forwards}
@keyframes up{to{opacity:1;transform:none}}
.card{background:var(--card);border:1px solid var(--line);border-radius:22px;padding:16px;margin-bottom:14px}
.card h2{font-size:12px;color:var(--mut);text-transform:uppercase;letter-spacing:.08em;margin:2px 2px 12px;font-weight:600}
.tiles{display:grid;grid-template-columns:1fr 1fr;gap:10px}
.tile{position:relative;display:flex;flex-direction:column;justify-content:flex-end;gap:10px;height:118px;padding:14px;border-radius:18px;background:#17171B;color:var(--fg);font:600 15px system-ui;cursor:pointer;overflow:hidden;transition:transform .18s var(--ease),background .2s}
.tile.main{background:linear-gradient(145deg,#F4F4F7,#C9C9D2);color:#000}
.tile .ic{width:38px;height:38px;border-radius:12px;display:grid;place-items:center;background:rgba(255,255,255,.08)}
.tile.main .ic{background:rgba(0,0,0,.08)}
.tile small{display:block;font-weight:500;font-size:12px;opacity:.6;margin-top:-6px}
.tile:active,.btn:active,.dl:active{transform:scale(.96)}
input[type=file]{display:none}
#q:not(:empty){margin-top:8px}
.tx{position:relative;margin-top:12px}
textarea{display:block;width:100%;min-height:54px;max-height:200px;background:#000;color:var(--fg);border:1px solid #26262B;border-radius:16px;padding:15px 58px 15px 15px;font:15px system-ui;resize:none;outline:none;transition:border-color .2s}
textarea:focus{border-color:#55555F}
.send{position:absolute;right:8px;bottom:8px;width:40px;height:40px;border-radius:12px;border:0;background:var(--fg);color:#000;display:grid;place-items:center;cursor:pointer;transition:transform .18s var(--ease),opacity .2s,background .3s}
.send:disabled{opacity:.25}.send.done{background:var(--ok)}
.it{display:flex;align-items:center;gap:12px;padding:10px 2px;border-top:1px solid var(--line);font-size:14px}
.it:first-child{border-top:0}
.it.new{animation:slide .45s var(--ease)}
@keyframes slide{from{opacity:0;transform:translateY(-10px) scale(.98)}}
.th{width:44px;height:44px;border-radius:12px;background:#1C1C21 center/cover no-repeat;flex:none;display:grid;place-items:center;color:var(--mut)}
.it .m{flex:1;min-width:0}.it .n{overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-weight:600}
.it .st{color:var(--mut);font-size:12px;margin-top:1px}
.ring{width:34px;height:34px;flex:none}
.ring circle{fill:none;stroke-width:3}.ring .bg{stroke:#26262B}.ring .fg{stroke:var(--ok);stroke-linecap:round;transform:rotate(-90deg);transform-origin:50% 50%;transition:stroke-dashoffset .2s}
.ring .ck{fill:none;stroke:var(--ok);stroke-width:3;stroke-linecap:round;stroke-linejoin:round;stroke-dasharray:20;stroke-dashoffset:20}
.ring.ok .ck{animation:draw .4s .1s var(--ease) forwards}.ring.ok .fg{stroke-dashoffset:0!important}
.ring.err .fg{stroke:var(--bad)}
@keyframes draw{to{stroke-dashoffset:0}}
.dl,.btn{display:inline-flex;align-items:center;gap:6px;color:var(--fg);text-decoration:none;background:#1C1C21;border:0;border-radius:12px;padding:9px 13px;font:600 13px system-ui;flex:none;cursor:pointer;transition:transform .18s var(--ease),background .3s}
.btn.ok{background:#14321F;color:var(--ok)}
.txt{background:#000;border:1px solid #26262B;border-radius:16px;padding:14px;margin-bottom:6px}
.txt pre{white-space:pre-wrap;word-break:break-word;margin:0 0 12px;font:15px/1.45 system-ui;max-height:220px;overflow:auto}
.empty{color:#6F6F79;font-size:14px;text-align:center;padding:18px 10px}
.empty svg{display:block;margin:0 auto 10px;opacity:.5}
@media (prefers-reduced-motion:reduce){*{animation:none!important;transition:none!important}.in{opacity:1;transform:none}}
</style></head><body>
<div id="isl"><span class="d"></span><span class="t">Isle</span></div>
<h1 class="in">Отправить на ПК</h1><p class="s in" style="animation-delay:.05s">Фото, файлы и текст — прямо в Isle, без проводов и приложений</p>
<div class="card in" style="animation-delay:.1s"><h2>На компьютер</h2>
<div class="tiles">
<label class="tile main"><span class="ic"><svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="5" width="18" height="15" rx="3"/><circle cx="9" cy="10.5" r="1.6"/><path d="M21 16l-5-5-8 9"/></svg></span>Фото и видео<small>галерея или камера</small><input id="ph" type="file" accept="image/*,video/*" multiple></label>
<label class="tile"><span class="ic"><svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z"/><path d="M14 3v5h5"/></svg></span>Файлы<small>любые документы</small><input id="fl" type="file" multiple></label>
</div>
<div id="q"></div>
<div class="tx"><textarea id="tx" rows="1" placeholder="Текст или ссылка → в буфер ПК"></textarea>
<button class="send" id="sendt" disabled aria-label="Отправить"><svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"><path d="M12 19V5M5 12l7-7 7 7"/></svg></button></div>
</div>
<div class="card in" style="animation-delay:.18s"><h2>С компьютера</h2><div id="out"></div></div>
<script>
const base=location.pathname.endsWith('/')?location.pathname:location.pathname+'/';
const $=id=>document.getElementById(id),q=$('q'),isl=$('isl'),islT=isl.querySelector('.t');
const ICON={ok:'<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="#4ADE80" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"><path d="M5 12.5l4.5 4.5L19 7.5"/></svg>',
file:'<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z"/><path d="M14 3v5h5"/></svg>',
dl:'<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"><path d="M12 4v12M6 11l6 6 6-6M5 20h14"/></svg>',
cp:'<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><rect x="8" y="8" width="12" height="12" rx="2.5"/><path d="M16 8V6a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h2"/></svg>',
pc:'<svg width="34" height="34" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="18" height="12" rx="2"/><path d="M8 20h8M12 16v4"/></svg>'};
let islTimer,online=true;
function island(html,ms){isl.classList.add('big');islT.innerHTML=html;clearTimeout(islTimer);islTimer=setTimeout(()=>{isl.classList.remove('big');islT.textContent=online?'Isle':'Нет связи с ПК'},ms||1800)}
function buzz(){try{navigator.vibrate&&navigator.vibrate(18)}catch(e){}}
function fmt(n){return n>1048576?(n/1048576).toFixed(1)+' МБ':Math.max(1,Math.round(n/1024))+' КБ'}
function esc(s){const d=document.createElement('div');d.textContent=s;return d.innerHTML}
let pending=0,sent=0;
function up(files){files=[...files];if(!files.length)return;pending+=files.length;files.reduce((p,f)=>p.then(()=>one(f)),Promise.resolve()).then(()=>{if(!pending&&sent){island(ICON.ok+' '+(sent==1?'Отправлено':'Отправлено: '+sent),2000);buzz();sent=0}})}
function one(f){return new Promise(res=>{const d=document.createElement('div');d.className='it new';
const img=f.type.startsWith('image/')?URL.createObjectURL(f):null;
d.innerHTML='<div class="th"'+(img?' style="background-image:url('+img+')"':'')+'>'+(img?'':ICON.file)+'</div><div class="m"><div class="n">'+esc(f.name)+'</div><div class="st">'+fmt(f.size)+'</div></div>'+
'<svg class="ring" viewBox="0 0 36 36"><circle class="bg" cx="18" cy="18" r="15"/><circle class="fg" cx="18" cy="18" r="15" stroke-dasharray="94.2" stroke-dashoffset="94.2"/><path class="ck" d="M12 18.5l4 4 8-8.5"/></svg>';
q.prepend(d);const ring=d.querySelector('.ring'),fg=d.querySelector('.fg'),st=d.querySelector('.st');
const x=new XMLHttpRequest();x.open('POST',base+'up?name='+encodeURIComponent(f.name));
x.upload.onprogress=e=>{if(e.lengthComputable){const p=e.loaded/e.total;fg.style.strokeDashoffset=94.2*(1-p);st.textContent=fmt(e.loaded)+' из '+fmt(e.total)}};
const done=ok=>{pending--;ring.classList.add(ok?'ok':'err');st.textContent=ok?fmt(f.size)+' · на компьютере':'не отправилось — нет связи';if(ok)sent++;else{st.style.color='var(--bad)';island('Не отправилось',2200)}res()};
x.onload=()=>done(x.status==200);x.onerror=()=>done(false);x.send(f)})}
$('ph').onchange=e=>{up(e.target.files);e.target.value=''};
$('fl').onchange=e=>{up(e.target.files);e.target.value=''};
const tx=$('tx'),sb=$('sendt');
tx.oninput=()=>{tx.style.height='auto';tx.style.height=Math.min(200,tx.scrollHeight+2)+'px';sb.disabled=!tx.value.trim()};
sb.onclick=()=>{if(!tx.value.trim())return;sb.disabled=true;
fetch(base+'text',{method:'POST',body:tx.value}).then(r=>{if(!r.ok)throw 0;tx.value='';tx.oninput();sb.classList.add('done');sb.innerHTML=ICON.ok.replace('#4ADE80','#000');buzz();island(ICON.ok+' Текст в буфере ПК');
setTimeout(()=>{sb.classList.remove('done');sb.innerHTML='<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"><path d="M12 19V5M5 12l7-7 7 7"/></svg>'},1500)}).catch(()=>{sb.disabled=false;island('Нет связи с ПК',2200)})};
function copy(t,b){const ok=()=>{b.classList.add('ok');b.innerHTML=ICON.ok.replace('18','15').replace('18','15')+' Скопировано';buzz();setTimeout(()=>{b.classList.remove('ok');b.innerHTML=ICON.cp+' Скопировать'},1500)};
if(navigator.clipboard&&window.isSecureContext)navigator.clipboard.writeText(t).then(ok);else{const a=document.createElement('textarea');a.value=t;a.style.position='fixed';a.style.opacity=0;document.body.appendChild(a);a.select();try{document.execCommand('copy');ok()}catch(e){}a.remove()}}
let last=null,known=new Set();
function render(d){const o=$('out');o.innerHTML='';
if(d.text){const c=document.createElement('div');c.className='txt'+(known.has('t:'+d.text)?'':' it new');c.style.display='block';c.innerHTML='<pre></pre><button class="btn">'+ICON.cp+' Скопировать</button>';c.querySelector('pre').textContent=d.text;
const b=c.querySelector('button');b.onclick=()=>copy(d.text,b);o.appendChild(c)}
d.files.forEach(f=>{const r=document.createElement('div');r.className='it'+(last&&!known.has(f.id)?' new':'');
r.innerHTML='<div class="th"'+(f.img?' style="background-image:url('+base+'f/'+f.id+'?view=1)"':'')+'>'+(f.img?'':ICON.file)+'</div><div class="m"><div class="n"></div><div class="st">'+fmt(f.size)+'</div></div><a class="dl">'+ICON.dl+' Скачать</a>';
r.querySelector('.n').textContent=f.name;r.querySelector('a').href=base+'f/'+f.id;o.appendChild(r)});
if(!d.text&&!d.files.length)o.innerHTML='<div class="empty">'+ICON.pc+'Перетащи файлы на вкладку «Телефон» в Isle — они появятся здесь</div>';
if(last&&(d.files.some(f=>!known.has(f.id))||(d.text&&!known.has('t:'+d.text)))){island('Новое с компьютера',1800);buzz()}
known=new Set(d.files.map(f=>f.id));if(d.text)known.add('t:'+d.text)}
function poll(){fetch(base+'list',{cache:'no-store'}).then(r=>r.json()).then(d=>{if(!online){online=true;isl.classList.remove('off');island(ICON.ok+' Снова на связи',1500)}
const s=JSON.stringify(d);if(s===last)return;render(d);last=s}).catch(()=>{if(online){online=false;isl.classList.add('off');islT.textContent='Нет связи с ПК'}})}
setTimeout(()=>island(ICON.ok+' Подключено к компьютеру',2000),350);
poll();setInterval(poll,2500);
</script></body></html>
""";
    }
}
