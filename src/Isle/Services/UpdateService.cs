using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Isle.Services
{
    public class UpdateInfo
    {
        public Version Version;
        public string Tag = "";
        public string Notes = "";
        public string SetupUrl = "";
        public long Size;
        public DateTime Published;
    }

    /// <summary>
    /// Обновления из GitHub Releases репозитория Jeredpoi/-: проверка при запуске и раз в 6 часов,
    /// скачивание Isle-Setup.exe и тихая установка поверх с перезапуском.
    /// </summary>
    public static class UpdateService
    {
        private const string Repo = "Jeredpoi/-";
        private static readonly HttpClient Http = CreateClient();
        private static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromHours(6) };
        private static bool _notified;

        public static UpdateInfo Available { get; private set; }
        public static DateTime? LastCheck { get; private set; }
        public static event Action Changed;

        public static Version Current
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Isle-Updater");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return c;
        }

        public static void StartAutoCheck()
        {
            Timer.Tick += async (_, __) => await CheckAsync(true);
            Timer.Start();
            _ = Task.Delay(TimeSpan.FromSeconds(20)).ContinueWith(_ =>
                System.Windows.Application.Current?.Dispatcher.InvokeAsync(async () => await CheckAsync(true)));
        }

        /// <summary>Проверить, есть ли версия новее. Возвращает её или null.</summary>
        public static async Task<UpdateInfo> CheckAsync(bool quiet = false)
        {
            if (quiet && !AppSettings.Current.AutoUpdateCheck) return null;
            try
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://api.github.com/repos/" + Repo + "/releases/latest"));
                var r = doc.RootElement;
                var tag = r.GetProperty("tag_name").GetString() ?? "";
                if (!Version.TryParse(tag.TrimStart('v', 'V'), out var ver)) return null;
                ver = new Version(ver.Major, ver.Minor, Math.Max(0, ver.Build));
                var asset = r.GetProperty("assets").EnumerateArray()
                    .FirstOrDefault(a => (a.GetProperty("name").GetString() ?? "").Equals("Isle-Setup.exe", StringComparison.OrdinalIgnoreCase));
                LastCheck = DateTime.Now;
                if (asset.ValueKind != JsonValueKind.Object || ver <= Current)
                {
                    Available = null;
                    Changed?.Invoke();
                    return null;
                }
                Available = new UpdateInfo
                {
                    Version = ver, Tag = tag,
                    Notes = r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                    SetupUrl = asset.GetProperty("browser_download_url").GetString(),
                    Size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
                    Published = r.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String ? p.GetDateTime().ToLocalTime() : DateTime.Now
                };
                Changed?.Invoke();
                if (quiet && !_notified)
                {
                    _notified = true;
                    ToastBus.Show(new Toast
                    {
                        Title = "Доступна новая версия Isle " + ver.ToString(3),
                        Subtitle = "Настройки → Обновления → «Обновить»",
                        Icon = Icons.Restart, Important = true, Duration = TimeSpan.FromSeconds(6)
                    });
                }
                return Available;
            }
            catch (Exception ex)
            {
                if (!quiet) throw new Exception(ex is HttpRequestException hre && hre.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? "релизов пока нет" : ex.Message);
                return null;
            }
        }

        /// <summary>Скачивает установщик и запускает тихую установку. Isle закроется и запустится уже новой версией.</summary>
        public static async Task DownloadAndInstallAsync(UpdateInfo info, IProgress<double> progress)
        {
            var path = Path.Combine(Path.GetTempPath(), "Isle-Setup-" + info.Version.ToString(3) + ".exe");
            using (var resp = await Http.GetAsync(info.SetupUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? info.Size;
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(path);
                var buf = new byte[81920];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    done += n;
                    if (total > 0) progress?.Report(done / (double)total);
                }
            }
            progress?.Report(1);
            // /VERYSILENT — без окон; установщик сам закроет Isle и запустит новую версию
            Process.Start(new ProcessStartInfo(path, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = true });
            await Task.Delay(500);
            System.Windows.Application.Current.Shutdown();
        }
    }
}
