using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace Isle.Helpers
{
    /// <summary>
    /// «Пружина» как у Dynamic Island: быстрый разгон, лёгкий перелёт и затухающее покачивание.
    /// Использовать с EasingMode.EaseIn (кривая задаётся целиком здесь).
    /// </summary>
    public class Spring : EasingFunctionBase
    {
        public double Damping { get; set; } = 7.0;
        public double Frequency { get; set; } = 11.0;

        public Spring() { EasingMode = EasingMode.EaseIn; }

        protected override double EaseInCore(double t)
        {
            if (t >= 1) return 1;
            return 1 - Math.Exp(-Damping * t) * Math.Cos(Frequency * t);
        }

        protected override Freezable CreateInstanceCore() => new Spring();
    }
}
