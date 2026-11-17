using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Isle.Helpers;
using Isle.Services;

namespace Isle.Views
{
    /// <summary>
    /// Вкладка «Телефон»: QR-код со ссылкой на страничку, всё, что пришло с телефона, и файлы для телефона.
    /// Сервер (PhoneLink) работает, пока вкладку открывают; без неё выключается через 15 минут простоя.
    /// </summary>
    public partial class PhoneView : UserControl, IIslandView
    {
        private string _ip;
        private readonly DispatcherTimer _status = new() { Interval = TimeSpan.FromSeconds(1) };
        private static bool _hooked;

        public PhoneView()
        {
            InitializeComponent();
            PhoneLink.Changed += () => Dispatcher.InvokeAsync(Refresh);
            _status.Tick += (_, __) => UpdateStatus();
            IsVisibleChanged += (_, __) =>
            {
                PhoneLink.KeepAlive = IsVisible;
                if (IsVisible) { PhoneLink.Start(); _status.Start(); Refresh(); } else _status.Stop();
            };
            if (!_hooked) { _hooked = true; PhoneLink.ItemReceived += OnReceived; }
        }

        public void OnShown() { PhoneLink.Start(); Refresh(); }

        /// <summary>С телефона пришло: в «Буфер» (текст — сразу в буфер обмена) и плашка в чёлке.</summary>
        private static void OnReceived(PhoneItem it)
        {
            if (it.Text != null)
            {
                ClipboardService.IgnoreUntil = DateTime.Now.AddMilliseconds(400); // не дублировать: сами положим в историю
                ClipboardService.SetText(it.Text);
                AppHost.Clipboard.AddExternal(new ClipItem { Text = it.Text, Source = "Телефон", Kind = ClipboardService.Classify(it.Text, "") });
                ToastBus.Show(new Toast { Kind = ToastKind.Success, Title = "Текст с телефона — в буфере", Subtitle = it.Text.Length > 60 ? it.Text[..60] + "…" : it.Text, Icon = Icons.Phone, Important = true, Duration = TimeSpan.FromSeconds(3) });
                return;
            }
            AppHost.Clipboard.AddExternal(new ClipItem { Kind = ClipKind.Files, Files = new() { it.Path }, Text = it.Path, Source = "Телефон" });
            BitmapSource thumb = null;
            if (it.IsPhoto) thumb = LoadThumb(it.Path, 120);
            ToastBus.Show(new Toast
            {
                Kind = ToastKind.Success, Title = it.IsPhoto ? "Фото с телефона" : "Файл с телефона", Subtitle = it.Name + " · " + Size(it.Size),
                Icon = Icons.Phone, Image = thumb, FilePath = it.Path, Important = true, Duration = TimeSpan.FromSeconds(3.5),
            });
        }

        private static BitmapSource LoadThumb(string path, int h)
        {
            try
            {
                var b = new BitmapImage();
                b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.DecodePixelHeight = h; b.UriSource = new Uri(path); b.EndInit(); b.Freeze();
                return b;
            }
            catch { return null; }
        }

        private static string Size(long n) => n > 1048576 ? (n / 1048576.0).ToString("0.0") + " МБ" : Math.Max(1, n / 1024) + " КБ";

        // ---------- QR и сеть ----------
        private void Refresh()
        {
            var addrs = PhoneLink.Addresses();
            if (_ip == null || addrs.All(a => a.ip != _ip)) _ip = addrs.FirstOrDefault().ip;
            Networks.Children.Clear();
            if (addrs.Count > 1)
                foreach (var (ip, name) in addrs)
                {
                    var b = new System.Windows.Controls.Primitives.ToggleButton { Style = (Style)FindResource("Chip"), Content = name.Split('·')[0].Trim(), ToolTip = name + " · " + ip, IsChecked = ip == _ip, Margin = new Thickness(0, 0, 6, 6) };
                    var chosen = ip;
                    b.Click += (_, __) => { _ip = chosen; Refresh(); };
                    Networks.Children.Add(b);
                }
            if (_ip == null || !PhoneLink.Running)
            {
                Qr.Source = null; QrNone.Visibility = Visibility.Visible;
                QrNone.Text = _ip == null ? "Нет сети" : "Не запустилось";
                UrlBox.Text = "";
            }
            else
            {
                var url = PhoneLink.Url(_ip);
                Qr.Source = MakeQr(url); QrNone.Visibility = Visibility.Collapsed;
                UrlBox.Text = url;
            }
            BuildIn();
            BuildOut();
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            bool online = PhoneLink.PhoneOnline;
            StatusDot.Fill = new SolidColorBrush(online ? Color.FromRgb(0x4A, 0xDE, 0x80) : Color.FromRgb(0x6F, 0x6F, 0x79));
            StatusText.Text = !PhoneLink.Running ? "Сервер не запущен" : _ip == null ? "Подключись к Wi-Fi или к телефону" : online ? "Телефон на связи" : "Ждёт телефон";
        }

        private static BitmapSource MakeQr(string text)
        {
            try
            {
                var hints = new System.Collections.Generic.Dictionary<ZXing.EncodeHintType, object> { [ZXing.EncodeHintType.MARGIN] = 0 };
                var m = new ZXing.QrCode.QRCodeWriter().encode(text, ZXing.BarcodeFormat.QR_CODE, 0, 0, hints);
                int w = m.Width, h = m.Height;
                var px = new byte[w * h];
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) px[y * w + x] = m[x, y] ? (byte)0 : (byte)255;
                var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Gray8, null, px, w);
                b.Freeze();
                return b;
            }
            catch (Exception ex) { Journal.Warn("phone", "QR не получился: " + ex.Message); return null; }
        }

        // ---------- с телефона ----------
        private void BuildIn()
        {
            InList.Children.Clear();
            InEmpty.Visibility = PhoneLink.Received.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var it in PhoneLink.Received.Take(50))
            {
                var row = new Border { Background = (Brush)FindResource("Card"), CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 6), Cursor = Cursors.Hand };
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                FrameworkElement icon;
                if (it.IsPhoto && LoadThumb(it.Path, 72) is BitmapSource th)
                    icon = new Border { Width = 56, Height = 40, CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = new Image { Source = th, Stretch = Stretch.UniformToFill } };
                else
                    icon = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x20)),
                        Child = new Icon { Kind = it.Text != null ? "Text" : "File", Width = 15, Height = 15, Foreground = (Brush)FindResource("Muted") } };
                g.Children.Add(icon);
                var info = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                info.Children.Add(new TextBlock { Text = it.Text != null ? it.Text.Replace('\n', ' ') : it.Name, FontSize = 13, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis });
                info.Children.Add(new TextBlock { Text = (it.Text != null ? "текст · в буфере" : Size(it.Size)) + " · " + it.Time.ToString("HH:mm"), Style = (Style)FindResource("MutedText"), FontSize = 11 });
                Grid.SetColumn(info, 1); g.Children.Add(info);
                row.Child = g;
                row.ToolTip = it.Text != null ? "Клик — скопировать снова" : "Клик — открыть · правый клик — показать в папке";
                row.MouseLeftButtonUp += (_, __) =>
                {
                    if (it.Text != null) { ClipboardService.SetText(it.Text); ToastBus.Show(new Toast { Kind = ToastKind.Success, Title = "Скопировано", Icon = Icons.Copy, Important = true, Duration = TimeSpan.FromSeconds(1.2) }); }
                    else if (File.Exists(it.Path)) { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(it.Path) { UseShellExecute = true }); AppHost.Window.Collapse(); } catch { } }
                };
                row.MouseRightButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    if (it.Path != null && File.Exists(it.Path)) { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + it.Path + "\""); AppHost.Window.Collapse(); }
                };
                InList.Children.Add(row);
            }
        }

        // ---------- на телефон ----------
        private void BuildOut()
        {
            OutList.Children.Clear();
            OutHint.Text = PhoneLink.Outgoing.Count == 0 && PhoneLink.OutgoingText.Length == 0
                ? "Перетащи сюда файлы — на телефоне появится «Скачать»"
                : "Ждут на страничке телефона:";
            if (PhoneLink.OutgoingText.Length > 0) OutList.Children.Add(OutChip("Text", "Текст из буфера", () => PhoneLink.OfferText("")));
            foreach (var f in PhoneLink.Outgoing.ToList()) OutList.Children.Add(OutChip("File", f.Name, () => PhoneLink.Unoffer(f)));
        }

        private FrameworkElement OutChip(string icon, string name, Action remove)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new Icon { Kind = icon, Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("Muted") });
            sp.Children.Add(new TextBlock { Text = name, FontSize = 12, Margin = new Thickness(6, 0, 6, 0), MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var x = new Button { Style = (Style)FindResource("Ghost"), Padding = new Thickness(2), Content = new Icon { Kind = "Close", Width = 11, Height = 11 }, ToolTip = "Убрать" };
            x.Click += (_, __) => remove();
            sp.Children.Add(x);
            return new Border { Child = sp, Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x1A)), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 4, 4, 4), Margin = new Thickness(0, 0, 6, 6) };
        }

        private void OnDragEnter(object s, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) { Drop.BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)); e.Effects = DragDropEffects.Copy; }
            e.Handled = true;
        }

        private void OnDragLeave(object s, DragEventArgs e) => Drop.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x33));

        private void OnDrop(object s, DragEventArgs e)
        {
            OnDragLeave(s, e);
            e.Handled = true;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) { PhoneLink.Start(); PhoneLink.Offer(files); }
        }

        private void OnPickFiles(object s, RoutedEventArgs e)
        {
            AppHost.Window.SuppressAutoCollapse = true;
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Отправить на телефон", Multiselect = true };
                if (dlg.ShowDialog() == true) PhoneLink.Offer(dlg.FileNames);
            }
            finally { AppHost.Window.SuppressAutoCollapse = false; }
        }

        private void OnSendClipboard(object s, RoutedEventArgs e)
        {
            string text = null;
            try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); } catch { }
            if (string.IsNullOrWhiteSpace(text))
            {
                ToastBus.Show(new Toast { Kind = ToastKind.Error, Title = "В буфере нет текста", Icon = Icons.Clipboard, Important = true, Duration = TimeSpan.FromSeconds(2) });
                return;
            }
            PhoneLink.OfferText(text);
        }
    }
}
