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
                    if (method == "GET" && route == "") await Send(stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(Page));
                    else if (method == "GET" && route == "list") await Send(stream, 200, "application/json", JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        files = Outgoing.ToList().Select(o => new { id = o.Id, name = o.Name, size = o.Size }),
                        text = OutgoingText,
                    }));
                    else if (method == "GET" && route == "f" && parts.Length > 2) await SendFile(stream, Outgoing.FirstOrDefault(o => o.Id == parts[2]));
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

        private static async Task SendFile(NetworkStream s, OutgoingFile f)
        {
            if (f == null || !File.Exists(f.Path)) { await Send(s, 404, "text/plain", Encoding.UTF8.GetBytes("Файл больше не доступен")); return; }
            using var fs = File.OpenRead(f.Path);
            var head = "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: " + fs.Length
                + "\r\nContent-Disposition: attachment; filename*=UTF-8''" + Uri.EscapeDataString(f.Name) + "\r\nConnection: close\r\n\r\n";
            await s.WriteAsync(Encoding.ASCII.GetBytes(head));
            await fs.CopyToAsync(s);
            Journal.Info("phone", "телефон скачал файл (" + fs.Length / 1024 + " КБ)");
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
<title>Isle</title><style>
:root{color-scheme:dark}*{box-sizing:border-box}body{margin:0;font:16px/1.4 -apple-system,system-ui,Segoe UI,Roboto,sans-serif;background:#000;color:#ECECF1;padding:20px 16px 40px}
h1{font-size:22px;margin:4px 0 2px}p.s{color:#8B8B95;margin:0 0 18px;font-size:14px}
.card{background:#0E0E10;border:1px solid #1C1C20;border-radius:18px;padding:16px;margin-bottom:14px}
.card h2{font-size:13px;color:#8B8B95;text-transform:uppercase;letter-spacing:.06em;margin:0 0 12px}
.btns{display:grid;grid-template-columns:1fr 1fr;gap:10px}
label.b,button{display:flex;align-items:center;justify-content:center;gap:8px;height:52px;border-radius:14px;border:0;font:600 15px system-ui;background:#1C1C21;color:#ECECF1;cursor:pointer}
label.b.main{background:#ECECF1;color:#000}button.full{width:100%;margin-top:10px}
input[type=file]{display:none}textarea{width:100%;min-height:90px;background:#000;color:#ECECF1;border:1px solid #26262B;border-radius:12px;padding:12px;font:15px system-ui;resize:vertical}
.q{margin-top:12px}.it{display:flex;align-items:center;gap:10px;padding:8px 0;border-top:1px solid #1C1C20;font-size:14px}.it:first-child{border-top:0}
.it .n{flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.it .st{color:#8B8B95;font-size:12px}.ok{color:#4ADE80!important}.err{color:#F87171!important}
.bar{height:4px;background:#26262B;border-radius:2px;overflow:hidden;margin-top:4px}.bar i{display:block;height:100%;width:0;background:#4ADE80;transition:width .15s}
a.dl{color:#ECECF1;text-decoration:none;background:#1C1C21;border-radius:10px;padding:8px 12px;font-weight:600}
pre{white-space:pre-wrap;word-break:break-word;background:#000;border:1px solid #26262B;border-radius:12px;padding:12px;margin:0;font:14px system-ui}
.empty{color:#6F6F79;font-size:14px}
</style></head><body>
<h1>Isle</h1><p class="s">Отправь на компьютер — без проводов и приложений</p>
<div class="card"><h2>На компьютер</h2>
<div class="btns"><label class="b main">📷 Фото и видео<input id="ph" type="file" accept="image/*,video/*" multiple></label>
<label class="b">📄 Файлы<input id="fl" type="file" multiple></label></div>
<div id="q" class="q"></div>
<textarea id="tx" placeholder="Текст или ссылка — появится в буфере обмена компьютера" style="margin-top:12px"></textarea>
<button class="full" id="sendt">Отправить текст</button></div>
<div class="card"><h2>С компьютера</h2><div id="out"><div class="empty">Перетащи файлы на вкладку «Телефон» в Isle — они появятся здесь</div></div></div>
<script>
const base=location.pathname.endsWith('/')?location.pathname:location.pathname+'/';
const q=document.getElementById('q');
function fmt(n){return n>1048576?(n/1048576).toFixed(1)+' МБ':Math.max(1,Math.round(n/1024))+' КБ'}
function up(files){[...files].reduce((p,f)=>p.then(()=>one(f)),Promise.resolve())}
function one(f){return new Promise(res=>{const d=document.createElement('div');d.className='it';d.innerHTML='<div class="n"></div><div class="st">0%</div>';d.querySelector('.n').textContent=f.name;
const w=document.createElement('div');w.innerHTML='<div class="bar"><i></i></div>';q.prepend(w);q.prepend(d);
const x=new XMLHttpRequest();x.open('POST',base+'up?name='+encodeURIComponent(f.name));
x.upload.onprogress=e=>{if(e.lengthComputable){const p=Math.round(e.loaded*100/e.total);d.querySelector('.st').textContent=p+'%';w.querySelector('i').style.width=p+'%'}};
x.onload=()=>{const ok=x.status==200;d.querySelector('.st').textContent=ok?'✓ отправлено':'ошибка';d.querySelector('.st').className='st '+(ok?'ok':'err');w.querySelector('i').style.width='100%';res()};
x.onerror=()=>{d.querySelector('.st').textContent='нет связи';d.querySelector('.st').className='st err';res()};x.send(f)})}
document.getElementById('ph').onchange=e=>{up(e.target.files);e.target.value=''};
document.getElementById('fl').onchange=e=>{up(e.target.files);e.target.value=''};
document.getElementById('sendt').onclick=()=>{const t=document.getElementById('tx');if(!t.value.trim())return;
fetch(base+'text',{method:'POST',body:t.value}).then(r=>{if(r.ok){t.value='';const b=document.getElementById('sendt');b.textContent='✓ В буфере компьютера';setTimeout(()=>b.textContent='Отправить текст',1600)}})};
let last='';
function poll(){fetch(base+'list',{cache:'no-store'}).then(r=>r.json()).then(d=>{const s=JSON.stringify(d);if(s==last)return;last=s;const o=document.getElementById('out');o.innerHTML='';
if(d.text){const p=document.createElement('pre');p.textContent=d.text;o.appendChild(p)}
d.files.forEach(f=>{const r=document.createElement('div');r.className='it';r.innerHTML='<div class="n"></div><div class="st"></div><a class="dl">Скачать</a>';
r.querySelector('.n').textContent=f.name;r.querySelector('.st').textContent=fmt(f.size);r.querySelector('a').href=base+'f/'+f.id;o.appendChild(r)});
if(!d.text&&!d.files.length)o.innerHTML='<div class="empty">Перетащи файлы на вкладку «Телефон» в Isle — они появятся здесь</div>'}).catch(()=>{})}
poll();setInterval(poll,3000);
</script></body></html>
""";
    }
}
