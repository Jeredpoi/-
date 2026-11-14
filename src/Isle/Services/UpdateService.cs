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
        /// <summary>Отдельный клиент для скачивания файла: заголовок GitHub API тут мешает.</summary>
        private static readonly HttpClient Download = new() { Timeout = TimeSpan.FromMinutes(10) };
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
            // вскоре после запуска (в том числе вместе с Windows) — чтобы плашка «есть новая версия» была сразу
            _ = Task.Delay(TimeSpan.FromSeconds(8)).ContinueWith(_ =>
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
                    ShowAvailableToast();
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

        /// <summary>Плашка «есть новая версия»: клик по ней — сразу скачать и установить.</summary>
        public static void ShowAvailableToast()
        {
            var a = Available;
            if (a == null) return;
            ToastBus.Show(new Toast
            {
                Kind = ToastKind.Success, Title = "Новая версия Isle " + a.Version.ToString(3),
                Subtitle = "Нажми — скачаю и установлю" + (a.Size > 0 ? " · " + Mb(a.Size) : ""),
                Icon = Icons.Download, Important = true, Duration = TimeSpan.FromSeconds(12), ReplaceKey = "update",
                OnClick = () => _ = InstallFromToastAsync(),
            });
        }

        private static string Mb(long bytes) => (bytes / 1048576.0).ToString(bytes < 10 * 1048576 ? "0.0" : "0") + " МБ";

        public static bool Installing { get; private set; }

        /// <summary>Скачивание с прогрессом прямо в чёлке, затем тихая установка и перезапуск.</summary>
        public static async Task InstallFromToastAsync()
        {
            var a = Available;
            if (a == null || Installing) return;
            string title = "Обновление до " + a.Version.ToString(3);
            double last = -1;
            void Show(double p, string sub) => ToastBus.Show(new Toast
            {
                Kind = ToastKind.Download, Title = title, Subtitle = sub, Icon = Icons.Download, Progress = p,
                ReplaceKey = "update", Important = true, Duration = TimeSpan.FromMinutes(10),
            });
            Show(0, "Скачиваю…");
            var progress = new Progress<double>(p =>
            {
                // плашку обновляем не чаще чем на каждый процент
                if (p < 1 && p - last < 0.01) return;
                last = p;
                Show(p, p >= 1 ? "Устанавливаю — Isle перезапустится сам" : "Скачиваю… " + (int)(p * 100) + "%" + (a.Size > 0 ? " · " + Mb((long)(a.Size * p)) + " из " + Mb(a.Size) : ""));
            });
            try { await DownloadAndInstallAsync(a, progress); }
            catch (InvalidOperationException) when (Installing) { } // уже обновляется (кнопкой в настройках) — второй загрузки не будет
            catch (Exception ex)
            {
                Journal.Info("update", "не удалось обновить: " + ex.Message);
                ToastBus.Show(new Toast
                {
                    Kind = ToastKind.Error, Title = "Не удалось обновить", Subtitle = "Нажми, чтобы попробовать ещё раз",
                    Icon = Icons.Restart, Important = true, Duration = TimeSpan.FromSeconds(10), ReplaceKey = "update",
                    OnClick = () => _ = InstallFromToastAsync(),
                });
            }
        }

        /// <summary>Скачивает установщик и запускает тихую установку. Isle закроется и запустится уже новой версией.</summary>
        public static async Task DownloadAndInstallAsync(UpdateInfo info, IProgress<double> progress)
        {
            // одна загрузка за раз: плашка в чёлке и кнопка в настройках писали в один и тот же файл установщика
            if (Installing) throw new InvalidOperationException("Обновление уже скачивается");
            Installing = true;
            try { await DownloadAndRunAsync(info, progress); }
            catch { Installing = false; throw; }
        }

        private static async Task DownloadAndRunAsync(UpdateInfo info, IProgress<double> progress)
        {
            var path = Path.Combine(Path.GetTempPath(), "Isle-Setup-" + info.Version.ToString(3) + ".exe");
            Download.DefaultRequestHeaders.UserAgent.Clear();
            Download.DefaultRequestHeaders.UserAgent.ParseAdd("Isle-Updater");
            using (var resp = await Download.GetAsync(info.SetupUrl, HttpCompletionOption.ResponseHeadersRead))
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
