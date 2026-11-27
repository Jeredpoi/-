using System.Windows.Media;

namespace Isle.Helpers
{
    /// <summary>
    /// Акцент: один цвет («#4ADE80») или градиент из двух («#22D3EE>#A855F7»).
    /// Градиент кладётся в ресурс Accent целиком — кнопки, таймер, полоски музыки переливаются.
    /// </summary>
    public static class AccentPaint
    {
        public static (Color a, Color? b) Parse(string s)
        {
            try
            {
                var parts = (s ?? "").Split('>');
                var a = (Color)ColorConverter.ConvertFromString(parts[0].Trim());
                Color? b = parts.Length > 1 ? (Color)ColorConverter.ConvertFromString(parts[1].Trim()) : null;
                return (a, b);
            }
            catch { return (Color.FromRgb(0xEC, 0xEC, 0xF1), null); }
        }

        /// <summary>Основной цвет (для мест, где нужен один цвет): у градиента — середина.</summary>
        public static Color Main(string s)
        {
            var (a, b) = Parse(s);
            return b is Color c ? Color.FromRgb((byte)((a.R + c.R) / 2), (byte)((a.G + c.G) / 2), (byte)((a.B + c.B) / 2)) : a;
        }

        /// <summary>Кисть: градиент слева направо (angle 0) или снизу вверх (angle 90, для полосок музыки).</summary>
        public static Brush Brush(string s, double angle = 0)
        {
            var (a, b) = Parse(s);
            Brush br = b is Color c
                ? angle == 90 ? new LinearGradientBrush(a, c, new System.Windows.Point(0, 1), new System.Windows.Point(0, 0))
                              : new LinearGradientBrush(a, c, angle)
                : new SolidColorBrush(a);
            br.Freeze();
            return br;
        }

        public static bool IsGradient(string s) => (s ?? "").Contains('>');
    }
}
