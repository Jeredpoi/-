using System;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using Isle.Services;

namespace Isle
{
    /// <summary>Значок Isle в трее: видно, что остров запущен, и есть меню «Открыть / Настройки / Выйти».</summary>
    public sealed class TrayIcon : IDisposable
    {
        private readonly Forms.NotifyIcon _icon;
        private readonly Drawing.Icon _img;

        private static string HintText() => string.IsNullOrWhiteSpace(AppSettings.Current.Hotkey)
            ? "Isle — чёлка работает" : "Isle — чёлка работает. " + AppSettings.Current.Hotkey.Replace("Space", "Пробел") + " — открыть";

        /// <summary>Сочетание клавиш поменяли — подсказка у значка тоже.</summary>
        public void Refresh() { try { if (_icon != null) _icon.Text = HintText(); } catch { } }

        public TrayIcon()
        {
            _img = LoadAppIcon() ?? MakeIcon();
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Открыть остров", null, (_, __) => AppHost.Window?.BringUp());
            menu.Items.Add("Настройки", null, (_, __) => AppHost.Window?.BringUp("settings"));
            menu.Items.Add("Проверить обновления", null, (_, __) => AppHost.Window?.OpenSettings("updates"));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Выйти из Isle", null, (_, __) => System.Windows.Application.Current.Shutdown());

            _icon = new Forms.NotifyIcon
            {
                Icon = _img,
                Text = HintText(),
                ContextMenuStrip = menu,
                Visible = true
            };
            _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) AppHost.Window?.BringUp(); };
            // подсказка «где чёлка» — только при первом запуске, а не при каждом включении компьютера
            if (!AppSettings.Current.TrayHintShown)
            {
                _icon.ShowBalloonTip(3000, "Isle запущен",
                    "Чёлка сверху по центру экрана. Наведи курсор на верхний край" + (string.IsNullOrWhiteSpace(AppSettings.Current.Hotkey) ? "." : " или нажми " + AppSettings.Current.Hotkey + "."), Forms.ToolTipIcon.None);
                AppSettings.Current.TrayHintShown = true;
                AppSettings.Current.Save();
            }
        }

        /// <summary>Значок приложения (Клоди) из ресурсов — тот же, что у exe и ярлыка.</summary>
        private static Drawing.Icon LoadAppIcon()
        {
            try
            {
                var res = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Isle;component/app.ico"));
                if (res == null) return null;
                using var st = res.Stream;
                return new Drawing.Icon(st, new Drawing.Size(32, 32));
            }
            catch { return null; }
        }

        /// <summary>Запасной вариант: рисуем значок сами — чёрная капсула с двумя «глазами».</summary>
        private static Drawing.Icon MakeIcon()
        {
            using var bmp = new Drawing.Bitmap(32, 32);
            using (var g = Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Drawing.Color.Transparent);
                using var path = new Drawing.Drawing2D.GraphicsPath();
                var r = new Drawing.Rectangle(1, 8, 30, 16);
                int d = 16;
                path.AddArc(r.X, r.Y, d, d, 90, 180);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
                path.CloseFigure();
                using var black = new Drawing.SolidBrush(Drawing.Color.Black);
                using var edge = new Drawing.Pen(Drawing.Color.FromArgb(120, 255, 255, 255), 1.5f);
                g.FillPath(black, path);
                g.DrawPath(edge, path);
                using var white = new Drawing.SolidBrush(Drawing.Color.White);
                g.FillRectangle(white, 11, 12, 3, 8);
                g.FillRectangle(white, 18, 12, 3, 8);
            }
            return Drawing.Icon.FromHandle(bmp.GetHicon());
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
            _img.Dispose();
        }
    }
}
