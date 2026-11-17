using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Isle.Helpers
{
    /// <summary>
    /// Снег из-под чёлки: снежинки срываются с нижнего края острова, покачиваются, медленно падают и тают.
    /// Рисуется под островом, клики пропускает. Кадры — общий такт приложения (в частоту монитора).
    /// </summary>
    public class SnowFall : FrameworkElement
    {
        private class Flake { public double X, Y, Vy, R, Phase, Sway, Life, Spin; }

        private readonly List<Flake> _flakes = new();
        private readonly Random _rnd = new();
        private double _spawn;
        private bool _subscribed;

        /// <summary>Ширина нижнего края чёлки, с которого сыплется снег (по центру элемента).</summary>
        public double SourceWidth { get; set; } = 200;
        /// <summary>Идёт ли снег сейчас. Выключили — уже летящие снежинки долетают и тают.</summary>
        public bool Active { get; set; }
        /// <summary>Снежинок в секунду.</summary>
        public double Rate { get; set; } = 7;

        private static readonly Brush[] Brushes = MakeBrushes();
        private static Brush[] MakeBrushes()
        {
            var a = new Brush[16];
            for (int i = 0; i < a.Length; i++) { var b = new SolidColorBrush(Color.FromArgb((byte)(i * 16 + 15), 0xF4, 0xF7, 0xFF)); b.Freeze(); a[i] = b; }
            return a;
        }

        public SnowFall()
        {
            IsHitTestVisible = false;
            IsVisibleChanged += (_, __) => Sync();
            Loaded += (_, __) => Sync();
            Unloaded += (_, __) => { if (_subscribed) { FrameClock.Tick -= OnFrame; _subscribed = false; } };
        }

        /// <summary>Подписка на кадры — только пока есть что рисовать.</summary>
        public void Sync()
        {
            bool want = IsVisible && (Active || _flakes.Count > 0);
            if (want && !_subscribed) { FrameClock.Tick += OnFrame; _subscribed = true; }
            else if (!want && _subscribed) { FrameClock.Tick -= OnFrame; _subscribed = false; InvalidateVisual(); }
        }

        private void OnFrame(double dt)
        {
            if (FrameClock.Hidden) return;
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            if (Active)
            {
                _spawn += dt * Rate;
                while (_spawn >= 1)
                {
                    _spawn -= 1;
                    double src = Math.Min(SourceWidth, w) - 24;
                    _flakes.Add(new Flake
                    {
                        X = w / 2 + (_rnd.NextDouble() - 0.5) * Math.Max(10, src),
                        Y = -2,
                        Vy = 14 + _rnd.NextDouble() * 16,      // точек в секунду — медленно, как настоящий снег
                        R = 0.9 + _rnd.NextDouble() * 1.6,
                        Phase = _rnd.NextDouble() * Math.PI * 2,
                        Sway = 4 + _rnd.NextDouble() * 9,
                        Spin = 0.8 + _rnd.NextDouble() * 1.4,
                        Life = 1,
                    });
                }
            }
            for (int i = _flakes.Count - 1; i >= 0; i--)
            {
                var f = _flakes[i];
                f.Y += f.Vy * dt;
                f.Phase += f.Spin * dt;
                // тает к низу области: последние 40 % пути
                double fade = f.Y / h;
                f.Life = fade < 0.6 ? 1 : Math.Max(0, 1 - (fade - 0.6) / 0.4);
                if (f.Life <= 0 || f.Y > h) _flakes.RemoveAt(i);
            }
            InvalidateVisual();
            if (!Active && _flakes.Count == 0) Sync();
        }

        protected override void OnRender(DrawingContext dc)
        {
            foreach (var f in _flakes)
            {
                double x = f.X + Math.Sin(f.Phase) * f.Sway;
                // появляется плавно, сразу под краем чёлки
                double appear = Math.Clamp(f.Y / 8, 0, 1);
                int a = (int)Math.Clamp(f.Life * appear * 15, 0, 15);
                if (a <= 0) continue;
                dc.DrawEllipse(Brushes[a], null, new Point(x, f.Y), f.R, f.R);
            }
        }
    }
}
