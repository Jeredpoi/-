using System;
using System.Diagnostics;
using System.Windows.Threading;

namespace Isle.Helpers
{
    /// <summary>
    /// Один общий «кадр» на всё приложение (~30 кадров/с): Клоди, жидкий край и чёлка обновляются
    /// в один и тот же момент. Раньше у каждого был свой таймер (33, 40, 45 мс) — прозрачное окно
    /// перерисовывалось почти вдвое чаще, чем нужно, и анимации шли рвано.
    /// </summary>
    public static class FrameClock
    {
        private static readonly DispatcherTimer Timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        private static readonly Stopwatch Watch = Stopwatch.StartNew();
        private static double _last;
        private static int _subscribers;
        private static event Action<double> _tick;

        /// <summary>Остров спрятан (OLED) — рисовать незачем, анимации стоят.</summary>
        public static bool Hidden { get; set; }

        /// <summary>Подписка: в обработчик приходит время кадра в секундах (не больше 0,1 — после зависаний без скачков).</summary>
        public static event Action<double> Tick
        {
            add { _tick += value; if (++_subscribers == 1) { _last = Watch.Elapsed.TotalSeconds; Timer.Start(); } }
            remove { _tick -= value; if (--_subscribers <= 0) { _subscribers = 0; Timer.Stop(); } }
        }

        static FrameClock()
        {
            Timer.Tick += (_, __) =>
            {
                double now = Watch.Elapsed.TotalSeconds;
                double dt = Math.Clamp(now - _last, 0, 0.1);
                _last = now;
                _tick?.Invoke(dt);
            };
        }
    }
}
