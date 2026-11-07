using System;
using System.Windows;
using System.Windows.Media;

namespace Isle.Helpers
{
    /// <summary>
    /// Волна голоса во время диктовки, как в ChatGPT: столбики бегут справа налево,
    /// каждый растёт вверх и вниз от середины — чем громче говоришь, тем выше. Сразу видно, что микрофон тебя слышит.
    /// </summary>
    public class VoiceWave : FrameworkElement
    {
        private const int Bars = 32;
        private const double Step = 0.12; // новый столбик каждые 120 мс — волна идёт спокойно, не мельтешит
        private readonly double[] _levels = new double[Bars];
        private double _pending, _acc, _shown;
        private bool _subscribed;

        public Brush Fill { get; set; } = Brushes.White;

        public VoiceWave()
        {
            IsHitTestVisible = false;
            IsVisibleChanged += (_, __) => { if (IsVisible) Subscribe(); else Unsubscribe(); };
            Unloaded += (_, __) => Unsubscribe();
        }

        /// <summary>Громкость с микрофона 0..1 (приходит много раз за кадр — берём максимум за шаг).</summary>
        public void Push(float level) => _pending = Math.Max(_pending, level);

        public void Clear() { Array.Clear(_levels); _pending = 0; _shown = 0; InvalidateVisual(); }

        private void Subscribe() { if (_subscribed) return; _subscribed = true; FrameClock.Tick += OnFrame; }
        private void Unsubscribe() { if (!_subscribed) return; _subscribed = false; FrameClock.Tick -= OnFrame; }

        private void OnFrame(double dt)
        {
            _acc += dt;
            while (_acc >= Step)
            {
                _acc -= Step;
                Array.Copy(_levels, 1, _levels, 0, Bars - 1);
                // тихая речь тоже должна быть заметна: усиливаем и «сжимаем» корнем; сглаживаем с соседним столбиком
                double v = Math.Sqrt(Math.Min(1, _pending * 3.5));
                _levels[Bars - 1] = _levels[Bars - 2] * 0.35 + v * 0.65;
                _pending = 0;
            }
            _shown = _acc / Step; // плавный сдвиг между столбиками
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
            double slot = w / (Bars - 1), bar = Math.Max(2, slot * 0.55), mid = h / 2;
            for (int i = 0; i < Bars; i++)
            {
                double x = (i - _shown) * slot;
                if (x < -bar) continue;
                double amp = Math.Max(bar, _levels[i] * h); // в тишине — точка
                double fade = Math.Min(1, (x + bar) / (w * 0.18)); // слева столбики плавно гаснут
                dc.PushOpacity(Math.Clamp(fade, 0, 1));
                dc.DrawRoundedRectangle(Fill, null, new Rect(x, mid - amp / 2, bar, amp), bar / 2, bar / 2);
                dc.Pop();
            }
        }
    }
}
