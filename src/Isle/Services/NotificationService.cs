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
        private readonly HashSet<uint> _seen = new();
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
                foreach (var n in await _listener.GetNotificationsAsync(Notif.NotificationKinds.Toast)) _seen.Add(n.Id);
                Available = true;
                Status = "Работает";
                _timer.Tick += async (_, __) => await Poll();
                _timer.Start();
            }
            catch (Exception ex)
            {
                Status = "Недоступно: " + ex.Message;
            }
        }

        private async Task Poll()
        {
            if (!AppSettings.Current.NotificationToasts) return;
            try
            {
                var list = await _listener.GetNotificationsAsync(Notif.NotificationKinds.Toast);
                foreach (var n in list.OrderBy(x => x.CreationTime))
                {
                    if (!_seen.Add(n.Id)) continue;
                    string app = "";
                    try { app = n.AppInfo?.DisplayInfo?.DisplayName ?? ""; } catch { }
                    var binding = n.Notification?.Visual?.GetBinding(Notif.KnownNotificationBindings.ToastGeneric);
                    var texts = binding?.GetTextElements()?.Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? new List<string>();
                    var title = texts.Count > 0 ? texts[0] : app;
                    var body = string.Join(" ", texts.Skip(1));
                    ToastBus.Show(new Toast
                    {
                        Kind = ToastKind.Notification,
                        Title = string.IsNullOrEmpty(app) || app == title ? title : title + "  ·  " + app,
                        Subtitle = body,
                        Icon = Icons.Bell,
                        Duration = TimeSpan.FromSeconds(5)
                    });
                }
                if (_seen.Count > 2000) _seen.Clear();
            }
            catch { }
        }
    }
}
