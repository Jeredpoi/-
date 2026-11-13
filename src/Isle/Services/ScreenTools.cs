using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Isle.Native;

namespace Isle.Services
{
    /// <summary>Скриншоты, распознавание текста (Windows OCR), QR-коды, пипетка.</summary>
    public static class ScreenTools
    {
        public static string ShotsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Isle");

        public static Int32Rect VirtualScreen() => new(
            Win32.GetSystemMetrics(76), Win32.GetSystemMetrics(77),   // SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN
            Win32.GetSystemMetrics(78), Win32.GetSystemMetrics(79));  // SM_CXVIRTUALSCREEN, SM_CYVIRTUALSCREEN

        public static BitmapSource Capture(Int32Rect r)
        {
            IntPtr screen = GetDC(IntPtr.Zero);
            IntPtr mem = CreateCompatibleDC(screen);
            IntPtr bmp = CreateCompatibleBitmap(screen, r.Width, r.Height);
            IntPtr old = SelectObject(mem, bmp);
            try
            {
                BitBlt(mem, 0, 0, r.Width, r.Height, screen, r.X, r.Y, 0x00CC0020 | 0x40000000);
                var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                return Opaque(src);
            }
            finally
            {
                SelectObject(mem, old);
                Win32.DeleteObject(bmp);
                DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        public static BitmapSource CaptureAll() => Capture(VirtualScreen());

        /// <summary>
        /// Снимок экрана без «дыр». BitBlt при включённом DWM не заполняет альфа-канал: у окон на Chromium/Electron
        /// (Claude, браузеры, Discord) там нули. WPF рисовал такие места прозрачными — сквозь них было видно чёрный фон
        /// слоя выделения (чёрные прямоугольники, «пелена», чёрные квадраты при рисовании), а PNG сохранялся с дырами.
        /// Делаем каждый пиксель непрозрачным и отдаём Bgr32 — формат без альфы.
        /// </summary>
        public static BitmapSource Opaque(BitmapSource src)
        {
            int w = src.PixelWidth, h = src.PixelHeight;
            var px = new byte[w * h * 4];
            if (src.Format == PixelFormats.Bgra32 || src.Format == PixelFormats.Bgr32 || src.Format == PixelFormats.Pbgra32) src.CopyPixels(px, w * 4, 0);
            else new FormatConvertedBitmap(src, PixelFormats.Bgr32, null, 0).CopyPixels(px, w * 4, 0);
            for (int i = 3; i < px.Length; i += 4) px[i] = 255;
            var opaque = BitmapSource.Create(w, h, src.DpiX > 0 ? src.DpiX : 96, src.DpiY > 0 ? src.DpiY : 96, PixelFormats.Bgr32, null, px, w * 4);
            opaque.Freeze();
            return opaque;
        }

        /// <summary>Путь для нового снимка в «Изображения\\Isle» (папка создаётся).</summary>
        public static string NewShotPath()
        {
            Directory.CreateDirectory(ShotsDir);
            var path = Path.Combine(ShotsDir, "Снимок " + DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss") + ".png");
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(ShotsDir, "Снимок " + DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss") + " (" + i + ").png");
            return path;
        }

        public static string SavePng(BitmapSource bmp) => SavePng(bmp, NewShotPath());

        public static string SavePng(BitmapSource bmp, string path)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(path);
            enc.Save(fs);
            return path;
        }

        public static void CopyImage(BitmapSource bmp)
        {
            for (int i = 0; i < 5; i++)
            {
                try { Clipboard.SetImage(bmp); return; }
                catch { System.Threading.Thread.Sleep(50); }
            }
        }

        public static byte[] Bgra(BitmapSource bmp, out int w, out int h)
        {
            // снимок экрана уже в Bgr32/Bgra32 — копируем напрямую, без медленного преобразования (важно на 4K)
            if (bmp.Format == PixelFormats.Bgr32 || bmp.Format == PixelFormats.Bgra32 || bmp.Format == PixelFormats.Pbgra32)
            {
                w = bmp.PixelWidth; h = bmp.PixelHeight;
                var raw = new byte[w * h * 4];
                bmp.CopyPixels(raw, w * 4, 0);
                return raw;
            }
            var conv = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            w = conv.PixelWidth; h = conv.PixelHeight;
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);
            return px;
        }

        public static string ReadQr(BitmapSource bmp)
        {
            try
            {
                var px = Bgra(bmp, out int w, out int h);
                var src = new ZXing.RGBLuminanceSource(px, w, h, ZXing.RGBLuminanceSource.BitmapFormat.BGRA32);
                var reader = new ZXing.BarcodeReaderGeneric { AutoRotate = true };
                reader.Options.TryHarder = true;
                reader.Options.TryInverted = true;
                var res = reader.Decode(src);
                return res?.Text;
            }
            catch { return null; }
        }

        public static async Task<string> OcrAsync(BitmapSource bmp)
        {
            var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine == null) return null;

            // Маленькие фрагменты OCR читает плохо — увеличиваем.
            BitmapSource input = bmp;
            if (bmp.PixelHeight < 60 || bmp.PixelWidth < 200)
            {
                double k = Math.Min(4, Math.Max(60.0 / bmp.PixelHeight, 200.0 / bmp.PixelWidth));
                input = new TransformedBitmap(bmp, new ScaleTransform(k, k));
            }
            var px = Bgra(input, out int w, out int h);
            var sb = new Windows.Graphics.Imaging.SoftwareBitmap(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, w, h, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
            sb.CopyFromBuffer(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(px));
            var result = await engine.RecognizeAsync(sb);
            return string.Join(Environment.NewLine, result.Lines.Select(l => l.Text)).Trim();
        }

        public static Color PixelAt(BitmapSource bmp, int x, int y)
        {
            x = Math.Clamp(x, 0, bmp.PixelWidth - 1);
            y = Math.Clamp(y, 0, bmp.PixelHeight - 1);
            var px = new byte[4];
            var conv = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            conv.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
            return Color.FromRgb(px[2], px[1], px[0]);
        }

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    }
}
