using System;
using System.Diagnostics;
using System.Windows.Media;

namespace Isle.Helpers
{
    /// <summary>
    /// Замер плавности: пока включён, считает кадры отрисовки WPF и паузы между ними.
    /// Итог для журнала — «кадров N за T мс (~F к/с), худший кадр W мс, рывков K».
    /// </summary>
    public sealed class FrameMeter
    {
        private readonly Stopwatch _sw = new();
        private TimeSpan _last = TimeSpan.Zero;
        private int _frames, _hitches;
        private double _worst;
        private bool _on;

        public void Start()
        {
            if (_on) CompositionTarget.Rendering -= OnFrame; // прошлый замер не закрыли — начинаем заново
            _on = true;
            _frames = 0; _hitches = 0; _worst = 0; _last = TimeSpan.Zero;
            _sw.Restart();
            CompositionTarget.Rendering += OnFrame;
        }

        private void OnFrame(object s, EventArgs e)
        {
            // RenderingTime повторяется, если кадр не изменился, — считаем только новые
            var t = ((RenderingEventArgs)e).RenderingTime;
            if (t == _last) return;
            if (_last != TimeSpan.Zero)
            {
                double gap = (t - _last).TotalMilliseconds;
                if (gap > _worst) _worst = gap;
                if (gap > 50) _hitches++;
            }
            _last = t;
            _frames++;
        }

        /// <summary>Остановить и вернуть кадры в секунду, худший кадр и итог текстом.</summary>
        public (double fps, double worst, string text) StopStats()
        {
            double ms = _sw.Elapsed.TotalMilliseconds;
            int frames = _frames; double worst = _worst;
            var text = Stop();
            return (ms > 0 ? frames * 1000.0 / ms : 0, worst, text);
        }

        /// <summary>Остановить и вернуть итог.</summary>
        public string Stop()
        {
            if (!_on) return "замер не шёл";
            _on = false;
            CompositionTarget.Rendering -= OnFrame;
            _sw.Stop();
            double ms = _sw.Elapsed.TotalMilliseconds;
            double fps = ms > 0 ? _frames * 1000.0 / ms : 0;
            return "кадров " + _frames + " за " + Math.Round(ms) + " мс (~" + Math.Round(fps) + " к/с), худший кадр " + Math.Round(_worst) + " мс, рывков >50 мс: " + _hitches;
        }
    }
}
