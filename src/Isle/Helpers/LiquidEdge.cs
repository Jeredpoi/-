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
    /// — роняет каплю, когда что-то получилось, и капает в дождь.
    /// Рисуется под островом, клики пропускает.
    /// </summary>
    public class LiquidEdge : FrameworkElement
    {
        private const int Samples = 72;
        private readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromMilliseconds(33) };
        private readonly float[] _spec = new float[Services.SpectrumService.BandCount];
        private readonly double[] _depth = new double[Samples];
        private double _bulgeX, _bulgeAmp, _sag;
        private readonly List<Drop> _drops = new();
        private readonly Random _rnd = new();
        private DateTime _nextDrip = DateTime.Now.AddSeconds(5);

        private class Drop { public double X, Y, Vy, R, Life; }

        public Brush Fill { get; set; } = Brushes.Black;
        public Color SpikeColor { get; set; } = Color.FromRgb(0xEC, 0xEC, 0xF1);
        public double CornerRadius { get; set; } = 18;
        /// <summary>Поставщик спектра (null — музыки нет).</summary>
        public Func<float[]> Spectrum { get; set; }
        public bool Raining { get; set; }
        public bool Away { get; set; }
        public bool Active { get; set; } = true;

        public LiquidEdge()
        {
            IsHitTestVisible = false;
            Height = 46;
            _t.Tick += (_, __) => Step();
            _t.Start();
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
                _spec[i] += (target - _spec[i]) * 0.45f;
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
                        double dist = Math.Max(0, dy) + dxOut;
                        targetAmp = 11 * Math.Max(0, 1 - dist / 140.0);
                        targetX = Math.Clamp(local.X, CornerRadius, w - CornerRadius);
                    }
                }
                catch { }
            }
            _bulgeAmp += (targetAmp - _bulgeAmp) * 0.18;
            _bulgeX += (targetX - _bulgeX) * 0.25;

            // провисание, когда пользователя нет
            _sag += ((Away && Active ? 5.5 : 0) - _sag) * 0.02;

            // дождь — редкие капли
            if (Raining && Active && DateTime.Now >= _nextDrip)
            {
                _nextDrip = DateTime.Now.AddSeconds(4 + _rnd.NextDouble() * 6);
                _drops.Add(new Drop { X = CornerRadius + _rnd.NextDouble() * (w - 2 * CornerRadius), Y = 1, Vy = 0.2, R = 2.4, Life = 1 });
            }
            for (int i = _drops.Count - 1; i >= 0; i--)
            {
                var d = _drops[i];
                d.Vy += 0.35; d.Y += d.Vy;
                if (d.Y > 14) d.Life -= 0.06;
                if (d.Life <= 0 || d.Y > ActualHeight) _drops.RemoveAt(i);
            }

            // профиль края
            bool any = _bulgeAmp > 0.05 || _sag > 0.05 || _drops.Count > 0;
            double r = CornerRadius;
            double span = Math.Max(1, w - 2 * r);
            for (int i = 0; i < Samples; i++)
            {
                double t = i / (double)(Samples - 1);
                double x = r + t * span;
                double taper = Math.Sin(Math.PI * t); // к углам — ноль
                double fb = t * (_spec.Length - 1);
                int b0 = (int)fb; int b1 = Math.Min(_spec.Length - 1, b0 + 1);
                double k = fb - b0; k = (1 - Math.Cos(k * Math.PI)) / 2;
                double band = _spec[b0] * (1 - k) + _spec[b1] * k;
                double music = Math.Pow(band, 1.35) * 15;
                double bulge = _bulgeAmp * Math.Exp(-Math.Pow((x - _bulgeX) / 34.0, 2));
                double sag = _sag * Math.Exp(-Math.Pow((t - 0.5) / 0.28, 2));
                _depth[i] = (music + bulge + sag) * Math.Pow(taper, 0.6);
                if (_depth[i] > 0.05) any = true;
            }
            if (any || _wasAny) InvalidateVisual();
            _wasAny = any;
        }

        private bool _wasAny;

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth;
            if (w <= 0) return;
            double r = CornerRadius, span = Math.Max(1, w - 2 * r);
            double maxDepth = 0;
            foreach (var d in _depth) maxDepth = Math.Max(maxDepth, d);

            if (maxDepth > 0.05)
            {
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(r - 2, -1), true, true);
                    var pts = new List<Point>();
                    for (int i = 0; i < Samples; i++) pts.Add(new Point(r + i / (double)(Samples - 1) * span, _depth[i] - 0.5));
                    // гладкая кривая через точки (Катмулл-Ром → Безье)
                    for (int i = 0; i < pts.Count - 1; i++)
                    {
                        var p0 = pts[Math.Max(0, i - 1)]; var p1 = pts[i]; var p2 = pts[i + 1]; var p3 = pts[Math.Min(pts.Count - 1, i + 2)];
                        var c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
                        var c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
                        c.BezierTo(c1, c2, p2, true, true);
                    }
                    c.LineTo(new Point(w - r + 2, -1), true, true);
                }
                g.Freeze();
                dc.DrawGeometry(Fill, null, g);

                // кончики всплесков светятся цветом обложки
                double musicLevel = 0;
                foreach (var v in _spec) musicLevel = Math.Max(musicLevel, v);
                if (musicLevel > 0.05)
                {
                    var tip = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1), MappingMode = BrushMappingMode.Absolute };
                    tip.StartPoint = new Point(0, 2);
                    tip.EndPoint = new Point(0, Math.Max(4, maxDepth));
                    tip.GradientStops.Add(new GradientStop(Color.FromArgb(0, SpikeColor.R, SpikeColor.G, SpikeColor.B), 0));
                    tip.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(230 * Math.Min(1, musicLevel * 1.4)), SpikeColor.R, SpikeColor.G, SpikeColor.B), 1));
                    dc.DrawGeometry(tip, null, g);
                }
            }

            foreach (var d in _drops)
            {
                var a = (byte)(255 * Math.Clamp(d.Life, 0, 1));
                var fill = Fill is SolidColorBrush sb ? new SolidColorBrush(Color.FromArgb(a, sb.Color.R, sb.Color.G, sb.Color.B)) : Fill;
                dc.DrawEllipse(fill, new Pen(new SolidColorBrush(Color.FromArgb((byte)(a / 3), 255, 255, 255)), 0.6), new Point(d.X, d.Y), d.R, d.R * 1.15);
            }
        }
    }
}
