using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Isle.Services;

namespace Isle.Helpers
{
    /// <summary>
    /// Тема «Закат»: неоновый вечер у океана. Фон раскрытой панели — закатное небо, ретро-солнце в полоску
    /// и силуэты пальм, размытые, как за матовым стеклом. Рисуется один раз в картинку, дальше ничего не стоит.
    /// Своя тема по настроению, без чужих логотипов, шрифтов и арта.
    /// </summary>
    public static class SunsetTheme
    {
        public static bool On => (AppSettings.Current.Theme ?? "dark") == "sunset";

        public static readonly Color Pink = Color.FromRgb(0xFF, 0x4F, 0xA3);
        public static readonly Color Teal = Color.FromRgb(0x2D, 0xE2, 0xE6);
        public static readonly Color Orange = Color.FromRgb(0xFF, 0x8A, 0x5B);

        private static BitmapSource _back;

        /// <summary>Фон панели (готовая размытая картинка), кэшируется.</summary>
        public static BitmapSource Background(double w, double h)
        {
            if (_back != null) return _back;
            const double s = 2; // рисуем в 2×, чтобы размытие было мягким и на 4K
            int pw = (int)(w * s), ph = (int)(h * s);

            var scene = new DrawingVisual();
            using (var dc = scene.RenderOpen())
            {
                // небо: ночная синева сверху → лиловый → розово-оранжевое зарево у горизонта
                var sky = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                sky.GradientStops.Add(new GradientStop(Color.FromRgb(0x14, 0x0C, 0x2E), 0));
                sky.GradientStops.Add(new GradientStop(Color.FromRgb(0x3B, 0x14, 0x52), 0.45));
                sky.GradientStops.Add(new GradientStop(Color.FromRgb(0xB0, 0x2E, 0x72), 0.72));
                sky.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0x7A, 0x59), 0.86));
                sky.GradientStops.Add(new GradientStop(Color.FromRgb(0x2A, 0x10, 0x3E), 1));
                dc.DrawRectangle(sky, null, new Rect(0, 0, pw, ph));

                // ретро-солнце в полоску у горизонта
                double sunR = ph * 0.30, sx = pw * 0.5, sy = ph * 0.80;
                var sun = new LinearGradientBrush(Color.FromRgb(0xFF, 0xD2, 0x6E), Color.FromRgb(0xFF, 0x3F, 0x8E), 90);
                var sunGeo = new EllipseGeometry(new Point(sx, sy), sunR, sunR);
                var stripes = new GeometryGroup();
                for (int i = 0; i < 6; i++)
                {
                    double y = sy + sunR * (0.05 + i * 0.16), hgt = sunR * (0.03 + i * 0.018);
                    stripes.Children.Add(new RectangleGeometry(new Rect(sx - sunR, y, sunR * 2, hgt)));
                }
                dc.DrawGeometry(sun, null, Geometry.Combine(sunGeo, stripes, GeometryCombineMode.Exclude, null));
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2A, 0x10, 0x3E)), null, new Rect(0, ph * 0.90, pw, ph * 0.10)); // море

                // пальмы по краям — тёмные силуэты
                var palm = new SolidColorBrush(Color.FromRgb(0x1A, 0x08, 0x26));
                Palm(dc, palm, pw * 0.07, ph * 1.02, ph * 0.95, 0.22);
                Palm(dc, palm, pw * 0.19, ph * 1.04, ph * 0.70, -0.10);
                Palm(dc, palm, pw * 0.83, ph * 1.03, ph * 0.88, -0.24);
                Palm(dc, palm, pw * 0.95, ph * 1.05, ph * 0.62, 0.12);
                Palm(dc, palm, pw * 0.70, ph * 1.06, ph * 0.45, 0.08);
            }

            // «матовое стекло»: сильное размытие и тёмная вуаль поверх — чтобы текст и карточки читались
            var host = new DrawingVisual();
            var blurred = new ContainerVisual { Effect = new BlurEffect { Radius = 46 * s / 2, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality } };
            blurred.Children.Add(scene);
            var bmp = new RenderTargetBitmap(pw, ph, 96 * s, 96 * s, PixelFormats.Pbgra32);
            var layer = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
            layer.Render(blurred);
            using (var dc = host.RenderOpen())
            {
                dc.DrawImage(layer, new Rect(0, 0, w, h));
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(120, 0x0A, 0x05, 0x14)), null, new Rect(0, 0, w, h));
            }
            bmp.Render(host);
            bmp.Freeze();
            return _back = bmp;
        }

        /// <summary>Силуэт пальмы: изогнутый ствол и веер листьев на макушке.</summary>
        private static void Palm(DrawingContext dc, Brush b, double x, double baseY, double height, double lean)
        {
            double topX = x + lean * height, topY = baseY - height;
            double w0 = height * 0.045, w1 = height * 0.02;
            var trunk = new StreamGeometry();
            using (var g = trunk.Open())
            {
                g.BeginFigure(new Point(x - w0, baseY), true, true);
                g.QuadraticBezierTo(new Point(x + lean * height * 0.2 - w0, baseY - height * 0.6), new Point(topX - w1, topY), true, true);
                g.LineTo(new Point(topX + w1, topY), true, true);
                g.QuadraticBezierTo(new Point(x + lean * height * 0.2 + w0, baseY - height * 0.6), new Point(x + w0, baseY), true, true);
            }
            dc.DrawGeometry(b, null, trunk);
            // листья: дуги во все стороны, кончики свисают вниз
            double len = height * 0.42;
            foreach (var deg in new[] { -170.0, -140, -110, -70, -40, -10, 15, -90 })
            {
                double a = deg * Math.PI / 180;
                var tip = new Point(topX + Math.Cos(a) * len, topY + Math.Sin(a) * len * 0.55 + len * 0.35);
                var mid = new Point(topX + Math.Cos(a) * len * 0.55, topY + Math.Sin(a) * len * 0.55 - len * 0.12);
                double t = len * 0.09;
                var leaf = new StreamGeometry();
                using (var g = leaf.Open())
                {
                    g.BeginFigure(new Point(topX, topY), true, true);
                    g.QuadraticBezierTo(new Point(mid.X, mid.Y - t), tip, true, true);
                    g.QuadraticBezierTo(new Point(mid.X, mid.Y + t), new Point(topX, topY + t * 0.4), true, true);
                }
                dc.DrawGeometry(b, null, leaf);
            }
        }
    }
}
