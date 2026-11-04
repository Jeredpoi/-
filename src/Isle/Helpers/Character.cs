using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Isle.Native;

namespace Isle.Helpers
{
    /// <summary>Drowsy — ночью, пока ты ещё за компьютером: глаза слипаются, зевает и потягивается.</summary>
    public enum Mood { Idle, Music, Thinking, Done, Attention, Sleep, Working, Drowsy }

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
        /// <summary>Ночь: спит в ночном колпаке.</summary>
        public bool Night { get; set; }

        private double _yawn;                // 0..1 — фаза зевка
        private DateTime _nextYawn = DateTime.Now.AddSeconds(4);

        /// <summary>Своё занятие в покое: стоит, сидит или оглядывается — меняется само раз в полминуты.</summary>
        private int _activity;
        private DateTime _nextActivity = DateTime.Now.AddSeconds(20);
        /// <summary>Короткие занятия (помахать, подпрыгнуть, потянуться, подмигнуть) длятся пару секунд.</summary>
        private DateTime _activityUntil = DateTime.MaxValue;
        private DateTime _nextGreet = DateTime.Now.AddSeconds(5);
        private bool _cursorFar = true;

        // занятия в покое
        private const int Stand = 0, Sit = 1, Look = 2, Wave = 3, Hop = 4, Walk = 5, Stretch = 6, Ball = 7, Book = 8, Coffee = 9, Wink = 10;

        private void StartActivity(int a, double seconds)
        {
            _activity = a;
            _activityUntil = seconds > 0 ? DateTime.Now.AddSeconds(seconds) : DateTime.MaxValue;
            if (a == Look) _nextWander = DateTime.Now;
        }

        /// <summary>Следить глазами за курсором.</summary>
        public bool FollowCursor { get; set; } = true;

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
            // общий кадр приложения; пока Клоди не виден (или остров спрятан) — не считаем и не рисуем
            IsVisibleChanged += (_, __) => { if (IsVisible) Subscribe(); else Unsubscribe(); };
            Loaded += (_, __) => { if (IsVisible) Subscribe(); };
            Unloaded += (_, __) => Unsubscribe();
        }

        private bool _subscribed;
        private void Subscribe() { if (_subscribed) return; _subscribed = true; FrameClock.Tick += OnFrame; }
        private void Unsubscribe() { if (!_subscribed) return; _subscribed = false; FrameClock.Tick -= OnFrame; }
        private void OnFrame(double dt)
        {
            if (FrameClock.Hidden) return;
            _time += dt;
            Tick();
            InvalidateVisual();
        }

        // ---- танцы под музыку: меняются сами каждые 7–12 секунд
        private int _dance;
        private DateTime _nextDance = DateTime.Now;
        public const int DanceCount = 9;
        private static readonly string[] DanceNames = { "прыжки", "покачивание", "присед", "робот", "хедбэнг", "кружится", "диско", "лунная походка", "брейк-данс" };

        /// <summary>
        /// Брейк-данс, цикл ~6 с: падает на спину → крутится на спине, болтая ножками → вскакивает в «фриз» с руками вверх.
        /// Возвращает угол поворота вокруг центра тела, сдвиг вниз (в пикселях спрайта) и фазу.
        /// </summary>
        private (double angle, double down, double phase) Breakdance()
        {
            double ph = (_time * 0.17) % 1;
            if (ph < 0.15)
            {
                double k = ph / 0.15;                       // падает назад
                return (k * k * 90, k * 2.2, ph);
            }
            if (ph < 0.8)
            {
                double k = (ph - 0.15) / 0.65;              // три оборота на спине, в начале быстрее
                double ease = 1 - Math.Pow(1 - k, 1.6);
                return (90 + ease * 1080, 2.2 + Math.Sin(k * Math.PI * 6) * 0.25, ph);
            }
            {
                double k = (ph - 0.8) / 0.2;                // вскакивает и замирает
                return (90 * (1 - k) * (1 - k), 2.2 * (1 - k) - Math.Sin(k * Math.PI) * 2.4, ph);
            }
        }
        public string DanceName => DanceNames[_dance];

        /// <summary>Сдвиг ручки по вертикали в пикселях спрайта для текущего танца (минус — выше).</summary>
        private double DanceArm(bool left)
        {
            double side = left ? 0 : Math.PI;
            switch (_dance)
            {
                case 1: return Math.Sin(_time * 3.2 + side) > 0 ? -1 : 0;                          // машет в такт покачиванию
                case 2: return (_time * 1.9) % 1 >= 0.55 ? -2 : 0;                                    // в прыжке — руки вверх
                case 3: { int pose = (int)(_time * 3.5) % 4; return (pose + (left ? 0 : 2)) % 4 < 2 ? -2 : 1; } // робот: вверх-вниз рывками
                case 4: return Math.Abs(Math.Sin(_time * 6.5)) > 0.7 ? 1 : 0;                         // хедбэнг — руки внизу, дёргаются
                case 5: return -1 - (Math.Sin(_time * 6 + side) > 0 ? 1 : 0);                         // кружится, руки в стороны и вверх
                case 6: return (Math.Sin(_time * 1.6) > 0) == left ? -3 : 0.5;                        // диско: одна рука указывает в небо
                case 7: return Math.Sin(_time * 9 + side) > 0 ? -0.5 : 0.5;                           // лунная походка — руки покачиваются
                case 8:
                {
                    var ph = Breakdance().phase;
                    if (ph >= 0.8) return -3;                                                            // фриз — руки в небо
                    if (ph >= 0.15) return Math.Sin(_time * 16 + side) > 0 ? -1 : 0.5;                 // на спине — машет ручками
                    return -1;                                                                           // падает — руки в стороны
                }
                default: return Math.Sin(_time * 10 + side) > 0 ? -1 : 0;
            }
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
            else if (mood == Mood.Drowsy) { tx = Math.Sin(_time * 0.4) * 0.3; ty = 0.45; } // клюёт носом, смотрит вниз
            else if (mood == Mood.Working) { tx = Math.Sin(_time * 0.9) * 0.5; ty = 0.9; } // смотрит в ноутбук, читает строки
            else if (FollowCursor && Win32.GetCursorPos(out var p))
            {
                try
                {
                    var c = PointToScreen(new Point(ActualWidth / 2, ActualHeight / 2));
                    double dx = p.X - c.X, dy = p.Y - c.Y;
                    if (p.X != _lastCursor.X || p.Y != _lastCursor.Y) { _lastCursor = new Point(p.X, p.Y); _cursorMoved = DateTime.Now; }
                    // курсор подлетел к Клоди издалека — он машет «привет»
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    if (dist < 160 && _cursorFar && mood == Mood.Idle && DateTime.Now >= _nextGreet)
                    {
                        StartActivity(Wave, 1.8);
                        _nextGreet = DateTime.Now.AddSeconds(25);
                    }
                    _cursorFar = dist > 380;
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

            // ---- зевок: раз в 7–15 секунд, ~1,6 с
            if (mood == Mood.Drowsy)
            {
                if (_yawn > 0) _yawn = Math.Min(1.0001, _yawn + 0.025);
                if (_yawn > 1) { _yawn = 0; _nextYawn = DateTime.Now.AddSeconds(7 + _rnd.NextDouble() * 8); }
                else if (_yawn == 0 && DateTime.Now >= _nextYawn) _yawn = 0.001;
            }
            else _yawn = 0;

            // ---- моргание
            if (mood == Mood.Drowsy)
            {
                // веки полуприкрыты, моргает медленно, во время зевка жмурится
                if (DateTime.Now >= _nextBlink)
                {
                    _blinkUntil = DateTime.Now.AddMilliseconds(420);
                    _nextBlink = DateTime.Now.AddSeconds(2 + _rnd.NextDouble() * 3);
                }
                double target = _yawn > 0.15 && _yawn < 0.85 || DateTime.Now < _blinkUntil ? 1 : 0.55;
                _blink += (target - _blink) * 0.15;
            }
            else if (mood != Mood.Sleep)
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
            if (mood == Mood.Idle && DateTime.Now >= _activityUntil) StartActivity(Stand, 0); // короткое занятие закончилось
            if (mood == Mood.Idle && DateTime.Now >= _nextActivity)
            {
                // своё занятие: стоит, сидит, оглядывается, машет, прыгает, гуляет, потягивается,
                // подкидывает мячик, читает книжку, пьёт кофе (по утрам), подмигивает
                int h = DateTime.Now.Hour;
                var pool = new System.Collections.Generic.List<int> { Stand, Sit, Sit, Look, Look, Wave, Hop, Walk, Walk, Stretch, Ball, Ball, Book, Book, Wink };
                if (h >= 6 && h < 12) { pool.Add(Coffee); pool.Add(Coffee); pool.Add(Coffee); }
                int a = pool[_rnd.Next(pool.Count)];
                if (a == _activity) a = Stand;
                double len = a switch { Wave => 2.2, Hop => 2.0, Stretch => 2.4, Wink => 0.9, Walk => 9 + _rnd.Next(5), _ => 0 };
                StartActivity(a, len);
                _nextActivity = DateTime.Now.AddSeconds(a == Walk ? len + 4 : 14 + _rnd.Next(22));
            }
            if (mood != Mood.Idle && _activity != Stand && _activity != Sit && _activity != Look) StartActivity(Stand, 0);
            if (mood == Mood.Idle && _activity == Book)
            {
                // читает: глаза бегут по строчкам вниз-вправо и возвращаются
                double line = (_time * 0.7) % 1;
                _lookX += ((line * 1.6 - 0.8) - _lookX) * 0.3; _lookY += (0.85 - _lookY) * 0.3;
            }
            if (mood == Mood.Idle && _activity == Ball)
            {
                // следит за мячиком
                _lookX += (0 - _lookX) * 0.3; _lookY += (-0.9 + Math.Abs(Math.Sin(_time * 3.2)) * -0.1 - _lookY) * 0.3;
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
            if (mood == Mood.Music && DateTime.Now >= _nextDance)
            {
                int d = _rnd.Next(DanceCount - 1);
                _dance = d >= _dance ? d + 1 : d; // всегда другой танец
                _nextDance = DateTime.Now.AddSeconds(7 + _rnd.NextDouble() * 5);
            }
            if (mood == Mood.Music && (_dance == 4)) { _lookY += (0.8 - _lookY) * 0.3; } // хедбэнг — смотрит в пол
        }

        private static readonly Brush BookCover = Freeze(new SolidColorBrush(Color.FromRgb(0x3B, 0x6F, 0xD9)));
        private static readonly Brush Paper = Freeze(new SolidColorBrush(Color.FromRgb(0xF4, 0xF1, 0xE8)));
        private static readonly Brush BallRed = Freeze(new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)));
        private static readonly Brush Cup = Freeze(new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xF1)));
        private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

        /// <summary>Книжка, мячик и чашка кофе для занятий в покое.</summary>
        private void DrawProps(DrawingContext dc, double ox, double oy, double px)
        {
            switch (_activity)
            {
                case Book:
                {
                    // раскрытая книжка перед Клоди, страница иногда перелистывается
                    double bx = ox + 3 * px, by = oy + 4.6 * px;
                    dc.DrawRectangle(BookCover, null, new Rect(bx - 0.3 * px, by, 7.6 * px, 2.6 * px));
                    dc.DrawRectangle(Paper, null, new Rect(bx, by + 0.3 * px, 3.3 * px, 2 * px));
                    dc.DrawRectangle(Paper, null, new Rect(bx + 3.7 * px, by + 0.3 * px, 3.3 * px, 2 * px));
                    double flip = (_time % 5) / 5;
                    if (flip > 0.85)
                    {
                        double k = (flip - 0.85) / 0.15; // страница переворачивается справа налево
                        dc.DrawRectangle(Paper, null, new Rect(bx + 3.5 * px - 3.3 * px * k, by - 0.4 * px, 3.3 * px * Math.Max(0.15, 1 - Math.Abs(k * 2 - 1)), 2.2 * px));
                    }
                    var ink = new SolidColorBrush(Color.FromArgb(120, 0x55, 0x55, 0x60));
                    for (int i = 0; i < 3; i++)
                    {
                        dc.DrawRectangle(ink, null, new Rect(bx + 0.4 * px, by + (0.7 + i * 0.55) * px, 2.4 * px, 0.25 * px));
                        dc.DrawRectangle(ink, null, new Rect(bx + 4.1 * px, by + (0.7 + i * 0.55) * px, 2.4 * px, 0.25 * px));
                    }
                    break;
                }
                case Ball:
                {
                    // подкидывает мячик над головой
                    double up = Math.Abs(Math.Sin(_time * 3.2));
                    dc.DrawEllipse(BallRed, null, new Point(ox + 6.5 * px, oy - (1.2 + up * 4.2) * px), 0.8 * px, 0.8 * px);
                    break;
                }
                case Coffee:
                {
                    // чашка в правой ручке, над ней поднимается пар
                    bool sipping = Math.Sin(_time * 0.8) > 0.85;
                    double cx = sipping ? ox + 10.2 * px : ox + 12.8 * px, cy = sipping ? oy + 3.2 * px : oy + 3.6 * px;
                    dc.DrawRectangle(Cup, null, new Rect(cx, cy, 1.8 * px, 1.9 * px));
                    dc.DrawRectangle(Cup, null, new Rect(cx + 1.8 * px, cy + 0.4 * px, 0.6 * px, 0.9 * px)); // ручка
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x6B, 0x3E, 0x26)), null, new Rect(cx + 0.2 * px, cy + 0.2 * px, 1.4 * px, 0.4 * px));
                    for (int i = 0; i < 2; i++)
                    {
                        double ph = (_time * 0.9 + i * 0.5) % 1;
                        var steam = new SolidColorBrush(Color.FromArgb((byte)(170 * (1 - ph)), 0xFF, 0xFF, 0xFF));
                        dc.DrawRectangle(steam, null, new Rect(cx + (0.4 + i * 0.7 + Math.Sin(_time * 3 + i) * 0.25) * px, cy - (0.6 + ph * 2.2) * px, 0.4 * px, 0.4 * px));
                    }
                    break;
                }
            }
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
                    if (_activity == Sit || _activity == Book) dy += 1.0 * px;   // сидит (и читает сидя) — чуть ниже
                    if (_activity == Hop) { dy -= Math.Abs(Math.Sin(_time * 5.5)) * 1.8 * px; step = true; }
                    if (_activity == Walk)
                    {
                        // прогуливается туда-сюда, переставляя ножки
                        dx = Math.Sin(_time * 0.9) * 1.3 * px;
                        step = Math.Sin(_time * 7) > 0;
                        tilt = Math.Sin(_time * 7) * 2.5;
                    }
                    if (_activity == Stretch) dy -= 0.6 * px;
                    if (_activity == Wave) tilt = Math.Sin(_time * 7) * 3;
                    break;
                case Mood.Music:
                    // танцует всегда, даже на тихих местах; под громкие доли прыгает выше
                    switch (_dance)
                    {
                        case 1: // покачивание из стороны в сторону
                            dx = Math.Sin(_time * 3.2) * 1.6 * px;
                            tilt = Math.Sin(_time * 3.2) * 12;
                            dy = -(Math.Abs(Math.Sin(_time * 6.4)) * 0.4 + _bob * 0.8) * px;
                            step = Math.Sin(_time * 3.2) > 0;
                            break;
                        case 2: // присед и прыжок на сильную долю
                        {
                            double ph = (_time * 1.9) % 1;
                            dy = ph < 0.55 ? Math.Sin(ph / 0.55 * Math.PI) * 1.2 * px : -Math.Sin((ph - 0.55) / 0.45 * Math.PI) * (2.2 + _bob * 1.2) * px;
                            step = ph >= 0.55;
                            break;
                        }
                        case 3: // робот: резкие фиксированные позы
                        {
                            int pose = (int)(_time * 3.5) % 4;
                            dx = (pose == 1 ? 1 : pose == 3 ? -1 : 0) * px;
                            tilt = pose == 1 ? 6 : pose == 3 ? -6 : 0;
                            dy = -_bob * 0.6 * px;
                            step = pose % 2 == 1;
                            break;
                        }
                        case 4: // хедбэнг: резкие кивки в бит
                            tilt = Math.Pow(Math.Abs(Math.Sin(_time * 6.5)), 3) * 14 + _bob * 6;
                            dy = Math.Abs(Math.Sin(_time * 6.5)) * 0.7 * px;
                            break;
                        case 5: // кружится (поворачивается то одним боком, то другим)
                            dy = -(Math.Abs(Math.Sin(_time * 6)) * 0.9 + _bob) * px;
                            step = Math.Sin(_time * 6) > 0;
                            break;
                        case 6: // диско: рука в небо то слева, то справа, бёдрами в бит
                            dx = Math.Sin(_time * 7) * 0.7 * px;
                            tilt = Math.Sin(_time * 1.6) > 0 ? 7 : -7;
                            dy = -_bob * 1.0 * px;
                            step = Math.Sin(_time * 7) > 0;
                            break;
                        case 7: // лунная походка: скользит назад, переставляя ножки
                        {
                            double ph = (_time * 0.35) % 1;
                            dx = (1.6 - ph * 3.2) * px;
                            step = Math.Sin(_time * 9) > 0;
                            dy = -_bob * 0.5 * px;
                            tilt = -4;
                            break;
                        }
                        case 8: // брейк-данс: угол и сдвиг считаются в Breakdance(), здесь — ножки
                        {
                            double bph = Breakdance().phase;
                            step = bph >= 0.15 && bph < 0.8 && Math.Sin(_time * 16) > 0; // на спине болтает ножками
                        }
                            break;
                        default: // прыжки (как раньше)
                            dy = -(Math.Abs(Math.Sin(_time * 7.5)) * 1.1 + _bob * 1.4) * px;
                            tilt = Math.Sin(_time * 3.75) * 9;
                            dx = Math.Sin(_time * 3.75) * 0.8 * px;
                            step = Math.Sin(_time * 7.5) > 0;
                            break;
                    }
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
                case Mood.Drowsy:
                    // медленно покачивается и клюёт носом, на зевке вытягивается вверх
                    dy = (0.3 + Math.Sin(_time * 0.9) * 0.35) * px - Math.Sin(_yawn * Math.PI) * 0.8 * px;
                    tilt = Math.Sin(_time * 0.45) * 4;
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

            // «кружится»: сжимаем по горизонтали — будто поворачивается боком
            double spin = mood == Mood.Music && _dance == 5 ? Math.Cos(_time * 2.6) : 1;
            if (Math.Abs(spin) < 0.18) spin = spin < 0 ? -0.18 : 0.18;
            var tf = new TransformGroup();
            tf.Children.Add(new ScaleTransform(spin, 1, ActualWidth / 2, ActualHeight / 2));
            if (mood == Mood.Music && _dance == 8 && _cheer <= 0)
            {
                // брейк-данс: падает и крутится на спине вокруг центра тела
                var (ang, down, _) = Breakdance();
                oy += down * px;
                tf.Children.Add(new RotateTransform(-ang, ox + 6.5 * px, oy + 4.5 * px));
            }
            else tf.Children.Add(new RotateTransform(tilt, ActualWidth / 2, ActualHeight));
            dc.PushTransform(tf);
            for (int y = 0; y < H; y++)
            {
                var row = Sprite[y];
                for (int x = 0; x < W; x++)
                {
                    char c = row[x];
                    if (c == '.') continue;
                    double cy = y;
                    if (c == 'l' && mood == Mood.Idle && (_activity == Sit || _activity == Book))
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
                        cy = mood == Mood.Music && _cheer <= 0 ? y + DanceArm(x == 0) : (Math.Sin(_time * 10 + (x == 0 ? 0 : Math.PI)) > 0 ? y - 1 : y);
                    }
                    // зевая, потягивается — ручки вверх
                    if ((x == 0 || x == 12) && _yawn > 0.1 && _yawn < 0.9) cy = y - 1 - (Math.Sin(_yawn * Math.PI) > 0.6 ? 1 : 0);
                    // занятия в покое: машет одной ручкой, потягивается, подкидывает мячик
                    if ((x == 0 || x == 12) && mood == Mood.Idle && _cheer <= 0)
                    {
                        if (_activity == Wave && x == 12) cy = y - 1 - (Math.Sin(_time * 14) > 0 ? 1 : 0);
                        if (_activity == Stretch) cy = y - 2;
                        if (_activity == Ball) cy = (Math.Sin(_time * 6.4 + (x == 0 ? 0 : Math.PI)) > 0.3) ? y - 1 : y;
                        if (_activity == Coffee && x == 12) cy = y - (Math.Sin(_time * 0.8) > 0.85 ? 2 : 0); // отпивает
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
            bool nightcap = Night && (mood == Mood.Sleep || mood == Mood.Drowsy);
            if (nightcap)
            {
                // ночной колпак со звёздочками, кончик свисает набок, на конце помпон
                var cap = new SolidColorBrush(Color.FromRgb(0x2E, 0x3F, 0x9E));
                var star = new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x82));
                var pom = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xF1));
                for (int x = 2; x <= 10; x++) dc.DrawRectangle(pom, null, new Rect(ox + x * px, oy + 0 * px, px + 0.35, px * 0.6)); // отворот
                for (int x = 2; x <= 10; x++) dc.DrawRectangle(cap, null, new Rect(ox + x * px, oy - 1 * px, px + 0.35, px + 0.35));
                for (int x = 4; x <= 10; x++) dc.DrawRectangle(cap, null, new Rect(ox + x * px, oy - 2 * px, px + 0.35, px + 0.35));
                for (int x = 8; x <= 11; x++) dc.DrawRectangle(cap, null, new Rect(ox + x * px, oy - 3 * px, px + 0.35, px + 0.35));
                dc.DrawRectangle(cap, null, new Rect(ox + 12 * px, oy - 2 * px, px + 0.35, px + 0.35));
                double sway = Math.Sin(_time * 1.2) * 0.25 * px; // помпон чуть качается
                dc.DrawRectangle(pom, null, new Rect(ox + 12.2 * px + sway, oy - 1 * px, px, px));
                dc.DrawRectangle(star, null, new Rect(ox + 5 * px, oy - 1.6 * px, px * 0.5, px * 0.5));
                dc.DrawRectangle(star, null, new Rect(ox + 9 * px, oy - 2.6 * px, px * 0.5, px * 0.5));
            }
            if (Outfit == Outfit.Winter && nightcap)
            {
                // в холод под колпаком ещё и шарф
                var scarf = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
                for (int x = 2; x <= 10; x++) dc.DrawRectangle(scarf, null, new Rect(ox + x * px, oy + 4 * px, px + 0.35, px + 0.35));
                dc.DrawRectangle(scarf, null, new Rect(ox + 9 * px, oy + 5 * px, px + 0.35, 2 * px));
            }
            else if (Outfit == Outfit.Winter)
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
            else if (Outfit == Outfit.Rain && !nightcap)
            {
                var canopy = new SolidColorBrush(Color.FromRgb(0x5B, 0x6C, 0xF0));
                for (int x = 1; x <= 11; x++) dc.DrawRectangle(canopy, null, new Rect(ox + x * px, oy - 2 * px, px + 0.35, px + 0.35));
                for (int x = 3; x <= 9; x++) dc.DrawRectangle(canopy, null, new Rect(ox + x * px, oy - 3 * px, px + 0.35, px + 0.35));
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x8B, 0x8B, 0x95)), null, new Rect(ox + 6 * px, oy - 1 * px, px * 0.6, px));
            }

            // ---- вещи для занятий
            if (mood == Mood.Idle && _cheer <= 0) DrawProps(dc, ox, oy, px);

            // ---- глаза: двигаются за взглядом, моргают
            var eye = Brushes.Black;
            double ex = _lookX * 0.85 * px, ey = _lookY * 0.55 * px;
            double eh = 2 * px * (1 - _blink);
            bool wink = mood == Mood.Idle && _activity == Wink;
            bool sip = mood == Mood.Idle && _activity == Coffee && Math.Sin(_time * 0.8) > 0.85;
            foreach (var e in glasses ? Array.Empty<Point>() : new[] { EyeL, EyeR })
            {
                double x = ox + e.X * px + ex, y = oy + e.Y * px + ey;
                if ((wink && e == EyeL) || sip) { dc.DrawRectangle(eye, null, new Rect(x - 0.15 * px, y + 1.2 * px, px * 1.3, Math.Max(0.4, 0.35 * px))); continue; } // подмигивает / жмурится от кофе
                if (eh < 0.45 * px) dc.DrawRectangle(eye, null, new Rect(x - 0.15 * px, y + 1.2 * px, px * 1.3, Math.Max(0.4, 0.35 * px)));
                else dc.DrawRectangle(eye, null, new Rect(x, y + (2 * px - eh) / 2, px, eh));
            }
            if (_yawn > 0)
            {
                // рот при зевке: открывается и закрывается
                double open = Math.Sin(_yawn * Math.PI);
                double mh = 1.8 * px * open, mw = (1 + 0.6 * open) * px;
                if (mh > 0.2 * px) dc.DrawRectangle(eye, null, new Rect(ox + 6.5 * px - mw / 2, oy + 4.6 * px - mh / 2, mw, mh));
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
