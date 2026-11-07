using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Isle.Helpers
{
    /// <summary>
    /// Минималистичная крутилка громкости: тонкое кольцо, дуга — уровень, точка — «риска».
    /// Крутится колесиком мыши, можно тянуть вверх/вниз; при наведении в центре видны проценты.
    /// </summary>
    public class VolumeKnob : FrameworkElement
    {
        private const double Start = 135, Sweep = 270; // дуга от «7 часов» до «5 часов»
        private double _value;
        private bool _drag;
        private Point _dragFrom;
        private double _dragValue;

        public event Action<double> ValueChanged;

        /// <summary>Проценты в центре всегда, а не только при наведении.</summary>
        public bool ShowValue { get; set; } = true;

        public Brush Track { get; set; } = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x30));
        public Brush Fill { get; set; } = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xF1));

        public VolumeKnob()
        {
            Cursor = Cursors.SizeNS;
            Focusable = false;
            MouseEnter += (_, __) => InvalidateVisual();
            MouseLeave += (_, __) => InvalidateVisual();
        }

        /// <summary>0..1</summary>
        public double Value
        {
            get => _value;
            set { var v = Math.Clamp(value, 0, 1); if (Math.Abs(v - _value) < 0.0005) return; _value = v; InvalidateVisual(); }
        }

        private void Set(double v)
        {
            v = Math.Round(Math.Clamp(v, 0, 1), 2);
            if (Math.Abs(v - _value) < 0.0005) return;
            _value = v;
            InvalidateVisual();
            ValueChanged?.Invoke(v);
        }

        // Preview — чтобы колесико не перехватил прокручиваемый контейнер выше
        protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
        {
            Set(_value + Math.Sign(e.Delta) * 0.02 * Math.Max(1, Math.Abs(e.Delta) / 120));
            e.Handled = true;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            _drag = true; _dragFrom = e.GetPosition(this); _dragValue = _value;
            CaptureMouse(); e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!_drag) return;
            var p = e.GetPosition(this);
            Set(_dragValue + (_dragFrom.Y - p.Y) / 150.0); // 150 px вверх — от 0 до 100 %
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            _drag = false; ReleaseMouseCapture(); e.Handled = true;
        }

        private static Point On(Point c, double r, double deg)
        {
            double a = deg * Math.PI / 180;
            return new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
        }

        private static Geometry Arc(Point c, double r, double from, double sweep)
        {
            if (sweep <= 0.01) return Geometry.Empty;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(On(c, r, from), false, false);
                ctx.ArcTo(On(c, r, from + sweep), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            return g;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            var c = new Point(w / 2, h / 2);
            double r = Math.Min(w, h) / 2 - 2.5;
            dc.DrawEllipse(Brushes.Transparent, null, c, r + 2, r + 2); // ловим колесико по всему кругу
            var track = new Pen(Track, 2.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            var fill = new Pen(Fill, 2.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, track, Arc(c, r, Start, Sweep));
            dc.DrawGeometry(null, fill, Arc(c, r, Start, Sweep * _value));
            if (ShowValue || IsMouseOver || _drag)
            {
                var t = new FormattedText(Math.Round(_value * 100).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                    Math.Max(9, r * 0.7), Fill, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(t, new Point(c.X - t.Width / 2, c.Y - t.Height / 2));
            }
            else
            {
                // риска-указатель внутри кольца
                double a = Start + Sweep * _value;
                dc.DrawLine(fill, On(c, r * 0.18, a), On(c, r * 0.55, a));
            }
        }
    }
}
