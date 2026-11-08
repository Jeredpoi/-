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
    public enum Outfit { None, Winter, Summer, Rain, Sunny }

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
            new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0xE0, 0x7A, 0x5F)), FrameworkPropertyMetadataOptions.AffectsRender));

        public Mood Mood { get => (Mood)GetValue(MoodProperty); set => SetValue(MoodProperty, value); }
        public Brush Body { get => (Brush)GetValue(BodyProperty); set => SetValue(BodyProperty, value); }

        /// <summary>Громкость музыки 0..1 — пляшет в такт.</summary>
        public float Beat { get; set; }
        public Outfit Outfit { get; set; }
        /// <summary>Идёт снег — вокруг падают снежинки.</summary>
        public bool Snowing { get; set; }

        /// <summary>Праздник по календарю: "ny" — с 20 декабря по 10 января, "halloween" — с 25 октября по 1 ноября.</summary>
        public static string Holiday(DateTime d) =>
            (d.Month == 12 && d.Day >= 20) || (d.Month == 1 && d.Day <= 10) ? "ny"
            : (d.Month == 10 && d.Day >= 25) || (d.Month == 11 && d.Day == 1) ? "halloween" : null;
        private static string Holiday() => Holiday(DateTime.Now);

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
        private const int Stand = 0, Sit = 1, Look = 2, Wave = 3, Hop = 4, Walk = 5, Stretch = 6, Ball = 7, Book = 8, Coffee = 9, Wink = 10,
            Phone = 11, Game = 12, Snack = 13, Guitar = 14, Bubbles = 15, Skate = 16, Paint = 17;

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

        /// <summary>Неподвижный Клоди (те же пиксели) — аватар у ответов в чате, без покадровой анимации.</summary>
        public static ImageSource Avatar { get; } = BuildAvatar();

        private static ImageSource BuildAvatar()
        {
            var body = new SolidColorBrush(Color.FromRgb(0xE0, 0x7A, 0x5F)); body.Freeze();
            var group = new DrawingGroup();
            using (var dc = group.Open())
            {
                dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, W, H));
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                        if (Sprite[y][x] != '.') dc.DrawRectangle(body, null, new Rect(x, y, 1.02, 1.02));
                dc.DrawRectangle(Brushes.Black, null, new Rect(EyeL.X, EyeL.Y, 1, 2));
                dc.DrawRectangle(Brushes.Black, null, new Rect(EyeR.X, EyeR.Y, 1, 2));
            }
            group.Freeze();
            var img = new DrawingImage(group);
            img.Freeze();
            return img;
        }

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
            // «танцевальное» время идёт в темпе музыки: быстрый трек — быстрые движения, медленный — плавные
            double speed = Bpm > 0 ? Math.Clamp(Bpm / 128.0, 0.6, 1.45) : 1;
            _dtime += dt * speed;
            Tick();
            InvalidateVisual();
        }

        // ---- танцы под музыку: меняются сами каждые 7–12 секунд
        private int _dance;
        private double _dtime;
        /// <summary>Темп музыки (удары в минуту, 0 — неизвестен) и её «плотность» 0..1 — по ним выбирается танец.</summary>
        public double Bpm { get; set; }
        public double Energy { get; set; }
        private DateTime _nextDance = DateTime.Now.AddSeconds(6); // первым всегда классический танец
        private double _danceStart;
        public const int DanceCount = 9;
        private static readonly string[] DanceNames = { "прыжки", "покачивание", "присед", "робот", "хедбэнг", "кружится", "диско", "лунная походка", "брейк-данс" };

        /// <summary>
        /// Брейк-данс, цикл ~6 с: падает на спину → крутится на спине, болтая ножками → вскакивает в «фриз» с руками вверх.
        /// Возвращает угол поворота вокруг центра тела, сдвиг вниз (в пикселях спрайта) и фазу.
        /// </summary>
        private (double angle, double down, double phase) Breakdance()
        {
            double ph = ((_dtime - _danceStart) * 0.17) % 1; // от начала танца: всегда начинает с падения
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
        /// <summary>
        /// Танец по музыке: медленная — покачивание и лунная походка, средняя — классика, диско и робот,
        /// быстрая — прыжки, присед и хедбэнг, очень быстрая и плотная — хедбэнг и брейк-данс.
        /// Темп ещё не понят — любой. Один и тот же танец два раза подряд не идёт.
        /// </summary>
        public static int PickDance(double bpm, double energy, int current, Random rnd)
        {
            int[] pool;
            if (bpm <= 0) pool = new[] { 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 };
            else if (bpm < 95) pool = new[] { 1, 1, 7, 7, 5, 0, 6 };
            else if (bpm < 122) pool = new[] { 0, 0, 6, 6, 3, 3, 1, 5, 7 };
            else if (bpm < 150 && energy < 0.55) pool = new[] { 0, 0, 2, 2, 6, 3, 4, 8 };
            else pool = new[] { 4, 4, 2, 2, 8, 8, 0 };
            int d = current;
            for (int i = 0; i < 8 && d == current; i++) d = pool[rnd.Next(pool.Length)];
            return d;
        }

        private double DanceArm(bool left)
        {
            double side = left ? 0 : Math.PI;
            switch (_dance)
            {
                case 1: return Math.Sin(_dtime * 3.2 + side) > 0 ? -1 : 0;                          // машет в такт покачиванию
                case 2: return (_dtime * 1.9) % 1 >= 0.55 ? -2 : 0;                                    // в прыжке — руки вверх
                case 3: { int pose = (int)(_dtime * 3.5) % 4; return (pose + (left ? 0 : 2)) % 4 < 2 ? -2 : 1; } // робот: вверх-вниз рывками
                case 4: return Math.Abs(Math.Sin(_dtime * 6.5)) > 0.7 ? 1 : 0;                         // хедбэнг — руки внизу, дёргаются
                case 5: return -1 - (Math.Sin(_dtime * 6 + side) > 0 ? 1 : 0);                         // кружится, руки в стороны и вверх
                case 6: return (Math.Sin(_dtime * 1.6) > 0) == left ? -3 : 0.5;                        // диско: одна рука указывает в небо
                case 7: return Math.Sin(_dtime * 9 + side) > 0 ? -0.5 : 0.5;                           // лунная походка — руки покачиваются
                case 8:
                {
                    var ph = Breakdance().phase;
                    if (ph >= 0.8) return -3;                                                            // фриз — руки в небо
                    if (ph >= 0.15) return Math.Sin(_dtime * 16 + side) > 0 ? -1 : 0.5;                 // на спине — машет ручками
                    return -1;                                                                           // падает — руки в стороны
                }
                default: return Math.Sin(_dtime * 10 + side) > 0 ? -1 : 0;
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
            _lookX += (tx - _lookX) * FrameClock.Lerp(0.28);
            _lookY += (ty - _lookY) * FrameClock.Lerp(0.28);

            // ---- зевок: раз в 7–15 секунд, ~1,6 с
            if (mood == Mood.Drowsy)
            {
                if (_yawn > 0) _yawn = Math.Min(1.0001, _yawn + 0.025 * FrameClock.K);
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
                _blink += (target - _blink) * FrameClock.Lerp(0.15);
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
                _blink += (target - _blink) * FrameClock.Lerp(0.6);
            }
            else _blink += (1 - _blink) * FrameClock.Lerp(0.2);

            if (_cheer > 0) _cheer = Math.Max(0, _cheer - 0.035 * FrameClock.K);
            if (mood == Mood.Idle && DateTime.Now >= _activityUntil) StartActivity(Stand, 0); // короткое занятие закончилось
            if (mood == Mood.Idle && DateTime.Now >= _nextActivity)
            {
                // своё занятие: стоит, сидит, оглядывается, машет, прыгает, гуляет, потягивается,
                // подкидывает мячик, читает книжку, пьёт кофе (по утрам), подмигивает
                int h = DateTime.Now.Hour;
                var pool = new System.Collections.Generic.List<int> { Stand, Sit, Sit, Look, Look, Wave, Hop, Walk, Walk, Stretch, Ball, Ball, Book, Book, Wink };
                if (h >= 6 && h < 12) { pool.Add(Coffee); pool.Add(Coffee); pool.Add(Coffee); }
                pool.AddRange(new[] { Phone, Phone, Game, Game, Snack, Guitar, Guitar, Bubbles, Skate, Paint });
                int a = pool[_rnd.Next(pool.Count)];
                if (a == _activity) a = Stand;
                double len = a switch { Wave => 2.2, Hop => 2.0, Stretch => 2.4, Wink => 0.9, Walk => 9 + _rnd.Next(5), Skate => 8 + _rnd.Next(4), Snack => 6, _ => 0 };
                StartActivity(a, len);
                _nextActivity = DateTime.Now.AddSeconds(a == Walk ? len + 4 : 14 + _rnd.Next(22));
            }
            if (mood != Mood.Idle && _activity != Stand && _activity != Sit && _activity != Look) StartActivity(Stand, 0);
            if (mood == Mood.Idle && _activity == Book)
            {
                // читает: глаза бегут по строчкам вниз-вправо и возвращаются
                double line = (_time * 0.7) % 1;
                _lookX += ((line * 1.6 - 0.8) - _lookX) * FrameClock.Lerp(0.3); _lookY += (0.85 - _lookY) * FrameClock.Lerp(0.3);
            }
            if (mood == Mood.Idle && (_activity == Phone || _activity == Game || _activity == Paint))
            {
                // уткнулся в телефон / приставку / холст: смотрит вниз-вперёд, иногда косится по сторонам
                double gx = _activity == Paint ? 0.6 : Math.Sin(_time * 0.7) * 0.3;
                _lookX += (gx - _lookX) * FrameClock.Lerp(0.25); _lookY += (0.75 - _lookY) * FrameClock.Lerp(0.25);
            }
            if (mood == Mood.Idle && _activity == Ball)
            {
                // следит за мячиком сбоку: глаза вправо, вверх-вниз за полётом
                var (_, by) = BallPos();
                _lookX += (0.85 - _lookX) * FrameClock.Lerp(0.3); _lookY += ((by - 4.5) / 4.5 - _lookY) * FrameClock.Lerp(0.3);
            }
            if (mood == Mood.Idle && _activity == 2)
            {
                if (DateTime.Now >= _nextWander)
                {
                    _nextWander = DateTime.Now.AddSeconds(0.8 + _rnd.NextDouble() * 1.5);
                    _wander = new Point(_rnd.NextDouble() * 2 - 1, _rnd.NextDouble() * 1.2 - 0.5);
                }
                _lookX += (_wander.X - _lookX) * FrameClock.Lerp(0.2); _lookY += (_wander.Y - _lookY) * FrameClock.Lerp(0.2);
            }
            _bob += ((mood == Mood.Music ? Math.Min(1, Beat * 2.2) : 0) - _bob) * FrameClock.Lerp(0.5);
            if (mood == Mood.Music && DateTime.Now >= _nextDance)
            {
                _dance = PickDance(Bpm, Energy, _dance, _rnd); // по темпу и «плотности» музыки
                _danceStart = _dtime;
                // брейк-данс — ровно два полных круга (падение → вращение → фриз), чтобы не обрывался посреди
                _nextDance = DateTime.Now.AddSeconds(_dance == 8 ? 2 / 0.17 : 7 + _rnd.NextDouble() * 5);
            }
            if (mood == Mood.Music && (_dance == 4)) { _lookY += (0.8 - _lookY) * FrameClock.Lerp(0.3); } // хедбэнг — смотрит в пол
        }

        private static readonly Brush BookCover = Freeze(new SolidColorBrush(Color.FromRgb(0x3B, 0x6F, 0xD9)));
        private static readonly Brush Paper = Freeze(new SolidColorBrush(Color.FromRgb(0xF4, 0xF1, 0xE8)));
        private static readonly Brush BallRed = Freeze(new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)));
        private static readonly Brush Cup = Freeze(new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xF1)));
        private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

        /// <summary>Фаза чеканки 0..1 (0 — мяч у ноги) и положение мяча в клетках спрайта: справа от тела, по параболе.</summary>
        private double BallPhase() => (_time * 1.5) % 1;
        private (double x, double y) BallPos()
        {
            double p = BallPhase();
            double h = 4 * p * (1 - p);              // 0 у ноги → 1 в верхней точке
            return (13.2 + Math.Sin(p * Math.PI) * 0.4, 8.6 - h * 5.2);
        }

        private static void DrawHeart(DrawingContext dc, double x, double y, double s)
        {
            var b = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
            dc.DrawEllipse(b, null, new Point(x - s * 0.25, y), s * 0.3, s * 0.3);
            dc.DrawEllipse(b, null, new Point(x + s * 0.25, y), s * 0.3, s * 0.3);
            var g = new StreamGeometry();
            using (var c = g.Open()) { c.BeginFigure(new Point(x - s * 0.55, y + s * 0.05), true, true); c.LineTo(new Point(x + s * 0.55, y + s * 0.05), true, false); c.LineTo(new Point(x, y + s * 0.7), true, false); }
            g.Freeze();
            dc.DrawGeometry(b, null, g);
        }

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
                    // чеканит мячик ногой сбоку от себя — мяч не залезает на голову и шапку
                    var (bx, by) = BallPos();
                    dc.DrawEllipse(BallRed, null, new Point(ox + bx * px, oy + by * px), 0.85 * px, 0.85 * px);
                    dc.DrawRectangle(Cup, null, new Rect(ox + (bx - 0.3) * px, oy + (by - 0.55) * px, 0.35 * px, 0.35 * px)); // блик
                    break;
                }
                case Phone:
                {
                    // телефон в правой ручке, экран светится и «листается»
                    double x = ox + 10.6 * px, y = oy + 2.6 * px;
                    dc.DrawRectangle(Brushes.Black, null, new Rect(x, y, 1.6 * px, 2.6 * px));
                    var glow = new SolidColorBrush(Color.FromRgb(0x7C, 0xC4, 0xFF));
                    dc.DrawRectangle(glow, null, new Rect(x + 0.2 * px, y + 0.2 * px, 1.2 * px, 2.2 * px));
                    double scroll = (_time * 1.4) % 1;
                    dc.DrawRectangle(Brushes.White, null, new Rect(x + 0.35 * px, y + (0.4 + scroll * 1.5) * px, 0.9 * px, 0.25 * px));
                    if ((_time % 7) < 0.8) DrawHeart(dc, ox + 11.2 * px, oy - (0.6 + (_time % 7) * 2) * px, px * 0.9); // лайкнул
                    break;
                }
                case Game:
                {
                    // приставка в обеих ручках: экран мигает, кнопки
                    double x = ox + 3.5 * px, y = oy + 5 * px;
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x44)), null, new Rect(x, y, 6 * px, 2.4 * px));
                    var scr = Color.FromRgb(0x4A, 0xDE, 0x80);
                    dc.DrawRectangle(new SolidColorBrush(Math.Sin(_time * 6) > 0.8 ? Colors.White : scr), null, new Rect(x + 1.8 * px, y + 0.4 * px, 2.4 * px, 1.6 * px));
                    dc.DrawRectangle(BallRed, null, new Rect(x + 4.7 * px, y + 0.6 * px, 0.6 * px, 0.6 * px));
                    dc.DrawRectangle(Cup, null, new Rect(x + 0.5 * px, y + 0.9 * px, 0.9 * px, 0.4 * px));
                    break;
                }
                case Snack:
                {
                    // печенье в левой ручке, крошки падают
                    bool bite = Math.Sin(_time * 1.3) > 0.6;
                    double x = bite ? ox + 4.6 * px : ox - 1.2 * px, y = bite ? oy + 3.6 * px : oy + 3.4 * px;
                    dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xC8, 0x8A, 0x4A)), null, new Point(x + 0.8 * px, y + 0.8 * px), 0.9 * px, 0.9 * px);
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x5B, 0x34, 0x1C)), null, new Rect(x + 0.5 * px, y + 0.5 * px, 0.3 * px, 0.3 * px));
                    for (int i = 0; i < 2; i++)
                    {
                        double ph = (_time * 1.1 + i * 0.5) % 1;
                        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(200 * (1 - ph)), 0xC8, 0x8A, 0x4A)), null, new Rect(ox + (5.5 + i) * px, oy + (5 + ph * 4) * px, 0.35 * px, 0.35 * px));
                    }
                    break;
                }
                case Guitar:
                {
                    // гитара поперёк живота, над головой всплывают ноты
                    var wood = new SolidColorBrush(Color.FromRgb(0xB4, 0x6A, 0x2E));
                    dc.DrawEllipse(wood, null, new Point(ox + 4.2 * px, oy + 5.8 * px), 1.9 * px, 1.5 * px);
                    dc.DrawEllipse(Brushes.Black, null, new Point(ox + 4.4 * px, oy + 5.7 * px), 0.45 * px, 0.45 * px);
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x5B, 0x34, 0x1C)), null, new Rect(ox + 5.6 * px, oy + 4.9 * px, 5.8 * px, 0.6 * px));
                    for (int i = 0; i < 2; i++)
                    {
                        double ph = (_time * 0.6 + i * 0.5) % 1;
                        var note = new SolidColorBrush(Color.FromArgb((byte)(230 * (1 - ph)), 0xFF, 0xE9, 0xD6));
                        double nx = ox + (9 + i * 2 + Math.Sin(_time * 2 + i) * 0.5) * px, ny = oy - (0.5 + ph * 3.5) * px;
                        dc.DrawRectangle(note, null, new Rect(nx, ny, 0.7 * px, 0.6 * px));
                        dc.DrawRectangle(note, null, new Rect(nx + 0.5 * px, ny - 1.2 * px, 0.25 * px, 1.4 * px));
                    }
                    break;
                }
                case Bubbles:
                {
                    // палочка с кольцом в левой ручке, мыльные пузыри поднимаются и лопаются
                    var ring = new Pen(new SolidColorBrush(Color.FromRgb(0xF4, 0xF1, 0xE8)), 0.25 * px);
                    dc.DrawEllipse(null, ring, new Point(ox - 0.8 * px, oy + 2.4 * px), 0.6 * px, 0.6 * px);
                    for (int i = 0; i < 3; i++)
                    {
                        double ph = (_time * 0.35 + i / 3.0) % 1;
                        double bx = ox + (-1 - ph * 3 + Math.Sin(_time * 2 + i * 2) * 0.6) * px, by = oy + (2 - ph * 6) * px;
                        var c = Color.FromArgb((byte)(ph > 0.9 ? 0 : 150), 0xB9, 0xE6, 0xFF);
                        dc.DrawEllipse(null, new Pen(new SolidColorBrush(c), 0.2 * px), new Point(bx, by), (0.5 + i * 0.15) * px, (0.5 + i * 0.15) * px);
                    }
                    break;
                }
                case Skate:
                {
                    // скейт под ножками
                    var deck = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
                    dc.DrawRectangle(deck, null, new Rect(ox + 1.2 * px, oy + 9 * px, 10.6 * px, 0.7 * px));
                    dc.DrawEllipse(Cup, null, new Point(ox + 3 * px, oy + 10.1 * px), 0.5 * px, 0.5 * px);
                    dc.DrawEllipse(Cup, null, new Point(ox + 10 * px, oy + 10.1 * px), 0.5 * px, 0.5 * px);
                    break;
                }
                case Paint:
                {
                    // маленький холст справа, на нём постепенно появляются мазки
                    double x = ox + 13.4 * px, y = oy + 1 * px;
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x8B, 0x5A, 0x2B)), null, new Rect(x + 1.4 * px, y + 4.5 * px, 0.4 * px, 3.6 * px));
                    dc.DrawRectangle(Paper, null, new Rect(x, y, 3.2 * px, 4.6 * px));
                    double progress = (_time * 0.15) % 1;
                    Color[] colors = { Color.FromRgb(0xF8, 0x71, 0x71), Color.FromRgb(0x4A, 0xDE, 0x80), Color.FromRgb(0x60, 0xA5, 0xFA), Color.FromRgb(0xFA, 0xCC, 0x15) };
                    for (int i = 0; i < 4 && i < progress * 5; i++)
                        dc.DrawRectangle(new SolidColorBrush(colors[i]), null, new Rect(x + (0.3 + (i % 2) * 1.4) * px, y + (0.4 + (i / 2) * 2) * px, 1.3 * px, 1.6 * px));
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
                    if (_activity == Skate)
                    {
                        // катается на скейте туда-сюда, на разворотах чуть приседает
                        dx = Math.Sin(_time * 0.8) * 1.6 * px;
                        tilt = Math.Cos(_time * 0.8) * 6;
                        dy -= 1.0 * px;
                    }
                    if (_activity == Guitar) tilt = Math.Sin(_time * 2.4) * 3;
                    if (_activity == Snack) dy += Math.Abs(Math.Sin(_time * 9)) * 0.15 * px; // жуёт
                    if (_activity == Wave) tilt = Math.Sin(_time * 7) * 3;
                    break;
                case Mood.Music:
                    // танцует всегда, даже на тихих местах; под громкие доли прыгает выше
                    switch (_dance)
                    {
                        case 1: // покачивание из стороны в сторону
                            dx = Math.Sin(_dtime * 3.2) * 1.6 * px;
                            tilt = Math.Sin(_dtime * 3.2) * 12;
                            dy = -(Math.Abs(Math.Sin(_dtime * 6.4)) * 0.4 + _bob * 0.8) * px;
                            step = Math.Sin(_dtime * 3.2) > 0;
                            break;
                        case 2: // присед и прыжок на сильную долю
                        {
                            double ph = (_dtime * 1.9) % 1;
                            dy = ph < 0.55 ? Math.Sin(ph / 0.55 * Math.PI) * 1.2 * px : -Math.Sin((ph - 0.55) / 0.45 * Math.PI) * (2.2 + _bob * 1.2) * px;
                            step = ph >= 0.55;
                            break;
                        }
                        case 3: // робот: резкие фиксированные позы
                        {
                            int pose = (int)(_dtime * 3.5) % 4;
                            dx = (pose == 1 ? 1 : pose == 3 ? -1 : 0) * px;
                            tilt = pose == 1 ? 6 : pose == 3 ? -6 : 0;
                            dy = -_bob * 0.6 * px;
                            step = pose % 2 == 1;
                            break;
                        }
                        case 4: // хедбэнг: резкие кивки в бит
                            tilt = Math.Pow(Math.Abs(Math.Sin(_dtime * 6.5)), 3) * 14 + _bob * 6;
                            dy = Math.Abs(Math.Sin(_dtime * 6.5)) * 0.7 * px;
                            break;
                        case 5: // кружится (поворачивается то одним боком, то другим)
                            dy = -(Math.Abs(Math.Sin(_dtime * 6)) * 0.9 + _bob) * px;
                            step = Math.Sin(_dtime * 6) > 0;
                            break;
                        case 6: // диско: рука в небо то слева, то справа, бёдрами в бит
                            dx = Math.Sin(_dtime * 7) * 0.7 * px;
                            tilt = Math.Sin(_dtime * 1.6) > 0 ? 7 : -7;
                            dy = -_bob * 1.0 * px;
                            step = Math.Sin(_dtime * 7) > 0;
                            break;
                        case 7: // лунная походка: скользит назад, переставляя ножки
                        {
                            double ph = (_dtime * 0.35) % 1;
                            dx = (1.6 - ph * 3.2) * px;
                            step = Math.Sin(_dtime * 9) > 0;
                            dy = -_bob * 0.5 * px;
                            tilt = -4;
                            break;
                        }
                        case 8: // брейк-данс: угол и сдвиг считаются в Breakdance(), здесь — ножки
                        {
                            double bph = Breakdance().phase;
                            step = bph >= 0.15 && bph < 0.8 && Math.Sin(_dtime * 16) > 0; // на спине болтает ножками
                        }
                            break;
                        default: // прыжки (как раньше)
                            dy = -(Math.Abs(Math.Sin(_dtime * 7.5)) * 1.1 + _bob * 1.4) * px;
                            tilt = Math.Sin(_dtime * 3.75) * 9;
                            dx = Math.Sin(_dtime * 3.75) * 0.8 * px;
                            step = Math.Sin(_dtime * 7.5) > 0;
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
            double spin = mood == Mood.Music && _dance == 5 ? Math.Cos(_dtime * 2.6) : 1;
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
                        // чеканка: правая ножка подбрасывает мяч, когда он у ноги
                        if (mood == Mood.Idle && _activity == Ball && _cheer <= 0 && x == 10 && y == 8 && (BallPhase() < 0.12 || BallPhase() > 0.94)) continue;
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
                        if (_activity == Ball) cy = x == 0 ? y - 1 : y; // левая ручка в сторону для равновесия
                        if (_activity == Coffee && x == 12) cy = y - (Math.Sin(_time * 0.8) > 0.85 ? 2 : 0); // отпивает
                        if (_activity == Phone && x == 12) cy = y - 1;                                          // держит телефон
                        if (_activity == Game) cy = y - (Math.Sin(_time * 18 + (x == 0 ? 0 : 2)) > 0.4 ? 1 : 0); // жмёт кнопки
                        if (_activity == Snack && x == 0) cy = y - (Math.Sin(_time * 1.3) > 0.6 ? 2 : 0);       // откусывает печенье
                        if (_activity == Guitar && x == 12) cy = y + (Math.Sin(_time * 12) > 0 ? 1 : 0);        // бренчит
                        if (_activity == Bubbles && x == 0) cy = y - 1;                                        // держит палочку для пузырей
                        if (_activity == Paint && x == 12) cy = y - 1 - (Math.Sin(_time * 4) > 0 ? 1 : 0);    // водит кистью
                        if (_activity == Skate) cy = y - 1;                                                    // балансирует руками
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
                if (Holiday() == null) // в праздник вместо шапки — праздничный колпак
                {
                    for (int x = 2; x <= 10; x++) dc.DrawRectangle(hat, null, new Rect(ox + x * px, oy - 1 * px, px + 0.35, px + 0.35));
                    for (int x = 3; x <= 9; x++) dc.DrawRectangle(hat, null, new Rect(ox + x * px, oy - 2 * px, px + 0.35, px + 0.35));
                    for (int x = 2; x <= 10; x++) dc.DrawRectangle(band, null, new Rect(ox + x * px, oy + 0 * px, px + 0.35, px * 0.6));
                    dc.DrawRectangle(band, null, new Rect(ox + 6 * px, oy - 3 * px, px + 0.35, px + 0.35)); // помпон
                }
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
            else if (Outfit == Outfit.Sunny && !nightcap && Holiday() == null)
            {
                // солнечно и тепло — кепка козырьком набок
                var capB = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
                for (int x = 2; x <= 10; x++) dc.DrawRectangle(capB, null, new Rect(ox + x * px, oy - 1 * px, px + 0.35, px + 0.35));
                for (int x = 4; x <= 8; x++) dc.DrawRectangle(capB, null, new Rect(ox + x * px, oy - 1.8 * px, px + 0.35, px * 0.9));
                dc.DrawRectangle(capB, null, new Rect(ox + 9 * px, oy - 0.3 * px, 3.5 * px, 0.6 * px)); // козырёк
                dc.DrawRectangle(Cup, null, new Rect(ox + 6 * px, oy - 2.1 * px, px * 0.6, px * 0.4));
            }
            else if (Outfit == Outfit.Rain && !nightcap)
            {
                var canopy = new SolidColorBrush(Color.FromRgb(0x5B, 0x6C, 0xF0));
                for (int x = 1; x <= 11; x++) dc.DrawRectangle(canopy, null, new Rect(ox + x * px, oy - 2 * px, px + 0.35, px + 0.35));
                for (int x = 3; x <= 9; x++) dc.DrawRectangle(canopy, null, new Rect(ox + x * px, oy - 3 * px, px + 0.35, px + 0.35));
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x8B, 0x8B, 0x95)), null, new Rect(ox + 6 * px, oy - 1 * px, px * 0.6, px));
            }

            // ---- праздничные шапки (вместо обычной): Новый год — колпак Деда Мороза, Хэллоуин — шляпа ведьмы
            var holiday = Holiday();
            if (holiday != null && !nightcap && Outfit != Outfit.Rain)
            {
                if (holiday == "ny")
                {
                    var red = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
                    for (int x = 2; x <= 10; x++) dc.DrawRectangle(Cup, null, new Rect(ox + x * px, oy - 0.2 * px, px + 0.35, px * 0.8));
                    for (int x = 3; x <= 10; x++) dc.DrawRectangle(red, null, new Rect(ox + x * px, oy - 1.2 * px, px + 0.35, px + 0.35));
                    for (int x = 5; x <= 10; x++) dc.DrawRectangle(red, null, new Rect(ox + x * px, oy - 2.2 * px, px + 0.35, px + 0.35));
                    dc.DrawRectangle(red, null, new Rect(ox + 11 * px, oy - 1.6 * px, px + 0.35, px + 0.35));
                    dc.DrawEllipse(Cup, null, new Point(ox + 12.4 * px, oy - 0.6 * px), 0.6 * px, 0.6 * px);
                }
                else
                {
                    var witch = new SolidColorBrush(Color.FromRgb(0x3B, 0x1F, 0x5C));
                    for (int x = 1; x <= 11; x++) dc.DrawRectangle(witch, null, new Rect(ox + x * px, oy - 0.6 * px, px + 0.35, px * 0.6));
                    for (int x = 4; x <= 8; x++) dc.DrawRectangle(witch, null, new Rect(ox + x * px, oy - 1.6 * px, px + 0.35, px + 0.35));
                    for (int x = 5; x <= 7; x++) dc.DrawRectangle(witch, null, new Rect(ox + x * px, oy - 2.6 * px, px + 0.35, px + 0.35));
                    dc.DrawRectangle(witch, null, new Rect(ox + 7 * px, oy - 3.6 * px, px + 0.35, px + 0.35));
                    for (int x = 4; x <= 8; x++) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF9, 0x73, 0x16)), null, new Rect(ox + x * px, oy - 0.9 * px, px + 0.35, px * 0.35));
                }
            }

            // ---- наушники, пока играет музыка
            if (mood == Mood.Music && !nightcap)
            {
                var hp = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x24));
                var pad = new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA));
                for (int x = 2; x <= 10; x++) dc.DrawRectangle(hp, null, new Rect(ox + x * px, oy - 1 * px, px + 0.35, px * 0.55)); // дужка
                dc.DrawRectangle(hp, null, new Rect(ox + 1.4 * px, oy - 0.6 * px, px * 0.7, px * 2.4));
                dc.DrawRectangle(hp, null, new Rect(ox + 10.9 * px, oy - 0.6 * px, px * 0.7, px * 2.4));
                dc.DrawRectangle(pad, null, new Rect(ox + 1.1 * px, oy + 1 * px, px * 1.4, px * 2));
                dc.DrawRectangle(pad, null, new Rect(ox + 10.5 * px, oy + 1 * px, px * 1.4, px * 2));
            }

            // ---- очки ботаника за работой
            bool nerd = mood == Mood.Working && Outfit != Outfit.Summer;
            if (nerd)
            {
                var frame = new Pen(new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x24)), 0.35 * px);
                dc.DrawRectangle(null, frame, new Rect(ox + 3.2 * px, oy + 1.8 * px, 2.6 * px, 2.2 * px));
                dc.DrawRectangle(null, frame, new Rect(ox + 7.2 * px, oy + 1.8 * px, 2.6 * px, 2.2 * px));
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x24)), null, new Rect(ox + 5.8 * px, oy + 2.4 * px, 1.4 * px, 0.3 * px));
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

            // ---- снегопад: снежинки падают вокруг Клоди
            if (Snowing)
            {
                for (int i = 0; i < 5; i++)
                {
                    double ph = (_time * 0.25 + i * 0.21) % 1;
                    double sx = (i * 0.23 + 0.05) % 1 * ActualWidth + Math.Sin(_time * 1.5 + i) * px;
                    var flake = new SolidColorBrush(Color.FromArgb((byte)(220 * Math.Sin(ph * Math.PI)), 255, 255, 255));
                    dc.DrawRectangle(flake, null, new Rect(sx, ph * ActualHeight, px * 0.5, px * 0.5));
                }
            }

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
