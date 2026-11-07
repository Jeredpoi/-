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
        private static Action<double>[] _handlers = Array.Empty<Action<double>>();
        private static readonly System.Collections.Generic.Dictionary<Action<double>, int> Failures = new();

        private static void RefreshHandlers()
        {
            var invocationList = _tick?.GetInvocationList();
            _handlers = invocationList == null
                ? Array.Empty<Action<double>>()
                : Array.ConvertAll(invocationList, handler => (Action<double>)handler);
        }

        /// <summary>Остров спрятан (OLED) — рисовать незачем, анимации стоят.</summary>
        public static bool Hidden { get; set; }

        /// <summary>Подписка: в обработчик приходит время кадра в секундах (не больше 0,1 — после зависаний без скачков).</summary>
        public static event Action<double> Tick
        {
            add
            {
                if (value == null) return;
                int before = _tick?.GetInvocationList().Length ?? 0;
                _tick += value;
                int added = (_tick?.GetInvocationList().Length ?? 0) - before;
                if (added <= 0) return;
                RefreshHandlers();
                _subscribers += added;
                if (_subscribers == added) { _last = Watch.Elapsed.TotalSeconds; Timer.Start(); }
            }
            remove
            {
                if (value == null) return;
                int before = _tick?.GetInvocationList().Length ?? 0;
                _tick -= value;
                int removed = before - (_tick?.GetInvocationList().Length ?? 0);
                if (removed <= 0) return;
                RefreshHandlers();
                _subscribers = Math.Max(0, _subscribers - removed);
                if (_subscribers == 0) Timer.Stop();
            }
        }

        static FrameClock()
        {
            Timer.Tick += (_, __) =>
            {
                double now = Watch.Elapsed.TotalSeconds;
                double dt = Math.Clamp(now - _last, 0, 0.1);
                _last = now;
                var handlers = _handlers;
                foreach (Action<double> handler in handlers)
                {
                    try { handler(dt); Failures.Remove(handler); }
                    catch (Exception ex)
                    {
                        // Одна случайная ошибка не должна навсегда выключать анимацию (а через этот кадр
                        // работает и логика чёлки: наведение, закрытие, эквалайзер). Отключаем только
                        // обработчик, который падает ~3 секунды подряд.
                        Failures.TryGetValue(handler, out int n);
                        Failures[handler] = ++n;
                        if (n == 1) App.Log("ошибка кадра анимации: " + ex);
                        if (n >= 90)
                        {
                            App.Trace("анимация отключена после повторяющихся ошибок: " + ex.Message);
                            Failures.Remove(handler);
                            Tick -= handler;
                        }
                    }
                }
            };
        }
    }
}
