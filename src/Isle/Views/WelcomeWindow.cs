using System;
using System.Collections.Generic;
using System.Windows.Threading;
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
        private Border _card;
        private bool _flying, _landed;
        private readonly List<Func<FrameworkElement>> _pages = new();
        private readonly int _firstSetup; // первый шаг личной настройки (после знакомства)
        private int Steps => _pages.Count;

        private static WelcomeWindow _open;

        /// <summary>Показать знакомство; если оно уже открыто — просто вывести его вперёд (второе окно не нужно).</summary>
        public static void ShowOnce()
        {
            if (_open != null && _open.IsLoaded) { _open.Activate(); return; }
            _open = new WelcomeWindow();
            _open.Closed += (_, __) => _open = null;
            _open.Show();
        }

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
            Width = 660; Height = 600;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            _hasSteam = SteamDownloadService.SteamRoot() != null;
            // знакомство: что это → как устроено → вкладки → клавиши → ИИ → Клоди; потом личная настройка
            _pages.AddRange(new Func<FrameworkElement>[] { Intro, HowItWorks, TabsStep, KeysStep, AiStep, BuddyStep });
            _firstSetup = _pages.Count;
            if (_hasSteam) _pages.Add(SteamStep);
            _pages.Add(PrintScreenStep);
            _pages.Add(Done);

            var card = _card = new Border
            {
                Background = CardBg, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(28),
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            var root = new DockPanel();
            // внизу — точки прогресса (кликабельные) и подсказка по клавишам
            var bottom = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            _dots.Margin = new Thickness(0, 0, 0, 8);
            bottom.Children.Add(_dots);
            bottom.Children.Add(new TextBlock
            {
                Text = "← → — листать  ·  Enter — дальше  ·  Esc — закрыть", FontSize = 11, Foreground = Frz(Color.FromRgb(0x5A, 0x5A, 0x64)),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(bottom);
            root.Children.Add(_host);
            // слои карточки: мягкое «живое» свечение позади, содержимое, конфетти поверх
            var layers = new Grid();
            layers.Children.Add(BuildGlow());
            layers.Children.Add(root);
            layers.Children.Add(_confetti);
            layers.SizeChanged += (_, __) => layers.Clip = new RectangleGeometry(new Rect(0, 0, layers.ActualWidth, layers.ActualHeight), 27, 27);
            card.Child = layers;
            Content = card;
            card.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is Border || e.OriginalSource is Grid || e.OriginalSource is Canvas) try { DragMove(); } catch { } };
            KeyDown += (_, e) =>
            {
                if (_flying) return;
                if (e.Key == Key.Escape) { Close(); return; }
                // стрелками листаются шаги знакомства; на шагах настройки решения принимаются кнопками
                if ((e.Key == Key.Right || e.Key == Key.Enter) && _step < _firstSetup) { Next(); e.Handled = true; }
                else if (e.Key == Key.Enter && _step == _pages.Count - 1) { FlyToIsland(); e.Handled = true; }
                else if (e.Key == Key.Left && _step > 0) { Back(); e.Handled = true; }
            };

            for (int i = 0; i < Steps; i++)
            {
                int target = i;
                var dot = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), Margin = new Thickness(4, 0, 4, 0), Background = Line, Cursor = Cursors.Hand };
                // невидимая «подушка» вокруг точки — по ней легче попасть
                var hit = new Border { Background = Brushes.Transparent, Padding = new Thickness(0, 6, 0, 6), Child = dot, Cursor = Cursors.Hand };
                hit.MouseLeftButtonUp += (_, e) => { if (!_flying && target != _step) Go(target); e.Handled = true; };
                _dots.Children.Add(hit);
            }

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
                AppHost.Window?.HideForWelcome(true); // чёлка появится в конце — «прилетит» из этого окна
                Next();
            };
            Closed += (_, __) =>
            {
                AppSettings.Current.WelcomeShown = true;
                AppSettings.Current.Save();
                Journal.Info("welcome", "приветствие закрыто на шаге " + _step);
                if (!_landed) AppHost.Window?.HideForWelcome(false); // закрыли без анимации (Esc и т. п.) — просто вернуть чёлку
            };
        }

        // ---------- шаги ----------

        private void Next() => Go(_step + 1);
        private void Back() => Go(Math.Max(0, _step - 1));
        private void SkipIntro() { Journal.Info("welcome", "знакомство пропущено на шаге " + _step); Go(_firstSetup); }

        private void Go(int step)
        {
            if (_flying) return;
            if (step >= _pages.Count) { FlyToIsland(); return; }
            bool forward = step >= _step;
            _step = step;
            Journal.Info("welcome", "шаг " + step);
            Show(_pages[step](), forward);
            for (int i = 0; i < _dots.Children.Count; i++)
            {
                var d = (Border)((Border)_dots.Children[i]).Child;
                d.Background = i == _step ? Fg : Line;
                d.BeginAnimation(WidthProperty, new DoubleAnimation(i == _step ? 22 : 7, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
        }

        /// <summary>Шаг въезжает справа и проявляется, прошлый уезжает влево (при «Назад» — наоборот).</summary>
        private void Show(FrameworkElement view, bool forward = true)
        {
            double dir = forward ? 1 : -1;
            var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
            if (_host.Children.Count > 0 && _host.Children[^1] is FrameworkElement old)
            {
                var ot = new TranslateTransform();
                old.RenderTransform = ot;
                ot.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, -40 * dir, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
                var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
                fade.Completed += (_, __) => _host.Children.Remove(old);
                old.BeginAnimation(OpacityProperty, fade);
                old.IsHitTestVisible = false;
            }
            var tt = new TranslateTransform(40 * dir, 0);
            view.RenderTransform = tt;
            view.Opacity = 0;
            _host.Children.Add(view);
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(40 * dir, 0, TimeSpan.FromMilliseconds(420)) { BeginTime = TimeSpan.FromMilliseconds(80), EasingFunction = ease });
            view.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { BeginTime = TimeSpan.FromMilliseconds(80) });
        }

        private StackPanel Page(string title, string text, bool buddy = true)
        {
            var sp = new StackPanel { Margin = new Thickness(40, 20, 40, 10), VerticalAlignment = VerticalAlignment.Center };
            if (buddy)
            {
                // Клоди один на всё окно: переносим его со старого шага на новый
                if (_buddy.Parent is Panel old) old.Children.Remove(_buddy);
                sp.Children.Add(_buddy);
            }
            sp.Children.Add(new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = Fg, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, buddy ? 10 : 0, 0, 0) });
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
            var sp = Page("Привет! Это Isle", "Чёлка как у iPhone — только для Windows. Чёрная капсула сверху по центру экрана: показывает главное, а если упереть курсор в верх экрана — раскрывается в панель с плеером, инструментами и ИИ.");
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
            sp.Children.Add(Buttons(Pill("Пропустить знакомство", false, SkipIntro), Pill("Расскажи подробнее", true, Next)));
            return sp;
        }

        // ---------- знакомство ----------

        /// <summary>Строка «назад / дальше» для информационных шагов.</summary>
        private StackPanel NavRow() => Buttons(Pill("Назад", false, Back), Pill("Дальше", true, Next));

        /// <summary>Появление элементов по очереди с лёгким «подпрыгиванием».</summary>
        private static void Stagger(UIElement el, int index, int startMs = 250, int stepMs = 90)
        {
            var tt = new TranslateTransform(0, 10);
            el.RenderTransform = tt;
            el.Opacity = 0;
            var begin = TimeSpan.FromMilliseconds(startMs + index * stepMs);
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(480)) { BeginTime = begin, EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut } });
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { BeginTime = begin });
        }

        /// <summary>Таймер, который живёт, пока шаг на экране.</summary>
        private static void Loop(FrameworkElement owner, TimeSpan every, Action tick)
        {
            var t = new DispatcherTimer { Interval = every };
            t.Tick += (_, __) => tick();
            owner.Loaded += (_, __) => t.Start();
            owner.Unloaded += (_, __) => t.Stop();
        }

        private static string KeyText(string hotkey) => string.IsNullOrWhiteSpace(hotkey) ? "—" : hotkey.Replace("Space", "Пробел");

        private static FrameworkElement Keycaps(string hotkey)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var parts = string.IsNullOrWhiteSpace(hotkey) ? new[] { "—" } : hotkey.Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) row.Children.Add(new TextBlock { Text = "+", Foreground = Muted, FontSize = 12, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(new Border
                {
                    Background = Tile, BorderBrush = Line, BorderThickness = new Thickness(1, 1, 1, 2), CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 3, 8, 3), MinWidth = 26,
                    Child = new TextBlock { Text = KeyText(parts[i]), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Fg, HorizontalAlignment = HorizontalAlignment.Center }
                });
            }
            return row;
        }

        /// <summary>Строка-пояснение: значок, жирный заголовок, серый текст.</summary>
        private static Border InfoRow(string icon, string title, string text)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var ic = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(9), Background = Tile, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left,
                Child = new Icon { Data = icon, Width = 16, Height = 16, Foreground = Fg } };
            g.Children.Add(ic);
            var tx = new StackPanel();
            tx.Children.Add(new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Fg });
            tx.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            Grid.SetColumn(tx, 1);
            g.Children.Add(tx);
            return new Border { Child = g, Margin = new Thickness(0, 6, 0, 6) };
        }

        // 2) Как устроена чёлка: живая мини-модель — свёрнута → всплывашка → панель
        private FrameworkElement HowItWorks()
        {
            var sp = Page("Как это устроено", "У чёлки три состояния. Она сама переключается между ними — смотри:", buddy: false);

            // «экран» с чёлкой
            var screen = new Border { Width = 400, Height = 150, CornerRadius = new CornerRadius(16), Background = Tile, BorderBrush = Line, BorderThickness = new Thickness(1), Margin = new Thickness(0, 16, 0, 4), ClipToBounds = true };
            var area = new Grid();
            screen.Child = area;
            var island = new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(15), Width = 130, Height = 30, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0), ClipToBounds = true };
            area.Children.Add(island);
            var layers = new Grid();
            island.Child = layers;

            // свёрнута: время · погода
            var mini = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            mini.Children.Add(new TextBlock { Text = DateTime.Now.ToString("HH:mm"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Fg });
            mini.Children.Add(new Icon { Data = Icons.Sun, Width = 12, Height = 12, Foreground = Fg, Margin = new Thickness(14, 0, 4, 0) });
            mini.Children.Add(new TextBlock { Text = "+7°", FontSize = 12, Foreground = Fg });
            // всплывашка: загрузка завершена
            var toast = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Opacity = 0 };
            toast.Children.Add(new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(8), Background = Green, Child = new Icon { Data = Icons.Check, Width = 14, Height = 14, Foreground = Brushes.Black, StrokeWidth = 2.4 } });
            var tt = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            tt.Children.Add(new TextBlock { Text = "Загрузка завершена", FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Fg });
            tt.Children.Add(new TextBlock { Text = "Игра · Steam", FontSize = 10, Foreground = Muted });
            toast.Children.Add(tt);
            // раскрыта: вкладки и «содержимое»
            var panel = new StackPanel { Margin = new Thickness(12, 10, 12, 10), Opacity = 0 };
            var tabsRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            foreach (var ic in new[] { Icons.Home, Icons.Clipboard, Icons.Tray, Icons.Text, Icons.Chat, Icons.Note, Icons.Sun, Icons.Sliders })
                tabsRow.Children.Add(new Icon { Data = ic, Width = 12, Height = 12, Foreground = Muted, Margin = new Thickness(5, 0, 5, 0) });
            panel.Children.Add(tabsRow);
            var body = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            body.ColumnDefinitions.Add(new ColumnDefinition());
            body.Children.Add(new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(8), Background = Tile, HorizontalAlignment = HorizontalAlignment.Left });
            var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            lines.Children.Add(new Border { Height = 7, Width = 150, CornerRadius = new CornerRadius(3.5), Background = Tile, HorizontalAlignment = HorizontalAlignment.Left });
            lines.Children.Add(new Border { Height = 7, Width = 100, CornerRadius = new CornerRadius(3.5), Background = Tile, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) });
            lines.Children.Add(new Border { Height = 4, Width = 220, CornerRadius = new CornerRadius(2), Background = Line, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 9, 0, 0) });
            Grid.SetColumn(lines, 1);
            body.Children.Add(lines);
            panel.Children.Add(body);
            var tiles = new UniformGrid { Columns = 5, Margin = new Thickness(0, 10, 0, 0) };
            for (int i = 0; i < 5; i++) tiles.Children.Add(new Border { Height = 18, CornerRadius = new CornerRadius(6), Background = Tile, Margin = new Thickness(3, 0, 3, 0) });
            panel.Children.Add(tiles);
            layers.Children.Add(mini); layers.Children.Add(toast); layers.Children.Add(panel);
            sp.Children.Add(screen);

            var legend = new StackPanel { Margin = new Thickness(10, 8, 10, 0) };
            var rows = new[]
            {
                InfoRow(Icons.Monitor, "Свёрнута", "Время, погода, музыка и Клоди. Не мешает работать, прячется в полноэкранных играх."),
                InfoRow(Icons.Bell, "Всплывашки", "Громкость, раскладка, Caps Lock, загрузки, уведомления. Зелёный — получилось, красный — ошибка."),
                InfoRow(Icons.Home, "Раскрыта", "Упри курсор в самый верх экрана над чёлкой или нажми " + KeyText(AppSettings.Current.Hotkey) + ". Просто водить по чёлке можно — не откроется."),
            };
            foreach (var r in rows) legend.Children.Add(r);
            sp.Children.Add(legend);
            sp.Children.Add(NavRow());

            // сценарий: свёрнута → всплывашка → свёрнута → раскрыта
            int[] script = { 0, 1, 0, 2 };
            int at = 0;
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
            void Apply(int state)
            {
                double w = state switch { 1 => 230, 2 => 340, _ => 130 };
                double h = state switch { 1 => 40, 2 => 124, _ => 30 };
                var d = TimeSpan.FromMilliseconds(520);
                island.BeginAnimation(WidthProperty, new DoubleAnimation(w, d) { EasingFunction = ease });
                island.BeginAnimation(HeightProperty, new DoubleAnimation(h, d) { EasingFunction = ease });
                FrameworkElement[] content = { mini, toast, panel };
                for (int i = 0; i < 3; i++)
                {
                    content[i].BeginAnimation(OpacityProperty, new DoubleAnimation(i == state ? 1 : 0, TimeSpan.FromMilliseconds(i == state ? 320 : 160)) { BeginTime = TimeSpan.FromMilliseconds(i == state ? 260 : 0) });
                    rows[i].BeginAnimation(OpacityProperty, new DoubleAnimation(i == state ? 1 : 0.4, TimeSpan.FromMilliseconds(300)));
                }
            }
            Apply(0);
            Loop(sp, TimeSpan.FromMilliseconds(2200), () => { at = (at + 1) % script.Length; Apply(script[at]); });
            return sp;
        }

        // 3) Вкладки панели
        private FrameworkElement TabsStep()
        {
            var sp = Page("Что внутри панели", "Восемь вкладок сверху панели. Переключай колёсиком мыши или кликом.", buddy: false);
            var grid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 14, 0, 0) };
            (string icon, string name, string text)[] tabs =
            {
                (Icons.Home, "Главная", "Плеер, громкость, снимки, пипетка, QR, ярлыки"),
                (Icons.Clipboard, "Буфер", "История копирования с поиском и закрепом"),
                (Icons.Tray, "Полка", "Перетащи файл на чёлку — потом вытащи куда надо"),
                (Icons.Text, "Текст", "ghbdtn → привет, регистр, транслит, JSON"),
                (Icons.Chat, "Чат", "ИИ открывает программы, ставит таймеры"),
                (Icons.Note, "Заметка", "Заметки, чек-листы, таймер, напоминания"),
                (Icons.Sun, "Погода", "Сейчас, по часам, на неделю и предупреждения"),
                (Icons.Sliders, "Управление", "Сеть, Bluetooth, тема, сон, выключение"),
            };
            int i = 0;
            foreach (var (icon, name, text) in tabs)
            {
                var card = new Border { Background = Tile, CornerRadius = new CornerRadius(14), Margin = new Thickness(4), Padding = new Thickness(12, 10, 12, 10) };
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.Children.Add(new Icon { Data = icon, Width = 18, Height = 18, Foreground = Fg, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 1, 0, 0) });
                var tx = new StackPanel();
                tx.Children.Add(new TextBlock { Text = name, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Fg });
                tx.Children.Add(new TextBlock { Text = text, FontSize = 11.5, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
                Grid.SetColumn(tx, 1);
                g.Children.Add(tx);
                card.Child = g;
                Stagger(card, i++, 250, 70);
                grid.Children.Add(card);
            }
            sp.Children.Add(grid);
            sp.Children.Add(NavRow());
            return sp;
        }

        // 4) Горячие клавиши (берутся из настроек — показываем те, что реально работают)
        private FrameworkElement KeysStep()
        {
            var s = AppSettings.Current;
            var sp = Page("Горячие клавиши", "Работают в любой программе. Поменять можно в Настройки → Клавиши.", buddy: false);
            var list = new StackPanel { Margin = new Thickness(30, 14, 30, 0) };
            (string keys, string text)[] keys =
            {
                (s.Hotkey, "Открыть или закрыть панель"),
                (s.HotkeyCommand, "Командная строка: калькулятор, таймер, сайт"),
                (s.HotkeyClipboard, "История буфера обмена"),
                (s.HotkeyScreenshot, "Снимок области с пометками"),
                (s.HotkeyVoice, "Голос в текст — туда, где курсор"),
                (s.HotkeyVoiceCommand, "Голосовая команда ИИ"),
                (s.HotkeyCollapse, "Свернуть панель"),
            };
            int i = 0;
            foreach (var (k, text) in keys)
            {
                var g = new Grid { Margin = new Thickness(0, 5, 0, 5) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.Children.Add(Keycaps(k));
                var tb = new TextBlock { Text = text, FontSize = 13, Foreground = Fg, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(tb, 1);
                g.Children.Add(tb);
                Stagger(g, i++, 250, 70);
                list.Children.Add(g);
            }
            sp.Children.Add(list);
            sp.Children.Add(new TextBlock { Text = "Правый клик по чёлке — меню с настройками и выходом.", FontSize = 12, Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) });
            sp.Children.Add(NavRow());
            return sp;
        }

        // 5) ИИ и голос
        private FrameworkElement AiStep()
        {
            var s = AppSettings.Current;
            var sp = Page("ИИ и голос", "Isle умеет разговаривать — нужен только ключ ИИ-сервиса.", buddy: false);
            var list = new StackPanel { Margin = new Thickness(20, 12, 20, 0) };
            var rows = new[]
            {
                InfoRow(Icons.Chat, "Чат", "Отвечает на вопросы, открывает программы и сайты, ставит таймеры и напоминания, меняет громкость."),
                InfoRow(Icons.Mic, "Голос в текст · " + KeyText(s.HotkeyVoice), "Говоришь — текст появляется там, где стоит курсор: в мессенджере, браузере, документе."),
                InfoRow(Icons.Wave, "Голосовая команда · " + KeyText(s.HotkeyVoiceCommand), "«Открой телеграм», «таймер на 10 минут». Выдели текст и скажи «сделай официальнее» — он перепишется."),
                InfoRow(Icons.Shield, "Где взять ключ", "Бесплатно — у Groq (console.groq.com). Вставь его в Настройки → ИИ, он же подойдёт и для голоса."),
            };
            for (int i = 0; i < rows.Length; i++) { Stagger(rows[i], i, 250, 90); list.Children.Add(rows[i]); }
            sp.Children.Add(list);
            sp.Children.Add(NavRow());
            return sp;
        }

        // 6) Клоди: показывает свои занятия по очереди
        private FrameworkElement BuddyStep()
        {
            _buddy.Mood = Mood.Idle;
            var sp = Page("Знакомься, Клоди", "Маленький друг, который живёт в чёлке. Танцует под музыку, думает вместе с ИИ и радуется ответу, переодевается по погоде, а ночью ложится спать.");
            var facts = new StackPanel { Margin = new Thickness(20, 12, 20, 0) };
            var rows = new[]
            {
                InfoRow(Icons.Sun, "Когда скучно", "Запускает воздушного змея, играет на гитаре, поливает цветы, качается с гантелями…"),
                InfoRow(Icons.Gear, "Одежда", "Ковбой, викинг, ниндзя, детектив и другие — Настройки → Внешний вид."),
                InfoRow(Icons.Lock, "Твои данные", "Всё хранится только на этом компьютере. Пароли из менеджеров паролей в буфер не попадают."),
            };
            for (int i = 0; i < rows.Length; i++) { Stagger(rows[i], i, 300, 90); facts.Children.Add(rows[i]); }
            sp.Children.Add(facts);
            sp.Children.Add(NavRow());
            int[] acts = { 21, 14, 23, 26, 30, 28, 29, 18 };
            int at = 0;
            sp.Loaded += (_, __) => _buddy.ForceActivity(acts[0], 2.6);
            Loop(sp, TimeSpan.FromMilliseconds(2700), () => { at = (at + 1) % acts.Length; _buddy.ForceActivity(acts[at], 2.6); });
            return sp;
        }

        // 7) Точная скорость Steam — только с согласия: Steam и игры из него закроются
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
            buttons = Buttons(Pill("Назад", false, Back), Pill("Не сейчас", false, () => { Journal.Info("welcome", "точная скорость Steam: отказ"); Next(); }), yes);
            sp.Children.Add(buttons);
            return sp;
        }

        // 8) PrintScreen через Isle
        private FrameworkElement PrintScreenStep()
        {
            _buddy.Mood = Mood.Idle;
            var sp = Page("PrintScreen через Isle?",
                "Клавиша PrintScreen будет открывать выделение области Isle: мозаика, стрелки, текст, а снимок улетает прямо в чёлку.");
            sp.Children.Add(new TextBlock { Text = "Если не понравится, можно отключить в Настройках.", FontSize = 12, Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) });
            sp.Children.Add(Buttons(
                Pill("Назад", false, Back),
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

        // 9) Готово
        private FrameworkElement Done()
        {
            _buddy.Mood = Mood.Done;
            _buddy.Cheer();
            Dispatcher.BeginInvoke(new Action(BurstConfetti), System.Windows.Threading.DispatcherPriority.Background);
            var sp = Page("Всё готово!", "Упри курсор в самый верх экрана над чёлкой, кликни по ней или нажми " + KeyText(AppSettings.Current.Hotkey) + ". Правый клик по чёлке — меню. Это знакомство можно открыть снова: Настройки → Обновления.");
            sp.Children.Add(Buttons(Pill("Начать", true, FlyToIsland)));
            return sp;
        }

        // ---------- украшения: свечение и конфетти ----------

        private readonly Canvas _confetti = new() { IsHitTestVisible = false };

        /// <summary>Мягкое пятно света, которое медленно плавает и «дышит» позади содержимого.</summary>
        private static FrameworkElement BuildGlow()
        {
            var host = new Canvas { IsHitTestVisible = false };
            Ellipse Blob(Color c, double size, double x, double y, double dx, double dy, double sec)
            {
                var e = new Ellipse
                {
                    Width = size, Height = size,
                    Fill = new RadialGradientBrush(Color.FromArgb(46, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B))
                };
                Canvas.SetLeft(e, x); Canvas.SetTop(e, y);
                var tt = new TranslateTransform();
                e.RenderTransform = tt;
                var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
                tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, dx, TimeSpan.FromSeconds(sec)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
                tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, dy, TimeSpan.FromSeconds(sec * 1.3)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
                e.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 1, TimeSpan.FromSeconds(sec * 0.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
                host.Children.Add(e);
                return e;
            }
            Blob(Color.FromRgb(0x8B, 0x7C, 0xFF), 460, -120, -180, 90, 50, 9);   // сиреневое — слева сверху
            Blob(Color.FromRgb(0x4A, 0xC8, 0xFF), 420, 330, 260, -80, -60, 11);  // голубое — справа снизу
            return host;
        }

        /// <summary>Конфетти на последнем шаге: разноцветные кусочки разлетаются из-под заголовка и падают.</summary>
        private void BurstConfetti()
        {
            _confetti.Children.Clear();
            var rnd = new Random();
            Color[] colors = { Color.FromRgb(0x4A, 0xDE, 0x80), Color.FromRgb(0xFB, 0xBF, 0x24), Color.FromRgb(0x60, 0xA5, 0xFA),
                               Color.FromRgb(0xF4, 0x72, 0xB6), Color.FromRgb(0xA7, 0x8B, 0xFA), Color.FromRgb(0xEC, 0xEC, 0xF1) };
            double cx = ActualWidth / 2, cy = ActualHeight * 0.36;
            var bits = new List<(FrameworkElement el, double vx, double vy, double spin, RotateTransform rt, TranslateTransform tt)>();
            for (int i = 0; i < 70; i++)
            {
                var c = colors[rnd.Next(colors.Length)];
                var el = new Rectangle { Width = 5 + rnd.NextDouble() * 5, Height = 3 + rnd.NextDouble() * 4, RadiusX = 1.5, RadiusY = 1.5, Fill = Frz(c) };
                var rt = new RotateTransform(rnd.NextDouble() * 360);
                var tt = new TranslateTransform(cx, cy);
                var g = new TransformGroup(); g.Children.Add(rt); g.Children.Add(tt);
                el.RenderTransform = g;
                _confetti.Children.Add(el);
                double ang = -Math.PI / 2 + (rnd.NextDouble() - 0.5) * 2.4, sp = 260 + rnd.NextDouble() * 320;
                bits.Add((el, Math.Cos(ang) * sp, Math.Sin(ang) * sp, (rnd.NextDouble() - 0.5) * 720, rt, tt));
            }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            double prev = 0;
            EventHandler tick = null;
            tick = (_, __) =>
            {
                double t = clock.Elapsed.TotalSeconds, dt = Math.Min(0.05, t - prev);
                prev = t;
                for (int i = 0; i < bits.Count; i++)
                {
                    var b = bits[i];
                    b.vy += 900 * dt;                 // падает
                    b.vx *= Math.Pow(0.35, dt);       // и тормозит в воздухе
                    b.tt.X += b.vx * dt; b.tt.Y += b.vy * dt;
                    b.rt.Angle += b.spin * dt;
                    b.el.Opacity = Math.Clamp(1.8 - t, 0, 1);
                    bits[i] = b;
                }
                if (t < 1.9 && IsLoaded) return;
                CompositionTarget.Rendering -= tick;
                _confetti.Children.Clear();
            };
            CompositionTarget.Rendering += tick;
        }

        // ---------- финал: окно сминается в пилюлю и «прилипает» на место чёлки ----------

        /// <summary>
        /// Окно сминается в чёрную пилюлю размером с чёлку, тяжело плывёт к верху экрана, зависает под своим местом
        /// и дрожит, будто его тянет магнит, — и со шлепком прилипает. В этот момент проявляется настоящая чёлка.
        /// </summary>
        private void FlyToIsland()
        {
            if (_flying) return;
            var main = AppHost.Window;
            if (main == null || !IsLoaded || _card == null) { Close(); return; }
            _flying = true;
            Rect target; double radiusPx;
            try { target = main.IslandScreenRect(out radiusPx); }
            catch (Exception ex) { Journal.Warn("welcome", "не нашёл чёлку на экране: " + ex.Message); Close(); return; }

            // экранные пиксели → точки этого окна
            var m = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var tl = m.Transform(target.TopLeft);
            var br = m.Transform(target.BottomRight);
            double pw = Math.Max(80, br.X - tl.X), ph = Math.Max(24, br.Y - tl.Y), pr = Math.Max(4, radiusPx * m.M11);
            var goal = new Point((tl.X + br.X) / 2, (tl.Y + br.Y) / 2);
            // страховка: центр пилюли не выше половины её высоты от верха экрана
            double screenTop = SystemParameters.VirtualScreenTop;
            if (goal.Y - ph / 2 < screenTop) goal.Y = screenTop + ph / 2;
            Journal.Info("welcome", "знакомство завершено — летим к чёлке: " + Math.Round(goal.X) + "," + Math.Round(goal.Y) + " " + Math.Round(pw) + "×" + Math.Round(ph));

            var card = _card;
            card.RenderTransform = null;
            card.BeginAnimation(OpacityProperty, null);
            card.Opacity = 1;
            double w0 = card.ActualWidth, h0 = card.ActualHeight;
            card.Width = w0; card.Height = h0;
            card.HorizontalAlignment = HorizontalAlignment.Center;
            card.VerticalAlignment = VerticalAlignment.Center;
            card.ClipToBounds = true;
            // содержимое гаснет, карточка темнеет до чёрного, как чёлка
            card.Child?.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)));
            var bg = new SolidColorBrush(((SolidColorBrush)CardBg).Color);
            card.Background = bg;
            bg.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Colors.Black, TimeSpan.FromMilliseconds(420)));

            var start = new Point(Left + Width / 2, Top + Height / 2);
            var hover = new Point(goal.X, goal.Y + ph + 22); // зависает чуть ниже своего места
            const double Morph = 0.52, Travel = 1.05, Shake = 0.55, Snap = 0.16;
            const double pad = 40;
            bool shrunk = false;
            var clock = System.Diagnostics.Stopwatch.StartNew();

            void Place(Point c, double w, double h)
            {
                Left = c.X - Width / 2;
                Top = c.Y - Height / 2;
                card.Width = w; card.Height = h;
            }
            static double Smooth(double t) => t * t * t * (t * (t * 6 - 15) + 10);  // плавный старт и финиш
            static double Lerp(double a, double b, double t) => a + (b - a) * t;

            EventHandler tick = null;
            bool landed = false;
            tick = (_, __) =>
            {
                if (landed) return; // окно закрывается — вложенный кадр не должен «приземлить» чёлку второй раз
                double t = clock.Elapsed.TotalSeconds;
                if (t < Morph)
                {
                    // 1) сминается: размер → пилюля, углы → полукруг
                    double k = Smooth(t / Morph);
                    double w = Lerp(w0, pw, k), h = Lerp(h0, ph, k);
                    card.CornerRadius = new CornerRadius(Lerp(28, h / 2, k));
                    // лёгкое «сжатие» в середине: поперёк чуть уже, как будто комкают
                    double squeeze = Math.Sin(Math.PI * k) * 0.06;
                    Place(start, w * (1 - squeeze), h * (1 + squeeze));
                    return;
                }
                if (!shrunk)
                {
                    // окно ужимаем до пилюли (с полями): дальше двигаем маленькое окно, а не большое прозрачное
                    shrunk = true;
                    Width = pw + pad * 2; Height = ph + pad * 2;
                    card.CornerRadius = new CornerRadius(ph / 2);
                }
                t -= Morph;
                if (t < Travel)
                {
                    // 2) тяжело плывёт: медленно трогается, разгоняется, долго тормозит; путь — пологая дуга
                    double u = t / Travel;
                    double k = u < 0.35 ? 0.35 * Math.Pow(u / 0.35, 2.4) : 0.35 + 0.65 * (1 - Math.Pow(1 - (u - 0.35) / 0.65, 3));
                    var c = new Point(Lerp(start.X, hover.X, k), Lerp(start.Y, hover.Y, k));
                    c.X += Math.Sin(Math.PI * k) * 36;                      // дуга вбок
                    double stretch = Math.Sin(Math.PI * k) * 0.08;          // на ходу чуть вытягивается вверх
                    Place(c, pw * (1 - stretch * 0.5), ph * (1 + stretch));
                    return;
                }
                t -= Travel;
                if (t < Shake)
                {
                    // 3) дрожит под своим местом — магнит тянет всё сильнее, пилюля медленно подползает
                    double u = t / Shake;
                    double amp = 1 + 5 * u;
                    var c = new Point(hover.X + Math.Sin(t * 70) * amp, hover.Y - 8 * u * u + Math.Sin(t * 53) * amp * 0.35);
                    Place(c, pw, ph);
                    return;
                }
                t -= Shake;
                var from = new Point(hover.X, hover.Y - 8);
                if (t < Snap)
                {
                    // 4) щёлк! — рывок вверх с ускорением, верхние углы выпрямляются, как у чёлки
                    double u = t / Snap;
                    double k = u * u * u;
                    Place(new Point(from.X, Lerp(from.Y, goal.Y, k)), pw * (1 - 0.06 * k), ph * (1 + 0.1 * k));
                    double r = Lerp(ph / 2, 0, k);
                    card.CornerRadius = new CornerRadius(r, r, Lerp(ph / 2, pr, k), Lerp(ph / 2, pr, k));
                    return;
                }
                // 5) прилипла: настоящая чёлка проявляется на этом месте и пружинит, окно исчезает
                landed = true;
                CompositionTarget.Rendering -= tick;
                Place(goal, pw, ph);
                _landed = true;
                main.LandFromWelcome();
                Opacity = 0;
                Close();
            };
            CompositionTarget.Rendering += tick;
        }
    }
}
