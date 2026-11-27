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

        // розовый чуть спокойнее: неоновый #FF4FA3 спорил с приглушённым фоном
        public static readonly Color Pink = Color.FromRgb(0xF2, 0x67, 0x9F);
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

            // «заполнить с обрезкой»: по ширине панели, по высоте — кадр чуть выше середины (кроны пальм и небо)
            double iw = w, ih = iw * photo.PixelHeight / photo.PixelWidth;
            if (ih < h) { ih = h; iw = ih * photo.PixelWidth / photo.PixelHeight; }
            // Размытие запекаем в картинку: без него «стекло» было лиловой плёнкой поверх резких веток, и ветки мешали
            // читать текст. Картинку берём с запасом по краям, чтобы размытие не темнело у границ
            const double blur = 30, pad = blur * 1.5;
            var img = new System.Windows.Controls.Image { Source = photo, Stretch = Stretch.Fill, Width = iw + pad * 2, Height = ih + pad * 2,
                Effect = new BlurEffect { Radius = blur, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality } };
            var canvas = new System.Windows.Controls.Canvas { Width = w, Height = h, ClipToBounds = true };
            System.Windows.Controls.Canvas.SetLeft(img, (w - iw) / 2 - pad);
            System.Windows.Controls.Canvas.SetTop(img, (h - ih) * 0.42 - pad);
            canvas.Children.Add(img);
            canvas.Measure(new Size(w, h)); canvas.Arrange(new Rect(0, 0, w, h)); canvas.UpdateLayout();
            var blurred = new RenderTargetBitmap(pw, ph, 96 * s, 96 * s, PixelFormats.Pbgra32);
            blurred.Render(canvas);
            blurred.Freeze();

            var host = new DrawingVisual();
            using (var dc = host.RenderOpen())
            {
                dc.DrawImage(blurred, new Rect(0, 0, w, h));
                // вуаль — нейтральная тень (не лиловая), у низа тёплая полоса горизонта: это и есть закат
                var veil = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                veil.GradientStops.Add(new GradientStop(Color.FromArgb(0xB0, 0x0D, 0x0A, 0x14), 0));
                veil.GradientStops.Add(new GradientStop(Color.FromArgb(0x80, 0x0D, 0x0A, 0x14), 0.45));
                veil.GradientStops.Add(new GradientStop(Color.FromArgb(0x90, 0x2A, 0x14, 0x1A), 0.85));
                veil.GradientStops.Add(new GradientStop(Color.FromArgb(0xC0, 0x0D, 0x0A, 0x14), 1));
                dc.DrawRectangle(veil, null, new Rect(0, 0, w, h));
                var warm = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                warm.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0x8A, 0x5B), 0.6));
                warm.GradientStops.Add(new GradientStop(Color.FromArgb(0x38, 0xFF, 0x8A, 0x5B), 0.88));
                warm.GradientStops.Add(new GradientStop(Color.FromArgb(0x10, 0xFF, 0x8A, 0x5B), 1));
                dc.DrawRectangle(warm, null, new Rect(0, 0, w, h));
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
            // нейтральное матовое стекло поверх размытого фона: светлый налёт сверху, к низу — лёгкая тень
            // (раньше — лиловая плёнка того же оттенка, что фон: карточки сливались)
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF), 0));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF), 0.3));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0x33, 0x00, 0x00, 0x00), 1));
            g.Freeze();
            return g;
        }

        public static Brush GlassBorder()
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            // тонкая кромка вместо яркого светящегося канта
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF), 0));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF), 1));
            g.Freeze();
            return g;
        }
    }
}
