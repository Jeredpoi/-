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

        /// <summary>Фон панели (готовая картинка), кэшируется.</summary>
        public static BitmapSource Background(double w, double h)
        {
            if (_back != null) return _back;
            const double s = 2; // рисуем в 2×: чётко и на 4K
            int pw = (int)(w * s), ph = (int)(h * s);
            double horizon = ph * 0.70;
            var rnd = new Random(7);

            var scene = new DrawingVisual();
            using (var dc = scene.RenderOpen())
            {
                // небо: ночная синева → лиловый → малиновый → оранжевое зарево у горизонта
                var sky = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                sky.GradientStops.Add(new GradientStop(Color.FromRgb(0x16, 0x0A, 0x3A), 0));
                sky.GradientStops.Add(new GradientStop(Color.FromRgb(0x4B, 0x16, 0x6E), 0.38));
                sky.GradientStops.Add(new GradientStop(Color.FromRgb(0xC2, 0x2E, 0x76), 0.68));
                sky.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0x7A, 0x4D), 0.88));
                sky.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0xC2, 0x6B), 1));
                dc.DrawRectangle(sky, null, new Rect(0, 0, pw, horizon));

                // редкие звёзды в верхней части
                for (int i = 0; i < 70; i++)
                {
                    double x = rnd.NextDouble() * pw, y = rnd.NextDouble() * horizon * 0.35, r = 0.6 + rnd.NextDouble() * 1.4;
                    dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(90 + rnd.Next(140)), 255, 240, 250)), null, new Point(x, y), r, r);
                }

                // мягкие вытянутые облака, подсвеченные закатом снизу
                foreach (var (cx, cy, rx, ry, a) in new[] { (0.22, 0.30, 0.30, 0.035, 120), (0.70, 0.22, 0.26, 0.03, 110), (0.48, 0.42, 0.40, 0.03, 140), (0.88, 0.47, 0.22, 0.025, 150), (0.12, 0.52, 0.25, 0.022, 150) })
                {
                    var cloud = new RadialGradientBrush(Color.FromArgb((byte)a, 0xFF, 0x9E, 0xB8), Color.FromArgb(0, 0xFF, 0x9E, 0xB8));
                    dc.DrawEllipse(cloud, null, new Point(cx * pw, cy * ph), rx * pw, ry * ph);
                }

                // солнце: свечение, диск с градиентом и прорези-полосы снизу
                double sunR = ph * 0.22, sx = pw * 0.60, sy = horizon - sunR * 0.35;
                dc.DrawEllipse(new RadialGradientBrush(Color.FromArgb(150, 0xFF, 0x8A, 0x5B), Color.FromArgb(0, 0xFF, 0x4F, 0xA3)), null, new Point(sx, sy), sunR * 2.4, sunR * 2.0);
                var sun = new LinearGradientBrush(Color.FromRgb(0xFF, 0xE0, 0x7A), Color.FromRgb(0xFF, 0x3F, 0x8E), 90);
                var stripes = new GeometryGroup();
                for (int i = 0; i < 7; i++)
                {
                    double y = sy + sunR * (0.02 + i * 0.14), hgt = sunR * (0.025 + i * 0.016);
                    stripes.Children.Add(new RectangleGeometry(new Rect(sx - sunR, y, sunR * 2, hgt)));
                }
                var disc = Geometry.Combine(new EllipseGeometry(new Point(sx, sy), sunR, sunR), stripes, GeometryCombineMode.Exclude, null);
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, pw, horizon)));
                dc.DrawGeometry(sun, null, disc);
                dc.Pop();

                // силуэт города на горизонте с огоньками окон
                var city = new SolidColorBrush(Color.FromRgb(0x3A, 0x12, 0x46));
                double bx = pw * 0.12;
                while (bx < pw * 0.92)
                {
                    double bw = pw * (0.012 + rnd.NextDouble() * 0.022), bh = ph * (0.02 + rnd.NextDouble() * (bx > pw * 0.3 && bx < pw * 0.5 ? 0.11 : 0.05));
                    dc.DrawRectangle(city, null, new Rect(bx, horizon - bh, bw, bh));
                    for (int k = 0; k < (int)(bh / 9); k++)
                        if (rnd.NextDouble() < 0.35)
                            dc.DrawRectangle(new SolidColorBrush(rnd.NextDouble() < 0.5 ? Color.FromArgb(200, 0xFF, 0xD2, 0x8A) : Color.FromArgb(200, 0xFF, 0x7E, 0xC0)), null,
                                new Rect(bx + 2 + rnd.NextDouble() * (bw - 5), horizon - bh + 4 + k * 9, 2.2, 2.2));
                    bx += bw + pw * rnd.NextDouble() * 0.01;
                }

                // море: темнее книзу, дорожка отражённого солнца из полос
                var sea = new LinearGradientBrush(Color.FromRgb(0x5A, 0x1C, 0x5E), Color.FromRgb(0x12, 0x06, 0x22), 90);
                dc.DrawRectangle(sea, null, new Rect(0, horizon, pw, ph - horizon));
                for (int i = 0; i < 16; i++)
                {
                    double y = horizon + 4 + i * (ph - horizon) / 17, half = sunR * (1.0 - i * 0.045) * (0.6 + rnd.NextDouble() * 0.5);
                    byte al = (byte)(220 - i * 11);
                    var glint = new LinearGradientBrush(Color.FromArgb(0, 0xFF, 0x9A, 0x5B), Color.FromArgb(0, 0xFF, 0x4F, 0xA3), 0);
                    glint.GradientStops.Insert(1, new GradientStop(Color.FromArgb(al, 0xFF, 0xB0, 0x6B), 0.5));
                    dc.DrawRectangle(glint, null, new Rect(sx - half, y, half * 2, 2.5 + i * 0.25));
                }

                // пальмы: дальние поменьше и светлее, ближние крупные и тёмные
                var far = new SolidColorBrush(Color.FromRgb(0x2C, 0x0E, 0x3C));
                var near = new SolidColorBrush(Color.FromRgb(0x10, 0x04, 0x18));
                Palm(dc, far, pw * 0.30, horizon + ph * 0.03, ph * 0.42, -0.10, rnd);
                Palm(dc, far, pw * 0.78, horizon + ph * 0.04, ph * 0.38, 0.14, rnd);
                Palm(dc, near, pw * 0.05, ph * 1.04, ph * 0.98, 0.26, rnd);
                Palm(dc, near, pw * 0.17, ph * 1.06, ph * 0.74, -0.06, rnd);
                Palm(dc, near, pw * 0.90, ph * 1.04, ph * 0.92, -0.24, rnd);

                // виньетка по краям
                var vig = new RadialGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(150, 0x08, 0x02, 0x10)) { RadiusX = 0.75, RadiusY = 0.85 };
                dc.DrawRectangle(vig, null, new Rect(0, 0, pw, ph));
            }

            // лёгкое смягчение (почти чётко) и вуаль, чтобы стеклянные карточки и текст читались
            var soft = new ContainerVisual { Effect = new BlurEffect { Radius = 3, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality } };
            soft.Children.Add(scene);
            var layer = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
            layer.Render(soft);
            var host = new DrawingVisual();
            using (var dc = host.RenderOpen())
            {
                dc.DrawImage(layer, new Rect(0, 0, w, h));
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(55, 0x0A, 0x04, 0x16)), null, new Rect(0, 0, w, h));
            }
            var bmp = new RenderTargetBitmap(pw, ph, 96 * s, 96 * s, PixelFormats.Pbgra32);
            bmp.Render(host);
            bmp.Freeze();
            return _back = bmp;
        }

        /// <summary>Стеклянная карточка: светлый налёт сверху (блик), почти прозрачная к низу.</summary>
        public static Brush GlassCard()
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            g.GradientStops.Add(new GradientStop(Color.FromArgb(70, 0xFF, 0xFF, 0xFF), 0));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(34, 0xFF, 0xE6, 0xF4), 0.18));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(60, 0x1A, 0x08, 0x2A), 1));
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

        /// <summary>Пальма: изогнутый ствол с кольцами и веер резных листьев, свисающих вниз.</summary>
        private static void Palm(DrawingContext dc, Brush b, double x, double baseY, double height, double lean, Random rnd)
        {
            double topX = x + lean * height, topY = baseY - height;
            double w0 = height * 0.035, w1 = height * 0.016;
            var ctrl = new Point(x + lean * height * 0.15, baseY - height * 0.55);
            var trunk = new StreamGeometry();
            using (var g = trunk.Open())
            {
                g.BeginFigure(new Point(x - w0, baseY), true, true);
                g.QuadraticBezierTo(new Point(ctrl.X - w0, ctrl.Y), new Point(topX - w1, topY), true, true);
                g.LineTo(new Point(topX + w1, topY), true, true);
                g.QuadraticBezierTo(new Point(ctrl.X + w0, ctrl.Y), new Point(x + w0, baseY), true, true);
            }
            dc.DrawGeometry(b, null, trunk);
            // листья: каждый — изогнутый стебель с «перьями» по обе стороны
            double len = height * 0.40;
            foreach (var deg in new[] { -178.0, -150, -122, -98, -75, -52, -25, 0, 18 })
            {
                double a = (deg + rnd.NextDouble() * 8 - 4) * Math.PI / 180;
                double l = len * (0.82 + rnd.NextDouble() * 0.3);
                Point P(double t)
                {
                    // стебель уходит вверх-в-сторону и свисает к концу
                    double px = topX + Math.Cos(a) * l * t;
                    double py = topY + Math.Sin(a) * l * t * 0.5 - l * 0.22 * Math.Sin(t * Math.PI * 0.9) + l * 0.55 * t * t;
                    return new Point(px, py);
                }
                var leaf = new StreamGeometry();
                using (var g = leaf.Open())
                {
                    const int n = 14;
                    var upper = new System.Collections.Generic.List<Point>();
                    var lower = new System.Collections.Generic.List<Point>();
                    for (int i = 0; i <= n; i++)
                    {
                        double t = i / (double)n;
                        var p = P(t); var q = P(Math.Min(1, t + 0.02));
                        double dx = q.X - p.X, dy = q.Y - p.Y, d = Math.Max(0.001, Math.Sqrt(dx * dx + dy * dy));
                        double nx = -dy / d, ny = dx / d;
                        double width = l * 0.11 * Math.Sin(Math.PI * Math.Min(1, t * 1.1)) * (i % 2 == 0 ? 1 : 0.45); // зубчики
                        upper.Add(new Point(p.X + nx * width, p.Y + ny * width));
                        lower.Add(new Point(p.X - nx * width * 0.8, p.Y - ny * width * 0.8 + width * 0.6));
                    }
                    g.BeginFigure(new Point(topX, topY), true, true);
                    foreach (var pt in upper) g.LineTo(pt, true, false);
                    for (int i = lower.Count - 1; i >= 0; i--) g.LineTo(lower[i], true, false);
                }
                dc.DrawGeometry(b, null, leaf);
            }
            dc.DrawEllipse(b, null, new Point(topX, topY + height * 0.01), height * 0.03, height * 0.025); // «кочан» у основания листьев
        }
    }
}
