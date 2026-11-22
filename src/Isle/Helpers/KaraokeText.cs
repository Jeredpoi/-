using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Isle.Services;

namespace Isle.Helpers
{
    /// <summary>
    /// «Караоке» для строки песни: слова загораются по очереди, как поют. В тексте песни есть время только
    /// начала строк, поэтому темп внутри строки оцениваем: длина строки в буквах и время до следующей строки.
    /// </summary>
    public static class KaraokeText
    {
        /// <summary>Разбить строку на слова (Run на каждое слово с пробелом после него).</summary>
        public static void Set(TextBlock tb, string text, Brush off)
        {
            tb.Inlines.Clear();
            tb.Tag = null;
            if (string.IsNullOrEmpty(text)) return;
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var ends = new List<int>(); int acc = 0;
            for (int i = 0; i < words.Length; i++)
            {
                tb.Inlines.Add(new Run(words[i] + (i < words.Length - 1 ? " " : "")) { Foreground = off });
                acc += words[i].Length + 1; ends.Add(acc);
            }
            tb.Tag = (ends, acc);
        }

        /// <summary>Подсветить слова до доли p (0..1) строки.</summary>
        public static void Update(TextBlock tb, double p, Brush on, Brush off)
        {
            if (tb.Tag is not ValueTuple<List<int>, int> t) return;
            var (ends, total) = t;
            int k = 0;
            foreach (var inline in tb.Inlines)
            {
                if (inline is not Run r || k >= ends.Count) break;
                // слово загорается, когда до его середины «допели»
                double mid = (ends[k] - (k == 0 ? ends[k] : ends[k] - ends[k - 1]) / 2.0) / total;
                var want = p >= mid ? on : off;
                if (!ReferenceEquals(r.Foreground, want)) r.Foreground = want;
                k++;
            }
        }

        /// <summary>Строка и доля пропетого в ней на позиции pos.</summary>
        public static (int index, double progress) Position(List<LyricLine> lines, TimeSpan pos)
        {
            if (lines == null || lines.Count == 0) return (-1, 0);
            var p = pos + TimeSpan.FromMilliseconds(250);
            int i = lines.FindLastIndex(l => l.At <= p);
            if (i < 0) return (-1, 0);
            double gap = i + 1 < lines.Count ? (lines[i + 1].At - lines[i].At).TotalSeconds : 4;
            int chars = Math.Max(1, lines[i].Text.Length);
            // поют обычно быстрее, чем до следующей строки: не дольше 90% паузы и примерно 0,12 с на букву
            double span = Math.Max(0.6, Math.Min(gap * 0.9, Math.Max(1.2, chars * 0.12)));
            return (i, Math.Clamp((p - lines[i].At).TotalSeconds / span, 0, 1));
        }
    }
}
