using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Isle.Services
{
    /// <summary>
    /// HTTPS для странички телефона без предупреждений браузера. Браузеры доверяют только сертификатам на домен,
    /// а у нас адрес вида 192.168.1.5, поэтому используем бесплатный сервис local-ip.sh: имя 192-168-1-5.local-ip.sh
    /// указывает на этот же адрес в твоей сети, а настоящий сертификат Let's Encrypt на *.local-ip.sh сервис раздаёт открыто.
    /// Ключ этого сертификата публичный — шифрование формальное, защита по-прежнему держится на случайном ключе в ссылке.
    /// Сертификат кэшируется и обновляется сам; нет интернета или сервиса — остаётся обычный http.
    /// </summary>
    public static class PhoneTls
    {
        public const string Domain = "local-ip.sh";
        private const string PemUrl = "https://local-ip.sh/server.pem", KeyUrl = "https://local-ip.sh/server.key";
        private static readonly string CacheDir = Path.Combine(Storage.Dir, "phone-tls");
        private static readonly SemaphoreSlim Gate = new(1, 1);

        /// <summary>Готовый сертификат для SslStream (null — работаем по http).</summary>
        public static SslStreamCertificateContext Context { get; private set; }
        public static bool Ready => Context != null;

        /// <summary>Имя для адреса: 192.168.1.5 → 192-168-1-5.local-ip.sh</summary>
        public static string Host(string ip) => ip.Replace('.', '-') + "." + Domain;

        /// <summary>Загрузить сертификат из кэша, при необходимости скачать свежий. Безопасно вызывать много раз.</summary>
        public static async Task EnsureAsync()
        {
            if (!await Gate.WaitAsync(0)) return;
            try
            {
                Directory.CreateDirectory(CacheDir);
                string pem = Path.Combine(CacheDir, "server.pem"), key = Path.Combine(CacheDir, "server.key");
                if (Context == null && File.Exists(pem) && File.Exists(key)) TryLoad(pem, key, "из кэша");
                var leaf = Context?.TargetCertificate;
                bool stale = leaf == null || leaf.NotAfter < DateTime.Now.AddDays(20) || File.GetLastWriteTime(pem) < DateTime.Now.AddDays(-7);
                if (!stale) return;
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                    var newPem = await http.GetStringAsync(PemUrl);
                    var newKey = await http.GetStringAsync(KeyUrl);
                    if (!newPem.Contains("BEGIN CERTIFICATE") || !newKey.Contains("PRIVATE KEY")) throw new InvalidDataException("не сертификат");
                    File.WriteAllText(pem + ".new", newPem); File.WriteAllText(key + ".new", newKey);
                    if (TryLoad(pem + ".new", key + ".new", "скачан"))
                    {
                        File.Move(pem + ".new", pem, true); File.Move(key + ".new", key, true);
                    }
                }
                catch (Exception ex) { Journal.Warn("phone", "сертификат для https не скачался: " + ex.Message + (Ready ? " — работаю со старым" : " — страничка будет по http")); }
            }
            finally { Gate.Release(); }
        }

        private static bool TryLoad(string pemPath, string keyPath, string how)
        {
            try
            {
                var all = new X509Certificate2Collection();
                all.ImportFromPemFile(pemPath);
                using var withKey = X509Certificate2.CreateFromPemFile(pemPath, keyPath);
                // SChannel в Windows не берёт ключ «из памяти» — пересобираем через PFX
                var leaf = new X509Certificate2(withKey.Export(X509ContentType.Pkcs12), (string)null, X509KeyStorageFlags.Exportable);
                if (leaf.NotAfter < DateTime.Now || !leaf.Subject.Contains(Domain, StringComparison.OrdinalIgnoreCase)) { Journal.Warn("phone", "сертификат https не подходит (" + leaf.Subject + ", до " + leaf.NotAfter.ToString("d") + ")"); return false; }
                var chain = new X509Certificate2Collection(all.Cast<X509Certificate2>().Where(c => c.Thumbprint != leaf.Thumbprint).ToArray());
                Context = SslStreamCertificateContext.Create(leaf, chain, offline: true);
                Journal.Info("phone", "https для телефона: сертификат " + how + ", действует до " + leaf.NotAfter.ToString("d"));
                return true;
            }
            catch (Exception ex) { Journal.Warn("phone", "сертификат https не загрузился: " + ex.Message); return false; }
        }
    }

    /// <summary>Поток, который сначала отдаёт уже прочитанные байты (по ним узнали, TLS это или обычный HTTP), потом — исходный.</summary>
    internal sealed class PrefixStream : Stream
    {
        private readonly Stream _inner;
        private byte[] _prefix;
        private int _pos;
        public PrefixStream(byte[] prefix, Stream inner) { _prefix = prefix; _inner = inner; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_prefix != null)
            {
                int n = Math.Min(count, _prefix.Length - _pos);
                Array.Copy(_prefix, _pos, buffer, offset, n);
                _pos += n; if (_pos >= _prefix.Length) _prefix = null;
                return n;
            }
            return _inner.Read(buffer, offset, count);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_prefix != null)
            {
                int n = Math.Min(buffer.Length, _prefix.Length - _pos);
                _prefix.AsMemory(_pos, n).CopyTo(buffer);
                _pos += n; if (_pos >= _prefix.Length) _prefix = null;
                return n;
            }
            return await _inner.ReadAsync(buffer, ct);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => _inner.WriteAsync(buffer, ct);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _inner.WriteAsync(buffer, offset, count, ct);
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
