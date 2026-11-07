using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Media;

namespace Isle.Helpers
{
    /// <summary>
    /// Один общий кадр на всё приложение: Клоди, жидкий край и чёлка обновляются в один момент,
    /// в такт кадрам монитора (CompositionTarget.Rendering), а не по своему таймеру.
    /// Частоту можно ограничить (<see cref="MaxFps"/>): 30, 60, 120 или без ограничения.
    /// Всё, что двигается, считает шаг через <see cref="K"/> и <see cref="Lerp"/>, поэтому
    /// скорость анимаций одинакова на 30, 60 и 144 Гц — меняется только плавность.
    /// </summary>
    public static class FrameClock
    {
        private const double BaseStep = 1 / 30.0; // все «шаги за кадр» в коде подобраны под 30 кадров/с

        private static readonly Stopwatch Watch = Stopwatch.StartNew();
        private static double _last;
        private static TimeSpan _lastRenderTime;
        private static bool _hooked;
        private static event Action<double> _tick;
        private static Action<double>[] _handlers = Array.Empty<Action<double>>();
        private static readonly Dictionary<Action<double>, double> FailingSince = new();

        /// <summary>Ограничение кадров в секунду; 0 — как у монитора.</summary>
        public static int MaxFps { get; set; } = 60;

        /// <summary>Остров спрятан (OLED) — рисовать незачем, анимации стоят.</summary>
        public static bool Hidden { get; set; }

        /// <summary>Во сколько раз текущий кадр длиннее «базового» (1/30 с). На 60 Гц ≈ 0,5.</summary>
        public static double K { get; private set; } = 1;

        /// <summary>Коэффициент сглаживания «x += (цель − x) · f», пересчитанный под длину кадра.</summary>
        public static double Lerp(double perBaseFrame) => 1 - Math.Pow(1 - Math.Clamp(perBaseFrame, 0, 1), K);

        private static void RefreshHandlers()
        {
            var list = _tick?.GetInvocationList();
            _handlers = list == null ? Array.Empty<Action<double>>() : Array.ConvertAll(list, h => (Action<double>)h);
            bool want = _handlers.Length > 0;
            if (want && !_hooked) { _last = Watch.Elapsed.TotalSeconds; CompositionTarget.Rendering += OnRendering; _hooked = true; }
            else if (!want && _hooked) { CompositionTarget.Rendering -= OnRendering; _hooked = false; }
        }

        /// <summary>Подписка: в обработчик приходит длина кадра в секундах (не больше 0,1 — после зависаний без скачков).</summary>
        public static event Action<double> Tick
        {
            add { if (value == null) return; _tick += value; RefreshHandlers(); }
            remove { if (value == null) return; _tick -= value; RefreshHandlers(); }
        }

        private static void OnRendering(object sender, EventArgs e)
        {
            // Rendering может прийти несколько раз за один кадр — берём только новые кадры
            if (e is RenderingEventArgs r)
            {
                if (r.RenderingTime == _lastRenderTime) return;
                _lastRenderTime = r.RenderingTime;
            }
            double now = Watch.Elapsed.TotalSeconds;
            double elapsed = now - _last;
            int max = MaxFps;
            if (max > 0 && elapsed < 1.0 / max - 0.002) return; // ограничение частоты
            _last = now;
            double dt = Math.Clamp(elapsed, 0.001, 0.1);
            K = dt / BaseStep;

            foreach (var handler in _handlers)
            {
                try { handler(dt); FailingSince.Remove(handler); }
                catch (Exception ex)
                {
                    // Одна случайная ошибка не должна навсегда выключать анимацию (через этот кадр работает
                    // и логика чёлки). Отключаем только обработчик, который падает 3 секунды подряд.
                    if (!FailingSince.TryGetValue(handler, out double since)) { FailingSince[handler] = now; App.Log("ошибка кадра анимации: " + ex); }
                    else if (now - since > 3)
                    {
                        App.Trace("анимация отключена после повторяющихся ошибок: " + ex.Message);
                        FailingSince.Remove(handler);
                        Tick -= handler;
                    }
                }
            }
        }
    }
}
