using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Isle.Services;

namespace Isle.Helpers
{
    /// <summary>
    /// Тема «Закат»: неоновый вечер у океана. Фон раскрытой панели — фотография пальм на фиолетовом закатном небе
    /// (свободная лицензия Pexels), стеклянные карточки, неоновый акцент. Без чужих логотипов, шрифтов и арта.
    /// </summary>
    public static class SunsetTheme
    {
        public static bool On => (AppSettings.Current.Theme ?? "dark") == "sunset";

        public static readonly Color Pink = Color.FromRgb(0xFF, 0x4F, 0xA3);
        public static readonly Color Teal = Color.FromRgb(0x2D, 0xE2, 0xE6);
        public static readonly Color Orange = Color.FromRgb(0xFF, 0x8A, 0x5B);

        private static BitmapSource _back;

        /// <summary>
        /// Фон панели: фотография «Palm Trees Over Purple Sky at Sunset» (Alexey Demidov, Pexels, лицензия Pexels —
        /// свободное использование). Кадрируется под панель, сверху лёгкая вуаль, чтобы текст и стеклянные карточки читались.
        /// </summary>
        public static BitmapSource Background(double w, double h)
        {
            if (_back != null) return _back;
            const double s = 2;
            int pw = (int)(w * s), ph = (int)(h * s);
            var photo = new BitmapImage();
            photo.BeginInit();
            photo.CacheOption = BitmapCacheOption.OnLoad;
            photo.UriSource = new Uri("pack://application:,,,/Assets/sunset-palms.jpg");
            photo.DecodePixelWidth = pw;
            photo.EndInit();
            photo.Freeze();

            var host = new DrawingVisual();
            using (var dc = host.RenderOpen())
            {
                // «заполнить с обрезкой»: по ширине панели, по высоте — кадр чуть выше середины (кроны пальм и небо)
                double iw = w, ih = iw * photo.PixelHeight / photo.PixelWidth;
                if (ih < h) { ih = h; iw = ih * photo.PixelWidth / photo.PixelHeight; }
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
                dc.DrawImage(photo, new Rect((w - iw) / 2, (h - ih) * 0.42, iw, ih));
                // вуаль: сверху (вкладки) и снизу чуть темнее, по центру лёгкая
                var veil = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                veil.GradientStops.Add(new GradientStop(Color.FromArgb(110, 0x0A, 0x04, 0x18), 0));
                veil.GradientStops.Add(new GradientStop(Color.FromArgb(40, 0x0A, 0x04, 0x18), 0.45));
                veil.GradientStops.Add(new GradientStop(Color.FromArgb(120, 0x0A, 0x04, 0x18), 1));
                dc.DrawRectangle(veil, null, new Rect(0, 0, w, h));
                dc.Pop();
            }
            var bmp = new RenderTargetBitmap(pw, ph, 96 * s, 96 * s, PixelFormats.Pbgra32);
            bmp.Render(host);
            bmp.Freeze();
            return _back = bmp;
        }

        /// <summary>Стеклянная карточка: светлый налёт сверху (блик), почти прозрачная к низу.</summary>
        public static Brush GlassCard()
        {
            // тёмное матовое стекло: сверху светлый блик, ниже — плотная полупрозрачная тёмно-лиловая заливка,
            // чтобы текст читался поверх ветвей пальм, а фон всё равно просвечивал
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            g.GradientStops.Add(new GradientStop(Color.FromArgb(150, 0x4A, 0x36, 0x66), 0));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(165, 0x1E, 0x12, 0x30), 0.14));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(185, 0x12, 0x09, 0x1E), 1));
            g.Freeze();
            return g;
        }

        public static Brush GlassBorder()
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            g.GradientStops.Add(new GradientStop(Color.FromArgb(150, 0xFF, 0xFF, 0xFF), 0));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(40, 0xFF, 0xC8, 0xE6), 1));
            g.Freeze();
            return g;
        }
    }
}
