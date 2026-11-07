using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Mgmt = Windows.UI.Notifications.Management;
using Notif = Windows.UI.Notifications;

namespace Isle.Services
{
    /// <summary>
    /// Показывает уведомления других приложений в острове.
    /// Требует разрешения Windows «Доступ к уведомлениям»; если система не даёт доступ
    /// (бывает у неупакованных приложений) — модуль тихо отключается.
    /// </summary>
    public class NotificationService
    {
        private Mgmt.UserNotificationListener _listener;
        private readonly Dictionary<uint, string> _seen = new();
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
        public bool Available { get; private set; }
        public string Status { get; private set; } = "Не запущено";

        public async Task StartAsync()
        {
            try
            {
                _listener = Mgmt.UserNotificationListener.Current;
                var access = await _listener.RequestAccessAsync();
                if (access != Mgmt.UserNotificationListenerAccessStatus.Allowed)
                {
                    Status = "Windows не дал доступ к уведомлениям";
                    return;
                }
                foreach (var n in await _listener.GetNotificationsAsync(Notif.NotificationKinds.Toast)) _seen[n.Id] = Fingerprint(n);
                Available = true;
                Status = "Работает";
                _timer.Tick += async (_, __) => await Poll();
                // Мгновенно — по событию Windows «уведомления изменились». Оно работает только у приложений-пакетов (MSIX),
                // у обычного exe Windows отказывает — тогда опрашиваем чаще, раз в секунду.
                try
                {
                    _listener.NotificationChanged += (_, __) => System.Windows.Application.Current?.Dispatcher.InvokeAsync(async () => await Poll());
                    _timer.Interval = TimeSpan.FromSeconds(10); // подстраховка
                }
                catch { _timer.Interval = TimeSpan.FromSeconds(1); }
                _timer.Start();
            }
            catch (Exception ex)
            {
                Status = "Недоступно: " + ex.Message;
            }
        }

        private bool _polling;

        private async Task Poll()
        {
            if (!AppSettings.Current.NotificationToasts || _polling) return; // опросы не накладываются друг на друга
            _polling = true;
            try
            {
                var list = await _listener.GetNotificationsAsync(Notif.NotificationKinds.Toast);
                var activeIds = new HashSet<uint>(list.Select(x => x.Id));
                foreach (var stale in _seen.Keys.Where(id => !activeIds.Contains(id)).ToList()) _seen.Remove(stale);
                foreach (var n in list.OrderBy(x => x.CreationTime))
                {
                    var fingerprint = Fingerprint(n);
                    bool updated = _seen.TryGetValue(n.Id, out var previous);
                    if (updated && previous == fingerprint) continue;
                    _seen[n.Id] = fingerprint;
                    string app = "";
                    try { app = n.AppInfo?.DisplayInfo?.DisplayName ?? ""; } catch { }
                    var binding = n.Notification?.Visual?.GetBinding(Notif.KnownNotificationBindings.ToastGeneric);
                    var texts = binding?.GetTextElements()?.Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? new List<string>();
                    var title = texts.Count > 0 ? texts[0] : app;
                    // «Ножницы» сообщают о снимке, который Isle уже показал своей карточкой с миниатюрой — не дублируем
                    if (DateTime.Now - ClipboardService.LastScreenshotToast < TimeSpan.FromSeconds(15)
                        && (app.Contains("Ножниц", StringComparison.OrdinalIgnoreCase) || app.Contains("Snipping", StringComparison.OrdinalIgnoreCase)
                            || app.Contains("Набросок", StringComparison.OrdinalIgnoreCase) || app.Contains("Snip", StringComparison.OrdinalIgnoreCase)))
                        continue;
                    var body = string.Join(" ", texts.Skip(1));
                    string appId = null;
                    try { appId = n.AppInfo?.AppUserModelId; } catch { }
                    ToastBus.Show(new Toast
                    {
                        Kind = ToastKind.Notification,
                        Title = string.IsNullOrEmpty(app) || app == title ? title : title + "  ·  " + app,
                        Subtitle = body,
                        Icon = Icons.Bell,
                        Image = await AppLogoAsync(n, app), // значок приложения, которое прислало уведомление
                        LaunchAppId = appId,                 // клик — открыть это приложение
                        Duration = TimeSpan.FromSeconds(5),
                        ReplaceKey = updated ? "win-notification-" + n.Id : null
                    });
                }
                if (_seen.Count > 2000)
                {
                    _seen.Clear();
                    foreach (var n in await _listener.GetNotificationsAsync(Notif.NotificationKinds.Toast)) _seen[n.Id] = Fingerprint(n);
                }
            }
            catch (Exception ex) { App.Log("notification poll: " + ex.Message); }
            finally { _polling = false; }
        }

        private static readonly Dictionary<string, System.Windows.Media.ImageSource> Logos = new();

        /// <summary>Логотип приложения из уведомления Windows (кэшируется по приложению). null — нет логотипа.</summary>
        private static async Task<System.Windows.Media.ImageSource> AppLogoAsync(Notif.UserNotification n, string app)
        {
            string key = app ?? "";
            try { key = n.AppInfo?.AppUserModelId ?? key; } catch { }
            if (Logos.TryGetValue(key, out var cached)) return cached;
            System.Windows.Media.ImageSource result = null;
            try
            {
                var logo = n.AppInfo?.DisplayInfo?.GetLogo(new Windows.Foundation.Size(64, 64));
                if (logo != null)
                {
                    using var stream = await logo.OpenReadAsync();
                    using var net = System.IO.WindowsRuntimeStreamExtensions.AsStreamForRead(stream);
                    var ms = new System.IO.MemoryStream();
                    await net.CopyToAsync(ms);
                    ms.Position = 0;
                    var img = new System.Windows.Media.Imaging.BitmapImage();
                    img.BeginInit();
                    img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    img.StreamSource = ms;
                    img.DecodePixelWidth = 64;
                    img.EndInit();
                    img.Freeze();
                    result = img;
                }
            }
            catch { }
            Logos[key] = result;
            return result;
        }

        /// <summary>Открыть приложение по его AppUserModelId (через shell:AppsFolder — так же, как из меню «Пуск»).</summary>
        public static bool LaunchApp(string appId)
        {
            if (string.IsNullOrWhiteSpace(appId)) return false;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + appId) { UseShellExecute = true });
                return true;
            }
            catch { return false; }
        }

        private static string Fingerprint(Notif.UserNotification n)
        {
            try
            {
                var binding = n.Notification?.Visual?.GetBinding(Notif.KnownNotificationBindings.ToastGeneric);
                var texts = binding?.GetTextElements()?.Select(t => t.Text) ?? Enumerable.Empty<string>();
                return string.Join("\n", texts);
            }
            catch { return n.Id.ToString(); }
        }
    }
}
