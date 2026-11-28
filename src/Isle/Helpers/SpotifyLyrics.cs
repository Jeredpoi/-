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
    /// Одна строка песни в TextBlock: Run на каждое слово (или слог Spicy Lyrics) и их время.
    /// <see cref="Paint"/> зажигает слова как в Spotify: спетые — ярко, поющееся — ярче всех, ещё не спетые — тускло.
    /// Без разметки по словам строка горит целиком.
    /// </summary>
    internal sealed class LyricRow
    {
        /// <summary>Яркость ещё не спетых слов текущей строки, спетых и того, что поётся сейчас.</summary>
        public const double Unsung = 0.42, Sung = 0.9, Singing = 1.0;

        public readonly TextBlock Text;
        private Run[] _runs = Array.Empty<Run>();
        private int[] _runWord = Array.Empty<int>(); // какому слову принадлежит Run (-1 — до первого слова)
        private double[] _ws = Array.Empty<double>(), _we = Array.Empty<double>();
        private int _runCount, _words;
        private double _last = double.MinValue;

        public LyricRow(TextBlock tb) { Text = tb; }

        public bool HasWords => _words > 0;

        /// <summary>Разложить строку на слова. Выделяет память — вызывать при смене строки, не каждый кадр.</summary>
        public void Build(LyricLine line, TimeSpan? next)
        {
            Text.Inlines.Clear();
            _runCount = 0; _words = 0; _last = double.MinValue;
            var text = line?.Text ?? "";
            if (text.Length == 0) return;
            if (line.Words == null || line.Words.Count == 0)
            {
                Ensure(1, 0);
                _runs[0] = new Run(text); _runWord[0] = -1; _runCount = 1;
                Text.Inlines.Add(_runs[0]);
                return;
            }

            int wc = line.Words.Count;
            Ensure(wc * 2 + 1, wc);
            double lineEnd = (line.End ?? next ?? line.At + TimeSpan.FromSeconds(4)).TotalSeconds;
            int pos = 0;
            for (int w = 0; w < wc && pos < text.Length; w++)
            {
                var word = (line.Words[w].Text ?? "").Trim();
                if (word.Length == 0) continue;
                int at = text.IndexOf(word, pos, StringComparison.Ordinal);
                if (at < 0) at = pos; // текст слова чуть отличается от строки — идём по порядку
                int len = Math.Min(word.Length, text.Length - at);
                if (len <= 0) break;
                // пробелы и знаки между словами — к предыдущему слову (как оно светится, так и они)
                if (at > pos) AddRun(text.Substring(pos, at - pos), _words - 1);
                double ws = line.Words[w].At.TotalSeconds;
                double we = w + 1 < wc ? line.Words[w + 1].At.TotalSeconds : lineEnd;
                // сервис знает точный конец слова/слога (Spicy Lyrics) — берём его, но не позже начала следующего
                if (line.Words[w].End is TimeSpan wEnd && wEnd.TotalSeconds > ws) we = Math.Min(we, wEnd.TotalSeconds);
                // протяжная нота тянется, но не бесконечно — иначе слово «зависает» в полутоне
                we = Math.Min(we, ws + Math.Max(0.35, len * 0.3 + 0.4));
                if (we <= ws) we = ws + 0.2;
                _ws[_words] = ws; _we[_words] = we;
                AddRun(text.Substring(at, len), _words);
                _words++;
                pos = at + len;
            }
            if (pos < text.Length) AddRun(text.Substring(pos), _words - 1);
        }

        /// <summary>Обычный текст одной строкой (название трека, «текст не найден»).</summary>
        public void SetPlain(string text)
        {
            Text.Inlines.Clear();
            _runCount = 0; _words = 0;
            if (string.IsNullOrEmpty(text)) return;
            Ensure(1, 0);
            _runs[0] = new Run(text); _runWord[0] = -1; _runCount = 1;
            Text.Inlines.Add(_runs[0]);
        }

        private void Ensure(int runs, int words)
        {
            if (_runs.Length < runs) { _runs = new Run[runs]; _runWord = new int[runs]; }
            if (_ws.Length < words) { _ws = new double[words]; _we = new double[words]; }
        }

        private void AddRun(string s, int word)
        {
            if (_runCount >= _runs.Length) return;
            var r = new Run(s);
            _runs[_runCount] = r; _runWord[_runCount] = word; _runCount++;
            Text.Inlines.Add(r);
        }

        /// <summary>Вся строка одним цветом (соседние строки — их тусклость задаёт прозрачность самого TextBlock).</summary>
        public void PaintFlat(Brush b)
        {
            for (int i = 0; i < _runCount; i++)
                if (!ReferenceEquals(_runs[i].Foreground, b)) _runs[i].Foreground = b;
        }

        /// <summary>Текущая строка: слова загораются по очереди. Без новых объектов — кисти берутся из палитры.</summary>
        public void Paint(double now, LyricPalette pal)
        {
            if (_words == 0) { PaintFlat(pal.Full); return; }
            // плеер иногда присылает позицию чуть «назад» — не гасим слово на доли секунды, иначе оно мигает
            if (now < _last && _last - now < 0.6) now = _last;
            _last = now;
            for (int i = 0; i < _runCount; i++)
            {
                int w = _runWord[i];
                double a = w < 0 ? Unsung : WordAlpha(now, _ws[w], _we[w]);
                var b = pal[a];
                if (!ReferenceEquals(_runs[i].Foreground, b)) _runs[i].Foreground = b;
            }
        }

        private static double WordAlpha(double now, double ws, double we)
        {
            if (now < ws) return Unsung;
            if (now < we)
            {
                // слово разгорается быстро (за первые ~60% своего времени) и держит максимум, пока поётся
                double p = Math.Min(1, (now - ws) / Math.Max(0.05, (we - ws) * 0.6));
                return Unsung + (Singing - Unsung) * Smooth(p);
            }
            // спето — мягко остывает до «спетой» яркости
            double t = Math.Min(1, (now - we) / 0.4);
            return Singing - (Singing - Sung) * Smooth(t);
        }

        internal static double Smooth(double x) { x = Math.Clamp(x, 0, 1); return x * x * (3 - 2 * x); }
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
                double f = LyricRow.Smooth(p * 3 - i);
                Set(_dots[i], UIElement.OpacityProperty, 0.28 + 0.72 * f);
                double s = 0.78 + 0.34 * f;
                SetScale(_scales[i], s);
            }
            // «дыхание» — около 1,6 с на вдох-выдох
            double breath = 1 + 0.07 * Math.Sin((now - start) * Math.PI * 2 / 1.6);
            double groupOp = 1;
            // появление в начале паузы
            double since = now - start;
            if (since < 0.35) { double k = LyricRow.Smooth(since / 0.35); breath *= 0.6 + 0.4 * k; groupOp = k; }
            // конец паузы: точки раздуваются и тают
            if (known)
            {
                double left = end - now;
                if (left < 0.5)
                {
                    double k = LyricRow.Smooth(Math.Max(0, left) / 0.5);
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
    /// Текст песни списком «как в Spotify» (колонка справа от плеера на «Главной»):
    /// текущая строка крупно, жирно и ярко, слова в ней загораются по очереди (по времени слов, если оно есть);
    /// соседние строки тусклые и чуть меньше, дальние ещё и слегка размыты; список плавно едет так,
    /// чтобы текущая строка стояла на верхней трети; в проигрышах — три наливающиеся точки.
    /// Строки «другого голоса» (<see cref="LyricLine.OppositeAligned"/>) — справа.
    ///
    /// Всё обновляется из <see cref="Render"/>, который зовут каждый кадр (FrameClock) только пока колонка видна;
    /// в кадре память не выделяется: Run-ы строятся один раз в <see cref="SetLines"/>, кисти — из палитры.
    /// Для стенда: <c>SetLines(fake)</c>, затем <c>Render(pos, snap: true)</c> — сразу итоговая картинка без анимации.
    /// </summary>
    public sealed class LyricsScroller : Grid
    {
        /// <summary>Где стоит текущая строка: доля высоты окна сверху.</summary>
        public double Anchor { get; set; } = 0.3;
        public double LineFontSize { get; set; } = 16;
        /// <summary>Масштаб нетекущих строк (текущая — 1).</summary>
        public double DimScale { get; set; } = 0.9;
        private const double LineGap = 12, GapRowHeight = 30;

        private readonly Canvas _viewport;
        private readonly StackPanel _stack;
        private readonly TranslateTransform _shift = new();

        private sealed class Item
        {
            public FrameworkElement Elem;
            public LyricRow Row;          // строка с текстом
            public GapDots Dots;          // или проигрыш
            public ScaleTransform Scale;
            public double Start, End;     // время, с (End ≤ Start — неизвестно)
            public bool Right;
            public double Op = -1, Sc = -1, H = -1;
            public int Fx = -1;           // какой эффект сейчас стоит: 0 — нет, 1/2 — размытие, 3 — свечение
            public bool Shown = true;
        }

        private Item[] _items = Array.Empty<Item>();
        private List<LyricLine> _lines;
        private bool _intro;
        private int _cur = -2;
        private double _y;
        private bool _snapNext = true;
        private LyricPalette _pal;

        private static readonly Effect Blur1 = MakeBlur(1.2), Blur2 = MakeBlur(2.4);
        private static readonly Dictionary<Color, Effect> Glows = new();

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
            IsVisibleChanged += (_, __) => { if (IsVisible) Snap(); };
        }

        public bool HasLines => _items.Length > 0;

        /// <summary>Следующий <see cref="Render"/> поставит всё на места сразу, без анимации (после показа, смены трека).</summary>
        public void Snap() => _snapNext = true;

        /// <summary>Построить строки (один раз на текст). null или пустой список — очистить.</summary>
        public void SetLines(List<LyricLine> lines)
        {
            _lines = lines;
            _stack.Children.Clear();
            _cur = -2;
            _y = 0;
            _shift.Y = 0;
            _snapNext = true;
            _pal = LyricPalette.FromTheme(this);
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
                var tb = new TextBlock
                {
                    FontSize = LineFontSize, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap,
                    TextAlignment = l.OppositeAligned ? TextAlignment.Right : TextAlignment.Left,
                    Margin = new Thickness(0, 0, 0, LineGap),
                    // запас справа: свечение и выносные элементы букв не упираются в обрезку
                    Padding = l.OppositeAligned ? new Thickness(8, 0, 2, 0) : new Thickness(0, 0, 8, 0),
                };
                var row = new LyricRow(tb);
                row.Build(l, next);
                row.PaintFlat(_pal.Full);
                var sc = new ScaleTransform(DimScale, DimScale);
                tb.RenderTransformOrigin = new Point(l.OppositeAligned ? 1 : 0, 0.5);
                tb.RenderTransform = sc;
                items.Add(new Item { Elem = tb, Row = row, Scale = sc, Start = l.At.TotalSeconds, End = (next ?? l.At + TimeSpan.FromSeconds(4)).TotalSeconds, Right = l.OppositeAligned });
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

        private static Effect Glow(Color c)
        {
            if (!Glows.TryGetValue(c, out var e))
            {
                var d = new DropShadowEffect { Color = c, ShadowDepth = 0, BlurRadius = 14, Opacity = 0.45, RenderingBias = RenderingBias.Performance };
                d.Freeze();
                Glows[c] = e = d;
            }
            return e;
        }

        /// <summary>Номер элемента списка для позиции (с учётом точек вступления).</summary>
        private int ItemAt(TimeSpan pos)
        {
            int li = KaraokeText.Index(_lines, pos);
            if (li < 0) return _intro ? 0 : -1;
            return li + (_intro ? 1 : 0);
        }

        /// <summary>
        /// Кадр: позиция трека → подсветка слов, тусклость/размытие соседей, прокрутка.
        /// dt — длина кадра, с; snap — сразу итоговое состояние (для стенда и после показа).
        /// </summary>
        public void Render(TimeSpan pos, double dt = 1 / 60.0, bool snap = false)
        {
            if (_items.Length == 0) return;
            if (_snapNext) snap = true;
            // для мгновенной расстановки нужны настоящие высоты строк — досчитываем раскладку прямо сейчас
            if (snap && (_viewport.ActualHeight <= 0 || !_stack.IsArrangeValid)) UpdateLayout();
            if (_viewport.ActualHeight > 0 && _stack.IsArrangeValid) _snapNext = false;

            double now = pos.TotalSeconds;
            int ci = ItemAt(pos);
            if (ci != _cur)
            {
                // ушедшая строка — снова целиком одним цветом (тусклость даёт прозрачность)
                if (_cur >= 0 && _cur < _items.Length) _items[_cur].Row?.PaintFlat(_pal.Full);
                _cur = ci;
                UpdateEffects();
            }

            // сглаживание «x += (цель − x)·k», одинаковое на любой частоте кадров
            double k = snap ? 1 : 1 - Math.Pow(1 - 0.2, Math.Clamp(dt, 0, 0.1) * 30);
            double kFast = snap ? 1 : 1 - Math.Pow(1 - 0.3, Math.Clamp(dt, 0, 0.1) * 30);

            double viewH = _viewport.ActualHeight, top = 0, curTop = 0;
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
                if (i == _cur) it.Row.Paint(now, _pal);
                top += it.Elem.ActualHeight + LineGap;
            }

            // прокрутка: текущая строка — на верхней трети
            double target = viewH * Anchor - (_cur >= 0 ? curTop : 0);
            if (_cur < 0) target = viewH * Anchor;
            double y = _y + (target - _y) * k;
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

        /// <summary>Свечение — у текущей строки, лёгкое размытие — у дальних (меняется только при смене строки).</summary>
        private void UpdateEffects()
        {
            for (int i = 0; i < _items.Length; i++)
            {
                var it = _items[i];
                if (it.Row == null) continue;
                int d = _cur < 0 ? i + 1 : Math.Abs(i - _cur);
                int fx = i == _cur ? 3 : d <= 1 ? 0 : d == 2 ? 1 : 2;
                if (fx == it.Fx) continue;
                it.Fx = fx;
                it.Elem.Effect = fx switch { 3 => Glow(_pal.Color), 1 => Blur1, 2 => Blur2, _ => null };
            }
        }
    }

    /// <summary>
    /// Строка песни в свёрнутой пилюле «С текстом» в стиле Spotify: спетые слова ярко, поющееся — ярче всех
    /// с мягким свечением, не спетые — тускло; смена строк — перекрёстное затухание со сдвигом вверх;
    /// в проигрыше — три наливающиеся точки. Две «ячейки» по очереди: уходящая и приходящая строка.
    /// Для стенда: <c>Render(lines, pos, "Название — Исполнитель", snap: true)</c>.
    /// </summary>
    public sealed class PillLyric : Grid
    {
        private sealed class Slot
        {
            public Grid Root;
            public LyricRow Row;
            public GapDots Dots;
            public TranslateTransform Shift;
            public bool IsDots, Words;
            public double Start, End;
            public double Op = -1, Y = double.NaN;
        }

        private readonly Slot _a, _b;
        private Slot _cur, _old;
        private double _swap = 1;
        private LyricLine _keyLine;
        private int _keyIdx = int.MinValue;
        private string _keyText;
        private readonly LyricPalette _pal = LyricPalette.For(Color.FromRgb(0xEC, 0xEC, 0xF1)); // пилюля всегда тёмная
        private static readonly Effect PillGlow = MakeGlow();

        public PillLyric()
        {
            _a = MakeSlot(); _b = MakeSlot();
            Children.Add(_a.Root); Children.Add(_b.Root);
            _cur = _a; _old = _b;
            _b.Root.Visibility = Visibility.Hidden;
        }

        private Slot MakeSlot()
        {
            var tb = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var dots = new GapDots(5, 4, _pal.Full) { Visibility = Visibility.Collapsed, Margin = new Thickness(1, 0, 0, 0) };
            var shift = new TranslateTransform();
            var root = new Grid { RenderTransform = shift };
            root.Children.Add(tb); root.Children.Add(dots);
            return new Slot { Root = root, Row = new LyricRow(tb), Dots = dots, Shift = shift };
        }

        private static Effect MakeGlow()
        {
            var d = new DropShadowEffect { Color = Color.FromRgb(0xFF, 0xFF, 0xFF), ShadowDepth = 0, BlurRadius = 8, Opacity = 0.35, RenderingBias = RenderingBias.Performance };
            d.Freeze();
            return d;
        }

        /// <summary>Сбросить показанное (смена вида пилюли): следующий кадр покажет строку без перехода.</summary>
        public void Reset()
        {
            _keyLine = null; _keyIdx = int.MinValue; _keyText = null; _swap = 1;
            _a.Row.SetPlain(""); _b.Row.SetPlain("");
        }

        /// <summary>
        /// Кадр пилюли. lines — текст (может быть null), pos — позиция трека; fallback — что показать без строки
        /// (до первой строки или без текста). dt — длина кадра, с; snap — без анимации перехода.
        /// </summary>
        public void Render(List<LyricLine> lines, TimeSpan pos, string fallback, double dt = 1 / 60.0, bool snap = false)
        {
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
            }

            double now = pos.TotalSeconds;
            _swap = snap ? 1 : Math.Min(1, _swap + Math.Clamp(dt, 0, 0.1) / 0.34);
            double e = 1 - Math.Pow(1 - _swap, 3); // плавное торможение
            double eo = Math.Min(1, _swap * 1.8);  // старая строка уходит быстрее, чем приходит новая
            Place(_cur, e, 9 * (1 - e));
            if (_swap < 1) Place(_old, 1 - eo, -8 * eo);
            else if (_old.Root.Visibility != Visibility.Hidden) _old.Root.Visibility = Visibility.Hidden;
            if (_cur.Root.Visibility != Visibility.Visible) _cur.Root.Visibility = Visibility.Visible;

            Paint(_cur, now);
            if (_swap < 1) Paint(_old, now);
        }

        private void Prepare(Slot s, List<LyricLine> lines, int idx, LyricLine line, string fallback)
        {
            s.IsDots = line != null && string.IsNullOrWhiteSpace(line.Text);
            s.Words = false;
            if (s.IsDots)
            {
                s.Row.SetPlain("");
                s.Start = line.At.TotalSeconds;
                s.End = KaraokeText.NextAt(lines, idx)?.TotalSeconds ?? 0;
            }
            else if (line != null) { s.Row.Build(line, KaraokeText.NextAt(lines, idx)); s.Words = true; }
            else { s.Row.SetPlain(fallback ?? ""); s.Row.PaintFlat(_pal.Full); }
            s.Dots.Visibility = s.IsDots ? Visibility.Visible : Visibility.Collapsed;
            s.Row.Text.Visibility = s.IsDots ? Visibility.Collapsed : Visibility.Visible;
            // свечение — только у строки песни с подсветкой по словам
            s.Row.Text.Effect = s.Words && s.Row.HasWords ? PillGlow : null;
        }

        private static void Place(Slot s, double op, double y)
        {
            if (Math.Abs(op - s.Op) > 0.002) { s.Op = op; s.Root.Opacity = op; }
            if (double.IsNaN(s.Y) || Math.Abs(y - s.Y) > 0.05) { s.Y = y; s.Shift.Y = y; }
        }

        private void Paint(Slot s, double now)
        {
            if (s.IsDots) s.Dots.Paint(now, s.Start, s.End);
            else if (s.Words) s.Row.Paint(now, _pal);
        }
    }
}
