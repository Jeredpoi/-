using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Isle.Native;
using Isle.Overlays;
using Isle.Services;

namespace Isle
{
    /// <summary>Действия экранных инструментов с ответом в виде всплывашки.</summary>
    public static class Tools
    {
        private static readonly Color Green = Color.FromRgb(0x4A, 0xDE, 0x80);

        private static async Task<object> Capture(CaptureMode mode)
        {
            AppHost.Window.Collapse();
            AppHost.Window.Visibility = Visibility.Hidden;
            await Task.Delay(180); // даём чёлке спрятаться, чтобы не попала в снимок
            try { return await CaptureOverlay.Run(mode); }
            finally { AppHost.Window.Visibility = Visibility.Visible; }
        }

        public static async Task RegionShot()
        {
            if (await Capture(CaptureMode.Region) is BitmapSource bmp) Done(bmp);
        }

        public static async Task FullShot()
        {
            AppHost.Window.Collapse();
            AppHost.Window.Visibility = Visibility.Hidden;
            await Task.Delay(220);
            BitmapSource bmp;
            try { bmp = ScreenTools.CaptureAll(); }
            finally { AppHost.Window.Visibility = Visibility.Visible; }
            Done(bmp);
        }

        private static void Done(BitmapSource bmp)
        {
            ScreenTools.CopyImage(bmp);
            string path = null;
            try { path = ScreenTools.SavePng(bmp); } catch { }
            ToastBus.Show(new Toast
            {
                Kind = ToastKind.Success, Title = "Снимок скопирован",
                Subtitle = path != null ? "Сохранён в Изображения\\Isle" : "В буфере обмена",
                Image = bmp, Icon = Icons.Crop, Important = true, Duration = TimeSpan.FromSeconds(3)
            });
        }

        public static async Task OcrShot()
        {
            if (await Capture(CaptureMode.Ocr) is not BitmapSource bmp) return;
            string text = null;
            try { text = await ScreenTools.OcrAsync(bmp); }
            catch (Exception ex) { App.Log("ocr: " + ex); }
            if (string.IsNullOrWhiteSpace(text))
            {
                ToastBus.Show(new Toast
                {
                    Title = "Текст не найден", Subtitle = "Проверь, что в Windows установлен язык с распознаванием текста",
                    Icon = Icons.Scan, Important = true, Duration = TimeSpan.FromSeconds(4)
                });
                return;
            }
            ClipboardService.SetText(text);
            ToastBus.Show(new Toast
            {
                Kind = ToastKind.Success, Title = "Текст распознан · " + text.Length + " симв.",
                Subtitle = text.Replace("\r", " ").Replace("\n", " "), Icon = Icons.Check, Accent = Green, Important = true,
                Duration = TimeSpan.FromSeconds(3.5)
            });
        }

        public static async Task QrShot()
        {
            if (await Capture(CaptureMode.Qr) is not BitmapSource bmp) return;
            var text = ScreenTools.ReadQr(bmp);
            if (string.IsNullOrEmpty(text))
            {
                ToastBus.Show(new Toast { Title = "QR-код не найден", Subtitle = "Выдели код целиком, с небольшим полем", Icon = Icons.Qr, Important = true, Duration = TimeSpan.FromSeconds(3) });
                return;
            }
            ClipboardService.SetText(text);
            bool url = Uri.TryCreate(text, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https");
            ToastBus.Show(new Toast
            {
                Kind = ToastKind.Success, Title = url ? "Ссылка из QR скопирована" : "QR прочитан и скопирован",
                Subtitle = text, Icon = Icons.Qr, Accent = Green, Important = true, Duration = TimeSpan.FromSeconds(4)
            });
        }

        public static async Task Pipette()
        {
            if (await Capture(CaptureMode.Pipette) is not Color c) return;
            var hex = CaptureOverlay.Hex(c);
            ClipboardService.SetText(hex);
            ToastBus.Show(new Toast
            {
                Kind = ToastKind.Success, Title = hex + " скопирован", Subtitle = "rgb(" + c.R + ", " + c.G + ", " + c.B + ")",
                Icon = Icons.Pipette, Accent = c, Important = true, Duration = TimeSpan.FromSeconds(3)
            });
        }

        /// <summary>Убирает форматирование из буфера (оставляет только текст).</summary>
        public static void CleanText()
        {
            string text = null;
            try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); } catch { }
            if (string.IsNullOrEmpty(text))
            {
                ToastBus.Show(new Toast { Title = "В буфере нет текста", Icon = Icons.Eraser, Important = true });
                return;
            }
            ClipboardService.SetText(text);
            ToastBus.Show(new Toast { Kind = ToastKind.Success, Title = "Форматирование убрано", Subtitle = text.Length + " симв. — вставляй", Icon = Icons.Eraser, Accent = Green, Important = true });
        }

        /// <summary>Запись экрана через встроенные «Ножницы» Windows 11 (Win+Shift+R).</summary>
        public static void Record()
        {
            AppHost.Window.Collapse();
            Win32.SendKeyCombo(Win32.VK_LWIN, 0x10 /*Shift*/, 0x52 /*R*/);
        }
    }
}
