using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Isle.Services;

namespace Isle.Helpers
{
    /// <summary>
    /// «Караоке» для строки песни: буквы закрашиваются по одной, как поют, а текущая буква — плавно.
    /// Если у текста есть время каждого слова (lrclib «lyricsfile»), идём точно по нему; иначе темп внутри
    /// строки оцениваем по её длине и паузе до следующей строки.
    /// </summary>
    public static class KaraokeText
    {
        private sealed class Timing { public double[] Start, End; public double Last = double.MinValue; }

        /// <summary>Разбить строку на буквы (Run на каждую) и посчитать, когда каждая должна загореться.</summary>
        public static void Set(TextBlock tb, LyricLine line, TimeSpan? next, Brush off)
        {
            tb.Inlines.Clear();
            tb.Tag = null;
            var text = line?.Text;
            if (string.IsNullOrEmpty(text)) return;

            var (starts, ends) = line.Words != null && line.Words.Count > 0 ? FromWords(line, next) : Estimate(line, next);
            for (int i = 0; i < text.Length; i++) tb.Inlines.Add(new Run(text[i].ToString()) { Foreground = off });
            tb.Tag = new Timing { Start = starts, End = ends };
        }

        /// <summary>Закрасить буквы, которые к позиции pos уже спеты.</summary>
        public static void Update(TextBlock tb, TimeSpan pos, Brush on, Brush off)
        {
            if (tb.Tag is not Timing t) return;
            double now = pos.TotalSeconds;
            // плеер иногда присылает позицию чуть «назад» — не откатываем заливку на доли секунды, иначе она дёргается
            if (now < t.Last && t.Last - now < 0.6) now = t.Last;
            t.Last = now;
            int k = 0;
            foreach (var inline in tb.Inlines)
            {
                if (inline is not Run r || k >= t.Start.Length) break;
                // мягкий край: каждая буква разгорается чуть дольше своего отрезка и заходит на соседние —
                // несколько букв светятся по-разному одновременно, и заливка катится волной, а не щёлкает
                double a = t.Start[k] - Soft * 0.5, b = t.End[k] + Soft * 0.5;
                double f = now <= a ? 0 : now >= b ? 1 : (now - a) / Math.Max(0.001, b - a);
                f = f * f * (3 - 2 * f); // плавный разгон и торможение
                var want = Blend(on, off, f);
                if (!ReferenceEquals(r.Foreground, want)) r.Foreground = want;
                k++;
            }
        }

        /// <summary>Ширина «гребня» волны, с: столько длится разгорание буквы сверх её собственного времени.</summary>
        private const double Soft = 0.35;

        /// <summary>Номер строки на позиции pos (с небольшим запасом, чтобы строка успела въехать).</summary>
        public static int Index(List<LyricLine> lines, TimeSpan pos)
        {
            if (lines == null || lines.Count == 0) return -1;
            var p = pos + TimeSpan.FromMilliseconds(250);
            return lines.FindLastIndex(l => l.At <= p);
        }

        /// <summary>Следующая строка после i — чтобы знать, сколько времени есть у последнего слова.</summary>
        public static TimeSpan? NextAt(List<LyricLine> lines, int i) => lines != null && i + 1 < lines.Count ? lines[i + 1].At : null;

        // ----- время букв -----

        /// <summary>По словам: буквы слова делят его время поровну; слово длится до начала следующего (но не бесконечно).</summary>
        private static (double[], double[]) FromWords(LyricLine line, TimeSpan? next)
        {
            var text = line.Text;
            var s = new double[text.Length];
            var e = new double[text.Length];
            double lineEnd = (line.End ?? next ?? line.At + TimeSpan.FromSeconds(4)).TotalSeconds;
            int pos = 0;
            for (int w = 0; w < line.Words.Count && pos < text.Length; w++)
            {
                var word = line.Words[w].Text.Trim();
                if (word.Length == 0) continue;
                int at = text.IndexOf(word, pos, StringComparison.Ordinal);
                if (at < 0) at = pos; // текст слова чуть отличается от строки — идём по порядку
                int len = Math.Min(word.Length, text.Length - at);
                double ws = line.Words[w].At.TotalSeconds;
                double we = w + 1 < line.Words.Count ? line.Words[w + 1].At.TotalSeconds : lineEnd;
                // протяжная нота может тянуться, но не дольше разумного: иначе слово «зависает» на секунды
                we = Math.Min(we, ws + Math.Max(0.35, len * 0.35 + 0.3));
                if (we <= ws) we = ws + 0.2;
                for (int i = pos; i < at; i++) { s[i] = ws; e[i] = ws; } // пробелы и знаки перед словом
                for (int i = 0; i < len; i++)
                {
                    s[at + i] = ws + (we - ws) * i / len;
                    e[at + i] = ws + (we - ws) * (i + 1) / len;
                }
                pos = at + len;
            }
            for (int i = pos; i < text.Length; i++) { s[i] = pos > 0 ? e[pos - 1] : lineEnd; e[i] = s[i]; }
            return (s, e);
        }

        /// <summary>
        /// Без слов: строку поют почти всю паузу до следующей (85%), но не быстрее ~0,22 с на букву —
        /// прежняя оценка (0,12 с) убегала вперёд песни.
        /// </summary>
        private static (double[], double[]) Estimate(LyricLine line, TimeSpan? next)
        {
            var text = line.Text;
            double start = line.At.TotalSeconds;
            double gap = next.HasValue ? (next.Value - line.At).TotalSeconds : 4;
            double span = line.End.HasValue ? (line.End.Value - line.At).TotalSeconds
                : Math.Max(0.8, Math.Min(gap * 0.85, Math.Max(1.5, text.Length * 0.22)));
            // время делим по буквам без пробелов: пробел не поётся
            int letters = 0;
            foreach (var ch in text) if (!char.IsWhiteSpace(ch)) letters++;
            letters = Math.Max(1, letters);
            var s = new double[text.Length];
            var e = new double[text.Length];
            int k = 0;
            for (int i = 0; i < text.Length; i++)
            {
                double a = start + span * k / letters;
                if (char.IsWhiteSpace(text[i])) { s[i] = e[i] = a; continue; }
                s[i] = a; e[i] = start + span * (k + 1) / letters; k++;
            }
            return (s, e);
        }

        // ----- цвет -----

        private static readonly Dictionary<(Color, Color), Brush[]> Steps = new();

        /// <summary>Промежуточный цвет между «не спето» и «спето»: 32 готовые кисти, без новых объектов на каждом кадре.</summary>
        private static Brush Blend(Brush on, Brush off, double f)
        {
            if (f <= 0) return off;
            if (f >= 1) return on;
            if (on is not SolidColorBrush a || off is not SolidColorBrush b) return f >= 0.5 ? on : off;
            var key = (a.Color, b.Color);
            if (!Steps.TryGetValue(key, out var arr))
            {
                arr = new Brush[32];
                for (int i = 0; i < arr.Length; i++)
                {
                    double t = (i + 1) / (double)(arr.Length + 1);
                    byte L(byte x, byte y) => (byte)Math.Round(y + (x - y) * t);
                    var br = new SolidColorBrush(Color.FromArgb(L(a.Color.A, b.Color.A), L(a.Color.R, b.Color.R), L(a.Color.G, b.Color.G), L(a.Color.B, b.Color.B)));
                    br.Freeze();
                    arr[i] = br;
                }
                Steps[key] = arr;
            }
            return arr[Math.Clamp((int)(f * arr.Length), 0, arr.Length - 1)];
        }
    }
}
