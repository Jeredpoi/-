using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Isle.Native;

namespace Isle.Helpers
{
    public enum Mood { Idle, Music, Thinking, Done, Attention, Sleep, Working }

    /// <summary>Одежда по погоде: шапка и шарф в холод, очки и зонтик от солнца в жару, зонт в дождь.</summary>
    public enum Outfit { None, Winter, Summer, Rain }

    /// <summary>
    /// Клоди — пиксельный оранжевый дружок в духе маскота Claude Code. Живёт в чёлке.
    /// Глазки следят за курсором и моргают; под музыку пляшет, когда ИИ думает — глаза бегают
    /// и над головой «…», радуется готовому, вздрагивает от уведомлений, засыпает без тебя.
    /// Те же пиксели нарисованы в Claude Design (лист «Персонаж»).
    /// </summary>
    public class Character : FrameworkElement
    {
        // 13×9: # — тело (ручки — крайние столбцы в рядах 4–5), l — ножки. Глаза рисуются отдельно, чтобы двигаться.
        private static readonly string[] Sprite =
        {
            "..#########..",
            "..#########..",
            "..#########..",
            "..#########..",
            "#############",
            "#############",
            "..#########..",
            "..l.l...l.l..",
            "..l.l...l.l..",
        };
        private const int W = 13, H = 9;
        private static readonly Point EyeL = new(4, 2), EyeR = new(8, 2); // глаза 1×2 пикселя

        public static readonly DependencyProperty MoodProperty = DependencyProperty.Register(
            nameof(Mood), typeof(Mood), typeof(Character), new FrameworkPropertyMetadata(Mood.Idle));

        public static readonly DependencyProperty BodyProperty = DependencyProperty.Register(
            nameof(Body), typeof(Brush), typeof(Character),
            new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x57)), FrameworkPropertyMetadataOptions.AffectsRender));

        public Mood Mood { get => (Mood)GetValue(MoodProperty); set => SetValue(MoodProperty, value); }
        public Brush Body { get => (Brush)GetValue(BodyProperty); set => SetValue(BodyProperty, value); }

        /// <summary>Громкость музыки 0..1 — пляшет в такт.</summary>
        public float Beat { get; set; }
        public Outfit Outfit { get; set; }

        /// <summary>Своё занятие в покое: стоит, сидит или оглядывается — меняется само раз в полминуты.</summary>
        private int _activity;
        private DateTime _nextActivity = DateTime.Now.AddSeconds(20);

        /// <summary>Следить глазами за курсором.</summary>
        public bool FollowCursor { get; set; } = true;

        private readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromMilliseconds(40) };
        private readonly Random _rnd = new();
        private double _time;
        private double _lookX, _lookY;       // текущий взгляд (−1..1)
        private double _blink;               // 0 — открыты, 1 — закрыты
        private DateTime _nextBlink = DateTime.Now.AddSeconds(2);
        private DateTime _blinkUntil;
        private double _cheer;               // >0 — прыгает от радости
        private double _bob;
        private Point _wander;
        private Point _lastCursor;
        private DateTime _cursorMoved = DateTime.Now;
        private DateTime _nextWander;

        public Character()
        {
            Width = 26; Height = 20;
            SnapsToDevicePixels = false;
            _t.Tick += (_, __) => { _time += 0.04; Tick(); InvalidateVisual(); };
            IsVisibleChanged += (_, __) => { if (IsVisible) _t.Start(); else _t.Stop(); };
            if (IsVisible) _t.Start();
        }

        /// <summary>Короткая радость (скриншот, ответ ИИ, файл на полке).</summary>
        public void Cheer() => _cheer = 1;

        private void Tick()
        {
            var mood = Mood;

            // ---- куда смотреть
            double tx = 0, ty = 0;
            if (mood == Mood.Thinking)
            {
                tx = Math.Sin(_time * 3.2) * 0.9;   // глаза бегают
                ty = -0.7;                           // и смотрят вверх
            }
            else if (mood == Mood.Sleep) { tx = 0; ty = 0.6; }
            else if (mood == Mood.Working) { tx = Math.Sin(_time * 0.9) * 0.5; ty = 0.9; } // смотрит в ноутбук, читает строки
            else if (FollowCursor && Win32.GetCursorPos(out var p))
            {
                try
                {
                    var c = PointToScreen(new Point(ActualWidth / 2, ActualHeight / 2));
                    double dx = p.X - c.X, dy = p.Y - c.Y;
                    if (p.X != _lastCursor.X || p.Y != _lastCursor.Y) { _lastCursor = new Point(p.X, p.Y); _cursorMoved = DateTime.Now; }
                    if (DateTime.Now - _cursorMoved > TimeSpan.FromSeconds(6))
                    {
                        // курсор давно стоит — сам оглядывается по сторонам
                        if (DateTime.Now >= _nextWander)
                        {
                            _nextWander = DateTime.Now.AddSeconds(1.5 + _rnd.NextDouble() * 3);
                            _wander = new Point(_rnd.NextDouble() * 2 - 1, _rnd.NextDouble() * 1.2 - 0.4);
                        }
                        tx = _wander.X; ty = _wander.Y;
                    }
                    else
                    {
                        // следим всегда, по всему экрану (и на 4K): чем дальше курсор, тем сильнее поворот глаз
                        double sw = Math.Max(800, Win32.GetSystemMetrics(0)), sh = Math.Max(600, Win32.GetSystemMetrics(1));
                        tx = Math.Clamp(Math.Sign(dx) * Math.Pow(Math.Min(1, Math.Abs(dx) / (sw * 0.45)), 0.6), -1, 1);
                        ty = Math.Clamp(Math.Sign(dy) * Math.Pow(Math.Min(1, Math.Abs(dy) / (sh * 0.5)), 0.6), -1, 1);
                    }
                }
                catch { }
            }
            _lookX += (tx - _lookX) * 0.28;
            _lookY += (ty - _lookY) * 0.28;

            // ---- моргание
            if (mood != Mood.Sleep)
            {
                if (DateTime.Now >= _nextBlink)
                {
                    _blinkUntil = DateTime.Now.AddMilliseconds(130);
                    _nextBlink = DateTime.Now.AddSeconds(2.2 + _rnd.NextDouble() * 4.5);
                    if (_rnd.NextDouble() < 0.2) _nextBlink = DateTime.Now.AddMilliseconds(260); // двойное моргание
                }
                double target = DateTime.Now < _blinkUntil ? 1 : 0;
                _blink += (target - _blink) * 0.6;
            }
            else _blink += (1 - _blink) * 0.2;

            if (_cheer > 0) _cheer = Math.Max(0, _cheer - 0.035);
            if (mood == Mood.Idle && DateTime.Now >= _nextActivity)
            {
                _activity = _rnd.Next(3); // 0 — стоит, 1 — сидит, 2 — оглядывается
                _nextActivity = DateTime.Now.AddSeconds(18 + _rnd.Next(25));
                if (_activity == 2) _nextWander = DateTime.Now;
            }
            if (mood == Mood.Idle && _activity == 2)
            {
                if (DateTime.Now >= _nextWander)
                {
                    _nextWander = DateTime.Now.AddSeconds(0.8 + _rnd.NextDouble() * 1.5);
                    _wander = new Point(_rnd.NextDouble() * 2 - 1, _rnd.NextDouble() * 1.2 - 0.5);
                }
                _lookX += (_wander.X - _lookX) * 0.2; _lookY += (_wander.Y - _lookY) * 0.2;
            }
            _bob += ((mood == Mood.Music ? Math.Min(1, Beat * 2.2) : 0) - _bob) * 0.5;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double px = Math.Min(ActualWidth / (W + 1.0), ActualHeight / (H + 2.0));
            if (px <= 0) return;
            var mood = Mood;

            double dx = 0, dy = 0, tilt = 0;
            bool step = false;
            switch (mood)
            {
                case Mood.Idle:
                    dy = Math.Sin(_time * 2.2) * 0.35 * px; // дышит
                    if (_activity == 1) dy += 1.0 * px;   // сидит — чуть ниже
                    break;
                case Mood.Music:
                    // танцует всегда, даже на тихих местах; под громкие доли прыгает выше
                    dy = -(Math.Abs(Math.Sin(_time * 7.5)) * 1.1 + _bob * 1.4) * px;
                    tilt = Math.Sin(_time * 3.75) * 9;
                    dx = Math.Sin(_time * 3.75) * 0.8 * px;
                    step = Math.Sin(_time * 7.5) > 0;
                    break;
                case Mood.Thinking:
                    dy = Math.Sin(_time * 1.5) * 0.3 * px;
                    break;
                case Mood.Attention:
                    dx = Math.Sin(_time * 40) * 0.7 * px;
                    break;
                case Mood.Sleep:
                    dy = 0.6 * px + Math.Sin(_time * 1.2) * 0.4 * px;
                    break;
                case Mood.Working:
                    dy = Math.Sin(_time * 1.6) * 0.2 * px;
                    break;
            }
            if (_cheer > 0)
            {
                dy -= Math.Abs(Math.Sin((1 - _cheer) * Math.PI * 3)) * 2.4 * px * _cheer;
                step = true;
            }

            double ox = (ActualWidth - W * px) / 2 + dx;
            double oy = (ActualHeight - H * px) / 2 + dy + 0.5 * px;
            var body = Body;

            dc.PushTransform(new RotateTransform(tilt, ActualWidth / 2, ActualHeight));
            for (int y = 0; y < H; y++)
            {
                var row = Sprite[y];
                for (int x = 0; x < W; x++)
                {
                    char c = row[x];
                    if (c == '.') continue;
                    double cy = y;
                    if (c == 'l' && mood == Mood.Idle && _activity == 1)
                    {
                        if (y == 8) continue; // сидит: ножки поджаты
                    }
                    if (c == 'l')
                    {
                        // шаг: пары ножек по очереди поднимаются
                        bool pairA = x == 2 || x == 8;
                        if (step && pairA && y == 8) continue;
                        if (step && !pairA && y == 8 && mood == Mood.Music) cy = 8;
                    }
                    // ручки машут под музыку и от радости, а за ноутбуком — «печатают»
                    if ((x == 0 || x == 12) && (mood == Mood.Music || _cheer > 0))
                    {
                        bool up = Math.Sin(_time * 10 + (x == 0 ? 0 : Math.PI)) > 0;
                        cy = up ? y - 1 : y;
                    }
                    if ((x == 0 || x == 12) && mood == Mood.Working)
                    {
                        bool tap = Math.Sin(_time * 22 + (x == 0 ? 0 : 1.7)) > 0.3;
                        cy = tap ? y + 1 : y;
                    }
                    dc.DrawRectangle(body, null, new Rect(ox + x * px, oy + cy * px, px + 0.35, px + 0.35));
                }
            }

            // ---- одежда по погоде
            bool glasses = false;
            if (Outfit == Outfit.Winter)
            {
                var hat = new SolidColorBrush(Color.FromRgb(0x3B, 0x6F, 0xD9));
                var band = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xF1));
                for (int x = 2; x <= 10; x++) dc.DrawRectangle(hat, null, new Rect(ox + x * px, oy - 1 * px, px + 0.35, px + 0.35));
                for (int x = 3; x <= 9; x++) dc.DrawRectangle(hat, null, new Rect(ox + x * px, oy - 2 * px, px + 0.35, px + 0.35));
                for (int x = 2; x <= 10; x++) dc.DrawRectangle(band, null, new Rect(ox + x * px, oy + 0 * px, px + 0.35, px * 0.6));
                dc.DrawRectangle(band, null, new Rect(ox + 6 * px, oy - 3 * px, px + 0.35, px + 0.35)); // помпон
                var scarf = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
                for (int x = 2; x <= 10; x++) dc.DrawRectangle(scarf, null, new Rect(ox + x * px, oy + 4 * px, px + 0.35, px + 0.35));
                dc.DrawRectangle(scarf, null, new Rect(ox + 9 * px, oy + 5 * px, px + 0.35, 2 * px));          // кончик шарфа
            }
            else if (Outfit == Outfit.Summer)
            {
                glasses = true;
                var lens = new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0C));
                dc.DrawRectangle(lens, null, new Rect(ox + 3 * px, oy + 2 * px, 3 * px, 1.6 * px));
                dc.DrawRectangle(lens, null, new Rect(ox + 7 * px, oy + 2 * px, 3 * px, 1.6 * px));
                dc.DrawRectangle(lens, null, new Rect(ox + 6 * px, oy + 2.3 * px, px, 0.5 * px));
                var shine = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255));
                dc.DrawRectangle(shine, null, new Rect(ox + 3.4 * px, oy + 2.3 * px, 0.6 * px, 0.4 * px));
                dc.DrawRectangle(shine, null, new Rect(ox + 7.4 * px, oy + 2.3 * px, 0.6 * px, 0.4 * px));
            }
            else if (Outfit == Outfit.Rain)
            {
                var canopy = new SolidColorBrush(Color.FromRgb(0x5B, 0x6C, 0xF0));
                for (int x = 1; x <= 11; x++) dc.DrawRectangle(canopy, null, new Rect(ox + x * px, oy - 2 * px, px + 0.35, px + 0.35));
                for (int x = 3; x <= 9; x++) dc.DrawRectangle(canopy, null, new Rect(ox + x * px, oy - 3 * px, px + 0.35, px + 0.35));
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x8B, 0x8B, 0x95)), null, new Rect(ox + 6 * px, oy - 1 * px, px * 0.6, px));
            }

            // ---- глаза: двигаются за взглядом, моргают
            var eye = Brushes.Black;
            double ex = _lookX * 0.85 * px, ey = _lookY * 0.55 * px;
            double eh = 2 * px * (1 - _blink);
            foreach (var e in glasses ? Array.Empty<Point>() : new[] { EyeL, EyeR })
            {
                double x = ox + e.X * px + ex, y = oy + e.Y * px + ey;
                if (eh < 0.45 * px) dc.DrawRectangle(eye, null, new Rect(x - 0.15 * px, y + 1.2 * px, px * 1.3, Math.Max(0.4, 0.35 * px)));
                else dc.DrawRectangle(eye, null, new Rect(x, y + (2 * px - eh) / 2, px, eh));
            }
            dc.Pop();

            // ---- пляжный зонтик рядом в жару
            if (Outfit == Outfit.Summer && (mood == Mood.Idle || mood == Mood.Sleep))
            {
                var red = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
                var white = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xF1));
                double ux = ox + 13 * px, uy = oy - 2 * px;
                for (int i = 0; i < 5; i++) dc.DrawRectangle(i % 2 == 0 ? red : white, null, new Rect(ux + i * px, uy, px + 0.35, px + 0.35));
                for (int i = 1; i < 4; i++) dc.DrawRectangle(i % 2 == 0 ? red : white, null, new Rect(ux + i * px, uy - px, px + 0.35, px + 0.35));
                dc.DrawRectangle(white, null, new Rect(ux + 2.2 * px, uy + px, px * 0.5, 8.5 * px));
            }

            // ---- ноутбук перед Клоди
            if (mood == Mood.Working)
            {
                var lid = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x42));
                var screen = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x22));
                var glow = new SolidColorBrush(Color.FromArgb(200, 0x7D, 0xB5, 0xFF));
                double lx = ox + 2.5 * px, ly = oy + 5.2 * px;
                dc.DrawRectangle(lid, null, new Rect(lx, ly, 8 * px, 3.6 * px));             // крышка
                dc.DrawRectangle(screen, null, new Rect(lx + 0.5 * px, ly + 0.5 * px, 7 * px, 2.6 * px));
                // бегущие строки кода на экране
                for (int i = 0; i < 3; i++)
                {
                    double len = 2 + ((int)(_time * 3 + i * 1.7) % 4);
                    dc.DrawRectangle(glow, null, new Rect(lx + 1 * px, ly + (0.9 + i * 0.75) * px, len * px * 0.9, 0.35 * px));
                }
                dc.DrawRectangle(lid, null, new Rect(lx - 1 * px, ly + 3.6 * px, 10 * px, 0.7 * px)); // клавиатура
                // «</>» всплывает над головой
                if ((int)(_time * 0.7) % 3 == 0)
                {
                    var tag = new FormattedText("</>", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface("Consolas"), 2.6 * px, new SolidColorBrush(Color.FromArgb(220, 0xFF, 0xE9, 0xD6)), VisualTreeHelper.GetDpi(this).PixelsPerDip);
                    double rise = (_time * 2) % 3;
                    dc.DrawText(tag, new Point(ox + 9.5 * px, oy - 1.5 * px - rise * px));
                }
            }

            // ---- значки настроения
            var light = new SolidColorBrush(Color.FromRgb(0xFF, 0xE9, 0xD6));
            if (mood == Mood.Thinking)
            {
                for (int i = 0; i < 3; i++)
                {
                    double a = 0.35 + 0.65 * Math.Max(0, Math.Sin(_time * 5 - i * 0.9));
                    var b = new SolidColorBrush(Color.FromArgb((byte)(255 * a), 0xFF, 0xE9, 0xD6));
                    dc.DrawRectangle(b, null, new Rect(ox + (4.5 + i * 1.6) * px, oy - 1.6 * px, px * 0.8, px * 0.8));
                }
            }
            else if (mood == Mood.Attention)
            {
                dc.DrawRectangle(light, null, new Rect(ox + 6 * px, oy - 3 * px, px, px * 1.6));
                dc.DrawRectangle(light, null, new Rect(ox + 6 * px, oy - 1 * px, px, px * 0.6));
            }
            else if (mood == Mood.Sleep && Math.Sin(_time * 2) > -0.2)
            {
                double zy = (_time * 6) % 6;
                var z = new FormattedText("z", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI Semibold"), 3.2 * px, light, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(z, new Point(ox + 11.5 * px, oy - zy * 0.4 * px));
            }
            if (_cheer > 0.2)
            {
                // искорки вокруг
                for (int i = 0; i < 4; i++)
                {
                    double ang = _time * 4 + i * Math.PI / 2;
                    double rr = (6.5 + (1 - _cheer) * 2) * px;
                    double sx = ox + 6.5 * px + Math.Cos(ang) * rr, sy = oy + 3.5 * px + Math.Sin(ang) * rr * 0.55;
                    dc.DrawRectangle(light, null, new Rect(sx, sy, px * 0.7, px * 0.7));
                }
            }
        }
    }
}
