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
                    // у обычного exe Windows событие часто «принимает», но не присылает — поэтому опрос всё равно частый
                    _timer.Interval = TimeSpan.FromSeconds(1.5);
                    // «только в острове» — чаще, чтобы плашка Windows успевала мелькнуть как можно меньше
                    _timer.Tick += (_, __) =>
                    {
                        var want = TimeSpan.FromMilliseconds(AppSettings.Current.NotificationsOnlyInIsland ? 400 : 1500);
                        if (_timer.Interval != want) _timer.Interval = want;
                    };
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
            SyncBanners();
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
                    // «Связь с телефоном» Windows пересылает уведомления телефона по Bluetooth — показываем их как «с телефона»
                    if (IsPhoneLink(app, appId))
                    {
                        PhoneLink.AddNotice(new PhoneNotice { App = app, Title = title, Body = body });
                        ToastBus.Show(new Toast
                        {
                            Kind = ToastKind.Notification,
                            Title = title,
                            Subtitle = string.IsNullOrEmpty(body) ? "С телефона" : body,
                            Icon = Icons.Phone,
                            LaunchAppId = appId,
                            Duration = TimeSpan.FromSeconds(5),
                            ReplaceKey = updated ? "win-notification-" + n.Id : null
                        });
                    }
                    else
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
                    if (BannersSuppressed && appId != null) SuppressBanner(appId); // следующие от этого приложения — без всплывашки Windows
                    // «только в острове»: убираем уведомление у Windows — плашка справа снизу исчезает (без дубля).
                    // В «Не беспокоить» остров уведомление не покажет — тогда оставляем его Windows, чтобы не потерялось.
                    if (AppSettings.Current.NotificationsOnlyInIsland && !AppSettings.Current.DoNotDisturb)
                    {
                        try { _listener.RemoveNotification(n.Id); Journal.Debug("notify", "убрано у Windows (только в острове): " + Journal.Short(app, 40)); }
                        catch (Exception ex) { Journal.Debug("notify", "не удалось убрать у Windows: " + ex.Message); }
                    }
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

        /// <summary>Уведомление переслала «Связь с телефоном» (Phone Link) — значит, оно пришло на телефон.</summary>
        public static bool IsPhoneLink(string app, string appId) =>
            (appId?.Contains("YourPhone", StringComparison.OrdinalIgnoreCase) ?? false)
            || (appId?.Contains("PhoneExperience", StringComparison.OrdinalIgnoreCase) ?? false)
            || app.Equals("Связь с телефоном", StringComparison.OrdinalIgnoreCase) || app.Equals("Phone Link", StringComparison.OrdinalIgnoreCase)
            || app.Equals("Ваш телефон", StringComparison.OrdinalIgnoreCase) || app.Equals("Your Phone", StringComparison.OrdinalIgnoreCase);

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

        // ===== Всплывающие баннеры Windows =====
        // «Только в острове»: убрать уведомление у Windows можно лишь после того, как оно пришло, — баннер справа снизу
        // успевал мелькнуть и резко исчезнуть. Поэтому выключаем сам баннер (как переключатель «Показывать баннеры»
        // в Параметры → Уведомления → приложение): уведомление тихо попадает в центр уведомлений, Isle его видит и показывает.
        // Прежние значения запоминаем и возвращаем, когда настройку выключат или включат «Не беспокоить».
        private const string NotifSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
        private static bool BannersSuppressed => AppSettings.Current.NotificationsOnlyInIsland && AppSettings.Current.NotificationToasts && !AppSettings.Current.DoNotDisturb;
        private bool? _bannersState;

        /// <summary>Выход из Isle: вернуть баннеры Windows, иначе без Isle уведомления приходили бы молча.</summary>
        public void RestoreBanners() { _bannersState = null; SyncBanners(false); }

        private void SyncBanners(bool? force = null)
        {
            bool want = force ?? (Available && BannersSuppressed);
            if (_bannersState == want) return;
            _bannersState = want;
            try
            {
                if (want)
                {
                    using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(NotifSettingsKey);
                    foreach (var id in root?.GetSubKeyNames() ?? Array.Empty<string>()) SuppressBanner(id);
                    Journal.Info("notify", "баннеры Windows выключены: уведомления только в острове");
                }
                else if (AppSettings.Current.BannersOff.Count > 0)
                {
                    foreach (var (id, was) in AppSettings.Current.BannersOff.ToList())
                    {
                        try
                        {
                            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(NotifSettingsKey + "\\" + id, true);
                            if (k == null) continue;
                            if (was < 0) k.DeleteValue("ShowBanner", false); else k.SetValue("ShowBanner", was, Microsoft.Win32.RegistryValueKind.DWord);
                        }
                        catch { }
                    }
                    AppSettings.Current.BannersOff.Clear();
                    AppSettings.Current.Save();
                    Journal.Info("notify", "баннеры Windows возвращены как были");
                }
            }
            catch (Exception ex) { Journal.Warn("notify", "баннеры Windows: " + ex.Message); }
        }

        private static void SuppressBanner(string appId)
        {
            var off = AppSettings.Current.BannersOff;
            if (off.ContainsKey(appId) || appId.Contains("Isle", StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(NotifSettingsKey + "\\" + appId, true);
                int was = k.GetValue("ShowBanner") is int v ? v : -1;
                if (was == 0) return; // пользователь сам выключил — не трогаем и не «возвращаем»
                k.SetValue("ShowBanner", 0, Microsoft.Win32.RegistryValueKind.DWord);
                off[appId] = was;
                AppSettings.Current.Save();
            }
            catch (Exception ex) { Journal.Debug("notify", "баннер не выключить: " + ex.Message); }
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
