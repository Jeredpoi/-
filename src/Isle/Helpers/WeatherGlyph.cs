using System;
using System.Windows;
using System.Windows.Media;

namespace Isle.Helpers
{
    /// <summary>
    /// Анимированный значок погоды (рисуется кодом, двигается в общем кадре приложения):
    /// солнце с вращающимися лучами, луна со звёздами, плывущие облака, дождь, ливень, гроза с молнией,
    /// град, снег, мокрый снег, туман, ветер, мороз, жара. Используется во вкладке «Погода» и в предупреждениях.
    /// </summary>
    public class WeatherGlyph : FrameworkElement
    {
        public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
            nameof(Kind), typeof(string), typeof(WeatherGlyph), new FrameworkPropertyMetadata("cloud", FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>sun, moon, partly, partly-night, cloud, rain, shower, thunder, hail, snow, sleet, fog, wind, frost, heat.</summary>
        public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }

        private double _t;
        private bool _subscribed;

        private static readonly Geometry CloudShape = Freeze(Geometry.Parse(
            "M 6,17 C 3.6,17 2,15.3 2,13.3 C 2,11.3 3.6,9.8 5.6,9.7 C 6.3,7 8.7,5 11.6,5 C 14.4,5 16.7,6.9 17.3,9.5 C 19.6,9.6 21.5,11.4 21.5,13.6 C 21.5,15.5 20,17 18,17 Z"));
        private static readonly Brush SunBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xC9, 0x4A)));
        private static readonly Brush MoonBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF4, 0xE7, 0xB8)));
        private static readonly Brush CloudLight = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0xEE, 0xF5)));
        private static readonly Brush CloudDark = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xB5)));
        private static readonly Brush Drop = Freeze(new SolidColorBrush(Color.FromRgb(0x7C, 0xC4, 0xFF)));
        private static readonly Brush Bolt = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x5A)));
        private static readonly Brush Ice = Freeze(new SolidColorBrush(Color.FromRgb(0xC8, 0xE8, 0xFF)));
        private static readonly Brush Fog = Freeze(new SolidColorBrush(Color.FromArgb(200, 0xD6, 0xDD, 0xE5)));

        private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

        public WeatherGlyph()
        {
            IsHitTestVisible = false;
            IsVisibleChanged += (_, __) => { if (IsVisible) Subscribe(); else Unsubscribe(); };
            Loaded += (_, __) => { if (IsVisible) Subscribe(); };
            Unloaded += (_, __) => Unsubscribe();
        }

        private void Subscribe() { if (_subscribed) return; _subscribed = true; FrameClock.Tick += OnFrame; }
        private void Unsubscribe() { if (!_subscribed) return; _subscribed = false; FrameClock.Tick -= OnFrame; }
        private void OnFrame(double dt) { if (FrameClock.Hidden) return; _t += dt; InvalidateVisual(); }

        /// <summary>Значок по коду погоды (как у Open-Meteo/WMO), времени суток, температуре и ветру.</summary>
        public static string KindFor(int code, bool isDay, double temp = 10, double wind = 0)
        {
            if (code is 96 or 99) return "hail";
            if (code >= 95) return "thunder";
            if (code == 67 || code == 66) return "sleet";
            if (code is >= 71 and <= 77 or 85 or 86) return "snow";
            if (code is >= 80 and <= 82 or 65) return "shower";
            if (code is >= 51 and <= 64) return "rain";
            if (code is 45 or 48) return "fog";
            if (wind >= 12) return "wind";
            if (code == 3) return "cloud";
            if (code is 1 or 2) return isDay ? "partly" : "partly-night";
            if (temp >= 30 && isDay) return "heat";
            if (temp <= -15) return "frost";
            return isDay ? "sun" : "moon";
        }

        /// <summary>Значок для предупреждения о погоде (вид явления из прогноза).</summary>
        public static string KindForAlert(string alertKind) => alertKind switch
        {
            "thunder" => "thunder", "hail" => "hail", "sleet" => "sleet", "snow" => "snow", "rain" => "shower",
            "fog" => "fog", "wind" => "wind", "frost" => "frost", "heat" => "heat", _ => "cloud"
        };

        protected override void OnRender(DrawingContext dc)
        {
            double size = Math.Min(ActualWidth, ActualHeight);
            if (size <= 0) return;
            double s = size / 24;
            dc.PushTransform(new TranslateTransform((ActualWidth - size) / 2, (ActualHeight - size) / 2));
            dc.PushTransform(new ScaleTransform(s, s));
            switch (Kind)
            {
                case "sun": DrawSun(dc, 12, 12, 1); break;
                case "heat": DrawSun(dc, 12, 10, 1.1); DrawHeat(dc); break;
                case "moon": DrawMoon(dc, 12, 12, 1); DrawStars(dc); break;
                case "partly": DrawSun(dc, 15.5, 8.5, 0.72); DrawCloud(dc, CloudLight, 1.2, 0); break;
                case "partly-night": DrawMoon(dc, 15.5, 8.5, 0.7); DrawCloud(dc, CloudLight, 1.2, 0); break;
                case "cloud": DrawCloud(dc, CloudDark, 1.6, -2.5, 0.65, 0.8); DrawCloud(dc, CloudLight, 1.4, 1); break;
                case "rain": DrawCloud(dc, CloudLight, 0.8, -2); DrawDrops(dc, 3, 1.1); break;
                case "shower": DrawCloud(dc, CloudDark, 0.8, -2); DrawDrops(dc, 5, 1.7); break;
                case "thunder": DrawCloud(dc, CloudDark, 0.6, -2); DrawDrops(dc, 3, 1.5); DrawBolt(dc); break;
                case "hail": DrawCloud(dc, CloudDark, 0.6, -2); DrawHail(dc); DrawBolt(dc); break;
                case "snow": DrawCloud(dc, CloudLight, 0.8, -2); DrawFlakes(dc, 4); break;
                case "sleet": DrawCloud(dc, CloudLight, 0.8, -2); DrawDrops(dc, 2, 1.2); DrawFlakes(dc, 2); break;
                case "fog": DrawCloud(dc, CloudLight, 0.6, -3.5, 0.8, 0.6); DrawFog(dc); break;
                case "wind": DrawWind(dc); break;
                case "frost": DrawFrost(dc); break;
                default: DrawCloud(dc, CloudLight, 1.2, 0); break;
            }
            dc.Pop(); dc.Pop();
        }

        // ---------- элементы ----------

        private void DrawSun(DrawingContext dc, double cx, double cy, double k)
        {
            double angle = _t * 25; // лучи медленно вращаются и «дышат»
            double pulse = 1 + Math.Sin(_t * 2.4) * 0.08;
            var pen = new Pen(SunBrush, 1.5 * k) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            for (int i = 0; i < 8; i++)
            {
                double a = (angle + i * 45) * Math.PI / 180;
                double r1 = 6.2 * k, r2 = (8.6 + Math.Sin(_t * 3 + i) * 0.5) * k * pulse;
                dc.DrawLine(pen, new Point(cx + Math.Cos(a) * r1, cy + Math.Sin(a) * r1), new Point(cx + Math.Cos(a) * r2, cy + Math.Sin(a) * r2));
            }
            dc.DrawEllipse(SunBrush, null, new Point(cx, cy), 4.6 * k, 4.6 * k);
        }

        private void DrawMoon(DrawingContext dc, double cx, double cy, double k)
        {
            var moon = new CombinedGeometry(GeometryCombineMode.Exclude,
                new EllipseGeometry(new Point(cx, cy), 6 * k, 6 * k),
                new EllipseGeometry(new Point(cx + 3.4 * k, cy - 2.2 * k), 5.2 * k, 5.2 * k));
            dc.PushOpacity(0.92 + Math.Sin(_t * 1.5) * 0.08);
            dc.DrawGeometry(MoonBrush, null, moon);
            dc.Pop();
        }

        private void DrawStars(DrawingContext dc)
        {
            (double x, double y, double ph)[] stars = { (19, 5, 0), (20.5, 12, 1.7), (16, 3, 3.1) };
            foreach (var (x, y, ph) in stars)
            {
                double a = 0.25 + 0.75 * Math.Max(0, Math.Sin(_t * 2.2 + ph));
                dc.PushOpacity(a);
                dc.DrawEllipse(MoonBrush, null, new Point(x, y), 0.8, 0.8);
                dc.Pop();
            }
        }

        private void DrawCloud(DrawingContext dc, Brush fill, double drift, double dy, double scale = 1, double opacity = 1)
        {
            double dx = Math.Sin(_t * 0.6) * drift; // облако медленно плывёт
            dc.PushOpacity(opacity);
            dc.PushTransform(new TranslateTransform(dx + 12 * (1 - scale), dy + 11 * (1 - scale)));
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawGeometry(fill, null, CloudShape);
            dc.Pop(); dc.Pop(); dc.Pop();
        }

        private void DrawDrops(DrawingContext dc, int count, double speed)
        {
            var pen = new Pen(Drop, 1.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            for (int i = 0; i < count; i++)
            {
                double x = 6 + i * (12.0 / Math.Max(1, count - 1));
                double ph = (_t * speed + i * 0.37) % 1;
                double y = 15.5 + ph * 7;
                dc.PushOpacity(1 - ph);
                dc.DrawLine(pen, new Point(x - ph * 0.8, y), new Point(x - 0.8 - ph * 0.8, y + 2.6));
                dc.Pop();
            }
        }

        private void DrawFlakes(DrawingContext dc, int count)
        {
            for (int i = 0; i < count; i++)
            {
                double ph = (_t * 0.45 + i * 0.29) % 1;
                double x = 7 + i * (10.0 / Math.Max(1, count - 1)) + Math.Sin(_t * 2 + i) * 1.1;
                double y = 16 + ph * 7.5;
                dc.PushOpacity(1 - ph * 0.8);
                dc.DrawEllipse(Brushes.White, null, new Point(x, y), 1.05, 1.05);
                dc.Pop();
            }
        }

        private void DrawHail(DrawingContext dc)
        {
            for (int i = 0; i < 4; i++)
            {
                double ph = (_t * 1.4 + i * 0.27) % 1;
                double bounce = ph > 0.8 ? Math.Sin((ph - 0.8) / 0.2 * Math.PI) * 1.2 : 0; // отскакивает от земли
                double y = 15.5 + Math.Min(ph, 0.8) / 0.8 * 7 - bounce;
                dc.DrawEllipse(Ice, null, new Point(6.5 + i * 3.6, y), 1.1, 1.1);
            }
        }

        private void DrawBolt(DrawingContext dc)
        {
            // молния вспыхивает раз в ~1,8 с двойной вспышкой
            double ph = _t % 1.8;
            double a = ph < 0.08 ? 1 : ph < 0.14 ? 0.2 : ph < 0.22 ? 1 : Math.Max(0, 1 - (ph - 0.22) * 4);
            if (a <= 0.02) return;
            var bolt = Geometry.Parse("M 12.8,13 L 9.6,18.4 L 12,18.4 L 10.6,23 L 15,16.8 L 12.6,16.8 L 14.2,13 Z");
            dc.PushOpacity(a);
            dc.DrawGeometry(Bolt, null, bolt);
            dc.Pop();
        }

        private void DrawFog(DrawingContext dc)
        {
            var pen = new Pen(Fog, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            for (int i = 0; i < 3; i++)
            {
                double shift = Math.Sin(_t * 0.9 + i * 1.6) * 2.2;
                double y = 15 + i * 3;
                dc.DrawLine(pen, new Point(4 + shift + (i % 2) * 2, y), new Point(19 + shift - (i % 2) * 2, y));
            }
        }

        private void DrawWind(DrawingContext dc)
        {
            var pen = new Pen(CloudLight, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            for (int i = 0; i < 3; i++)
            {
                double ph = (_t * 0.8 + i * 0.33) % 1;
                double y = 7 + i * 5;
                double x0 = 2 + ph * 6, len = 11 + (i == 1 ? 4 : 0);
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(x0, y), false, false);
                    c.LineTo(new Point(x0 + len, y), true, true);
                    c.ArcTo(new Point(x0 + len, y - 3.2), new Size(1.6, 1.6), 0, true, SweepDirection.Counterclockwise, true, true);
                }
                dc.PushOpacity(Math.Sin(ph * Math.PI));
                dc.DrawGeometry(null, pen, g);
                dc.Pop();
            }
        }

        private void DrawFrost(DrawingContext dc)
        {
            var pen = new Pen(Ice, 1.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.PushTransform(new RotateTransform(_t * 15, 12, 12)); // снежинка медленно вращается
            for (int i = 0; i < 6; i++)
            {
                double a = i * Math.PI / 3;
                var end = new Point(12 + Math.Cos(a) * 8, 12 + Math.Sin(a) * 8);
                dc.DrawLine(pen, new Point(12, 12), end);
                var mid = new Point(12 + Math.Cos(a) * 5, 12 + Math.Sin(a) * 5);
                dc.DrawLine(pen, mid, new Point(mid.X + Math.Cos(a + 0.8) * 2, mid.Y + Math.Sin(a + 0.8) * 2));
                dc.DrawLine(pen, mid, new Point(mid.X + Math.Cos(a - 0.8) * 2, mid.Y + Math.Sin(a - 0.8) * 2));
            }
            dc.Pop();
        }

        private void DrawHeat(DrawingContext dc)
        {
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(170, 0xFF, 0x9A, 0x4A)), 1.1) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            for (int i = 0; i < 3; i++)
            {
                double ph = (_t * 0.5 + i * 0.33) % 1;
                double x = 7 + i * 5, y = 22 - ph * 4;
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(x, y), false, false);
                    c.BezierTo(new Point(x + 1.4, y - 0.8), new Point(x - 1.4, y - 1.6), new Point(x, y - 2.4), true, true);
                }
                dc.PushOpacity(Math.Sin(ph * Math.PI));
                dc.DrawGeometry(null, pen, g);
                dc.Pop();
            }
        }
    }
}
