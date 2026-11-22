using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Isle.Helpers
{
    /// <summary>
    /// Сеть для всех запросов Isle. Системный прокси учитываем, но если это прокси VPN-клиента на этом же компьютере
    /// (127.0.0.1:10809 и т. п.), а сам клиент выключен — идём напрямую: иначе погода, тексты песен и ИИ
    /// падали с «конечный компьютер отверг запрос» до перезапуска VPN.
    /// </summary>
    public static class Net
    {
        public static HttpClient Client(TimeSpan timeout) => new(new SocketsHttpHandler { Proxy = new LocalProxyFallback(), UseProxy = true }) { Timeout = timeout };

        private sealed class LocalProxyFallback : IWebProxy
        {
            private static readonly object Lock = new();
            private static string _checkedKey;
            private static bool _alive;
            private static DateTime _checkedAt;

            public ICredentials Credentials { get; set; }

            public Uri GetProxy(Uri destination)
            {
                Uri p;
                try { p = HttpClient.DefaultProxy.GetProxy(destination); } catch { return null; }
                if (p == null || p == destination) return null;
                if (!IsLocal(p.Host)) return p;
                return Alive(p) ? p : null;
            }

            public bool IsBypassed(Uri host) => GetProxy(host) == null;

            private static bool IsLocal(string host) =>
                host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));

            /// <summary>Слушает ли кто-то порт прокси. Проверка — 200 мс, результат помним 15 секунд.</summary>
            private static bool Alive(Uri p)
            {
                string key = p.Host + ":" + p.Port;
                lock (Lock)
                {
                    if (key == _checkedKey && DateTime.Now - _checkedAt < TimeSpan.FromSeconds(15)) return _alive;
                }
                bool alive = false;
                try
                {
                    using var tcp = new TcpClient();
                    alive = tcp.ConnectAsync(p.Host == "localhost" ? "127.0.0.1" : p.Host, p.Port).Wait(200) && tcp.Connected;
                }
                catch { alive = false; }
                lock (Lock)
                {
                    if (key != _checkedKey || alive != _alive)
                        Services.Journal.Info("net", "прокси " + key + (alive ? " работает — идём через него" : " не отвечает (VPN выключен?) — идём напрямую"));
                    _checkedKey = key; _alive = alive; _checkedAt = DateTime.Now;
                }
                return alive;
            }
        }
    }
}
