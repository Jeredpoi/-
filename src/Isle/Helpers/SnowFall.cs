using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Isle.Helpers
{
    /// <summary>
    /// Снег из-под чёлки. Три слоя глубины (дальние — мелкие и медленные, ближние — крупные, часть — звёздочки),
    /// общий ветер с порывами, инерция, снежинки «набухают» на кромке острова и срываются, тают каждая на своей высоте.
    /// Снежинки — заранее запечённые картинки с мягкой тенью (видно и на белом), рисуются без новых кистей в кадре.
    /// Элемент — на всё окно; снежинки живут в его координатах и не ездят вместе с островом.
    /// </summary>
    public class SnowFall : FrameworkElement
    {
        private struct Flake
        {
            public double X, Y, Vx, Vy, VTerm, R, Alpha0, Kick, Phase, PhaseW, Sway, Rot, RotW, Tilt, TiltW, Y0, Fall, Hang, HangT;
            public int Layer; public bool Star;
        }

        private sealed record LayerDef(double Share, double RMin, double RMax, double VMin, double VMax, double AMin, double AMax,
                                       double SwayMin, double SwayMax, double WindK, double TauX, double FallMin, double FallMax);

        private static readonly LayerDef[] Layers =
        {
            new(.45, .6, 1.0,  9, 14, .40, .55, 2, 4,  .5, .9, .55, .80),
            new(.40, 1.1, 1.8, 16, 26, .70, .85, 4, 8,  .8, .7, .75, 1.05),
            new(.15, 2.0, 3.0, 28, 42, .90, 1.0, 6, 11, 1.2, .5, .90, 1.25),
        };

        private const int Max = 200;
        private readonly Flake[] _f = new Flake[Max + 48];
        private int _n, _stars;
        private readonly Random _rnd = new();
        private double _next, _t, _rampIn;
        private bool _subscribed;

        /// <summary>Остров, из-под нижнего края которого сыплется снег.</summary>
        public FrameworkElement Source { get; set; }
        /// <summary>Сколько точек снежинка пролетает (в среднем) до того, как растает.</summary>
        public double Fall { get; set; } = 190;
        /// <summary>Идёт ли снег сейчас. Выключили — уже летящие снежинки долетают и тают.</summary>
        public bool Active { get; set; }
        /// <summary>Сила снегопада, мм/ч осадков из прогноза (0 — снег «по сезону»).</summary>
        public double Intensity { get; set; }

        public SnowFall()
        {
            IsHitTestVisible = false;
            SnapsToDevicePixels = false;
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
            IsVisibleChanged += (_, __) => Sync();
            Loaded += (_, __) => Sync();
            Unloaded += (_, __) => { if (_subscribed) { FrameClock.Tick -= OnFrame; _subscribed = false; } };
        }

        /// <summary>Подписка на кадры — только пока есть что рисовать.</summary>
        public void Sync()
        {
            bool want = IsVisible && (Active || _n > 0);
            if (want && !_subscribed) { FrameClock.Tick += OnFrame; _subscribed = true; }
            else if (!want && _subscribed) { FrameClock.Tick -= OnFrame; _subscribed = false; InvalidateVisual(); }
            if (!Active) _rampIn = 0;
        }

        /// <summary>Убрать все снежинки сразу (остров спрятался — снег не должен висеть в воздухе).</summary>
        public void Clear()
        {
            if (_n == 0) return;
            _n = 0; _stars = 0;
            InvalidateVisual();
            Sync();
        }

        /// <summary>Остров раскрылся или свернулся — с его нового края «стряхивается» горсть снега.</summary>
        private DateTime _lastBurst;
        public void Burst(bool big)
        {
            if (!Active || DateTime.Now - _lastBurst < TimeSpan.FromSeconds(1.5) || !TryEdge(out var edge, out double cr)) return;
            _lastBurst = DateTime.Now;
            int count = big ? (int)(20 + 8 * I) : 7;
            for (int i = 0; i < count; i++) Spawn(edge, cr, burst: true);
            Sync();
        }

        private double I => Math.Clamp(Intensity, 0.15, 4);

        // ---------- ветер: медленный шум + порывы ----------
        private static readonly double[] Noise = MakeNoise();
        private static double[] MakeNoise() { var r = new Random(7); var a = new double[256]; for (int i = 0; i < a.Length; i++) a[i] = r.NextDouble() * 2 - 1; return a; }

        private static double VN(double x)
        {
            int i = (int)Math.Floor(x);
            double f = x - i, s = f * f * (3 - 2 * f);
            double a = Noise[i & 255], b = Noise[(i + 1) & 255];
            return a + (b - a) * s;
        }

        private double _base, _baseFrom, _baseTo, _baseT = 1, _nextBase = 25;
        private double _gustAmp, _gustT = -1, _gustHold, _nextGust = 8;

        private double Wind(double dt)
        {
            // основа меняется раз в 20–40 с, плавно за 4 с
            _nextBase -= dt;
            if (_nextBase <= 0) { _baseFrom = _base; _baseTo = _rnd.NextDouble() * 12 - 6; _baseT = 0; _nextBase = 20 + _rnd.NextDouble() * 20; }
            if (_baseT < 1) { _baseT = Math.Min(1, _baseT + dt / 4); double s = _baseT * _baseT * (3 - 2 * _baseT); _base = _baseFrom + (_baseTo - _baseFrom) * s; }
            // порыв: нарастание 0,7 с, удержание, спад 2,5 с
            double gust = 0;
            if (_gustT < 0)
            {
                _nextGust -= dt;
                if (_nextGust <= 0) { _gustT = 0; _gustHold = 0.5 + _rnd.NextDouble(); _gustAmp = (_rnd.NextDouble() < .5 ? -1 : 1) * (12 + _rnd.NextDouble() * 18) * (0.6 + I / 4); }
            }
            else
            {
                _gustT += dt;
                if (_gustT < 0.7) { double s = _gustT / 0.7; gust = _gustAmp * s * s * (3 - 2 * s); }
                else if (_gustT < 0.7 + _gustHold) gust = _gustAmp;
                else
                {
                    gust = _gustAmp * Math.Exp(-(_gustT - 0.7 - _gustHold) / 0.9);
                    if (_gustT > 0.7 + _gustHold + 2.5)
                    {
                        _gustT = -1;
                        double mean = I > 2 ? 7 : 11;
                        _nextGust = -Math.Log(1 - _rnd.NextDouble()) * mean;
                    }
                }
            }
            return _base + 9 * VN(_t / 6.0) + 3 * VN(_t / 1.7 + 37) + gust;
        }

        // ---------- кадры ----------
        private double _wind;
        private int _frameSkip;

        private bool TryEdge(out Rect edge, out double cr)
        {
            edge = default; cr = 0;
            var src = Source;
            if (src == null || !src.IsVisible || src.ActualWidth < 20 || src.Opacity < 0.5) return false;
            Point p;
            try { p = src.TranslatePoint(new Point(0, src.ActualHeight), this); } catch { return false; }
            if (p.Y < 0 || p.Y > ActualHeight - 20) return false;
            edge = new Rect(p.X, p.Y - 1, src.ActualWidth, 1);
            cr = src is System.Windows.Controls.Border b ? Math.Min(b.CornerRadius.BottomLeft, src.ActualWidth / 3) : 10;
            return true;
        }

        private void OnFrame(double dt)
        {
            // кадры остановлены (остров спрятан) — снежинки не замирают в воздухе, а исчезают
            if (FrameClock.Hidden) { Clear(); return; }
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            dt = Math.Min(dt, 0.1);
            _t += dt;
            _wind = Wind(dt);
            bool hasEdge = TryEdge(out var edge, out double cr);

            if (Active && hasEdge)
            {
                _rampIn = Math.Min(1, _rampIn + dt / 2);
                double widthK = Math.Pow(Math.Clamp(edge.Width / 220, 1, 4.5), 0.6);
                double rate = (2.5 + 6 * Math.Sqrt(I)) * widthK * _rampIn;
                if (_n > 0.8 * Max) rate *= Math.Max(0, (Max - _n) / (0.2 * Max));
                _next -= dt;
                int guard = 0;
                while (_next <= 0 && guard++ < 10)
                {
                    if (rate > 0.01) Spawn(edge, cr, burst: false);
                    _next += rate > 0.01 ? -Math.Log(1 - _rnd.NextDouble()) / rate : 0.5; // пуассоновский поток, а не метроном
                }
            }

            double edgeY = hasEdge ? edge.Bottom : -1;
            for (int i = _n - 1; i >= 0; i--)
            {
                ref var f = ref _f[i];
                var d = Layers[f.Layer];
                if (f.HangT < f.Hang)
                {
                    // «набухает» на кромке и едет вместе с ней
                    f.HangT += dt;
                    if (edgeY > 0) { f.Y = edgeY - 1; f.Y0 = edgeY; }
                    continue;
                }
                f.Kick *= Math.Exp(-dt / 1.5);
                f.Vx += (_wind * d.WindK + f.Kick - f.Vx) * (1 - Math.Exp(-dt / d.TauX));
                f.Vy += (f.VTerm * (1 + .12 * Math.Sin(2 * f.Phase + 1.3)) - f.Vy) * (1 - Math.Exp(-dt / .35));
                f.X += f.Vx * dt; f.Y += f.Vy * dt;
                f.Phase += f.PhaseW * (1 + .2 * VN(f.Phase * .3 + i)) * dt;
                f.Rot += f.RotW * dt; f.Tilt += f.TiltW * dt;
                if (f.Y - f.Y0 > f.Fall || f.Y > h || f.X < -20 || f.X > w + 20) Remove(i);
            }

            // выше 75 Гц перерисовываем через кадр: глаз разницы не видит, процессору легче
            bool fast = dt < 1 / 80.0;
            if (!fast || (++_frameSkip & 1) == 0) InvalidateVisual();
            if (!Active && _n == 0) Sync();
        }

        private void Remove(int i)
        {
            if (_f[i].Star) _stars--;
            _f[i] = _f[--_n];
        }

        private double Rnd() => _rnd.NextDouble();
        private static double Lerp(double a, double b, double t) => a + (b - a) * t;

        private void Spawn(Rect edge, double cr, bool burst)
        {
            if (_n >= Max + (burst ? 40 : 0) || _n >= _f.Length) return;
            // слой: при сильном снеге ближних больше
            double nearShare = I > 2 ? .20 : .15, u = Rnd();
            int li = u < nearShare ? 2 : u < nearShare + .40 ? 1 : 0;
            if (burst && li == 0) li = 1;
            var d = Layers[li];
            ref var f = ref _f[_n++];
            f = default;
            f.Layer = li;
            f.Star = li == 2 && Rnd() < .25 && _stars < 12;
            if (f.Star) _stars++;
            f.R = f.Star ? Lerp(3.5, 5.5, Rnd()) : Lerp(d.RMin, d.RMax, Rnd());
            double cx = edge.Left + edge.Width / 2, half = Math.Max(4, edge.Width / 2 - cr);
            f.X = Rnd() < .1 ? cx + Math.Sign(Rnd() - .5) * (half + Rnd() * cr * .7) : cx + (Rnd() * 2 - 1) * half;
            f.Y0 = edge.Bottom; f.Y = f.Y0 - 2;
            f.VTerm = Lerp(d.VMin, d.VMax, Rnd()) * (I > 2 ? 1.15 : 1);
            f.Vy = burst ? Rnd() * 10 : 0;
            f.Vx = _wind * d.WindK;
            f.Kick = (f.X - cx) / half * (burst ? Lerp(8, 25, Rnd()) : 6);
            f.Alpha0 = Lerp(d.AMin, d.AMax, Rnd());
            f.Sway = Lerp(d.SwayMin, d.SwayMax, Rnd());
            f.Phase = Rnd() * Math.PI * 2; f.PhaseW = Lerp(.5, 1.3, Rnd());
            f.RotW = (Rnd() < .5 ? -1 : 1) * Lerp(15, 45, Rnd());
            f.TiltW = Lerp(.8, 1.6, Rnd());
            f.Fall = Fall * Lerp(d.FallMin, d.FallMax, Rnd());
            f.Hang = !burst && li > 0 && Rnd() < .3 ? Lerp(.25, .6, Rnd()) : 0;
        }

        // ---------- отрисовка ----------
        protected override void OnRender(DrawingContext dc)
        {
            if (_n == 0) return;
            EnsureSprites();
            for (int i = 0; i < _n; i++)
            {
                ref var f = ref _f[i];
                double s = (f.Y - f.Y0) / Math.Max(1, f.Fall);
                double life = 1 - SmoothStep(.65, 1, s);
                bool hanging = f.HangT < f.Hang;
                double grow = f.Hang > 0 && hanging ? Lerp(.3, 1, f.HangT / f.Hang) : 1;
                double appear = f.Hang > 0 ? grow : Math.Clamp((f.Y - f.Y0 + 2) / 6, 0, 1);
                int ai = (int)(f.Alpha0 * life * appear * 15 + .5);
                if (ai <= 0) continue;
                ai = Math.Min(15, ai);
                double r = f.R * (.55 + .45 * life) * grow;
                double x = f.X + f.Sway * Math.Sin(f.Phase) + (hanging ? .5 * Math.Sin(f.HangT * 40) : 0);
                int kind = f.Star ? 3 : f.Layer;
                double sz = r * SpriteK[kind];
                var spr = Sprites[kind][ai];
                if (f.Star)
                {
                    var m = Matrix.Identity;
                    m.Scale(.7 + .3 * Math.Cos(f.Tilt), 1);
                    m.Rotate(f.Rot);
                    m.Translate(x, f.Y);
                    dc.PushTransform(new MatrixTransform(m));
                    dc.DrawImage(spr, new Rect(-sz / 2, -sz / 2, sz, sz));
                    dc.Pop();
                }
                else dc.DrawImage(spr, new Rect(x - sz / 2, f.Y - sz / 2, sz, sz));
            }
        }

        private static double SmoothStep(double a, double b, double x)
        {
            double t = Math.Clamp((x - a) / (b - a), 0, 1);
            return t * t * (3 - 2 * t);
        }

        // ---------- спрайты: мягкий диск с тенью трёх размеров и звёздочка, по 16 уровней прозрачности ----------
        private static BitmapSource[][] Sprites;
        private static readonly double[] BaseR = { 0.8, 1.45, 2.5, 4.5 };   // радиус, под который запечён спрайт
        private static readonly double[] SpriteK = new double[4];          // размер спрайта / радиус снежинки

        private static void EnsureSprites()
        {
            if (Sprites != null) return;
            Sprites = new BitmapSource[4][];
            const double scale = 3; // запекаем крупнее — при уменьшении края мягкие и без «лесенки»
            for (int k = 0; k < 4; k++)
            {
                double r = BaseR[k];
                double size = k == 3 ? 2 * (r + 1.6) : 2 * (r + 1.4);
                SpriteK[k] = size / r;
                var arr = new BitmapSource[16];
                for (int a = 0; a < 16; a++)
                {
                    var dv = new DrawingVisual();
                    using (var dc = dv.RenderOpen())
                    {
                        dc.PushOpacity(a / 15.0);
                        double c = size / 2;
                        if (k == 3) DrawStar(dc, c, r);
                        else DrawDisc(dc, c, r, k == 0 ? 0 : k == 1 ? .16 : .24);
                        dc.Pop();
                    }
                    int px = (int)Math.Ceiling(size * scale);
                    var bmp = new RenderTargetBitmap(px, px, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    bmp.Render(dv);
                    bmp.Freeze();
                    arr[a] = bmp;
                }
                Sprites[k] = arr;
            }
        }

        /// <summary>Мягкий диск: белый центр, к краю тает; под ним едва заметная тень — снежинку видно и на белой странице.</summary>
        private static void DrawDisc(DrawingContext dc, double c, double r, double shadow)
        {
            if (shadow > 0)
            {
                var sh = new RadialGradientBrush(Color.FromArgb((byte)(255 * shadow), 0, 0, 0), Color.FromArgb(0, 0, 0, 0));
                sh.Freeze();
                dc.DrawEllipse(sh, null, new Point(c, c + 0.6), r + 1.2, r + 1.2);
            }
            var g = new RadialGradientBrush();
            g.GradientStops.Add(new GradientStop(Color.FromArgb(255, 255, 255, 255), 0));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(242, 255, 255, 255), 0.55));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0xEE, 0xF4, 0xFF), 1));
            g.Freeze();
            dc.DrawEllipse(g, null, new Point(c, c), r, r);
        }

        /// <summary>Шестилучевая звёздочка с веточками; под ней такая же тень.</summary>
        private static void DrawStar(DrawingContext dc, double c, double r)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                for (int i = 0; i < 6; i++)
                {
                    double a = i * Math.PI / 3;
                    var tip = new Point(c + r * Math.Cos(a), c + r * Math.Sin(a));
                    ctx.BeginFigure(new Point(c, c), false, false); ctx.LineTo(tip, true, false);
                    var mid = new Point(c + .55 * r * Math.Cos(a), c + .55 * r * Math.Sin(a));
                    foreach (var off in new[] { Math.PI / 3, -Math.PI / 3 })
                    {
                        ctx.BeginFigure(mid, false, false);
                        ctx.LineTo(new Point(mid.X + .3 * r * Math.Cos(a + off), mid.Y + .3 * r * Math.Sin(a + off)), true, false);
                    }
                }
            }
            geo.Freeze();
            var shadow = new Pen(new SolidColorBrush(Color.FromArgb(46, 0, 0, 0)), 2.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            shadow.Freeze();
            dc.PushTransform(new TranslateTransform(0, 0.6));
            dc.DrawGeometry(null, shadow, geo);
            dc.Pop();
            var pen = new Pen(Brushes.White, 1.0) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            dc.DrawGeometry(null, pen, geo);
            dc.DrawEllipse(Brushes.White, null, new Point(c, c), .8, .8);
        }
    }
}
