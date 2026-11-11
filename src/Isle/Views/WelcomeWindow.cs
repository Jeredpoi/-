using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Isle.Helpers;
using Isle.Services;

namespace Isle.Views
{
    /// <summary>
    /// Приветствие при самом первом запуске: что такое Isle (живой Клоди и карточки возможностей),
    /// затем личная настройка — точная скорость Steam (с перезапуском Steam, только по согласию)
    /// и PrintScreen через Isle. Шаги сменяются с анимацией, внизу — точки прогресса.
    /// </summary>
    public class WelcomeWindow : Window
    {
        private static readonly Brush Fg = Frz(Color.FromRgb(0xEC, 0xEC, 0xF1));
        private static readonly Brush Muted = Frz(Color.FromRgb(0x8B, 0x8B, 0x95));
        private static readonly Brush CardBg = Frz(Color.FromRgb(0x0E, 0x0E, 0x10));
        private static readonly Brush Tile = Frz(Color.FromRgb(0x18, 0x18, 0x1C));
        private static readonly Brush Line = Frz(Color.FromRgb(0x23, 0x23, 0x2A));
        private static readonly Brush Green = Frz(Color.FromRgb(0x4A, 0xDE, 0x80));
        private static readonly Brush Red = Frz(Color.FromRgb(0xF8, 0x71, 0x71));
        private static SolidColorBrush Frz(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        private readonly Grid _host = new();
        private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 18) };
        private readonly Character _buddy = new() { Width = 130, Height = 96, HorizontalAlignment = HorizontalAlignment.Center };
        private int _step = -1;
        private readonly bool _hasSteam;
        private int Steps => _hasSteam ? 4 : 3;

        public WelcomeWindow()
        {
            Title = "Добро пожаловать в Isle";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowInTaskbar = true;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Width = 600; Height = 500;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            _hasSteam = SteamDownloadService.SteamRoot() != null;

            var card = new Border
            {
                Background = CardBg, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(28),
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            var root = new DockPanel();
            DockPanel.SetDock(_dots, Dock.Bottom);
            root.Children.Add(_dots);
            root.Children.Add(_host);
            card.Child = root;
            Content = card;
            card.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is Border || e.OriginalSource is Grid) try { DragMove(); } catch { } };
            KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

            for (int i = 0; i < Steps; i++)
                _dots.Children.Add(new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), Margin = new Thickness(4, 0, 4, 0), Background = Line });

            // окно «вырастает» из центра
            Loaded += (_, __) =>
            {
                var st = new ScaleTransform(0.9, 0.9);
                card.RenderTransform = st;
                var ease = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut };
                st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(520)) { EasingFunction = ease });
                st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(520)) { EasingFunction = ease });
                card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)));
                Journal.Info("welcome", "приветствие открыто, Steam " + (_hasSteam ? "найден" : "не найден"));
                Next();
            };
            Closed += (_, __) =>
            {
                AppSettings.Current.WelcomeShown = true;
                AppSettings.Current.Save();
                Journal.Info("welcome", "приветствие закрыто на шаге " + _step);
            };
        }

        // ---------- шаги ----------

        private void Next()
        {
            _step++;
            int page = _step;
            if (!_hasSteam && page >= 1) page++; // без Steam шаг про точную скорость пропускаем
            FrameworkElement view = page switch
            {
                0 => Intro(),
                1 => SteamStep(),
                2 => PrintScreenStep(),
                3 => Done(),
                _ => null
            };
            if (view == null) { Close(); return; }
            Show(view);
            for (int i = 0; i < _dots.Children.Count; i++)
            {
                var d = (Border)_dots.Children[i];
                d.Background = i == _step ? Fg : Line;
                d.BeginAnimation(WidthProperty, new DoubleAnimation(i == _step ? 22 : 7, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
        }

        /// <summary>Шаг въезжает справа и проявляется, прошлый уезжает влево.</summary>
        private void Show(FrameworkElement view)
        {
            var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
            if (_host.Children.Count > 0 && _host.Children[^1] is FrameworkElement old)
            {
                var ot = new TranslateTransform();
                old.RenderTransform = ot;
                ot.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, -40, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
                var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
                fade.Completed += (_, __) => _host.Children.Remove(old);
                old.BeginAnimation(OpacityProperty, fade);
                old.IsHitTestVisible = false;
            }
            if (_buddy.Parent is Panel p) p.Children.Remove(_buddy);
            var tt = new TranslateTransform(40, 0);
            view.RenderTransform = tt;
            view.Opacity = 0;
            _host.Children.Add(view);
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(40, 0, TimeSpan.FromMilliseconds(420)) { BeginTime = TimeSpan.FromMilliseconds(80), EasingFunction = ease });
            view.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { BeginTime = TimeSpan.FromMilliseconds(80) });
        }

        private StackPanel Page(string title, string text)
        {
            var sp = new StackPanel { Margin = new Thickness(40, 30, 40, 10) };
            sp.Children.Add(_buddy);
            sp.Children.Add(new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = Fg, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) });
            if (!string.IsNullOrEmpty(text))
                sp.Children.Add(new TextBlock { Text = text, FontSize = 14, Foreground = Muted, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 8, 0, 0) });
            return sp;
        }

        private Button Pill(string text, bool primary, Action click)
        {
            var b = new Button { Cursor = Cursors.Hand, Margin = new Thickness(6, 0, 6, 0), Focusable = false };
            b.Template = PillTemplate();
            b.Content = new Border
            {
                Height = 40, CornerRadius = new CornerRadius(20), Padding = new Thickness(22, 0, 22, 0),
                Background = primary ? Fg : Tile,
                Child = new TextBlock { Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Foreground = primary ? Brushes.Black : Fg }
            };
            b.Click += (_, __) => click();
            return b;
        }

        private static ControlTemplate PillTemplate()
        {
            var t = new ControlTemplate(typeof(Button));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(RenderTransformOriginProperty, new Point(0.5, 0.5));
            t.VisualTree = cp;
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(OpacityProperty, 0.88));
            t.Triggers.Add(hover);
            return t;
        }

        private StackPanel Buttons(params Button[] buttons)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 0) };
            foreach (var b in buttons) row.Children.Add(b);
            return row;
        }

        // 1) Что такое Isle
        private FrameworkElement Intro()
        {
            _buddy.Mood = Mood.Idle;
            var sp = Page("Привет! Это Isle", "Чёлка как у iPhone — только для Windows. Живёт сверху экрана и всегда под рукой.");
            var grid = new UniformGrid { Columns = 3, Margin = new Thickness(0, 18, 0, 0) };
            (string icon, string text)[] feats =
            {
                (Icons.Volume, "Музыка и тексты песен"), (Icons.Download, "Загрузки Steam и браузеров"), (Icons.Crop, "Снимки с пометками"),
                (Icons.Chat, "Чат с ИИ и голос"), (Icons.Copy, "История буфера"), (Icons.Bell, "Таймеры и напоминания"),
            };
            int i = 0;
            foreach (var (icon, text) in feats)
            {
                var chip = new Border { Background = Tile, CornerRadius = new CornerRadius(14), Margin = new Thickness(4), Padding = new Thickness(10, 9, 10, 9) };
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(new Icon { Data = icon, Width = 16, Height = 16, Foreground = Fg, VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = Fg, Margin = new Thickness(8, 0, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 120, VerticalAlignment = VerticalAlignment.Center });
                chip.Child = row;
                // карточки появляются по очереди и чуть «подпрыгивают»
                var tt = new TranslateTransform(0, 10);
                chip.RenderTransform = tt;
                chip.Opacity = 0;
                var begin = TimeSpan.FromMilliseconds(350 + i++ * 110);
                tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(480)) { BeginTime = begin, EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut } });
                chip.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { BeginTime = begin });
                grid.Children.Add(chip);
            }
            sp.Children.Add(grid);
            sp.Children.Add(Buttons(Pill("Дальше", true, Next)));
            return sp;
        }

        // 2) Точная скорость Steam — только с согласия: Steam и игры из него закроются
        private FrameworkElement SteamStep()
        {
            _buddy.Mood = Mood.Thinking;
            var sp = Page("Точная скорость Steam",
                "Isle может показывать скорость, объём и оставшееся время загрузок прямо из Steam — точно как в самом Steam. " +
                "Для этого Steam нужно один раз перезапустить с особым ключом.");
            var warn = new TextBlock
            {
                Text = "Steam, игры из него и зависшие процессы Steam будут закрыты. Сохрани игру, если она запущена.",
                FontSize = 12, Foreground = Frz(Color.FromRgb(0xFB, 0xBF, 0x24)), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 10, 0, 0)
            };
            sp.Children.Add(warn);
            var status = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0), Visibility = Visibility.Collapsed };
            var ring = new Grid { Width = 26, Height = 26 };
            var spinner = new Ellipse { Stroke = Fg, StrokeThickness = 3, StrokeDashArray = new DoubleCollection { 14, 30 }, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform() };
            var check = new Icon { Data = Icons.Check, Width = 26, Height = 26, Foreground = Green, StrokeWidth = 2.6, Visibility = Visibility.Collapsed, RenderTransformOrigin = new Point(0.5, 0.5) };
            ring.Children.Add(spinner); ring.Children.Add(check);
            var statusText = new TextBlock { FontSize = 13, Foreground = Fg, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
            status.Children.Add(ring); status.Children.Add(statusText);
            sp.Children.Add(status);
            StackPanel buttons = null;
            var yes = Pill("Включить", true, async () =>
            {
                buttons.Visibility = Visibility.Collapsed;
                warn.Visibility = Visibility.Collapsed;
                status.Visibility = Visibility.Visible;
                status.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
                ((RotateTransform)spinner.RenderTransform).BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever });
                Journal.Info("welcome", "точная скорость Steam: пользователь согласился");
                var progress = new Progress<string>(t => { statusText.Text = t; Journal.Info("welcome", "Steam: " + t); });
                bool ok;
                try { ok = await SteamClientApi.ActivateAsync(progress); }
                catch (Exception ex) { ok = false; statusText.Text = "Ошибка: " + ex.Message; }
                ((RotateTransform)spinner.RenderTransform).BeginAnimation(RotateTransform.AngleProperty, null);
                spinner.Visibility = Visibility.Collapsed;
                check.Data = ok ? Icons.Check : Icons.Close;
                check.Foreground = ok ? Green : Red;
                check.Visibility = Visibility.Visible;
                var pop = new ScaleTransform(0.3, 0.3);
                check.RenderTransform = pop;
                var ease = new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut };
                pop.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
                pop.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
                if (ok) _buddy.Cheer();
                sp.Children.Add(Buttons(Pill("Дальше", true, Next)));
            });
            buttons = Buttons(Pill("Не сейчас", false, () => { Journal.Info("welcome", "точная скорость Steam: отказ"); Next(); }), yes);
            sp.Children.Add(buttons);
            return sp;
        }

        // 3) PrintScreen через Isle
        private FrameworkElement PrintScreenStep()
        {
            _buddy.Mood = Mood.Idle;
            var sp = Page("PrintScreen через Isle?",
                "Клавиша PrintScreen будет открывать выделение области Isle: мозаика, стрелки, текст, а снимок улетает прямо в чёлку.");
            sp.Children.Add(new TextBlock { Text = "Если не понравится, можно отключить в Настройках.", FontSize = 12, Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) });
            sp.Children.Add(Buttons(
                Pill("Оставить как в Windows", false, () => { Journal.Info("welcome", "PrintScreen: как в Windows"); Next(); }),
                Pill("Включить", true, () =>
                {
                    bool ok = AppHost.Window?.SetPrintScreen(true) ?? false;
                    Journal.Info("welcome", "PrintScreen через Isle: " + (ok ? "включён" : "Windows не дала поставить перехват"));
                    if (ok) _buddy.Cheer();
                    Next();
                })));
            return sp;
        }

        // 4) Готово
        private FrameworkElement Done()
        {
            _buddy.Mood = Mood.Done;
            _buddy.Cheer();
            var sp = Page("Всё готово!", "Наведи мышь на чёлку сверху экрана или нажми Ctrl+Alt+Пробел. Остальное — в Настройках.");
            sp.Children.Add(Buttons(Pill("Начать", true, Close)));
            return sp;
        }
    }
}
