using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Isle.Native;

namespace Isle.Helpers
{
    /// <summary>
    /// «Жидкий» нижний край острова (как феррожидкость):
    /// — под музыку показывает спектр: басы слева, высокие справа, кончики всплесков — цвета обложки;
    /// — тянется к курсору, когда тот рядом;
    /// — провисает, когда пользователь отошёл;
    /// — роняет каплю, когда что-то получилось, и капает в дождь;
    /// — «ощетинивается» дрожащими шипами, пока звенит будильник/таймер.
    /// Рисуется под островом, клики пропускает.
    /// </summary>
    public class LiquidEdge : FrameworkElement
    {
        private const int Samples = 72;
        private readonly float[] _spec = new float[Services.SpectrumService.BandCount];
        private readonly double[] _depth = new double[Samples];
        /// <summary>Точек на каждом скруглённом углу: край начинается прямо со скругления острова и продолжает его.</summary>
        private const int CornerSamples = 12;
        private readonly double[] _cornerL = new double[CornerSamples], _cornerR = new double[CornerSamples];
        private double _bulgeX, _bulgeAmp, _sag;
        private readonly List<Drop> _drops = new();
        private readonly Random _rnd = new();
        private DateTime _nextDrip = DateTime.Now.AddSeconds(5);

        private class Drop { public double X, Y, Vy, R, Life; }

        public Brush Fill { get; set; } = Brushes.Black;
        /// <summary>
        /// Подсветка плашки (цвет и сила 0…1): низ острова под ней тонирован этим цветом, и вытянутый край
        /// должен продолжать его — иначе под зелёной плашкой «капля» была чёрной с серой обводкой.
        /// </summary>
        public Func<(Color color, double strength)?> Tint { get; set; }
        public Color SpikeColor { get; set; } = Color.FromRgb(0xEC, 0xEC, 0xF1);
        public double CornerRadius { get; set; } = 18;
        /// <summary>Поставщик спектра (null — музыки нет).</summary>
        public Func<float[]> Spectrum { get; set; }
        public bool Raining { get; set; }
        public bool Away { get; set; }
        public bool Active { get; set; } = true;
        /// <summary>Звенит будильник: край выпускает острые дрожащие шипы с красным свечением.</summary>
        public bool Bristle { get; set; }
        private double _bristle;
        private static readonly Color AlarmGlow = Color.FromRgb(0xFF, 0x5A, 0x5A);

        public LiquidEdge()
        {
            IsHitTestVisible = false;
            Height = 46;
            FrameClock.Tick += _ => { if (!FrameClock.Hidden) Step(); };
        }

        public void DropBead()
        {
            if (ActualWidth <= 0) return;
            _drops.Add(new Drop { X = ActualWidth / 2, Y = 2, Vy = 0.4, R = 3.6, Life = 1 });
        }

        private void Step()
        {
            if (!IsVisible) return;
            double w = ActualWidth;
            if (w <= 0) return;

            // спектр
            var spec = Active ? Spectrum?.Invoke() : null;
            for (int i = 0; i < _spec.Length; i++)
            {
                float target = spec != null ? spec[i] : 0;
                // всплеск — быстро, спад — мягче: спектр приходит реже кадров монитора (на 144–240 Гц),
                // и при одинаково резком спуске край опускался ступеньками
                _spec[i] += (target - _spec[i]) * (float)FrameClock.Lerp(target > _spec[i] ? 0.4 : 0.16);
            }

            // курсор рядом → край тянется к нему
            double targetAmp = 0, targetX = _bulgeX;
            if (Active && Win32.GetCursorPos(out var p))
            {
                try
                {
                    var local = PointFromScreen(new Point(p.X, p.Y));
                    double dy = local.Y, dxOut = local.X < 0 ? -local.X : local.X > w ? local.X - w : 0;
                    if (dy > -6 && dy < 140 && dxOut < 60)
                    {
                        // сила тянется от нуля на границах зоны: раньше на входе сбоку сразу было ~7 из 13 — капля «выпрыгивала»
                        double fd = Smooth(1 - Math.Max(0, dy) / 140.0);   // чем ниже курсор — тем слабее
                        double fx = Smooth(1 - dxOut / 60.0);              // сбоку от острова — слабеет к краю зоны
                        double fy = dy < 0 ? Smooth(1 + dy / 6.0) : 1;     // курсор заходит на сам остров — капля втягивается
                        targetAmp = 13 * fd * fx * fy;
                        // центр выпуклости держим подальше от углов: у самого угла кривая уходила вниз почти отвесно
                        double margin = CornerRadius + 18;
                        targetX = margin < w / 2 ? Math.Clamp(local.X, margin, w - margin) : w / 2;
                    }
                }
                catch { }
            }
            // вытягивается неторопливо, как густая жидкость, а втягивается чуть быстрее
            _bulgeAmp += (targetAmp - _bulgeAmp) * FrameClock.Lerp(targetAmp > _bulgeAmp ? 0.085 : 0.12);
            _bulgeX += (targetX - _bulgeX) * FrameClock.Lerp(0.25);

            // провисание, когда пользователя нет
            _sag += ((Away && Active ? 5.5 : 0) - _sag) * FrameClock.Lerp(0.02);

            // дождь — редкие капли
            if (Raining && Active && DateTime.Now >= _nextDrip)
            {
                _nextDrip = DateTime.Now.AddSeconds(4 + _rnd.NextDouble() * 6);
                _drops.Add(new Drop { X = CornerRadius + _rnd.NextDouble() * (w - 2 * CornerRadius), Y = 1, Vy = 0.2, R = 2.4, Life = 1 });
            }
            for (int i = _drops.Count - 1; i >= 0; i--)
            {
                var d = _drops[i];
                d.Vy += 0.35 * FrameClock.K; d.Y += d.Vy * FrameClock.K;
                if (d.Y > 14) d.Life -= 0.06 * FrameClock.K;
                if (d.Life <= 0 || d.Y > ActualHeight) _drops.RemoveAt(i);
            }

            // шипы будильника: вырастают быстро, втягиваются мягче
            _bristle += ((Bristle && Active ? 1 : 0) - _bristle) * FrameClock.Lerp(Bristle ? 0.12 : 0.08);
            if (_bristle < 0.002) _bristle = 0;
            double now = Environment.TickCount64 / 1000.0;

            // профиль края
            bool any = _bulgeAmp > 0.05 || _sag > 0.05 || _drops.Count > 0 || _bristle > 0;
            double r = CornerRadius;
            double span = Math.Max(1, w - 2 * r);
            // Капля и провисание сходят на нет у самых краёв острова (x = 0 и x = w), а не у начала скругления:
            // у угла край плавно продолжает дугу. Раньше всё обрывалось ровно там, где кончается скругление, —
            // капля у края упиралась в угол, и стык выглядел резким.
            double soft = r + 30;
            double TaperFull(double x) => Smooth(x / soft) * Smooth((w - x) / soft);
            double Blob(double x)
            {
                double bx = x - _bulgeX;
                // капля с «шейкой»: широкое мягкое основание + узкий кончик
                double bulge = _bulgeAmp * (0.45 * Math.Exp(-Math.Pow(bx / 52.0, 2)) + 0.55 * Math.Exp(-Math.Pow(bx / 22.0, 2)));
                double sag = _sag * Math.Exp(-Math.Pow((x / w - 0.5) / 0.28, 2));
                return (bulge + sag) * TaperFull(x);
            }
            double rho = Math.Max(0, r - 0.5);
            for (int j = 0; j < CornerSamples; j++)
            {
                double th = j / (double)CornerSamples * Math.PI / 2;
                _cornerL[j] = Blob(r - rho * Math.Cos(th));
                _cornerR[j] = Blob(w - r + rho * Math.Cos(th));
                if (_cornerL[j] > 0.05 || _cornerR[j] > 0.05) any = true;
            }
            for (int i = 0; i < Samples; i++)
            {
                double t = i / (double)(Samples - 1);
                double x = r + t * span;
                // спектр и шипы — только на прямом участке; к углам — ноль, так же плавно, как скругление
                double s0 = Math.Sin(Math.PI * t);
                double taper = s0 * s0 * (3 - 2 * s0);
                double fb = t * (_spec.Length - 1);
                int b0 = (int)fb; int b1 = Math.Min(_spec.Length - 1, b0 + 1);
                double k = fb - b0; k = (1 - Math.Cos(k * Math.PI)) / 2;
                double band = _spec[b0] * (1 - k) + _spec[b1] * k;
                double music = Math.Pow(band, 1.35) * 15;
                double spike = 0;
                if (_bristle > 0)
                {
                    // нечётные точки — острия, чётные — впадины между ними; шипы растут от центра к краям и дрожат
                    double reach = Math.Clamp(_bristle * 1.6 - Math.Abs(t - 0.5) * 1.2, 0, 1);
                    double jitter = 0.6 + 0.4 * Math.Sin(now * 19 + i * 1.7) * Math.Sin(now * 7.3 + i * 0.9);
                    spike = (i % 2 == 1 ? 10 * jitter : 1.2) * reach;
                }
                _depth[i] = (music + spike) * taper + Blob(x);
                if (_depth[i] > 0.05) any = true;
            }
            if (any || _wasAny) InvalidateVisual();
            _wasAny = any;
        }

        private bool _wasAny;
        private static double Smooth(double t) { t = Math.Clamp(t, 0, 1); return t * t * (3 - 2 * t); }
        private readonly Point[] _pts = new Point[Samples + 2 * CornerSamples];
        private readonly Point[] _inner = new Point[Samples + 2 * CornerSamples];
        private static readonly Pen OutlinePen = MakeOutline();
        private static Pen MakeOutline() { var p = new Pen(new SolidColorBrush(Color.FromRgb(0x23, 0x23, 0x2A)), 1); p.Freeze(); return p; }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth;
            if (w <= 0) return;
            double r = CornerRadius, span = Math.Max(1, w - 2 * r);
            double maxDepth = 0;
            foreach (var d in _depth) maxDepth = Math.Max(maxDepth, d);
            foreach (var d in _cornerL) maxDepth = Math.Max(maxDepth, d);
            foreach (var d in _cornerR) maxDepth = Math.Max(maxDepth, d);

            // цвет низа острова под подсвеченной плашкой: там градиент подсветки доходит до 0x3C/255 её цвета
            var tint = Tint?.Invoke();
            Brush fill = Fill;
            Pen outlinePen = OutlinePen;
            if (tint is var (tc, ts) && ts > 0.02)
            {
                var baseCol = Fill is SolidColorBrush sb ? sb.Color : Colors.Black;
                double a = 0x3C / 255.0 * ts;
                var f = Color.FromRgb((byte)(baseCol.R + (tc.R - baseCol.R) * a), (byte)(baseCol.G + (tc.G - baseCol.G) * a), (byte)(baseCol.B + (tc.B - baseCol.B) * a));
                fill = new SolidColorBrush(f); fill.Freeze();
                // обводка — как рамка подсветки (её цвет с прозрачностью 0xB0) поверх серого контура
                double k = 0xB0 / 255.0 * ts;
                var o = Color.FromRgb((byte)(0x23 + (tc.R - 0x23) * k), (byte)(0x23 + (tc.G - 0x23) * k), (byte)(0x2A + (tc.B - 0x2A) * k));
                outlinePen = new Pen(new SolidColorBrush(o), 1 + 0.5 * ts); outlinePen.Freeze();
            }

            if (maxDepth > 0.05)
            {
                // Контур края = контур острова (скругление угла + прямой низ, центр линии обводки) + глубина.
                // Скругление: центр (r, 1 − r) в координатах края, радиус r − 0,5 — ровно как у IslandOutline.
                var pts = _pts; var inner = _inner;
                double rho = Math.Max(0, r - 0.5);
                int n = 0;
                // заливка заходит внутрь острова на 2,5 px ПО НОРМАЛИ к контуру: перекрывает его обводку и рамку
                // подсветки плашки (1,5 px). Раньше сдвиг был только вниз — на крутой части дуги рамка проступала второй линией
                const double inset = 2.5;
                void Add(double x, double baseY, double depth, double nx, double ny)
                {
                    pts[n] = new Point(x, baseY + depth);
                    inner[n] = new Point(x - nx * inset, baseY - ny * inset);
                    n++;
                }
                for (int j = 0; j < CornerSamples; j++)
                {
                    double th = j / (double)CornerSamples * Math.PI / 2;
                    Add(r - rho * Math.Cos(th), 1 - r + rho * Math.Sin(th), _cornerL[j], -Math.Cos(th), Math.Sin(th));
                }
                for (int i = 0; i < Samples; i++) Add(r + i / (double)(Samples - 1) * span, 0.5, _depth[i], 0, 1);
                for (int j = CornerSamples - 1; j >= 0; j--)
                {
                    double th = j / (double)CornerSamples * Math.PI / 2;
                    Add(w - r + rho * Math.Cos(th), 1 - r + rho * Math.Sin(th), _cornerR[j], Math.Cos(th), Math.Sin(th));
                }

                void Curve(StreamGeometryContext c)
                {
                    // гладкая кривая через точки (Катмулл-Ром → Безье); когда шипы — острые ломаные
                    for (int i = 0; i < n - 1; i++)
                    {
                        bool middle = i >= CornerSamples && i < CornerSamples + Samples - 1;
                        if (_bristle > 0.3 && middle) { c.LineTo(pts[i + 1], true, true); continue; }
                        var p0 = pts[Math.Max(0, i - 1)]; var p1 = pts[i]; var p2 = pts[i + 1]; var p3 = pts[Math.Min(n - 1, i + 2)];
                        c.BezierTo(new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6),
                                   new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6), p2, true, true);
                    }
                }

                // заливка: по контуру края, обратно — чуть внутри контура острова
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(pts[0], true, true);
                    Curve(c);
                    for (int i = n - 1; i >= 0; i--) c.LineTo(inner[i], true, true);
                }
                g.Freeze();
                dc.DrawGeometry(fill, null, g);

                // обводка по изгибу — тем же цветом, что и контур острова; там, где глубины нет, она совпадает
                // с контуром острова точка в точку, поэтому стыка не видно
                var outline = new StreamGeometry();
                using (var c = outline.Open())
                {
                    c.BeginFigure(pts[0], false, false);
                    Curve(c);
                }
                outline.Freeze();
                dc.DrawGeometry(null, outlinePen, outline);

                // кончики всплесков светятся цветом обложки
                double musicLevel = 0;
                foreach (var v in _spec) musicLevel = Math.Max(musicLevel, v);
                var tipColor = _bristle > musicLevel ? AlarmGlow : SpikeColor;
                musicLevel = Math.Max(musicLevel, _bristle);
                if (musicLevel > 0.05)
                {
                    var tip = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1), MappingMode = BrushMappingMode.Absolute };
                    tip.StartPoint = new Point(0, 2);
                    tip.EndPoint = new Point(0, Math.Max(4, maxDepth));
                    tip.GradientStops.Add(new GradientStop(Color.FromArgb(0, tipColor.R, tipColor.G, tipColor.B), 0));
                    tip.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(230 * Math.Min(1, musicLevel * 1.4)), tipColor.R, tipColor.G, tipColor.B), 1));
                    dc.DrawGeometry(tip, null, g);
                }
            }

            foreach (var d in _drops)
            {
                var a = (byte)(255 * Math.Clamp(d.Life, 0, 1));
                var dropFill = fill is SolidColorBrush sb ? new SolidColorBrush(Color.FromArgb(a, sb.Color.R, sb.Color.G, sb.Color.B)) : Fill;
                dc.DrawEllipse(dropFill, new Pen(new SolidColorBrush(Color.FromArgb((byte)(a / 3), 255, 255, 255)), 0.6), new Point(d.X, d.Y), d.R, d.R * 1.15);
            }
        }
    }
}
