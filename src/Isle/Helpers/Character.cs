using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Isle.Helpers
{
    public enum Mood { Idle, Music, Thinking, Done, Attention, Sleep }

    /// <summary>
    /// «Клоди» — пиксельный персонаж, который живёт в чёлке. Нарисован в Claude Design
    /// (лист «Персонаж»), здесь те же пиксели. Реагирует на музыку, ИИ, уведомления и простой.
    /// </summary>
    public class Character : FrameworkElement
    {
        // 12×10: # — тело, e — глаз, * — искорка, l — ножка
        private static readonly string[] Sprite =
        {
            ".....*......",
            "....***.....",
            ".....*......",
            "..########..",
            ".##########.",
            "##ee####ee##",
            "############",
            ".##########.",
            "..ll....ll..",
            "..ll....ll..",
        };

        public static readonly DependencyProperty MoodProperty = DependencyProperty.Register(
            nameof(Mood), typeof(Mood), typeof(Character), new FrameworkPropertyMetadata(Mood.Idle, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty BodyProperty = DependencyProperty.Register(
            nameof(Body), typeof(Brush), typeof(Character),
            new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x57)), FrameworkPropertyMetadataOptions.AffectsRender));

        public Mood Mood { get => (Mood)GetValue(MoodProperty); set => SetValue(MoodProperty, value); }
        public Brush Body { get => (Brush)GetValue(BodyProperty); set => SetValue(BodyProperty, value); }

        /// <summary>Громкость музыки 0..1 — персонаж подпрыгивает в такт.</summary>
        public float Beat { get; set; }

        private readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromMilliseconds(110) };
        private int _frame;
        private readonly Random _rnd = new();
        private int _blinkIn = 25;
        private int _doneFrames;

        public Character()
        {
            Width = 24; Height = 20;
            SnapsToDevicePixels = true;
            _t.Tick += (_, __) => { _frame++; InvalidateVisual(); };
            IsVisibleChanged += (_, __) => { if (IsVisible) _t.Start(); else _t.Stop(); };
        }

        /// <summary>Короткая радость (сделал скриншот, ИИ ответил, файл на полке).</summary>
        public void Cheer() { _doneFrames = 14; }

        protected override void OnRender(DrawingContext dc)
        {
            double px = Math.Min(ActualWidth / 12.0, ActualHeight / 11.0);
            if (px <= 0) return;
            var mood = _doneFrames > 0 ? Mood.Done : Mood;
            if (_doneFrames > 0) _doneFrames--;

            double dy = 0, dx = 0;
            bool eyesClosed = false;
            int eyeShift = 0;
            bool legsAlt = false;
            bool sparkOn = true;

            switch (mood)
            {
                case Mood.Idle:
                    dy = (_frame / 6) % 2 == 0 ? 0 : 0.5 * px; // лёгкое «дыхание»
                    if (--_blinkIn <= 0) { eyesClosed = true; if (_blinkIn < -1) _blinkIn = 20 + _rnd.Next(30); }
                    sparkOn = (_frame / 8) % 3 != 0;
                    break;
                case Mood.Music:
                    dy = -Math.Min(1.5, Beat * 3.5) * px;
                    legsAlt = (_frame / 2) % 2 == 0;
                    dx = ((_frame / 4) % 2 == 0 ? -0.5 : 0.5) * px;
                    break;
                case Mood.Thinking:
                    eyeShift = (_frame / 5) % 4 switch { 0 => -1, 2 => 1, _ => 0 };
                    sparkOn = _frame % 2 == 0;
                    break;
                case Mood.Done:
                    dy = -Math.Abs(Math.Sin(_frame * 0.6)) * 2 * px;
                    legsAlt = true;
                    break;
                case Mood.Attention:
                    dx = (_frame % 2 == 0 ? -0.6 : 0.6) * px;
                    break;
                case Mood.Sleep:
                    eyesClosed = true;
                    dy = 0.5 * px;
                    sparkOn = false;
                    break;
            }

            var body = Body;
            var eye = Brushes.Black;
            var spark = new SolidColorBrush(Color.FromRgb(0xFF, 0xE9, 0xD6));
            double ox = (ActualWidth - 12 * px) / 2 + dx;
            double oy = (ActualHeight - 10 * px) / 2 + dy;

            for (int y = 0; y < Sprite.Length; y++)
            {
                var row = Sprite[y];
                for (int x = 0; x < row.Length; x++)
                {
                    char c = row[x];
                    if (c == '.') continue;
                    Brush b = body;
                    double cx = x, cy = y;
                    if (c == '*') { if (!sparkOn) continue; b = spark; }
                    if (c == 'e')
                    {
                        dc.DrawRectangle(body, null, new Rect(ox + x * px, oy + y * px, px + 0.3, px + 0.3));
                        if (eyesClosed) { b = body; }
                        else { b = eye; cx = x + eyeShift; }
                    }
                    if (c == 'l' && legsAlt && (x == 2 || x == 3) && y == 9) continue; // шаг
                    if (c == 'l' && legsAlt && (x == 8 || x == 9) && y == 8) cy = y + 1;
                    dc.DrawRectangle(b, null, new Rect(ox + cx * px, oy + cy * px, px + 0.3, px + 0.3));
                }
            }
            // закрытые глаза — тёмная полоска
            if (eyesClosed)
            {
                dc.DrawRectangle(eye, null, new Rect(ox + 2 * px, oy + 5.6 * px, 2 * px, 0.5 * px));
                dc.DrawRectangle(eye, null, new Rect(ox + 8 * px, oy + 5.6 * px, 2 * px, 0.5 * px));
            }
            if (mood == Mood.Sleep && (_frame / 6) % 2 == 0)
            {
                var z = new FormattedText("z", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 6 * px / 2, spark, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(z, new Point(ox + 11 * px, oy - 1 * px));
            }
        }
    }
}
