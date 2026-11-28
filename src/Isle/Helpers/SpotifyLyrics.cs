using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Isle.Services;

namespace Isle.Helpers
{
    /// <summary>
    /// Кисти одного цвета с разной прозрачностью (256 штук, создаются по мере надобности и замораживаются).
    /// Нужны, чтобы каждый кадр менять яркость слов, не создавая новых объектов.
    /// </summary>
    internal sealed class LyricPalette
    {
        private static readonly Dictionary<Color, LyricPalette> All = new();
        private readonly Brush[] _byAlpha = new Brush[256];
        public readonly Color Color;

        private LyricPalette(Color c) { Color = Color.FromRgb(c.R, c.G, c.B); }

        public static LyricPalette For(Color c)
        {
            c = Color.FromRgb(c.R, c.G, c.B);
            if (!All.TryGetValue(c, out var p)) All[c] = p = new LyricPalette(c);
            return p;
        }

        /// <summary>Цвет текста из темы («Fg»); вне дерева (стенд) — светлый по умолчанию.</summary>
        public static LyricPalette FromTheme(FrameworkElement owner)
        {
            var b = owner?.TryFindResource("Fg") as SolidColorBrush ?? Application.Current?.TryFindResource("Fg") as SolidColorBrush;
            return For(b?.Color ?? Color.FromRgb(0xED, 0xED, 0xF2));
        }

        public Brush this[double alpha] => Get((int)Math.Round(Math.Clamp(alpha, 0, 1) * 255));

        public Brush Get(int a)
        {
            a = Math.Clamp(a, 0, 255);
            var b = _byAlpha[a];
            if (b == null)
            {
                var s = new SolidColorBrush(Color.FromArgb((byte)a, Color.R, Color.G, Color.B));
                s.Freeze();
                _byAlpha[a] = b = s;
            }
            return b;
        }

        public Brush Full => Get(255);
    }

    /// <summary>
    /// Раскладка слов строки «как текст»: слова (группы слогов) идут подряд с пробелом и переносятся по ширине.
    /// Нужна вместо одного TextBlock, потому что каждое слово/слог в Spicy-караоке — отдельный элемент:
    /// у него своя заливка-градиент, свой подъём и своё свечение. Right — строки прижаты вправо (второй голос),
    /// NoWrap — всё в одну строку (пилюля). Позиции слов запоминаются для прокрутки пилюли за поющимся словом.
    /// </summary>
    internal sealed class WordFlow : Panel
    {
        public bool Right, NoWrap;
        public double Space = 4;
        private double[] _x = Array.Empty<double>();
        public double ContentWidth { get; private set; }

        public double ChildX(int i) => i >= 0 && i < _x.Length ? _x[i] : 0;

        protected override Size MeasureOverride(Size avail)
        {
            double maxW = NoWrap || double.IsInfinity(avail.Width) ? double.PositiveInfinity : avail.Width;
            var inf = new Size(double.PositiveInfinity, double.PositiveInfinity);
            double x = 0, y = 0, rowH = 0, widest = 0;
            var ch = InternalChildren;
            for (int i = 0; i < ch.Count; i++)
            {
                var c = ch[i];
                c.Measure(inf);
                var d = c.DesiredSize;
                double add = (x > 0 ? Space : 0) + d.Width;
                if (x > 0 && x + add > maxW) { widest = Math.Max(widest, x); y += rowH; x = 0; rowH = 0; add = d.Width; }
                x += add; rowH = Math.Max(rowH, d.Height);
            }
            widest = Math.Max(widest, x);
            ContentWidth = widest;
            return new Size(double.IsInfinity(maxW) ? widest : Math.Min(widest, maxW), y + rowH);
        }

        protected override Size ArrangeOverride(Size fin)
        {
            var ch = InternalChildren;
            int n = ch.Count;
            if (_x.Length < n) _x = new double[n];
            double maxW = NoWrap ? double.PositiveInfinity : fin.Width;
            int i = 0; double y = 0;
            while (i < n)
            {
                int j = i; double w = 0, h = 0;
                while (j < n)
                {
                    var d = ch[j].DesiredSize;
                    double add = (j > i ? Space : 0) + d.Width;
                    if (j > i && w + add > maxW) break;
                    w += add; h = Math.Max(h, d.Height); j++;
                }
                double x = Right && !NoWrap ? Math.Max(0, fin.Width - w) : 0;
                for (int k = i; k < j; k++)
                {
                    var d = ch[k].DesiredSize;
                    ch[k].Arrange(new Rect(x, y + (h - d.Height), d.Width, d.Height));
                    _x[k] = x;
                    x += d.Width + Space;
                }
                y += h; i = j;
            }
            return fin;
        }
    }

    /// <summary>Внешний вид караоке-строки: размер шрифта, подъём слов, тусклость не спетого, мягкость края заливки.</summary>
    internal sealed class KaraokeStyle
    {
        public double FontSize = 16;
        public FontWeight Weight = FontWeights.Bold;
        /// <summary>Подъём слова, px: не спетое стоит на +Lift, поющееся поднимается до −Lift и потом оседает в 0.</summary>
        public double Lift = 1.5;
        /// <summary>Яркость ещё не спетого текста текущей строки.</summary>
        public double Dim = 0.35;
        /// <summary>Ширина размытого края заливки — доля ширины слова.</summary>
        public double Feather = 0.2;
        /// <summary>Слог тянется дольше — буквы идут «волной» (каждая подрастает и вспыхивает по очереди).</summary>
        public double HoldLetters = 0.9;
        public double LetterScale = 1.08;
        public LyricPalette Pal = LyricPalette.For(Color.FromRgb(0xED, 0xED, 0xF2));
    }

    /// <summary>
    /// Строка караоке «как Spicy Lyrics»: внутри каждого слова/слога слева направо плавно едет заливка —
    /// спетая часть ярко-белая, не спетая тусклая, между ними мягкий край (~20% ширины слова). Слова чуть
    /// поднимаются, пока поются, и оседают обратно; протяжные слоги (≥0,9 с) разбиты на буквы, и те по очереди
    /// подрастают и вспыхивают. Без разметки по словам время слов раскладывается по числу букв.
    ///
    /// Память выделяется только в <see cref="Build"/> (раз на строку) и один раз на слог при первой заливке
    /// (его LinearGradientBrush); в кадре меняются лишь смещения двух GradientStop и свойства-числа.
    /// </summary>
    internal sealed class KaraokeLine
    {
        private sealed class Syl
        {
            public FrameworkElement Elem;
            public TextBlock Tb;                 // обычный слог
            public TextBlock[] Letters;          // протяжный слог — по буквам
            public ScaleTransform[] LScale;
            public TranslateTransform Lift;
            public LinearGradientBrush Grad;     // создаётся при первой заливке и дальше только «двигается»
            public GradientStop A, B;
            public double Ws, We, Y;
            public int Group, InGroup;
        }

        private struct Seg { public string Text; public double Ws, We; public bool NewWord; }

        public readonly WordFlow Flow;
        private readonly KaraokeStyle _st;
        private Syl[] _syl = Array.Empty<Syl>();
        private int _n;
        private bool _flat = true;

        /// <summary>После <see cref="Paint"/>: слог, которому положено свечение, его яркость и правый край поющегося слога (для пилюли).</summary>
        public FrameworkElement GlowTarget { get; private set; }
        public double GlowOpacity { get; private set; }
        public double ActiveRight { get; private set; }

        public KaraokeLine(KaraokeStyle st, bool noWrap = false, bool right = false)
        {
            _st = st;
            Flow = new WordFlow { NoWrap = noWrap, Right = right, Space = Math.Round(st.FontSize * 0.28, 1) };
            TextElement.SetFontSize(Flow, st.FontSize);
            TextElement.SetFontWeight(Flow, st.Weight);
        }

        public bool Right { get => Flow.Right; set { if (Flow.Right != value) { Flow.Right = value; Flow.InvalidateArrange(); } } }

        internal static double Smooth(double x) { x = Math.Clamp(x, 0, 1); return x * x * (3 - 2 * x); }
        private static double EaseOut(double x) { x = Math.Clamp(x, 0, 1); return 1 - (1 - x) * (1 - x) * (1 - x); }

        /// <summary>Разложить строку на слоги-элементы. Выделяет память — вызывать при смене строки, не каждый кадр.</summary>
        public void Build(LyricLine line, TimeSpan? next)
        {
            Flow.Children.Clear();
            _n = 0; _flat = true;
            GlowTarget = null; GlowOpacity = 0; ActiveRight = 0;
            var text = line?.Text?.Trim() ?? "";
            if (text.Length == 0) return;

            var segs = line.Words != null && line.Words.Count > 0 ? FromWords(line, text, next) : Synthesize(line, text, next);
            if (_syl.Length < segs.Count) _syl = new Syl[segs.Count];
            StackPanel group = null;
            int groupIdx = -1, inGroup = 0;
            foreach (var s in segs)
            {
                if (group == null || s.NewWord)
                {
                    group = new StackPanel { Orientation = Orientation.Horizontal };
                    Flow.Children.Add(group);
                    groupIdx++; inGroup = 0;
                }
                var syl = new Syl { Ws = s.Ws, We = s.We, Lift = new TranslateTransform(), Group = groupIdx, InGroup = inGroup++ };
                int len = s.Text.Length;
                if (s.We - s.Ws >= _st.HoldLetters && len >= 2 && len <= 16)
                {
                    var sp = new StackPanel { Orientation = Orientation.Horizontal };
                    syl.Letters = new TextBlock[len];
                    syl.LScale = new ScaleTransform[len];
                    for (int i = 0; i < len; i++)
                    {
                        var sc = new ScaleTransform(1, 1);
                        var tb = new TextBlock { Text = s.Text[i].ToString(), Foreground = _st.Pal.Full, RenderTransformOrigin = new Point(0.5, 0.8), RenderTransform = sc };
                        syl.Letters[i] = tb; syl.LScale[i] = sc;
                        sp.Children.Add(tb);
                    }
                    syl.Elem = sp;
                }
                else
                {
                    syl.Tb = new TextBlock { Text = s.Text, Foreground = _st.Pal.Full };
                    syl.Elem = syl.Tb;
                }
                syl.Elem.RenderTransform = syl.Lift;
                group.Children.Add(syl.Elem);
                _syl[_n++] = syl;
            }
        }

        /// <summary>Слоги из разметки сервиса: куски текста строки с их временем; пробел между кусками — новое слово.</summary>
        private static List<Seg> FromWords(LyricLine line, string text, TimeSpan? next)
        {
            var list = new List<Seg>(line.Words.Count);
            double lineEnd = (line.End ?? next ?? line.At + TimeSpan.FromSeconds(4)).TotalSeconds;
            int pos = 0, wc = line.Words.Count;
            for (int w = 0; w < wc && pos < text.Length; w++)
            {
                var word = (line.Words[w].Text ?? "").Trim();
                if (word.Length == 0) continue;
                int at = text.IndexOf(word, pos, StringComparison.Ordinal);
                if (at < 0) at = pos; // текст слова чуть отличается от строки — идём по порядку
                int len = Math.Min(word.Length, text.Length - at);
                if (len <= 0) break;
                string piece = text.Substring(at, len);
                bool newWord = list.Count == 0;
                if (at > pos)
                {
                    // между слогами: знаки до первого пробела — хвост прошлого слога, после пробела — начало этого
                    string between = text.Substring(pos, at - pos);
                    int sp = -1;
                    for (int i = 0; i < between.Length; i++) if (char.IsWhiteSpace(between[i])) { sp = i; break; }
                    if (sp < 0) AppendToLast(list, between);
                    else
                    {
                        newWord = true;
                        if (sp > 0) AppendToLast(list, between.Substring(0, sp));
                        piece = between.Substring(sp).Trim() + piece;
                    }
                }
                double ws = line.Words[w].At.TotalSeconds;
                double we = w + 1 < wc ? line.Words[w + 1].At.TotalSeconds : lineEnd;
                if (line.Words[w].End is TimeSpan wEnd && wEnd.TotalSeconds > ws)
                    we = w + 1 < wc ? Math.Min(we, wEnd.TotalSeconds) : wEnd.TotalSeconds; // точный конец слога (Spicy)
                else
                    we = Math.Min(we, ws + Math.Max(0.35, len * 0.3 + 0.4)); // конец неизвестен — не тянем бесконечно
                if (we <= ws) we = ws + 0.2;
                list.Add(new Seg { Text = piece, Ws = ws, We = we, NewWord = newWord });
                pos = at + len;
            }
            if (pos < text.Length) AppendToLast(list, text.Substring(pos).TrimEnd());
            return list;
        }

        private static void AppendToLast(List<Seg> list, string s)
        {
            if (list.Count == 0 || s.Length == 0) return;
            var last = list[^1]; last.Text += s; list[^1] = last;
        }

        /// <summary>
        /// Без разметки: время строки делим между словами по числу букв, чтобы заливка всё равно текла.
        /// Строку поют почти всю паузу до следующей (85%), но не быстрее ~0,22 с на букву.
        /// </summary>
        private static List<Seg> Synthesize(LyricLine line, string text, TimeSpan? next)
        {
            var words = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            var list = new List<Seg>(words.Length);
            double start = line.At.TotalSeconds;
            double gap = next.HasValue ? (next.Value - line.At).TotalSeconds : 4;
            int letters = 0;
            foreach (var w in words) letters += w.Length;
            letters = Math.Max(1, letters);
            double span = line.End.HasValue && line.End.Value > line.At ? (line.End.Value - line.At).TotalSeconds
                : Math.Max(0.8, Math.Min(gap * 0.85, Math.Max(1.5, letters * 0.22)));
            int k = 0;
            foreach (var w in words)
            {
                double ws = start + span * k / letters;
                k += w.Length;
                list.Add(new Seg { Text = w, Ws = ws, We = start + span * k / letters, NewWord = true });
            }
            return list;
        }

        /// <summary>
        /// Кадр. active — строка текущая: заливка и подъём по времени; since — сколько секунд она уже текущая
        /// (не спетые слова мягко «приседают» на +Lift). active=false — строка только что кончилась: всё
        /// дозаливается и оседает; когда осело, владелец зовёт <see cref="Flat"/>.
        /// </summary>
        public void Paint(double now, bool active, double since)
        {
            if (_n == 0) return;
            _flat = false;
            GlowTarget = null; GlowOpacity = 0;
            var pal = _st.Pal;
            double L = _st.Lift, f = _st.Feather, dim = _st.Dim;
            double pre = active ? Smooth(since / 0.25) : 0;
            Syl glowSyl = null;
            for (int i = 0; i < _n; i++)
            {
                var s = _syl[i];
                double p = active ? (now - s.Ws) / Math.Max(0.01, s.We - s.Ws) : 1;

                // заливка
                if (s.Letters != null)
                {
                    int m = s.Letters.Length;
                    double edge = p * (1 + f) - f * 0.5;
                    for (int j = 0; j < m; j++)
                    {
                        double c = (j + 0.5) / m;
                        double lit = Math.Clamp((edge - c) / f + 0.5, 0, 1);
                        var br = lit >= 1 ? pal.Full : pal[dim + (1 - dim) * lit];
                        if (!ReferenceEquals(s.Letters[j].Foreground, br)) s.Letters[j].Foreground = br;
                        // волна: буква подрастает, пока через неё идёт заливка, и возвращается
                        double lp = Math.Clamp(p * m - j, 0, 1);
                        double sc = active ? 1 + (_st.LetterScale - 1) * Math.Sin(Math.PI * lp) : 1;
                        var t = s.LScale[j];
                        if (Math.Abs(t.ScaleX - sc) > 0.001) { t.ScaleX = sc; t.ScaleY = sc; }
                    }
                }
                else if (p <= 0) SetFg(s.Tb, pal[dim]);
                else if (p >= 1) SetFg(s.Tb, pal.Full);
                else
                {
                    if (s.Grad == null)
                    {
                        s.A = new GradientStop(Color.FromArgb(255, pal.Color.R, pal.Color.G, pal.Color.B), 0);
                        s.B = new GradientStop(Color.FromArgb((byte)Math.Round(dim * 255), pal.Color.R, pal.Color.G, pal.Color.B), 0);
                        s.Grad = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
                        s.Grad.GradientStops.Add(s.A); s.Grad.GradientStops.Add(s.B);
                    }
                    // край заливки проходит от −f до 1+f: в начале слово целиком тусклое, в конце — целиком яркое
                    double e = p * (1 + f);
                    if (Math.Abs(s.A.Offset - (e - f)) > 0.0005) s.A.Offset = e - f;
                    if (Math.Abs(s.B.Offset - e) > 0.0005) s.B.Offset = e;
                    SetFg(s.Tb, s.Grad);
                }

                // подъём
                double y;
                if (!active) y = now - s.We < 0.45 && now >= s.We ? -L * (1 - Smooth((now - s.We) / 0.45)) : 0;
                else if (p <= 0) y = L * pre;
                else if (p < 1) y = L - 2 * L * EaseOut(p);
                else y = -L * (1 - Smooth((now - s.We) / 0.45));
                if (Math.Abs(y - s.Y) > 0.01) { s.Y = y; s.Lift.Y = y; }

                // свечение — у поющегося слога; в паузе между слогами — тает на последнем спетом
                if (active && p > 0 && p < 1) { glowSyl = s; GlowOpacity = 0.35 + 0.5 * Smooth(p); }
                else if (active && glowSyl == null && p >= 1 && now - s.We < 0.35) { glowSyl = s; GlowOpacity = 0.85 * (1 - (now - s.We) / 0.35); }
            }
            if (glowSyl != null)
            {
                GlowTarget = glowSyl.Elem;
                double x = Flow.ChildX(glowSyl.Group);
                if (glowSyl.Elem.Parent is Panel g)
                    for (int k = 0; k < glowSyl.InGroup && k < g.Children.Count; k++) x += ((FrameworkElement)g.Children[k]).ActualWidth;
                ActiveRight = x + glowSyl.Elem.ActualWidth;
            }
        }

        /// <summary>Строка не текущая: всё одним ярким цветом (тусклость — прозрачностью строки), без подъёма и волны.</summary>
        public void Flat()
        {
            GlowTarget = null; GlowOpacity = 0;
            if (_flat) return;
            _flat = true;
            var full = _st.Pal.Full;
            for (int i = 0; i < _n; i++)
            {
                var s = _syl[i];
                if (s.Letters != null)
                    for (int j = 0; j < s.Letters.Length; j++) { SetFg(s.Letters[j], full); s.LScale[j].ScaleX = s.LScale[j].ScaleY = 1; }
                else SetFg(s.Tb, full);
                if (s.Y != 0) { s.Y = 0; s.Lift.Y = 0; }
            }
        }

        /// <summary>Конец последнего слога — после него строка «допета».</summary>
        public double LastEnd => _n > 0 ? _syl[_n - 1].We : 0;

        private static void SetFg(TextBlock tb, Brush b) { if (!ReferenceEquals(tb.Foreground, b)) tb.Foreground = b; }
    }

    /// <summary>Одно свечение на всю строку: переезжает на поющийся слог (на остальных эффектов нет).</summary>
    internal sealed class MovingGlow
    {
        public readonly DropShadowEffect Effect;
        private FrameworkElement _on;

        public MovingGlow(double blur, Color c)
        {
            Effect = new DropShadowEffect { Color = c, ShadowDepth = 0, BlurRadius = blur, Opacity = 0, RenderingBias = RenderingBias.Performance };
        }

        public void Move(FrameworkElement target, double opacity)
        {
            if (!ReferenceEquals(target, _on))
            {
                if (_on != null && ReferenceEquals(_on.Effect, Effect)) _on.Effect = null;
                _on = target;
                if (target != null) target.Effect = Effect;
            }
            opacity = target == null ? 0 : Math.Clamp(opacity, 0, 1);
            if (Math.Abs(Effect.Opacity - opacity) > 0.01) Effect.Opacity = opacity;
        }
    }

    /// <summary>
    /// Три точки проигрыша, как в Spotify: за время паузы точки по очереди наливаются светом и чуть растут,
    /// вся группа мягко «дышит», а к концу паузы раздувается и исчезает — сейчас начнётся строка.
    /// </summary>
    internal sealed class GapDots : StackPanel
    {
        private readonly Ellipse[] _dots = new Ellipse[3];
        private readonly ScaleTransform[] _scales = new ScaleTransform[3];
        private readonly ScaleTransform _group = new(1, 1);

        public GapDots(double size, double gap, Brush fill)
        {
            Orientation = Orientation.Horizontal;
            HorizontalAlignment = HorizontalAlignment.Left;
            VerticalAlignment = VerticalAlignment.Center;
            RenderTransformOrigin = new Point(0.15, 0.5);
            RenderTransform = _group;
            IsHitTestVisible = false;
            for (int i = 0; i < 3; i++)
            {
                _scales[i] = new ScaleTransform(1, 1);
                _dots[i] = new Ellipse
                {
                    Width = size, Height = size, Fill = fill, Opacity = 0.3,
                    Margin = new Thickness(i == 0 ? 0 : gap, 0, 0, 0),
                    RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _scales[i],
                };
                Children.Add(_dots[i]);
            }
        }

        public void SetFill(Brush b) { foreach (var d in _dots) if (!ReferenceEquals(d.Fill, b)) d.Fill = b; }

        /// <summary>start/end — границы паузы в секундах; end ≤ start — длина неизвестна (точки просто дышат).</summary>
        public void Paint(double now, double start, double end)
        {
            bool known = end > start;
            double p = known ? Math.Clamp((now - start) / (end - start), 0, 1) : 1;
            for (int i = 0; i < 3; i++)
            {
                double f = KaraokeLine.Smooth(p * 3 - i);
                Set(_dots[i], UIElement.OpacityProperty, 0.28 + 0.72 * f);
                double s = 0.78 + 0.34 * f;
                SetScale(_scales[i], s);
            }
            // «дыхание» — около 1,6 с на вдох-выдох
            double breath = 1 + 0.07 * Math.Sin((now - start) * Math.PI * 2 / 1.6);
            double groupOp = 1;
            // появление в начале паузы
            double since = now - start;
            if (since < 0.35) { double k = KaraokeLine.Smooth(since / 0.35); breath *= 0.6 + 0.4 * k; groupOp = k; }
            // конец паузы: точки раздуваются и тают
            if (known)
            {
                double left = end - now;
                if (left < 0.5)
                {
                    double k = KaraokeLine.Smooth(Math.Max(0, left) / 0.5);
                    breath *= 1 + 0.25 * (1 - k);
                    groupOp = Math.Min(groupOp, k);
                }
            }
            SetScale(_group, breath);
            Set(this, UIElement.OpacityProperty, groupOp);
        }

        private static void SetScale(ScaleTransform t, double s)
        {
            if (Math.Abs(t.ScaleX - s) > 0.002) { t.ScaleX = s; t.ScaleY = s; }
        }

        internal static void Set(DependencyObject o, DependencyProperty p, double v)
        {
            if (Math.Abs((double)o.GetValue(p) - v) > 0.002) o.SetValue(p, v);
        }
    }


    /// <summary>
    /// Текст песни списком «как в Spotify / Spicy Lyrics» (колонка справа от плеера на «Главной»):
    /// в текущей строке внутри каждого слова плавно едет заливка, слова чуть поднимаются и светятся, пока поются;
    /// соседние строки тусклые и чуть меньше, дальние ещё и слегка размыты; список плавно едет так,
    /// чтобы текущая строка стояла на верхней трети; в проигрышах — три наливающиеся точки.
    /// Строки «другого голоса» (<see cref="LyricLine.OppositeAligned"/>) — справа.
    ///
    /// Всё обновляется из <see cref="Render"/>, который зовут каждый кадр (FrameClock) только пока колонка видна;
    /// в кадре память не выделяется: элементы строятся один раз в <see cref="SetLines"/>, кисти — из палитры,
    /// у градиентов двигаются только смещения.
    /// Для стенда: <see cref="Still"/> — готовая картинка на заданный момент.
    /// </summary>
    public sealed class LyricsScroller : Grid
    {
        /// <summary>Где стоит текущая строка: доля высоты окна сверху.</summary>
        public double Anchor { get; set; } = 0.3;
        public double LineFontSize { get; set; } = 16;
        /// <summary>Масштаб нетекущих строк (текущая — 1).</summary>
        public double DimScale { get; set; } = 0.9;
        private const double LineGap = 12, GapRowHeight = 30, SettleTime = 0.6;

        private readonly Canvas _viewport;
        private readonly StackPanel _stack;
        private readonly TranslateTransform _shift = new();
        private readonly MovingGlow _glow;

        private sealed class Item
        {
            public FrameworkElement Elem;
            public KaraokeLine Line;      // строка с текстом
            public GapDots Dots;          // или проигрыш
            public ScaleTransform Scale;
            public double Start, End;     // время, с (End ≤ Start — неизвестно)
            public double Op = -1, Sc = -1, H = -1;
            public int Fx = -1;           // какой эффект сейчас стоит: 0 — нет, 1/2 — размытие
            public bool Shown = true;
        }

        private Item[] _items = Array.Empty<Item>();
        private List<LyricLine> _lines;
        private bool _intro;
        private int _cur = -2, _settle = -1;
        private double _activeFor, _settleLeft;
        private double _y;
        private bool _snapNext = true;
        private LyricPalette _pal;
        private KaraokeStyle _style;

        private static readonly Effect Blur1 = MakeBlur(1.2), Blur2 = MakeBlur(2.4);

        public LyricsScroller()
        {
            _viewport = new Canvas { ClipToBounds = true };
            _stack = new StackPanel { RenderTransform = _shift };
            _viewport.Children.Add(_stack);
            _viewport.SizeChanged += (_, __) => { _stack.Width = _viewport.ActualWidth; };
            // края уходят в прозрачность — строки появляются и исчезают мягко, как в Spotify
            var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            mask.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0));
            mask.GradientStops.Add(new GradientStop(Color.FromArgb(255, 0, 0, 0), 0.14));
            mask.GradientStops.Add(new GradientStop(Color.FromArgb(255, 0, 0, 0), 0.78));
            mask.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1));
            mask.Freeze();
            _viewport.OpacityMask = mask;
            Children.Add(_viewport);
            _glow = new MovingGlow(10, Colors.White);
            IsVisibleChanged += (_, __) => { if (IsVisible) Snap(); };
        }

        public bool HasLines => _items.Length > 0;

        /// <summary>Следующий <see cref="Render"/> поставит всё на места сразу, без анимации (после показа, смены трека).</summary>
        public void Snap() => _snapNext = true;

        /// <summary>
        /// Для стенда: колонка заданного размера с текстом lines, показанная на момент pos (без анимации).
        /// Результат уже измерен и расставлен — можно сразу отдавать в RenderTargetBitmap.
        /// </summary>
        public static LyricsScroller Still(List<LyricLine> lines, TimeSpan pos, double width = 182, double height = 220)
        {
            var s = new LyricsScroller { Width = width, Height = height };
            s.SetLines(lines);
            for (int i = 0; i < 2; i++) // второй проход — с настоящими высотами строк
            {
                s.Measure(new Size(width, height));
                s.Arrange(new Rect(0, 0, width, height));
                s.Render(pos, snap: true);
            }
            s.Measure(new Size(width, height));
            s.Arrange(new Rect(0, 0, width, height));
            return s;
        }

        /// <summary>Построить строки (один раз на текст). null или пустой список — очистить.</summary>
        public void SetLines(List<LyricLine> lines)
        {
            _glow.Move(null, 0);
            _lines = lines;
            _stack.Children.Clear();
            _cur = -2; _settle = -1;
            _y = 0;
            _shift.Y = 0;
            _snapNext = true;
            _pal = LyricPalette.FromTheme(this);
            _style = new KaraokeStyle { FontSize = LineFontSize, Weight = FontWeights.Bold, Lift = 1.5, Pal = _pal };
            if (lines == null || lines.Count == 0) { _items = Array.Empty<Item>(); return; }

            // длинное вступление — сверху точки, как у Spotify перед первой строкой
            _intro = lines[0].At >= TimeSpan.FromSeconds(3);
            var items = new List<Item>(lines.Count + 1);
            if (_intro) items.Add(MakeGap(0, lines[0].At.TotalSeconds));
            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                var next = KaraokeText.NextAt(lines, i);
                if (string.IsNullOrWhiteSpace(l.Text))
                {
                    items.Add(MakeGap(l.At.TotalSeconds, next?.TotalSeconds ?? 0));
                    continue;
                }
                var kl = new KaraokeLine(_style, noWrap: false, right: l.OppositeAligned);
                kl.Build(l, next);
                var fl = kl.Flow;
                // запас по краю: подъём, свечение и выносные элементы букв не упираются в обрезку
                fl.Margin = l.OppositeAligned ? new Thickness(8, 0, 2, LineGap) : new Thickness(0, 0, 8, LineGap);
                var sc = new ScaleTransform(DimScale, DimScale);
                fl.RenderTransformOrigin = new Point(l.OppositeAligned ? 1 : 0, 0.5);
                fl.RenderTransform = sc;
                items.Add(new Item { Elem = fl, Line = kl, Scale = sc, Start = l.At.TotalSeconds, End = (next ?? l.At + TimeSpan.FromSeconds(4)).TotalSeconds });
            }
            _items = items.ToArray();
            foreach (var it in _items) _stack.Children.Add(it.Elem);
        }

        private Item MakeGap(double start, double end)
        {
            var dots = new GapDots(8, 7, _pal.Full);
            var box = new Border { Height = 0, Child = dots, ClipToBounds = false, Padding = new Thickness(2, 0, 0, 0) };
            return new Item { Elem = box, Dots = dots, Start = start, End = end, H = 0 };
        }

        private static Effect MakeBlur(double r) { var b = new BlurEffect { Radius = r, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance }; b.Freeze(); return b; }

        /// <summary>Номер элемента списка для позиции (с учётом точек вступления).</summary>
        private int ItemAt(TimeSpan pos)
        {
            int li = KaraokeText.Index(_lines, pos);
            if (li < 0) return _intro ? 0 : -1;
            return li + (_intro ? 1 : 0);
        }

        /// <summary>
        /// Кадр: позиция трека → заливка слов, тусклость/размытие соседей, прокрутка.
        /// dt — длина кадра, с; snap — сразу итоговое состояние (для стенда и после показа).
        /// </summary>
        public void Render(TimeSpan pos, double dt = 1 / 60.0, bool snap = false)
        {
            if (_items.Length == 0) return;
            if (_snapNext) snap = true;
            if (Math.Abs(_stack.Width - _viewport.ActualWidth) > 0.5 && _viewport.ActualWidth > 0) _stack.Width = _viewport.ActualWidth;
            // для мгновенной расстановки нужны настоящие высоты строк — досчитываем раскладку прямо сейчас
            if (snap && (_viewport.ActualHeight <= 0 || !_stack.IsArrangeValid)) UpdateLayout();
            if (_viewport.ActualHeight > 0 && _stack.IsArrangeValid) _snapNext = false;
            dt = Math.Clamp(dt, 0, 0.1);

            double now = pos.TotalSeconds;
            int ci = ItemAt(pos);
            if (ci != _cur)
            {
                // ушедшая строка ещё немного дозаливается и оседает, потом становится ровной
                if (_settle >= 0 && _settle < _items.Length) _items[_settle].Line?.Flat();
                _settle = _cur >= 0 && _cur < _items.Length && _items[_cur].Line != null ? _cur : -1;
                _settleLeft = snap ? 0 : SettleTime;
                _cur = ci;
                _activeFor = 0;
                UpdateEffects();
            }
            _activeFor += snap ? 10 : dt;

            // сглаживание «x += (цель − x)·k», одинаковое на любой частоте кадров; 0,27 за 1/30 с ≈ 250 мс на смену
            double k = snap ? 1 : 1 - Math.Pow(1 - 0.27, dt * 30);
            double kScroll = snap ? 1 : 1 - Math.Pow(1 - 0.2, dt * 30);
            double kFast = snap ? 1 : 1 - Math.Pow(1 - 0.3, dt * 30);

            // строка, которая только что кончилась
            if (_settle >= 0 && _settle != _cur)
            {
                _settleLeft -= dt;
                var sl = _items[_settle].Line;
                if (_settleLeft > 0) sl.Paint(now, false, 0); else { sl.Flat(); _settle = -1; }
            }

            double viewH = _viewport.ActualHeight, top = 0, curTop = 0;
            FrameworkElement glowAt = null; double glowOp = 0;
            for (int i = 0; i < _items.Length; i++)
            {
                var it = _items[i];
                int d = _cur < 0 ? i + 1 : Math.Abs(i - _cur);
                if (i == _cur) curTop = top;

                if (it.Dots != null)
                {
                    // проигрыш виден, только пока он идёт; остальное время схлопнут
                    double hT = i == _cur ? GapRowHeight : 0;
                    double h = it.H + (hT - it.H) * kFast;
                    if (Math.Abs(h - hT) < 0.3) h = hT;
                    if (Math.Abs(h - it.H) > 0.05) { it.H = h; it.Elem.Height = h; }
                    GapDots.Set(it.Elem, OpacityProperty, Math.Clamp(h / GapRowHeight, 0, 1));
                    if (h > 0.5) it.Dots.Paint(now, it.Start, it.End);
                    top += h;
                    continue;
                }

                double opT = i == _cur ? 1 : d == 1 ? 0.5 : d == 2 ? 0.38 : 0.3;
                double scT = i == _cur ? 1 : DimScale;
                double op = it.Op < 0 ? opT : it.Op + (opT - it.Op) * k;
                double sc = it.Sc < 0 ? scT : it.Sc + (scT - it.Sc) * k;
                if (Math.Abs(op - opT) < 0.003) op = opT;
                if (Math.Abs(sc - scT) < 0.0005) sc = scT;
                if (op != it.Op) { it.Op = op; it.Elem.Opacity = op; }
                if (sc != it.Sc) { it.Sc = sc; it.Scale.ScaleX = sc; it.Scale.ScaleY = sc; }
                if (i == _cur)
                {
                    it.Line.Paint(now, true, _activeFor);
                    glowAt = it.Line.GlowTarget; glowOp = it.Line.GlowOpacity;
                }
                top += it.Elem.ActualHeight + LineGap;
            }
            _glow.Move(glowAt, glowOp);

            // прокрутка: текущая строка — на верхней трети
            double target = viewH * Anchor - (_cur >= 0 ? curTop : 0);
            double y = _y + (target - _y) * kScroll;
            if (Math.Abs(y - target) < 0.2) y = target;
            if (y != _y) { _y = y; _shift.Y = Math.Round(y * 2) / 2; }

            // невидимые строки не рисуем вовсе (и их размытие тоже)
            double from = -_y - 40, to = -_y + viewH + 40, yy = 0;
            for (int i = 0; i < _items.Length; i++)
            {
                var it = _items[i];
                double h = it.Dots != null ? it.H : it.Elem.ActualHeight + LineGap;
                bool show = yy + h >= from && yy <= to;
                if (show != it.Shown) { it.Shown = show; it.Elem.Visibility = show ? Visibility.Visible : Visibility.Hidden; }
                yy += h;
            }
        }

        /// <summary>Лёгкое размытие у дальних строк (меняется только при смене строки).</summary>
        private void UpdateEffects()
        {
            for (int i = 0; i < _items.Length; i++)
            {
                var it = _items[i];
                if (it.Line == null) continue;
                int d = _cur < 0 ? i + 1 : Math.Abs(i - _cur);
                int fx = d <= 1 ? 0 : d == 2 ? 1 : 2;
                if (fx == it.Fx) continue;
                it.Fx = fx;
                it.Elem.Effect = fx switch { 1 => Blur1, 2 => Blur2, _ => null };
            }
        }
    }

    /// <summary>
    /// Строка песни в свёрнутой пилюле «С текстом» — та же заливка, что на «Главной» (Spicy Lyrics), только мельче:
    /// подъём 1 px, свечение 6 px. Смена строк — перекрёстное затухание со сдвигом вверх; длинная строка
    /// едет влево за поющимся словом; в проигрыше — три наливающиеся точки. Две «ячейки» по очереди.
    /// Для стенда: <see cref="Still"/> или <c>Render(lines, pos, "Название — Исполнитель", snap: true)</c>.
    /// </summary>
    public sealed class PillLyric : Grid
    {
        private sealed class Slot
        {
            public Grid Root;
            public TextBlock Plain;      // название трека (без текста песни)
            public Canvas Box;           // окно караоке-строки
            public KaraokeLine Line;
            public TranslateTransform Scroll;
            public GapDots Dots;
            public TranslateTransform Shift;
            public int Kind;             // 0 — название, 1 — строка песни, 2 — точки
            public double Start, End, X;
            public double Op = -1, Y = double.NaN;
            public int Mask = -1;
        }

        private readonly Slot _a, _b;
        private Slot _cur, _old;
        private double _swap = 1, _activeFor;
        private LyricLine _keyLine;
        private int _keyIdx = int.MinValue;
        private string _keyText;
        private readonly LyricPalette _pal = LyricPalette.For(Color.FromRgb(0xEC, 0xEC, 0xF1)); // пилюля всегда тёмная
        private readonly KaraokeStyle _style;
        private readonly MovingGlow _glow = new(6, Colors.White);
        private static readonly Brush FadeRight = MakeFade(false), FadeBoth = MakeFade(true);

        public PillLyric()
        {
            _style = new KaraokeStyle { FontSize = 12, Weight = FontWeights.SemiBold, Lift = 1, Pal = _pal };
            _a = MakeSlot(); _b = MakeSlot();
            Children.Add(_a.Root); Children.Add(_b.Root);
            _cur = _a; _old = _b;
            _b.Root.Visibility = Visibility.Hidden;
        }

        /// <summary>Для стенда: пилюльная строка заданного размера на момент pos (без анимации), уже расставленная.</summary>
        public static PillLyric Still(List<LyricLine> lines, TimeSpan pos, string fallback = "", double width = 300, double height = 18)
        {
            var p = new PillLyric { Width = width, Height = height };
            for (int i = 0; i < 2; i++)
            {
                p.Measure(new Size(width, height));
                p.Arrange(new Rect(0, 0, width, height));
                p.Render(lines, pos, fallback, snap: true);
            }
            p.Measure(new Size(width, height));
            p.Arrange(new Rect(0, 0, width, height));
            return p;
        }

        private Slot MakeSlot()
        {
            var plain = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Foreground = _pal.Full };
            var line = new KaraokeLine(_style, noWrap: true);
            var scroll = new TranslateTransform();
            line.Flow.RenderTransform = scroll;
            var box = new Canvas { Visibility = Visibility.Collapsed };
            box.Children.Add(line.Flow);
            // обрезаем только по бокам: сверху и снизу место для подъёма и свечения
            box.SizeChanged += (_, e) => box.Clip = new RectangleGeometry(new Rect(0, -12, e.NewSize.Width, e.NewSize.Height + 24));
            var dots = new GapDots(5, 4, _pal.Full) { Visibility = Visibility.Collapsed, Margin = new Thickness(1, 0, 0, 0) };
            var shift = new TranslateTransform();
            var root = new Grid { RenderTransform = shift };
            root.Children.Add(plain); root.Children.Add(box); root.Children.Add(dots);
            return new Slot { Root = root, Plain = plain, Box = box, Line = line, Scroll = scroll, Dots = dots, Shift = shift };
        }

        private static Brush MakeFade(bool left)
        {
            var b = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            b.GradientStops.Add(new GradientStop(Color.FromArgb(left ? (byte)0 : (byte)255, 0, 0, 0), 0));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(255, 0, 0, 0), left ? 0.08 : 0.0));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(255, 0, 0, 0), 0.9));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1));
            b.Freeze();
            return b;
        }

        /// <summary>Сбросить показанное (смена вида пилюли): следующий кадр покажет строку без перехода.</summary>
        public void Reset()
        {
            _keyLine = null; _keyIdx = int.MinValue; _keyText = null; _swap = 1;
            _glow.Move(null, 0);
            _a.Line.Build(null, null); _b.Line.Build(null, null);
            _a.Plain.Text = ""; _b.Plain.Text = "";
        }

        /// <summary>
        /// Кадр пилюли. lines — текст (может быть null), pos — позиция трека; fallback — что показать без строки
        /// (до первой строки или без текста). dt — длина кадра, с; snap — без анимации перехода.
        /// </summary>
        public void Render(List<LyricLine> lines, TimeSpan pos, string fallback, double dt = 1 / 60.0, bool snap = false)
        {
            dt = Math.Clamp(dt, 0, 0.1);
            int idx = KaraokeText.Index(lines, pos);
            var line = idx >= 0 ? lines[idx] : null;
            bool changed = line != null ? (!ReferenceEquals(line, _keyLine) || idx != _keyIdx)
                                        : (_keyLine != null || _keyIdx != -1 || !string.Equals(fallback, _keyText, StringComparison.Ordinal));
            if (changed)
            {
                bool first = _keyIdx == int.MinValue;
                _keyLine = line; _keyIdx = line != null ? idx : -1; _keyText = line == null ? fallback : null;
                (_old, _cur) = (_cur, _old);
                Prepare(_cur, lines, idx, line, fallback);
                _swap = snap || first ? 1 : 0;
                _activeFor = 0;
            }
            _activeFor += snap ? 10 : dt;

            double now = pos.TotalSeconds;
            _swap = snap ? 1 : Math.Min(1, _swap + dt / 0.34);
            double e = 1 - Math.Pow(1 - _swap, 3); // плавное торможение
            double eo = Math.Min(1, _swap * 1.8);  // старая строка уходит быстрее, чем приходит новая
            Place(_cur, e, 9 * (1 - e));
            if (_swap < 1) Place(_old, 1 - eo, -8 * eo);
            else if (_old.Root.Visibility != Visibility.Hidden) { _old.Root.Visibility = Visibility.Hidden; _old.Line.Flat(); }
            if (_cur.Root.Visibility != Visibility.Visible) _cur.Root.Visibility = Visibility.Visible;

            if (_swap < 1 && _old.Kind == 1) _old.Line.Paint(now, false, 0);
            if (_swap < 1 && _old.Kind == 2) _old.Dots.Paint(now, _old.Start, _old.End);
            if (_cur.Kind == 2) { _cur.Dots.Paint(now, _cur.Start, _cur.End); _glow.Move(null, 0); }
            else if (_cur.Kind == 1)
            {
                _cur.Line.Paint(now, true, _activeFor);
                _glow.Move(_cur.Line.GlowTarget, _cur.Line.GlowOpacity);
                ScrollToSinging(_cur, dt, snap);
            }
            else _glow.Move(null, 0);
        }

        /// <summary>Строка шире пилюли — едет влево так, чтобы поющееся слово оставалось на виду.</summary>
        private static void ScrollToSinging(Slot s, double dt, bool snap)
        {
            var fl = s.Line.Flow;
            double viewW = s.Box.ActualWidth, h = s.Box.ActualHeight;
            double top = Math.Round((h - fl.DesiredSize.Height) / 2);
            if (Math.Abs(Canvas.GetTop(fl) - top) > 0.4 || double.IsNaN(Canvas.GetTop(fl))) Canvas.SetTop(fl, top);
            double over = Math.Max(0, fl.ContentWidth - viewW);
            double target = over <= 0 ? 0 : Math.Clamp(s.Line.ActiveRight + 28 - viewW, 0, over);
            double k = snap ? 1 : 1 - Math.Pow(1 - 0.15, dt * 30);
            double x = s.X + (target - s.X) * k;
            if (Math.Abs(x - target) < 0.2) x = target;
            if (x != s.X) { s.X = x; s.Scroll.X = -Math.Round(x * 2) / 2; }
            int mask = over <= 0 ? 0 : x > 1 ? 2 : 1;
            if (mask != s.Mask) { s.Mask = mask; s.Box.OpacityMask = mask == 0 ? null : mask == 1 ? FadeRight : FadeBoth; }
        }

        private void Prepare(Slot s, List<LyricLine> lines, int idx, LyricLine line, string fallback)
        {
            s.Kind = line == null ? 0 : string.IsNullOrWhiteSpace(line.Text) ? 2 : 1;
            s.X = 0; s.Scroll.X = 0;
            if (s.Kind == 2)
            {
                s.Start = line.At.TotalSeconds;
                s.End = KaraokeText.NextAt(lines, idx)?.TotalSeconds ?? 0;
            }
            if (s.Kind == 1) s.Line.Build(line, KaraokeText.NextAt(lines, idx));
            else s.Line.Build(null, null);
            s.Plain.Text = s.Kind == 0 ? fallback ?? "" : "";
            s.Plain.Visibility = s.Kind == 0 ? Visibility.Visible : Visibility.Collapsed;
            s.Box.Visibility = s.Kind == 1 ? Visibility.Visible : Visibility.Collapsed;
            s.Dots.Visibility = s.Kind == 2 ? Visibility.Visible : Visibility.Collapsed;
            if (s.Kind == 1) { s.Line.Flow.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); s.Mask = -1; }
        }

        private static void Place(Slot s, double op, double y)
        {
            if (Math.Abs(op - s.Op) > 0.002) { s.Op = op; s.Root.Opacity = op; }
            if (double.IsNaN(s.Y) || Math.Abs(y - s.Y) > 0.05) { s.Y = y; s.Shift.Y = y; }
        }
    }
}
