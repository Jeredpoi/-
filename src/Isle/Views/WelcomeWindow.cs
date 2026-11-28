using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Isle.Helpers;
using Isle.Services;

namespace Isle.Views
{
    /// <summary>
    /// Знакомство при самом первом запуске (и снова — из меню чёлки). Десять шагов и финал:
    /// «Знакомство» — привет, открыть чёлку по-настоящему, буфер, снимки, музыка, таймер и календарь,
    /// телефон и полка, ИИ и голос; «Настройка» — город, акцент и Клоди, разрешения (автозапуск, уведомления,
    /// точная скорость Steam). В конце карточка сжимается в чёрную пилюлю и улетает на место чёлки.
    /// Цвета — из общих ресурсов (Card, ChipBg, SelBg, Accent, Muted, Fg), главная кнопка — цветом акцента.
    /// </summary>
    public class WelcomeWindow : Window
    {
        private static readonly Brush Green = Frz(Color.FromRgb(0x4A, 0xDE, 0x80));
        private static readonly Brush Red = Frz(Color.FromRgb(0xF8, 0x71, 0x71));
        private static readonly Brush Amber = Frz(Color.FromRgb(0xFB, 0xBF, 0x24));
        private static SolidColorBrush Frz(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        // шаги: 0 привет · 1 открыть чёлку · 2 буфер · 3 снимки · 4 музыка · 5 таймер и календарь · 6 телефон и полка · 7 ИИ и голос
        //       | 8 под себя · 9 разрешения | 10 финал
        private const int StepHello = 0, StepOpen = 1, StepClip = 2, StepShot = 3, StepMusic = 4, StepTime = 5, StepFiles = 6, StepAi = 7,
                          StepSetup = 8, StepPerms = 9, StepDone = 10;
        private const int Steps = 11;

        /// <summary>Шаги-рассказы без заданий: листаются стрелками и Enter.</summary>
        private static bool IsInfo(int step) => step is StepHello or StepShot or StepMusic or StepTime or StepFiles or StepAi;

        private readonly Grid _host = new();
        private readonly List<Border> _dotEls = new();
        private TextBlock _grpIntro, _grpSetup, _hint, _skip;
        private Grid _confirm;
        private readonly Character _buddy = new() { Width = 130, Height = 96, HorizontalAlignment = HorizontalAlignment.Center };
        /// <summary>Цвет текста на кнопке акцента: тёмный на светлом акценте, белый на тёмном.</summary>
        private readonly SolidColorBrush _onAccent = new(Color.FromRgb(0x0B, 0x0B, 0x0E));
        private int _step = -1;
        /// <summary>Меняется при каждой смене шага — отложенные действия прошлого шага по нему понимают, что опоздали.</summary>
        private int _token;
        /// <summary>Отписки текущего шага (события буфера и т. п.) — вызываются при уходе с шага.</summary>
        private Action _cleanup;
        private readonly bool _hasSteam;
        /// <summary>Это самый первый показ: только тогда применяем значения по умолчанию (автозапуск).</summary>
        private readonly bool _firstRun;
        private bool _autostartDecided;
        private Border _card;
        private bool _flying, _landed;
        /// <summary>Закрываемся по-настоящему (после финала или без чёлки) — OnClosing не перехватывает.</summary>
        private bool _closeNow, _closed;

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
            _firstRun = !AppSettings.Current.WelcomeShown;
            UpdateOnAccent();

            var card = _card = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(28), RenderTransformOrigin = new Point(0.5, 0.5) };
            card.SetResourceReference(Border.BackgroundProperty, "Card");
            card.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
            var root = new DockPanel();
            // внизу — точки прогресса двумя группами и подсказка по клавишам (только там, где клавиши работают)
            var bottom = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            bottom.Children.Add(BuildDots());
            _hint = Txt("", 11, "Muted");
            _hint.HorizontalAlignment = HorizontalAlignment.Center;
            _hint.Opacity = 0.7;
            _hint.Margin = new Thickness(0, 6, 0, 0);
            bottom.Children.Add(_hint);
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(bottom);
            root.Children.Add(_host);
            // слои карточки: мягкое «живое» свечение позади, содержимое, конфетти, «Пропустить» в углу, подтверждение закрытия
            var layers = new Grid();
            layers.Children.Add(BuildGlow());
            layers.Children.Add(root);
            layers.Children.Add(_confetti);
            layers.Children.Add(BuildSkip());
            layers.Children.Add(_confirm = BuildConfirm());
            layers.SizeChanged += (_, __) => layers.Clip = new RectangleGeometry(new Rect(0, 0, layers.ActualWidth, layers.ActualHeight), 27, 27);
            card.Child = layers;
            Content = card;
            card.MouseLeftButtonDown += (_, e) => { if (!_flying && (e.OriginalSource is Border || e.OriginalSource is Grid || e.OriginalSource is Canvas)) try { DragMove(); } catch { } };
            KeyDown += OnKey;

            // окно «вырастает» из центра
            Loaded += (_, __) =>
            {
                var st = new ScaleTransform(0.9, 0.9);
                card.RenderTransform = st;
                var ease = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut };
                st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(520)) { EasingFunction = ease });
                st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(520)) { EasingFunction = ease });
                card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)));
                Journal.Info("welcome", "приветствие открыто, Steam " + (_hasSteam ? "найден" : "не найден") + (_firstRun ? ", первый запуск" : ", повторно"));
                AppHost.Window?.HideForWelcome(true); // чёлка появится со второго шага — её надо открыть по-настоящему
                Go(StepHello);
            };
            Closed += (_, __) =>
            {
                _closed = true;
                _cleanup?.Invoke(); _cleanup = null;
                _token++;
                ApplyDefaults();
                AppSettings.Current.WelcomeShown = true;
                AppSettings.Current.Save();
                Journal.Info("welcome", "приветствие закрыто на шаге " + _step);
                if (!_landed) AppHost.Window?.HideForWelcome(false); // закрыли без анимации — просто вернуть чёлку
            };
        }

        // ---------- клавиши ----------

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (_flying) return;
            if (_confirm.Visibility == Visibility.Visible)
            {
                // открыт вопрос «Закрыть?»: Esc — остаться, Enter — закрыть
                if (e.Key == Key.Escape) { HideConfirm(); e.Handled = true; }
                else if (e.Key == Key.Enter) { CloseWithDefaults(); e.Handled = true; }
                return;
            }
            if (e.Key == Key.Escape)
            {
                if (_step == StepDone) FlyToIsland(); else ShowConfirm();
                e.Handled = true;
                return;
            }
            bool done = _step == StepOpen && _openDone || _step == StepClip && _clipDone;
            // стрелки и Enter листают только информационные шаги; на шагах-заданиях — когда задание выполнено
            if ((e.Key == Key.Right || e.Key == Key.Enter) && (IsInfo(_step) || done)) { Next(); e.Handled = true; }
            else if (e.Key == Key.Enter && _step == StepDone) { FlyToIsland(); e.Handled = true; }
            else if (e.Key == Key.Left && _step > 0 && _step < StepSetup) { Back(); e.Handled = true; }
        }

        // ---------- шаги ----------

        private void Next() => Go(_step + 1);
        private void Back() => Go(Math.Max(0, _step - 1));
        private void SkipIntro() { Journal.Info("welcome", "знакомство пропущено на шаге " + _step); Go(StepSetup); }

        private void Go(int step)
        {
            if (_flying) return;
            if (step >= Steps) { FlyToIsland(); return; }
            bool forward = step >= _step;
            _cleanup?.Invoke(); _cleanup = null;
            _token++;
            _step = step;
            Journal.Info("welcome", "шаг " + step);
            // настоящая чёлка нужна со второго шага (её открывают по-настоящему); в финале прячем — она «прилетит»
            var main = AppHost.Window;
            if (main != null)
            {
                if (step == StepDone) main.HideForWelcome(true);
                else if (step >= StepOpen)
                {
                    main.HideForWelcome(false);
                    if (main.State == IslandState.Expanded) main.Collapse(); // задание засчитывается только за новое раскрытие
                }
            }
            FrameworkElement view = step switch
            {
                StepHello => Hello(),
                StepOpen => OpenStep(),
                StepClip => ClipStep(),
                StepShot => ShotStep(),
                StepMusic => MusicStep(),
                StepTime => TimeStep(),
                StepFiles => FilesStep(),
                StepAi => AiStep(),
                StepSetup => SetupStep(),
                StepPerms => PermsStep(),
                _ => Done(),
            };
            Show(view, forward);
            UpdateChrome();
        }

        /// <summary>Точки, подписи групп, подсказка по клавишам и «Пропустить» — под текущий шаг.</summary>
        private void UpdateChrome()
        {
            for (int i = 0; i < _dotEls.Count; i++)
            {
                var d = _dotEls[i];
                d.SetResourceReference(Border.BackgroundProperty, i == _step ? "Fg" : i < _step ? "Muted" : "SelBg");
                d.BeginAnimation(WidthProperty, new DoubleAnimation(i == _step ? 22 : 7, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
            _grpIntro.SetResourceReference(TextBlock.ForegroundProperty, _step < StepSetup ? "Fg" : "Muted");
            _grpSetup.SetResourceReference(TextBlock.ForegroundProperty, _step is >= StepSetup and < StepDone ? "Fg" : "Muted");
            _hint.Text = _step switch
            {
                StepDone => "Enter — начать",
                _ when IsInfo(_step) => "← → — листать  ·  Enter — дальше",
                _ => "",
            };
            _skip.Visibility = _step == StepDone ? Visibility.Collapsed : Visibility.Visible;
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

        // ---------- рамка: точки, «Пропустить», подтверждение ----------

        private FrameworkElement BuildDots()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            StackPanel Group(string name, int from, int to, out TextBlock label)
            {
                var g = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 10, 0) };
                label = Txt(name, 11, "Muted", FontWeights.SemiBold);
                label.VerticalAlignment = VerticalAlignment.Center;
                label.Margin = new Thickness(0, 0, 8, 0);
                g.Children.Add(label);
                for (int i = from; i <= to; i++)
                {
                    int target = i;
                    var dot = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), Margin = new Thickness(3, 0, 3, 0), Cursor = Cursors.Hand };
                    dot.SetResourceReference(Border.BackgroundProperty, "SelBg");
                    // невидимая «подушка» вокруг точки — по ней легче попасть
                    var hit = new Border { Background = Brushes.Transparent, Padding = new Thickness(0, 6, 0, 6), Child = dot, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
                    hit.MouseLeftButtonUp += (_, e) => { if (!_flying && target != _step) Go(target); e.Handled = true; };
                    _dotEls.Add(dot);
                    g.Children.Add(hit);
                }
                return g;
            }
            row.Children.Add(Group("Знакомство", StepHello, StepAi, out _grpIntro));
            row.Children.Add(Group("Настройка", StepSetup, StepPerms, out _grpSetup));
            return row;
        }

        /// <summary>«Пропустить» в правом верхнем углу: в знакомстве — сразу к настройке, в настройке — закрыть со стандартными.</summary>
        private FrameworkElement BuildSkip()
        {
            _skip = Txt("Пропустить", 12.5, "Muted");
            _skip.Cursor = Cursors.Hand;
            _skip.HorizontalAlignment = HorizontalAlignment.Right;
            _skip.VerticalAlignment = VerticalAlignment.Top;
            _skip.Margin = new Thickness(0, 20, 26, 0);
            _skip.Padding = new Thickness(6, 2, 6, 2);
            _skip.MouseEnter += (_, __) => _skip.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            _skip.MouseLeave += (_, __) => _skip.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            _skip.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (_flying) return;
                if (_step < StepSetup) SkipIntro(); else ShowConfirm();
            };
            return _skip;
        }

        private Grid BuildConfirm()
        {
            var dim = new Grid { Background = new SolidColorBrush(Color.FromArgb(0xA8, 0, 0, 0)), Visibility = Visibility.Collapsed };
            var box = new Border { CornerRadius = new CornerRadius(20), Padding = new Thickness(26, 22, 26, 20), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            box.SetResourceReference(Border.BackgroundProperty, "Card");
            box.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
            var sp = new StackPanel();
            var t = Txt("Закрыть?", 18, "Fg", FontWeights.SemiBold);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            sp.Children.Add(t);
            var s = Txt("Настройки останутся стандартными", 13, "Muted");
            s.HorizontalAlignment = HorizontalAlignment.Center;
            s.Margin = new Thickness(0, 6, 0, 0);
            sp.Children.Add(s);
            var row = Buttons(Pill("Остаться", false, HideConfirm), Pill("Закрыть", true, CloseWithDefaults));
            row.Margin = new Thickness(0, 18, 0, 0);
            sp.Children.Add(row);
            box.Child = sp;
            dim.Children.Add(box);
            dim.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource == dim) HideConfirm(); };
            return dim;
        }

        private void ShowConfirm()
        {
            _confirm.Visibility = Visibility.Visible;
            _confirm.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        }

        private void HideConfirm() => _confirm.Visibility = Visibility.Collapsed;

        private void CloseWithDefaults()
        {
            HideConfirm();
            Journal.Info("welcome", "закрыто со стандартными настройками на шаге " + _step);
            ApplyDefaults();
            FlyToIsland();
        }

        /// <summary>
        /// Значения по умолчанию, которые человек не успел выбрать сам: автозапуск с Windows включён.
        /// Только при самом первом показе — повторное знакомство чужие настройки не трогает.
        /// </summary>
        private void ApplyDefaults()
        {
            if (!_firstRun || _autostartDecided) return;
            _autostartDecided = true;
            try
            {
                if (!SystemControls.Autostart) SystemControls.Autostart = true;
                Journal.Info("welcome", "автозапуск по умолчанию: включён");
            }
            catch (Exception ex) { Journal.Warn("welcome", "автозапуск не включился: " + ex.Message); }
        }

        // ---------- кирпичики ----------

        private static TextBlock Txt(string text, double size, string brushKey = "Fg", FontWeight? weight = null)
        {
            var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
            t.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            return t;
        }

        private static Icon Ico(string data, double size, string brushKey = "Fg")
        {
            var i = new Icon { Data = data, Width = size, Height = size };
            i.SetResourceReference(Isle.Helpers.Icon.ForegroundProperty, brushKey);
            return i;
        }

        private StackPanel Page(string title, string text, bool buddy = false)
        {
            var sp = new StackPanel { Margin = new Thickness(44, 48, 44, 6), VerticalAlignment = VerticalAlignment.Center };
            if (buddy)
            {
                // Клоди один на всё окно: переносим его со старого шага на новый
                if (_buddy.Parent is Panel old) old.Children.Remove(_buddy);
                sp.Children.Add(_buddy);
            }
            var t = Txt(title, 26, "Fg", FontWeights.SemiBold);
            t.TextAlignment = TextAlignment.Center;
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.Margin = new Thickness(0, buddy ? 10 : 0, 0, 0);
            sp.Children.Add(t);
            if (!string.IsNullOrEmpty(text))
            {
                var d = Txt(text, 14.5, "Muted");
                d.TextAlignment = TextAlignment.Center;
                d.Margin = new Thickness(0, 8, 0, 0);
                sp.Children.Add(d);
            }
            return sp;
        }

        /// <summary>Кнопка-пилюля: главная — цветом акцента, второстепенная — как чип.</summary>
        private Button Pill(string text, bool primary, Action click, bool small = false)
        {
            var b = new Button { Cursor = Cursors.Hand, Margin = new Thickness(6, 0, 6, 0), Focusable = false, Template = PillTemplate() };
            var label = new TextBlock { Text = text, FontSize = small ? 13 : 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            var body = new Border { Height = small ? 32 : 40, CornerRadius = new CornerRadius(small ? 16 : 20), Padding = new Thickness(small ? 16 : 22, 0, small ? 16 : 22, 0), Child = label };
            if (primary)
            {
                body.SetResourceReference(Border.BackgroundProperty, "Accent");
                label.Foreground = _onAccent;
            }
            else
            {
                body.SetResourceReference(Border.BackgroundProperty, "ChipBg");
                label.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            }
            b.Content = body;
            b.Click += (_, __) => { if (!_flying) click(); };
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
            var off = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            off.Setters.Add(new Setter(OpacityProperty, 0.45));
            t.Triggers.Add(off);
            return t;
        }

        private static StackPanel Buttons(params Button[] buttons)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 24, 0, 0) };
            foreach (var b in buttons) row.Children.Add(b);
            return row;
        }

        private StackPanel NavRow(string next = "Дальше") => Buttons(Pill("Назад", false, Back), Pill(next, true, Next));

        /// <summary>Тёмный текст на светлом акценте, белый — на тёмном (у градиента смотрим на светлый конец).</summary>
        private void UpdateOnAccent()
        {
            var br = TryFindResource("Accent") as Brush ?? Application.Current?.TryFindResource("Accent") as Brush;
            Color c = Color.FromRgb(0xEC, 0xEC, 0xF1);
            if (br is SolidColorBrush sb) c = sb.Color;
            else if (br is GradientBrush gb && gb.GradientStops.Count > 0)
            {
                c = gb.GradientStops[0].Color;
                foreach (var st in gb.GradientStops) if (Lum(st.Color) > Lum(c)) c = st.Color;
            }
            _onAccent.Color = Lum(c) > 150 ? Color.FromRgb(0x0B, 0x0B, 0x0E) : Colors.White;
        }

        private static double Lum(Color x) => 0.2126 * x.R + 0.7152 * x.G + 0.0722 * x.B;

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

        /// <summary>Сделать через ms миллисекунд — если шаг за это время не сменился (stepBound) и окно ещё открыто.</summary>
        private void After(int ms, Action action, bool stepBound = true)
        {
            int token = _token;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += (_, __) =>
            {
                t.Stop();
                if (!IsLoaded || (stepBound && token != _token)) return;
                action();
            };
            t.Start();
        }

        /// <summary>Свернуть настоящую чёлку (раскрытая панель закрывает верх окна) и вернуть фокус знакомству.</summary>
        private void CollapseIsland()
        {
            var main = AppHost.Window;
            if (main?.State == IslandState.Expanded) main.Collapse();
            try { Activate(); } catch { }
        }

        private static string KeyText(string hotkey) => string.IsNullOrWhiteSpace(hotkey) ? "—" : hotkey.Replace("Space", "Пробел").Replace("Escape", "Esc");

        private static FrameworkElement Keycaps(string hotkey)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var parts = string.IsNullOrWhiteSpace(hotkey) ? new[] { "—" } : hotkey.Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                {
                    var plus = Txt("+", 12, "Muted");
                    plus.Margin = new Thickness(4, 0, 4, 0);
                    plus.VerticalAlignment = VerticalAlignment.Center;
                    row.Children.Add(plus);
                }
                var cap = Txt(KeyText(parts[i]), 12.5, "Fg", FontWeights.SemiBold);
                cap.HorizontalAlignment = HorizontalAlignment.Center;
                cap.TextWrapping = TextWrapping.NoWrap;
                var b = new Border { BorderThickness = new Thickness(1, 1, 1, 2), CornerRadius = new CornerRadius(7), Padding = new Thickness(9, 4, 9, 4), MinWidth = 28, Child = cap };
                b.SetResourceReference(Border.BackgroundProperty, "SelBg");
                b.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
                row.Children.Add(b);
            }
            return row;
        }

        /// <summary>Строка «клавиши — что делают» (в финале и на шаге снимков).</summary>
        private static FrameworkElement KeyRow(string hotkey, string text)
        {
            var g = new Grid { Margin = new Thickness(0, 5, 0, 5) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var caps = Keycaps(hotkey);
            ((FrameworkElement)caps).HorizontalAlignment = HorizontalAlignment.Right;
            ((FrameworkElement)caps).Margin = new Thickness(0, 0, 14, 0);
            g.Children.Add(caps);
            var tb = Txt(text, 13.5);
            tb.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(tb, 1);
            g.Children.Add(tb);
            return g;
        }

        /// <summary>Плашка настройки: значок, заголовок, пояснение и элемент справа (переключатель, кнопка).</summary>
        private static (Border row, TextBlock sub) SettingRow(string icon, string title, string sub, FrameworkElement control)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var ic = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Child = Ico(icon, 16) };
            ic.SetResourceReference(Border.BackgroundProperty, "SelBg");
            g.Children.Add(ic);
            var tx = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            tx.Children.Add(Txt(title, 14, "Fg", FontWeights.SemiBold));
            var s = Txt(sub, 12, "Muted");
            s.Margin = new Thickness(0, 2, 0, 0);
            s.Visibility = string.IsNullOrEmpty(sub) ? Visibility.Collapsed : Visibility.Visible;
            tx.Children.Add(s);
            Grid.SetColumn(tx, 1);
            g.Children.Add(tx);
            if (control != null)
            {
                control.VerticalAlignment = VerticalAlignment.Center;
                control.Margin = new Thickness(12, 0, 0, 0);
                Grid.SetColumn(control, 2);
                g.Children.Add(control);
            }
            var row = new Border { CornerRadius = new CornerRadius(16), Padding = new Thickness(14, 11, 16, 11), Margin = new Thickness(0, 5, 0, 5), Child = g, Width = 480 };
            row.SetResourceReference(Border.BackgroundProperty, "ChipBg");
            return (row, s);
        }

        /// <summary>Переключатель в общем стиле приложения (CheckBox из Styles.xaml рисуется тумблером).</summary>
        private static CheckBox Switch(bool on) => new() { IsChecked = on, Margin = new Thickness(0) };

        /// <summary>Значок «готово»: зелёный кружок с галочкой, появляется с пружинкой.</summary>
        private static Border DoneBadge(double size = 22)
        {
            var b = new Border
            {
                Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = Green, Visibility = Visibility.Collapsed,
                RenderTransformOrigin = new Point(0.5, 0.5),
                Child = new Icon { Data = Icons.Check, Width = size * 0.55, Height = size * 0.55, Foreground = Brushes.Black, StrokeWidth = 2.6 }
            };
            return b;
        }

        private static void Pop(FrameworkElement el)
        {
            el.Visibility = Visibility.Visible;
            var st = new ScaleTransform(0.3, 0.3);
            el.RenderTransform = st;
            var ease = new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
        }

        // ---------- мини-чёлка ----------

        /// <summary>
        /// Маленькая модель чёлки на «экране»: свёрнута (Клоди или глаза, время, погода, полоски акцента)
        /// или раскрыта (вкладки, плеер). Перечитывает настройки по Refresh() — на шаге «Под себя» меняется вживую.
        /// </summary>
        private sealed class NotchPreview
        {
            public readonly Border Screen;
            public readonly Canvas Overlay = new() { IsHitTestVisible = false };
            public readonly Character Pet = new() { Width = 30, Height = 22, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            private readonly Border _island;
            private readonly FrameworkElement _mini, _panel;
            private readonly StackPanel _eyes = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            private readonly TextBlock _weather;
            private readonly List<Border> _accented = new();
            private readonly double _cw = 196, _ch = 32, _ew, _eh;
            public bool Expanded { get; private set; }

            public NotchPreview(double width, double height)
            {
                _ew = Math.Min(width - 40, 340); _eh = Math.Min(height - 20, 124);
                Screen = new Border { Width = width, Height = height, CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Center };
                Screen.SetResourceReference(Border.BackgroundProperty, "ChipBg");
                Screen.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
                var area = new Grid();
                Screen.Child = area;
                _island = new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(0, 0, 16, 16), Width = _cw, Height = _ch, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center, ClipToBounds = true };
                area.Children.Add(_island);
                area.Children.Add(Overlay);
                var layers = new Grid();
                _island.Child = layers;

                // свёрнута: Клоди (или глаза) · время · погода · полоски музыки цветом акцента
                var mini = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                mini.Children.Add(Pet);
                for (int i = 0; i < 2; i++)
                    _eyes.Children.Add(new Border { Width = 5, Height = 10, CornerRadius = new CornerRadius(2.5), Background = Brushes.White, Margin = new Thickness(i == 0 ? 0 : 4, 0, 0, 0) });
                mini.Children.Add(_eyes);
                var time = new TextBlock { Text = DateTime.Now.ToString("HH:mm"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
                mini.Children.Add(time);
                _weather = new TextBlock { FontSize = 12, Foreground = Brushes.White, Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                mini.Children.Add(_weather);
                var bars = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                double[] hs = { 6, 11, 8, 12 };
                for (int i = 0; i < hs.Length; i++)
                {
                    var bar = new Border { Width = 2.5, Height = hs[i], CornerRadius = new CornerRadius(1.25), Margin = new Thickness(1, 0, 1, 0), VerticalAlignment = VerticalAlignment.Center };
                    // полоски «играют», как в настоящей чёлке под музыку
                    bar.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(3, hs[i], TimeSpan.FromMilliseconds(380 + i * 90)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
                    _accented.Add(bar);
                    bars.Children.Add(bar);
                }
                mini.Children.Add(bars);
                _mini = mini;

                // раскрыта: вкладки (выбранная — подсветкой акцента) и плеер с полоской прогресса цвета акцента
                var panel = new StackPanel { Margin = new Thickness(14, 10, 14, 10), Opacity = 0 };
                var tabsRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                bool first = true;
                foreach (var ic in new[] { Icons.Home, Icons.Clipboard, Icons.Tray, Icons.Text, Icons.Chat, Icons.Note, Icons.Sun, Icons.Sliders })
                {
                    var cell = new Grid { Margin = new Thickness(2, 0, 2, 0) };
                    if (first)
                    {
                        var sel = new Border { CornerRadius = new CornerRadius(8), Opacity = 0.45 };
                        _accented.Add(sel);
                        cell.Children.Add(sel);
                    }
                    cell.Children.Add(new Icon { Data = ic, Width = 12, Height = 12, Foreground = first ? Brushes.White : Frz(Color.FromRgb(0x8B, 0x8B, 0x95)), Margin = new Thickness(6, 4, 6, 4) });
                    tabsRow.Children.Add(cell);
                    first = false;
                }
                panel.Children.Add(tabsRow);
                var body = new Grid { Margin = new Thickness(0, 12, 0, 0) };
                body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
                body.ColumnDefinitions.Add(new ColumnDefinition());
                var tile = Frz(Color.FromRgb(0x1E, 0x1E, 0x24));
                body.Children.Add(new Border { Width = 42, Height = 42, CornerRadius = new CornerRadius(9), Background = tile, HorizontalAlignment = HorizontalAlignment.Left });
                var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                lines.Children.Add(new Border { Height = 7, Width = 140, CornerRadius = new CornerRadius(3.5), Background = tile, HorizontalAlignment = HorizontalAlignment.Left });
                lines.Children.Add(new Border { Height = 7, Width = 90, CornerRadius = new CornerRadius(3.5), Background = tile, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) });
                var track = new Grid { Margin = new Thickness(0, 10, 0, 0), Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
                track.Children.Add(new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = tile });
                var progress = new Border { Height = 4, Width = 120, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };
                _accented.Add(progress);
                track.Children.Add(progress);
                lines.Children.Add(track);
                Grid.SetColumn(lines, 1);
                body.Children.Add(lines);
                panel.Children.Add(body);
                _panel = panel;
                layers.Children.Add(mini);
                layers.Children.Add(panel);
                Refresh();
            }

            /// <summary>Перечитать настройки: персонаж, погода, цвет акцента.</summary>
            public void Refresh()
            {
                var s = AppSettings.Current;
                Pet.Visibility = s.Character == "pixel" ? Visibility.Visible : Visibility.Collapsed;
                _eyes.Visibility = s.Character == "eyes" ? Visibility.Visible : Visibility.Collapsed;
                var w = AppHost.Weather;
                _weather.Text = w != null ? w.Glyph + " " + WeatherService.Deg(w.Temp) + "°" : "☀ +7°";
                foreach (var b in _accented) b.Background = AccentPaint.Brush(s.AccentHex, _accented.IndexOf(b) < 4 ? 90 : 0);
            }

            public void SetExpanded(bool on)
            {
                Expanded = on;
                var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
                var d = TimeSpan.FromMilliseconds(520);
                _island.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(on ? _ew : _cw, d) { EasingFunction = ease });
                _island.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(on ? _eh : _ch, d) { EasingFunction = ease });
                _island.CornerRadius = new CornerRadius(0, 0, on ? 24 : 16, on ? 24 : 16);
                _mini.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(on ? 0 : 1, TimeSpan.FromMilliseconds(on ? 160 : 320)) { BeginTime = TimeSpan.FromMilliseconds(on ? 0 : 260) });
                _panel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(on ? 1 : 0, TimeSpan.FromMilliseconds(on ? 320 : 160)) { BeginTime = TimeSpan.FromMilliseconds(on ? 260 : 0) });
            }
        }

        // ---------- 1) Привет ----------

        private FrameworkElement Hello()
        {
            _buddy.Mood = Mood.Idle;
            var sp = Page("Привет! Я Клоди", "Isle — чёлка для Windows. Покажу главное — это пара минут.", buddy: true);
            sp.Loaded += (_, __) => _buddy.ForceActivity(3, 2.2); // машет: «привет!»
            sp.Children.Add(Buttons(Pill("Я сам разберусь", false, SkipIntro), Pill("Поехали", true, Next)));
            return sp;
        }

        // ---------- 2) Открой чёлку — по-настоящему ----------

        private bool _openDone;

        private FrameworkElement OpenStep()
        {
            var s = AppSettings.Current;
            _openDone = false;
            bool hk = !string.IsNullOrWhiteSpace(s.Hotkey);
            string how = s.ExpandOnHover ? "Упри курсор в самый верх экрана" : "Кликни по чёлке вверху экрана";
            var sp = Page("Открой чёлку", how + (hk ? " — или нажми " + KeyText(s.Hotkey) + "." : "."));

            // модель: курсор едет вверх — чёлка раскрывается; после настоящего раскрытия — галочка и радость Клоди
            var preview = new NotchPreview(400, 160);
            preview.Screen.Margin = new Thickness(0, 22, 0, 0);
            var cursor = new Path
            {
                Data = Geometry.Parse("M0,0 L0,17 L4.6,12.6 L7.6,19.4 L10.4,18.2 L7.4,11.6 L13,11.6 Z"),
                Fill = Brushes.White, Stroke = Brushes.Black, StrokeThickness = 1.2, RenderTransform = new TranslateTransform(250, 130)
            };
            preview.Overlay.Children.Add(cursor);
            var badge = DoneBadge(30);
            badge.HorizontalAlignment = HorizontalAlignment.Right;
            badge.VerticalAlignment = VerticalAlignment.Bottom;
            badge.Margin = new Thickness(0, 0, 12, 10);
            ((Grid)preview.Screen.Child).Children.Add(badge);
            sp.Children.Add(preview.Screen);

            var status = Txt(hk ? "Жду…" : "Жду, когда откроешь", 13, "Muted");
            status.HorizontalAlignment = HorizontalAlignment.Center;
            status.Margin = new Thickness(0, 14, 0, 0);
            sp.Children.Add(status);
            if (hk)
            {
                var caps = Keycaps(s.Hotkey);
                ((FrameworkElement)caps).HorizontalAlignment = HorizontalAlignment.Center;
                ((FrameworkElement)caps).Margin = new Thickness(0, 10, 0, 0);
                sp.Children.Add(caps);
            }
            // через 15 секунд — тихая ссылка «Пропустить», если не получается
            var skip = Txt("Не получается? Пропустить", 12.5, "Muted");
            skip.HorizontalAlignment = HorizontalAlignment.Center;
            skip.Margin = new Thickness(0, 14, 0, 0);
            skip.Cursor = Cursors.Hand;
            skip.TextDecorations = TextDecorations.Underline;
            skip.Visibility = Visibility.Hidden;
            skip.MouseLeftButtonUp += (_, e) => { e.Handled = true; Journal.Info("welcome", "открыть чёлку: пропущено"); Next(); };
            sp.Children.Add(skip);
            After(15000, () => { if (!_openDone) { skip.Visibility = Visibility.Visible; skip.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300))); } });

            // показ: курсор плывёт к верху «экрана», модель раскрывается и сворачивается
            var ct = (TranslateTransform)cursor.RenderTransform;
            void Demo()
            {
                if (_openDone) return;
                var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
                cursor.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
                ct.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(250, 196, TimeSpan.FromMilliseconds(900)) { EasingFunction = ease });
                ct.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(130, 1, TimeSpan.FromMilliseconds(900)) { EasingFunction = ease });
                After(950, () => { if (!_openDone) preview.SetExpanded(true); });
                After(2500, () =>
                {
                    if (_openDone) return;
                    preview.SetExpanded(false);
                    cursor.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(250)));
                });
            }
            sp.Loaded += (_, __) => After(500, Demo);
            Loop(sp, TimeSpan.FromMilliseconds(3600), Demo);

            // настоящая чёлка раскрылась — шаг выполнен, через секунду дальше
            int mine = _token;
            Loop(sp, TimeSpan.FromMilliseconds(150), () =>
            {
                if (_openDone || mine != _token || AppHost.Window?.State != IslandState.Expanded) return;
                _openDone = true;
                Journal.Info("welcome", "открыть чёлку: получилось");
                cursor.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
                preview.SetExpanded(true);
                preview.Pet.Cheer();
                Pop(badge);
                status.Text = "Получилось!";
                status.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
                skip.Visibility = Visibility.Hidden;
                After(1100, () => { CollapseIsland(); Next(); });
            });
            return sp;
        }

        // ---------- 3) Буфер: скопировать строку и найти её в истории ----------

        private const string Sample = "Привет от Клоди! Эта строка сохранится в истории.";
        private bool _clipDone;

        private FrameworkElement ClipStep()
        {
            var s = AppSettings.Current;
            _clipDone = false;
            bool copied = false;
            bool hk = !string.IsNullOrWhiteSpace(s.HotkeyClipboard);
            var sp = Page("Всё, что копируешь, — сохраняется", "Текст, ссылки, картинки — всё попадает в историю буфера в чёлке.");

            // строка-образец: можно выделить и Ctrl+C или нажать кнопку
            var g = new Grid { Width = 480, Margin = new Thickness(0, 22, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var box = new TextBox { Text = Sample, IsReadOnly = true, IsReadOnlyCaretVisible = false, VerticalContentAlignment = VerticalAlignment.Center, Height = 40, FontSize = 13.5 };
            g.Children.Add(box);
            var copyBtn = Pill("Скопировать", true, () => { }, small: true);
            Grid.SetColumn(copyBtn, 1);
            copyBtn.Margin = new Thickness(10, 0, 0, 0);
            g.Children.Add(copyBtn);
            sp.Children.Add(g);
            var hint = Txt("…или выдели строку и нажми Ctrl+C", 12, "Muted");
            hint.HorizontalAlignment = HorizontalAlignment.Center;
            hint.Margin = new Thickness(0, 8, 0, 0);
            sp.Children.Add(hint);

            // два пункта: скопировать → открыть историю
            FrameworkElement Item(string num, FrameworkElement content, out Border badge, out Border numEl)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 7) };
                var holder = new Grid { Width = 24, Height = 24, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
                numEl = new Border { CornerRadius = new CornerRadius(12), Child = new TextBlock { Text = num, FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                numEl.SetResourceReference(Border.BackgroundProperty, "SelBg");
                ((TextBlock)numEl.Child).SetResourceReference(TextBlock.ForegroundProperty, "Fg");
                badge = DoneBadge(24);
                holder.Children.Add(numEl);
                holder.Children.Add(badge);
                row.Children.Add(holder);
                content.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(content);
                return row;
            }
            var list = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) };
            list.Children.Add(Item("1", Txt("Скопируй строку", 14), out var b1, out var n1));
            FrameworkElement second;
            if (hk)
            {
                var r = new StackPanel { Orientation = Orientation.Horizontal };
                var t = Txt("Нажми", 14);
                t.VerticalAlignment = VerticalAlignment.Center;
                t.Margin = new Thickness(0, 0, 10, 0);
                r.Children.Add(t);
                r.Children.Add(Keycaps(s.HotkeyClipboard));
                var t2 = Txt("— она уже там", 14);
                t2.VerticalAlignment = VerticalAlignment.Center;
                t2.Margin = new Thickness(10, 0, 0, 0);
                r.Children.Add(t2);
                second = r;
            }
            else second = Txt("Открой чёлку и вкладку «Буфер» — она уже там", 14);
            var item2 = Item("2", second, out var b2, out var n2);
            item2.Opacity = 0.45;
            list.Children.Add(item2);
            sp.Children.Add(list);
            var nav = NavRow();
            sp.Children.Add(nav);

            void MarkCopied()
            {
                if (copied || _step != StepClip) return;
                copied = true;
                Journal.Info("welcome", "буфер: строка скопирована"); // сам текст в журнал не пишем
                n1.Visibility = Visibility.Collapsed;
                Pop(b1);
                item2.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(250)));
            }
            copyBtn.Click += (_, __) =>
            {
                try { Clipboard.SetText(Sample); } catch (Exception ex) { Journal.Warn("welcome", "буфер занят: " + ex.Message); return; }
                MarkCopied();
            };
            // Ctrl+C или «Копировать» из меню поля — команда уже обработана полем, ловим и обработанные
            box.AddHandler(CommandManager.ExecutedEvent, new ExecutedRoutedEventHandler((_, e) =>
            {
                if (e.Command == ApplicationCommands.Copy && box.SelectionLength > 0) Dispatcher.BeginInvoke(new Action(MarkCopied));
            }), true);
            // строка появилась в истории буфера Isle (как бы её ни скопировали)
            var clip = AppHost.Clipboard;
            if (clip != null)
            {
                NotifyCollectionChangedEventHandler onClip = (_, e) =>
                {
                    if (e.Action == NotifyCollectionChangedAction.Add && clip.Items.Count > 0 && clip.Items[0].Text == Sample)
                        Dispatcher.BeginInvoke(new Action(MarkCopied));
                };
                clip.Items.CollectionChanged += onClip;
                _cleanup = () => clip.Items.CollectionChanged -= onClip;
            }

            // открыта история буфера — шаг выполнен
            int mine = _token;
            Loop(sp, TimeSpan.FromMilliseconds(150), () =>
            {
                var main = AppHost.Window;
                if (_clipDone || !copied || mine != _token || main?.State != IslandState.Expanded || main.CurrentTab != "clip") return;
                _clipDone = true;
                Journal.Info("welcome", "буфер: история открыта");
                n2.Visibility = Visibility.Collapsed;
                Pop(b2);
                _buddy.Cheer();
                // дать посмотреть на строку в истории, потом свернуть и дальше
                After(2200, () => { CollapseIsland(); Next(); });
            });
            return sp;
        }

        // ---------- 4) Снимки ----------

        private FrameworkElement ShotStep()
        {
            var s = AppSettings.Current;
            var sp = Page("Снимок за секунду", KeyText(s.HotkeyScreenshot) + " — область с пометками, " + KeyText(s.HotkeyRecord) + " — запись области.");

            // картинка: выделенная область «бегущими муравьями» и стрелка-пометка цветом акцента
            var art = new Grid { Width = 300, Height = 120, Margin = new Thickness(0, 20, 0, 4) };
            var bg = new Border { CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1) };
            bg.SetResourceReference(Border.BackgroundProperty, "ChipBg");
            bg.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
            art.Children.Add(bg);
            var sel = new Rectangle { Width = 170, Height = 74, StrokeThickness = 1.6, StrokeDashArray = new DoubleCollection { 4, 3 }, RadiusX = 3, RadiusY = 3 };
            sel.SetResourceReference(Shape.StrokeProperty, "Fg");
            sel.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(0, -14, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
            art.Children.Add(sel);
            var arrow = new Path
            {
                Data = Geometry.Parse("M0,34 L40,6 M40,6 L26,8 M40,6 L36,19"), StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(30, 0, 0, 6)
            };
            arrow.SetResourceReference(Shape.StrokeProperty, "Accent");
            art.Children.Add(arrow);
            var rec = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 14, 0) };
            var dot = new Ellipse { Width = 8, Height = 8, Fill = Red, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) };
            dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            rec.Children.Add(dot);
            rec.Children.Add(Txt("REC", 10.5, "Muted", FontWeights.SemiBold));
            art.Children.Add(rec);
            sp.Children.Add(art);

            var keys = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
            var k1 = KeyRow(s.HotkeyScreenshot, "снимок области с пометками");
            var k2 = KeyRow(s.HotkeyRecord, "запись области");
            Stagger(k1, 0, 250); Stagger(k2, 1, 250);
            keys.Children.Add(k1); keys.Children.Add(k2);
            sp.Children.Add(keys);

            var sw = Switch(s.PrintScreenIsle);
            var (row, sub) = SettingRow(Icons.Crop, "PrintScreen через Isle", "Клавиша PrintScreen откроет выделение Isle вместо «Ножниц»", sw);
            row.Margin = new Thickness(0, 14, 0, 0);
            sw.Click += (_, __) =>
            {
                bool want = sw.IsChecked == true;
                bool ok = AppHost.Window?.SetPrintScreen(want) ?? false;
                Journal.Info("welcome", "PrintScreen через Isle: " + (want ? ok ? "включён" : "Windows не дала поставить перехват" : "выключен"));
                if (want && !ok)
                {
                    sw.IsChecked = false;
                    sub.Text = "Windows не дал занять клавишу — выключи «PrintScreen открывает Ножницы» в Параметрах";
                    sub.Foreground = Red;
                }
                else if (ok && want) _buddy.Cheer();
            };
            sp.Children.Add(row);
            sp.Children.Add(NavRow());
            return sp;
        }

        // ---------- кирпичики для шагов-рассказов ----------

        /// <summary>«Экран» для живой картинки: скруглённая подложка, внутри — свободная сетка.</summary>
        private static (Border screen, Grid area) MiniScreen(double w, double h)
        {
            var screen = new Border { Width = w, Height = h, CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 6) };
            screen.SetResourceReference(Border.BackgroundProperty, "ChipBg");
            screen.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
            var area = new Grid();
            screen.Child = area;
            return (screen, area);
        }

        /// <summary>Чёрная модель чёлки, прижатая к верху «экрана».</summary>
        private static Border MiniIsland(double w, double h, double r = 18) => new()
        {
            Background = Brushes.Black, CornerRadius = new CornerRadius(0, 0, r, r), Width = w, Height = h,
            VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center, ClipToBounds = true
        };

        /// <summary>Плавно поменять размер модели чёлки.</summary>
        private static void Resize(FrameworkElement island, double w, double h)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
            var d = TimeSpan.FromMilliseconds(460);
            island.BeginAnimation(WidthProperty, new DoubleAnimation(w, d) { EasingFunction = ease });
            island.BeginAnimation(HeightProperty, new DoubleAnimation(h, d) { EasingFunction = ease });
        }

        /// <summary>Показать один слой из нескольких (остальные гаснут).</summary>
        private static void ShowLayer(FrameworkElement[] layers, int index)
        {
            for (int i = 0; i < layers.Length; i++)
                layers[i].BeginAnimation(OpacityProperty, new DoubleAnimation(i == index ? 1 : 0, TimeSpan.FromMilliseconds(i == index ? 300 : 150)) { BeginTime = TimeSpan.FromMilliseconds(i == index ? 220 : 0) });
        }

        /// <summary>Короткий факт: значок в плашке, жирный заголовок, серое пояснение.</summary>
        private static FrameworkElement Fact(string icon, string title, string text)
        {
            var g = new Grid { Width = 470, Margin = new Thickness(0, 5, 0, 5) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var ic = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(9), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Child = Ico(icon, 15) };
            ic.SetResourceReference(Border.BackgroundProperty, "SelBg");
            g.Children.Add(ic);
            var tx = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            tx.Children.Add(Txt(title, 13.5, "Fg", FontWeights.SemiBold));
            var sub = Txt(text, 12, "Muted");
            sub.Margin = new Thickness(0, 1, 0, 0);
            tx.Children.Add(sub);
            Grid.SetColumn(tx, 1);
            g.Children.Add(tx);
            return g;
        }

        private static StackPanel Facts(params FrameworkElement[] rows)
        {
            var list = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
            for (int i = 0; i < rows.Length; i++) { Stagger(rows[i], i, 250, 90); list.Children.Add(rows[i]); }
            return list;
        }

        private static Brush AccentNow(double angle = 0) => AccentPaint.Brush(AppSettings.Current.AccentHex, angle);
        private static readonly Brush IslandMuted = Frz(Color.FromRgb(0x8B, 0x8B, 0x95));

        // ---------- 5) Музыка ----------

        private FrameworkElement MusicStep()
        {
            var sp = Page("Музыка в чёлке", "Spotify, браузер, Яндекс Музыка — в чёлке обложка и полоски в такт, а Клоди танцует.");
            var (screen, area) = MiniScreen(420, 136);
            sp.Children.Add(screen);

            // модель плеера: обложка, название, полоски спектра и строка песни
            var island = MiniIsland(372, 108, 22);
            area.Children.Add(island);
            var body = new StackPanel { Margin = new Thickness(14, 10, 14, 0) };
            island.Child = body;
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition());
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.Children.Add(new Border
            {
                Width = 38, Height = 38, CornerRadius = new CornerRadius(9), Background = AccentNow(45),
                Child = new Icon { Data = Icons.Wave, Width = 18, Height = 18, Foreground = Brushes.White }
            });
            var names = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(new TextBlock { Text = "Песенка из чёлки", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
            names.Children.Add(new TextBlock { Text = "Клоди", FontSize = 10.5, Foreground = IslandMuted });
            Grid.SetColumn(names, 1);
            top.Children.Add(names);
            // полоски спектра «играют» цветом акцента
            var bars = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Height = 22 };
            double[] hs = { 10, 18, 13, 21, 9, 15 };
            var accent = AccentNow(90);
            for (int i = 0; i < hs.Length; i++)
            {
                var bar = new Border { Width = 3, Height = hs[i], CornerRadius = new CornerRadius(1.5), Margin = new Thickness(1.5, 0, 1.5, 0), VerticalAlignment = VerticalAlignment.Center, Background = accent };
                bar.BeginAnimation(HeightProperty, new DoubleAnimation(3, hs[i], TimeSpan.FromMilliseconds(280 + i * 70)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });
                bars.Children.Add(bar);
            }
            Grid.SetColumn(bars, 2);
            top.Children.Add(bars);
            body.Children.Add(top);

            // строка песни: слова загораются по очереди, как в караоке
            var line = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
            var words = "Я живу в чёлке и пою для тебя".Split(' ');
            var els = new List<TextBlock>();
            foreach (var w in words)
            {
                var tb = new TextBlock { Text = w, FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Opacity = 0.28, Margin = new Thickness(0, 0, 6, 0) };
                els.Add(tb);
                line.Children.Add(tb);
            }
            body.Children.Add(line);
            int at = -2;
            Loop(sp, TimeSpan.FromMilliseconds(400), () =>
            {
                if (++at > words.Length + 2) at = -1; // пауза на конце строки — и сначала
                for (int i = 0; i < els.Count; i++)
                {
                    els[i].Foreground = i == at ? accent : Brushes.White;
                    els[i].BeginAnimation(OpacityProperty, new DoubleAnimation(at < 0 ? 0.28 : i < at ? 0.85 : i == at ? 1 : 0.28, TimeSpan.FromMilliseconds(180)));
                }
            });

            sp.Children.Add(Facts(
                Fact(Icons.Text, "Текст песни", "Кликни по треку — слова поедут в такт, как в караоке"),
                Fact(Icons.Volume, "Своя громкость", "На Главной — крутилка громкости самого плеера, отдельно от системной")));
            sp.Children.Add(NavRow());
            return sp;
        }

        // ---------- 6) Таймер, будильник, календарь ----------

        private FrameworkElement TimeStep()
        {
            var sp = Page("Таймер и календарь", "Чёлка следит за временем, пока ты занят.");
            var (screen, area) = MiniScreen(420, 96);
            sp.Children.Add(screen);
            var bg = new SolidColorBrush(Colors.Black);
            var island = MiniIsland(200, 34, 17);
            island.Background = bg;
            area.Children.Add(island);
            var layers = new Grid();
            island.Child = layers;
            var red = Frz(Color.FromRgb(0xF8, 0x71, 0x71));

            // а) идёт таймер: красные часики и обратный отсчёт
            var timer = new Grid { Margin = new Thickness(14, 0, 14, 0) };
            timer.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
                Children = { new Icon { Data = Icons.Timer, Width = 14, Height = 14, Foreground = red }, new TextBlock { Text = "Таймер", FontSize = 12, Foreground = Brushes.White, Margin = new Thickness(8, 0, 0, 0) } }
            });
            var count = new TextBlock { Text = "0:03", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = red, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            timer.Children.Add(count);

            // б) время вышло: колокольчик качается, чёлка переливается красным
            var bell = new Icon { Data = Icons.Bell, Width = 16, Height = 16, Foreground = Brushes.White, RenderTransformOrigin = new Point(0.5, 0.1), RenderTransform = new RotateTransform() };
            var alarm = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0,
                Children = { bell, new TextBlock { Text = "Время вышло! Нажми, чтобы выключить", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(9, 0, 0, 0) } }
            };

            // в) событие календаря начинается
            var ev = new Grid { Margin = new Thickness(10, 0, 14, 0), Opacity = 0 };
            ev.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ev.ColumnDefinitions.Add(new ColumnDefinition());
            ev.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ev.Children.Add(new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Background = AccentNow(45), VerticalAlignment = VerticalAlignment.Center, Child = new Icon { Data = Icons.Calendar, Width = 15, Height = 15, Foreground = Brushes.White } });
            var evText = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            evText.Children.Add(new TextBlock { Text = "Созвон с командой", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
            evText.Children.Add(new TextBlock { Text = "Начинается · " + DateTime.Now.ToString("HH:mm"), FontSize = 10.5, Foreground = IslandMuted });
            Grid.SetColumn(evText, 1);
            ev.Children.Add(evText);
            var evBell = new Icon { Data = Icons.Bell, Width = 14, Height = 14, Foreground = IslandMuted, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(evBell, 2);
            ev.Children.Add(evBell);

            FrameworkElement[] all = { timer, alarm, ev };
            foreach (var l in all) layers.Children.Add(l);

            // сценарий по секундам: 3…2…1 → звенит (2 с) → напоминание календаря (3 с) → сначала
            int sec = -1;
            void Tick()
            {
                sec = (sec + 1) % 8;
                if (sec <= 2)
                {
                    if (sec == 0)
                    {
                        Resize(island, 200, 34);
                        ShowLayer(all, 0);
                        bg.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Colors.Black, TimeSpan.FromMilliseconds(250)));
                    }
                    count.Text = "0:0" + (3 - sec);
                }
                else if (sec == 3)
                {
                    count.Text = "0:00";
                    Resize(island, 330, 40);
                    ShowLayer(all, 1);
                    bg.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Color.FromRgb(0x9B, 0x1C, 0x1C), Color.FromRgb(0x3A, 0x3A, 0x42), TimeSpan.FromMilliseconds(420)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
                    ((RotateTransform)bell.RenderTransform).BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(-22, 22, TimeSpan.FromMilliseconds(140)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
                }
                else if (sec == 5)
                {
                    ((RotateTransform)bell.RenderTransform).BeginAnimation(RotateTransform.AngleProperty, null);
                    bg.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Colors.Black, TimeSpan.FromMilliseconds(250)));
                    Resize(island, 300, 46);
                    ShowLayer(all, 2);
                }
            }
            sp.Loaded += (_, __) => Tick();
            Loop(sp, TimeSpan.FromSeconds(1), Tick);

            sp.Children.Add(Facts(
                Fact(Icons.Timer, "Таймер и будильник", "Вкладка «Заметка». Время вышло — чёлка мигает и пищит, пока не нажмёшь"),
                Fact(Icons.Calendar, "Календарь", "События по дням, а в момент начала — напоминание со звуком"),
                Fact(Icons.Note, "Напоминания по-человечески", "«позвонить маме завтра в 9» — Isle сам поймёт, когда")));
            sp.Children.Add(NavRow());
            return sp;
        }

        // ---------- 7) Телефон и полка ----------

        private FrameworkElement FilesStep()
        {
            var sp = Page("Телефон и полка", "Файлы туда-сюда — без проводов и приложений.");
            var (screen, area) = MiniScreen(420, 140);
            sp.Children.Add(screen);
            var stage = new Canvas();
            area.Children.Add(stage);

            // телефон с QR-кодом слева
            var phone = new Border { Width = 66, Height = 118, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(2), Background = Brushes.Black, RenderTransformOrigin = new Point(0.5, 0.5) };
            phone.SetResourceReference(Border.BorderBrushProperty, "Fg");
            var qr = new System.Windows.Controls.Primitives.UniformGrid { Rows = 7, Columns = 7, Width = 42, Height = 42 };
            var rnd = new Random(7);
            for (int y = 0; y < 7; y++)
                for (int x = 0; x < 7; x++)
                {
                    // угловые «глазки» QR — всегда залиты по краю, середина — случайный узор
                    bool corner = (x < 3 && y < 3) || (x > 3 && y < 3) || (x < 3 && y > 3);
                    bool hole = x == 1 && y == 1 || x == 5 && y == 1 || x == 1 && y == 5;
                    bool on = corner ? !hole : rnd.Next(2) == 0;
                    qr.Children.Add(new Border { Background = on ? Brushes.White : Brushes.Transparent, Margin = new Thickness(0.4) });
                }
            phone.Child = qr;
            Canvas.SetLeft(phone, 70); Canvas.SetTop(phone, 10);
            stage.Children.Add(phone);

            // компьютер с чёлкой справа
            var monitor = new Border { Width = 150, Height = 92, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(2), RenderTransformOrigin = new Point(0.5, 0.5) };
            monitor.SetResourceReference(Border.BorderBrushProperty, "Fg");
            monitor.SetResourceReference(Border.BackgroundProperty, "SelBg");
            monitor.Child = new Border { Width = 50, Height = 11, CornerRadius = new CornerRadius(0, 0, 6, 6), Background = Brushes.Black, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center };
            Canvas.SetLeft(monitor, 222); Canvas.SetTop(monitor, 14);
            stage.Children.Add(monitor);
            var stand = new Border { Width = 46, Height = 4, CornerRadius = new CornerRadius(2) };
            stand.SetResourceReference(Border.BackgroundProperty, "Fg");
            Canvas.SetLeft(stand, 274); Canvas.SetTop(stand, 116);
            stage.Children.Add(stand);

            // летящая карточка: фото с телефона на компьютер, потом файл обратно
            var tokenIcon = new Icon { Data = Icons.Image, Width = 14, Height = 14, Foreground = Brushes.White };
            var token = new Border { Width = 30, Height = 24, CornerRadius = new CornerRadius(7), Background = AccentNow(45), Child = tokenIcon, Opacity = 0, RenderTransform = new TranslateTransform() };
            Canvas.SetLeft(token, 88); Canvas.SetTop(token, 57);
            stage.Children.Add(token);
            bool toPc = true;
            void Fly()
            {
                var tt = (TranslateTransform)token.RenderTransform;
                double from = toPc ? 0 : 194, to = toPc ? 0 + 194 : 0;
                tokenIcon.Data = toPc ? Icons.Image : Icons.File;
                var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
                tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(900)) { EasingFunction = ease });
                // путь — дуга: вверх и вниз
                var arc = new DoubleAnimationUsingKeyFrames();
                arc.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                arc.KeyFrames.Add(new EasingDoubleKeyFrame(-38, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(450)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
                arc.KeyFrames.Add(new EasingDoubleKeyFrame(-4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900)), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
                tt.BeginAnimation(TranslateTransform.YProperty, arc);
                var fade = new DoubleAnimationUsingKeyFrames();
                fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150))));
                fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(760))));
                fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900))));
                token.BeginAnimation(OpacityProperty, fade);
                // получатель чуть «вздрагивает», когда карточка долетела
                var receiver = toPc ? monitor : phone;
                After(880, () =>
                {
                    var st = new ScaleTransform();
                    receiver.RenderTransform = st;
                    var bump = new DoubleAnimation(1, 1.07, TimeSpan.FromMilliseconds(110)) { AutoReverse = true };
                    st.BeginAnimation(ScaleTransform.ScaleXProperty, bump);
                    st.BeginAnimation(ScaleTransform.ScaleYProperty, bump);
                });
                toPc = !toPc;
            }
            sp.Loaded += (_, __) => After(400, Fly);
            Loop(sp, TimeSpan.FromMilliseconds(2000), Fly);

            sp.Children.Add(Facts(
                Fact(Icons.Phone, "Телефон без приложений", "Вкладка «Телефон»: наведи камеру на QR — фото, видео, файлы и текст летят в обе стороны"),
                Fact(Icons.Wifi, "Одна сеть", "Общий Wi-Fi или интернет с телефона. Всё пришедшее остаётся в истории"),
                Fact(Icons.Tray, "Полка", "Перетащи файл на чёлку — полежит, пока не вытащишь в нужное окно")));
            sp.Children.Add(NavRow());
            return sp;
        }

        // ---------- 8) ИИ и голос ----------

        private FrameworkElement AiStep()
        {
            var s = AppSettings.Current;
            var sp = Page("ИИ и голос", "Чат в чёлке открывает программы, ставит таймеры и отвечает на вопросы.");
            var (screen, area) = MiniScreen(420, 150);
            sp.Children.Add(screen);

            // модель чата: строка ввода с «+» и микрофоном, над ней — пузыри
            var dock = new DockPanel { Margin = new Thickness(14, 10, 14, 12), LastChildFill = true };
            area.Children.Add(dock);
            var input = new Border { Height = 36, CornerRadius = new CornerRadius(18), Padding = new Thickness(12, 0, 10, 0) };
            input.SetResourceReference(Border.BackgroundProperty, "SelBg");
            DockPanel.SetDock(input, Dock.Bottom);
            dock.Children.Add(input);
            var ig = new Grid();
            ig.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ig.ColumnDefinitions.Add(new ColumnDefinition());
            ig.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var plus = Ico(Icons.Plus, 15, "Muted");
            plus.VerticalAlignment = VerticalAlignment.Center;
            ig.Children.Add(plus);
            var field = new Grid { Margin = new Thickness(10, 0, 10, 0) };
            Grid.SetColumn(field, 1);
            ig.Children.Add(field);
            var typed = Txt("", 13);
            typed.TextWrapping = TextWrapping.NoWrap;
            typed.VerticalAlignment = VerticalAlignment.Center;
            field.Children.Add(typed);
            // волна голоса — пока «слушаем»
            var wave = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Opacity = 0 };
            for (int i = 0; i < 22; i++)
            {
                var b = new Border { Width = 3, Height = 4, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(1.5, 0, 1.5, 0), VerticalAlignment = VerticalAlignment.Center };
                b.SetResourceReference(Border.BackgroundProperty, "Fg");
                b.BeginAnimation(HeightProperty, new DoubleAnimation(3, 6 + (i * 7 % 13), TimeSpan.FromMilliseconds(220 + i * 37 % 180)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
                wave.Children.Add(b);
            }
            field.Children.Add(wave);
            var mic = new Icon { Data = Icons.Mic, Width = 15, Height = 15, VerticalAlignment = VerticalAlignment.Center };
            mic.SetResourceReference(Isle.Helpers.Icon.ForegroundProperty, "Muted");
            Grid.SetColumn(mic, 2);
            ig.Children.Add(mic);
            input.Child = ig;

            var chat = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 8) };
            dock.Children.Add(chat);
            Border Bubble(string text, bool mine)
            {
                var t = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
                var b = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 6, 12, 7), Margin = new Thickness(0, 3, 0, 3), MaxWidth = 300, Child = t, Opacity = 0, HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left, RenderTransformOrigin = new Point(mine ? 1 : 0, 1) };
                if (mine) { b.Background = AccentNow(45); t.Foreground = _onAccent; }
                else { b.SetResourceReference(Border.BackgroundProperty, "SelBg"); t.SetResourceReference(TextBlock.ForegroundProperty, "Fg"); }
                return b;
            }
            const string ask = "поставь таймер на 10 минут";
            var q = Bubble(ask, true);
            var a = Bubble("Готово — таймер на 10:00 ⏱", false);
            chat.Children.Add(q); chat.Children.Add(a);
            void Pop2(UIElement el)
            {
                var st = new ScaleTransform(0.85, 0.85);
                el.RenderTransform = st;
                var ease = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut };
                st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.85, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
                st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.85, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
                el.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
            }

            // сценарий по «тикам» 70 мс: печатаем вопрос → он улетает в чат → ответ → голосовой ввод → сначала
            int n = -6, len = ask.Length;
            Loop(sp, TimeSpan.FromMilliseconds(70), () =>
            {
                n++;
                if (n >= 0 && n < len) typed.Text = ask.Substring(0, n + 1);
                else if (n == len + 5) { typed.Text = ""; Pop2(q); }
                else if (n == len + 14) Pop2(a);
                else if (n == len + 34)
                {
                    // голос: волна вместо текста, микрофон цветом акцента
                    typed.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)));
                    wave.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
                    mic.Foreground = AccentNow();
                }
                else if (n == len + 62)
                {
                    wave.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
                    typed.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
                    mic.SetResourceReference(Isle.Helpers.Icon.ForegroundProperty, "Muted");
                    q.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)));
                    a.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)));
                    n = -6;
                }
            });

            var keys = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
            var k1 = KeyRow(s.HotkeyVoice, "голос в текст — туда, где курсор");
            var k2 = KeyRow(s.HotkeyVoiceCommand, "голосовая команда: «открой телеграм»");
            Stagger(k1, 0, 250); Stagger(k2, 1, 250);
            keys.Children.Add(k1); keys.Children.Add(k2);
            sp.Children.Add(keys);
            var note = Txt("В чате «+» или Ctrl+V — картинки и файлы. Нужен ключ ИИ: бесплатный у Groq → Настройки → ИИ.", 12, "Muted");
            note.TextAlignment = TextAlignment.Center;
            note.HorizontalAlignment = HorizontalAlignment.Center;
            note.MaxWidth = 470;
            note.Margin = new Thickness(0, 10, 0, 0);
            sp.Children.Add(note);
            sp.Children.Add(NavRow());
            return sp;
        }

        // ---------- 9) Под себя: город, акцент, Клоди ----------

        private FrameworkElement SetupStep()
        {
            var s = AppSettings.Current;
            var sp = Page("Под себя", "Город для погоды, цвет и кто живёт в чёлке. Видно сразу.");
            var preview = new NotchPreview(420, 64);
            preview.Screen.Margin = new Thickness(0, 18, 0, 6);
            sp.Children.Add(preview.Screen);

            // город: поиск через ту же геокодировку, что в Настройках; по умолчанию — текущий
            var cityRow = new Grid { Width = 480, Margin = new Thickness(0, 12, 0, 0) };
            cityRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            cityRow.ColumnDefinitions.Add(new ColumnDefinition());
            cityRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var pin = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(10), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Child = Ico(Icons.MapPin, 16) };
            pin.SetResourceReference(Border.BackgroundProperty, "SelBg");
            cityRow.Children.Add(pin);
            var box = new TextBox { Text = s.City, Tag = "Город", Height = 38, VerticalContentAlignment = VerticalAlignment.Center, FontSize = 13.5 };
            Grid.SetColumn(box, 1);
            cityRow.Children.Add(box);
            var status = Txt("", 12, "Muted");
            status.HorizontalAlignment = HorizontalAlignment.Center;
            status.Margin = new Thickness(0, 6, 0, 0);
            bool busy = false;
            async void Find()
            {
                var q = box.Text.Trim();
                if (busy || q.Length == 0) return;
                if (string.Equals(q, s.City, StringComparison.CurrentCultureIgnoreCase)) { status.Text = "Уже стоит: " + s.City; return; }
                busy = true;
                status.Text = "Ищу…";
                try
                {
                    var r = await WeatherService.GeocodeAsync(q);
                    if (r == null) { status.Text = "Город не найден — попробуй иначе"; return; }
                    s.City = r.Value.name; s.Latitude = r.Value.lat; s.Longitude = r.Value.lon;
                    s.Save();
                    box.Text = s.City;
                    status.Text = "Готово: " + s.City;
                    Journal.Info("welcome", "город выбран");
                    if (AppHost.Window != null) await AppHost.Window.RefreshWeather();
                    preview.Refresh();
                }
                catch (Exception ex) { status.Text = "Нет связи: " + ex.Message; }
                finally { busy = false; }
            }
            var findBtn = Pill("Найти", false, Find, small: true);
            findBtn.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(findBtn, 2);
            cityRow.Children.Add(findBtn);
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Find(); e.Handled = true; } };
            sp.Children.Add(cityRow);
            sp.Children.Add(status);

            // акцент: те же цвета и градиенты, что в Настройках → Внешний вид; применяется сразу
            var accents = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0), MaxWidth = 500 };
            var rings = new List<(Border ring, string hex)>();
            void MarkAccent()
            {
                foreach (var (ring, hex) in rings)
                {
                    if (string.Equals(hex, s.AccentHex, StringComparison.OrdinalIgnoreCase)) ring.SetResourceReference(Border.BorderBrushProperty, "Fg");
                    else ring.BorderBrush = Brushes.Transparent;
                }
            }
            var all = new List<(string hex, string tip)>();
            foreach (var h in SettingsWindow.AccentColors) all.Add((h, null));
            foreach (var (value, name) in SettingsWindow.AccentGradients) all.Add((value, "Градиент «" + name + "»"));
            foreach (var (hex, tip) in all)
            {
                var ring = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), BorderThickness = new Thickness(2), Padding = new Thickness(3), Margin = new Thickness(3), Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = tip };
                ring.Child = new Border { CornerRadius = new CornerRadius(10), Background = AccentPaint.Brush(hex, 45) };
                ring.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    s.AccentHex = hex;
                    s.Save();
                    AppHost.Window?.ApplyAppearance();
                    UpdateOnAccent();
                    MarkAccent();
                    preview.Refresh();
                };
                rings.Add((ring, hex));
                accents.Children.Add(ring);
            }
            MarkAccent();
            sp.Children.Add(accents);

            // Клоди в чёлке: выключить — остаются прежние «глаза» (если их выбирали) или ничего
            string prevOther = s.Character == "pixel" ? "none" : s.Character;
            var sw = Switch(s.Character == "pixel");
            var (petRow, _) = SettingRow(Icons.Moon, "Клоди в чёлке", "Танцует под музыку, думает с ИИ, спит ночью", sw);
            petRow.Margin = new Thickness(0, 10, 0, 0);
            sw.Click += (_, __) =>
            {
                s.Character = sw.IsChecked == true ? "pixel" : prevOther;
                s.Save();
                AppHost.Window?.ApplyAppearance();
                preview.Refresh();
                if (sw.IsChecked == true) preview.Pet.Cheer();
                Journal.Info("welcome", "персонаж: " + s.Character);
            };
            sp.Children.Add(petRow);
            sp.Children.Add(NavRow());
            return sp;
        }

        // ---------- 10) Разрешения ----------

        private FrameworkElement PermsStep()
        {
            var s = AppSettings.Current;
            var sp = Page("Пара разрешений", "Можно поменять потом в Настройках.");
            var list = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };

            // автозапуск: при первом показе включён по умолчанию — тот же реестр, что в Настройках
            bool autoNow;
            try { autoNow = SystemControls.Autostart; } catch { autoNow = false; }
            var auto = Switch(_firstRun && !_autostartDecided ? true : autoNow);
            var (autoRow, autoSub) = SettingRow(Icons.Power, "Запускать с Windows", "Чёлка будет на месте сразу после включения", auto);
            auto.Click += (_, __) =>
            {
                bool want = auto.IsChecked == true;
                try
                {
                    SystemControls.Autostart = want;
                    _autostartDecided = true;
                    Journal.Info("welcome", "автозапуск: " + (want ? "включён" : "выключен"));
                }
                catch (Exception ex)
                {
                    try { auto.IsChecked = SystemControls.Autostart; } catch { }
                    autoSub.Text = "Не получилось: " + ex.Message;
                    autoSub.Foreground = Red;
                }
            };
            list.Children.Add(autoRow);

            // уведомления приложений: доступ выдаёт Windows по запросу
            var nf = AppHost.Notifications;
            bool works = nf?.Available == true;
            Button allow = null;
            var (nfRow, nfSub) = SettingRow(Icons.Bell, "Уведомления приложений", works ? "Уже работают — будут всплывать в чёлке" : "Сообщения из мессенджеров и почты — в чёлке", null);
            bool asking = false;
            allow = Pill(works ? "Готово" : "Разрешить", false, async () =>
            {
                if (asking || nf == null || nf.Available) return;
                asking = true;
                allow.IsEnabled = false;
                nfSub.Text = "Спрашиваю у Windows…";
                s.NotificationToasts = true;
                s.Save();
                try { await nf.StartAsync(); } catch { }
                Journal.Info("welcome", "уведомления: " + nf.Status);
                if (nf.Available)
                {
                    nfSub.Text = "Готово — уведомления будут в чёлке";
                    ((TextBlock)((Border)allow.Content).Child).Text = "Готово";
                    _buddy.Cheer();
                }
                else
                {
                    nfSub.Text = nf.Status + ". Разрешить можно в Параметрах Windows → Конфиденциальность → Уведомления";
                    allow.IsEnabled = true;
                }
                asking = false;
            }, small: true);
            allow.IsEnabled = !works;
            allow.Margin = new Thickness(12, 0, 0, 0);
            allow.VerticalAlignment = VerticalAlignment.Center;
            var nfGrid = (Grid)nfRow.Child;
            Grid.SetColumn(allow, 2);
            nfGrid.Children.Add(allow);
            list.Children.Add(nfRow);

            // точная скорость Steam — только если Steam есть; Steam перезапускается только по явному щелчку
            if (_hasSteam)
            {
                var steam = Switch(s.SteamExactSpeed && s.SteamExactActivated);
                steam.IsEnabled = !s.SteamExactActivated;
                var (stRow, stSub) = SettingRow(Icons.Download, "Точная скорость Steam",
                    s.SteamExactActivated ? "Уже работает" : "Steam перезапустится — игры из него закроются", steam);
                if (!s.SteamExactActivated) stSub.Foreground = Amber;
                // вдруг уже работает (включали раньше) — тогда просто отметить
                if (!s.SteamExactActivated)
                    sp.Loaded += async (_, __) =>
                    {
                        bool up = false;
                        try { up = await SteamClientApi.IsWorkingAsync(); } catch { }
                        if (!up || s.SteamExactActivated || steam.IsChecked == true) return;
                        s.SteamExactActivated = true;
                        s.Save();
                        steam.IsChecked = true; steam.IsEnabled = false;
                        stSub.Text = "Уже работает"; stSub.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                    };
                steam.Click += async (_, __) =>
                {
                    if (steam.IsChecked != true) return;
                    steam.IsEnabled = false;
                    stSub.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
                    Journal.Info("welcome", "точная скорость Steam: пользователь согласился");
                    var progress = new Progress<string>(t => { stSub.Text = t; Journal.Info("welcome", "Steam: " + t); });
                    bool ok;
                    try { ok = await SteamClientApi.ActivateAsync(progress); }
                    catch (Exception ex) { ok = false; stSub.Text = "Ошибка: " + ex.Message; }
                    if (ok)
                    {
                        s.SteamExactActivated = true;
                        s.Save();
                        stSub.Foreground = Green;
                        _buddy.Cheer();
                    }
                    else
                    {
                        steam.IsChecked = false;
                        steam.IsEnabled = true;
                        stSub.Foreground = Red;
                    }
                };
                list.Children.Add(stRow);
            }
            for (int i = 0; i < list.Children.Count; i++) Stagger(list.Children[i], i, 200, 80);
            sp.Children.Add(list);
            sp.Children.Add(NavRow());
            return sp;
        }

        // ---------- финал ----------

        private FrameworkElement Done()
        {
            var s = AppSettings.Current;
            ApplyDefaults();
            _buddy.Mood = Mood.Done;
            _buddy.Cheer();
            Dispatcher.BeginInvoke(new Action(BurstConfetti), DispatcherPriority.Background);
            var sp = Page("Готово", "Три клавиши, которые стоит запомнить:", buddy: true);
            var keys = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
            var rows = new[]
            {
                KeyRow(s.Hotkey, "открыть чёлку"),
                KeyRow(s.HotkeyClipboard, "история буфера"),
                KeyRow(s.HotkeyScreenshot, "снимок области"),
            };
            for (int i = 0; i < rows.Length; i++) { Stagger(rows[i], i, 300, 90); keys.Children.Add(rows[i]); }
            sp.Children.Add(keys);
            sp.Children.Add(Buttons(Pill("Начать", true, FlyToIsland)));
            var again = Txt("Знакомство можно пройти снова: правый клик по чёлке → Знакомство", 12, "Muted");
            again.HorizontalAlignment = HorizontalAlignment.Center;
            again.TextAlignment = TextAlignment.Center;
            again.Margin = new Thickness(0, 14, 0, 0);
            sp.Children.Add(again);
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

        // ---------- финал: карточка сжимается в чёрную пилюлю и улетает на место чёлки ----------

        /// <summary>Закрыть окно по-настоящему, мимо перехвата в OnClosing.</summary>
        private void CloseNow()
        {
            _closeNow = true;
            try { Close(); } catch (InvalidOperationException) { }
        }

        /// <summary>Alt+F4 и прочие «закрыть» — тоже через финал: пилюля улетает в чёлку, а не окно просто исчезает.</summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_closeNow && !_landed && Application.Current != null && !Dispatcher.HasShutdownStarted && AppHost.Window != null)
            {
                e.Cancel = true;
                if (!_flying)
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_closed || _flying) return;
                        Journal.Info("welcome", "окно закрыли на шаге " + _step + " — улетаем в чёлку");
                        HideConfirm();
                        ApplyDefaults();
                        FlyToIsland();
                    }));
                return;
            }
            base.OnClosing(e);
        }

        private static double Clamp01(double x) => x < 0 ? 0 : x > 1 ? 1 : x;
        private static double Lerp(double a, double b, double t) => a + (b - a) * t;
        private static double EaseInOutCubic(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
        /// <summary>Плавно туда с лёгким «приседанием» на старте и небольшим перелётом в конце.</summary>
        private static double EaseInOutBack(double t, double c1 = 0.7)
        {
            double c2 = c1 * 1.525;
            return t < 0.5
                ? Math.Pow(2 * t, 2) * ((c2 + 1) * 2 * t - c2) / 2
                : (Math.Pow(2 * t - 2, 2) * ((c2 + 1) * (t * 2 - 2) + c2) + 2) / 2;
        }
        private static Color LerpColor(Color a, Color b, double t) => Color.FromArgb(
            (byte)Lerp(a.A, b.A, t), (byte)Lerp(a.R, b.R, t), (byte)Lerp(a.G, b.G, t), (byte)Lerp(a.B, b.B, t));

        /// <summary>Самый светлый цвет акцента (у градиента — светлый конец): им светится вспышка при посадке.</summary>
        private Color AccentColor()
        {
            var br = TryFindResource("Accent") as Brush ?? Application.Current?.TryFindResource("Accent") as Brush;
            if (br is SolidColorBrush sb) return sb.Color;
            if (br is GradientBrush gb && gb.GradientStops.Count > 0)
            {
                var c = gb.GradientStops[0].Color;
                foreach (var st in gb.GradientStops) if (Lum(st.Color) > Lum(c)) c = st.Color;
                return c;
            }
            return Colors.White;
        }

        /// <summary>
        /// Финал. Карточка (~660×600) сминается в чёрную пилюлю размером со свёрнутую чёлку: углы скругляются,
        /// содержимое гаснет первым, фон уходит в чистый чёрный. Одновременно пилюля по дуге (с лёгким «приседанием»
        /// и перелётом) улетает к точному месту чёлки на экране, верхние углы выпрямляются, как у чёлки.
        /// На месте проявляется настоящая чёлка (с пружинкой) и вспыхивает мягкое свечение, окно закрывается.
        /// Окно растягивается один раз на общий прямоугольник «карточка + чёлка», дальше двигается только карточка
        /// внутри (TranslateTransform + размер) — без дёрганья размеров окна на каждом кадре. Около 1,2 с.
        /// </summary>
        private void FlyToIsland()
        {
            if (_flying || _closed) return;
            var main = AppHost.Window;
            if (main == null || !IsLoaded || _card == null) { CloseNow(); return; }
            // настоящая чёлка должна быть спрятана — пилюля «прилетит» на её место; раскрытой сначала дать свернуться
            if (main.State == IslandState.Expanded)
            {
                main.HideForWelcome(true);
                _flying = true;
                After(700, () => { _flying = false; FlyToIsland(); }, stepBound: false);
                return;
            }
            main.HideForWelcome(true);
            _confirm.Visibility = Visibility.Collapsed;
            _flying = true;
            _cleanup?.Invoke(); _cleanup = null;
            _token++; // отложенные действия шага больше не нужны
            Rect target; double radiusPx;
            try { target = main.IslandScreenRect(out radiusPx); }
            catch (Exception ex) { Journal.Warn("welcome", "не нашёл чёлку на экране: " + ex.Message); CloseNow(); return; }

            // экранные пиксели → точки этого окна (с учётом масштаба экрана)
            var m = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var tl = m.Transform(target.TopLeft);
            var br = m.Transform(target.BottomRight);
            double pw = Math.Max(80, br.X - tl.X), ph = Math.Max(24, br.Y - tl.Y), pr = Math.Min(ph / 2, Math.Max(4, radiusPx * m.M11));
            var goal = new Point((tl.X + br.X) / 2, (tl.Y + br.Y) / 2);
            // страховка: пилюля не выше верха экрана
            double screenTop = SystemParameters.VirtualScreenTop;
            if (goal.Y - ph / 2 < screenTop) goal.Y = screenTop + ph / 2;
            Journal.Info("welcome", "знакомство завершено — летим к чёлке: " + Math.Round(goal.X) + "," + Math.Round(goal.Y) + " " + Math.Round(pw) + "×" + Math.Round(ph));

            var card = _card;
            card.BeginAnimation(OpacityProperty, null);
            card.Opacity = 1;
            double w0 = card.ActualWidth, h0 = card.ActualHeight;
            var cardTl = card.TranslatePoint(new Point(0, 0), this);
            var start = new Point(Left + cardTl.X + w0 / 2, Top + cardTl.Y + h0 / 2);

            // содержимое замораживаем в прежнем размере (без перекладки на каждом кадре), оно сжимается вместе с карточкой и гаснет
            var inner = card.Child as FrameworkElement;
            ScaleTransform innerScale = null;
            if (inner != null)
            {
                inner.Width = inner.ActualWidth; inner.Height = inner.ActualHeight;
                inner.HorizontalAlignment = HorizontalAlignment.Center;
                inner.VerticalAlignment = VerticalAlignment.Center;
                inner.RenderTransformOrigin = new Point(0.5, 0.5);
                inner.RenderTransform = innerScale = new ScaleTransform();
            }
            card.ClipToBounds = true;
            var c0 = (TryFindResource("Card") as SolidColorBrush)?.Color ?? Color.FromRgb(0x13, 0x13, 0x16);
            var bg = new SolidColorBrush(c0);
            card.Background = bg;
            var edge = new SolidColorBrush((TryFindResource("CardBorder") as SolidColorBrush)?.Color ?? Color.FromRgb(0x23, 0x23, 0x2A));
            card.BorderBrush = edge;

            // окно — один раз на общий прямоугольник «карточка + чёлка» с запасом под дугу и свечение;
            // прозрачные поля пропускают клики насквозь
            var box = Rect.Union(new Rect(start.X - w0 / 2, start.Y - h0 / 2, w0, h0), new Rect(goal.X - pw / 2, goal.Y - ph / 2, pw, ph));
            box.Inflate(140, 70);
            var stage = new Canvas();
            Content = null;
            card.HorizontalAlignment = HorizontalAlignment.Left;
            card.VerticalAlignment = VerticalAlignment.Top;
            var pos = new TranslateTransform();
            card.RenderTransform = pos;
            stage.Children.Add(card);
            Content = stage;
            void Place(Point c, double w, double h)
            {
                card.Width = w; card.Height = h;
                pos.X = c.X - box.X - w / 2;
                pos.Y = c.Y - box.Y - h / 2;
            }
            Place(start, w0, h0);
            Left = box.X; Top = box.Y; Width = box.Width; Height = box.Height;

            // путь — квадратичная дуга с уходом вбок; на перелёте (k > 1) пилюля продолжает движение по касательной
            var ctrl = new Point((start.X + goal.X) / 2 + 120, (start.Y + goal.Y) / 2 + 30);
            Point Bezier(double k) => new(
                (1 - k) * (1 - k) * start.X + 2 * (1 - k) * k * ctrl.X + k * k * goal.X,
                (1 - k) * (1 - k) * start.Y + 2 * (1 - k) * k * ctrl.Y + k * k * goal.Y);

            const double Morph = 0.50, FlyFrom = 0.12, FlyTo = 0.90, Fade = 0.18, Dark = 0.32;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            bool contentGone = false, landed = false;
            EventHandler tick = null;
            tick = (_, __) =>
            {
                if (_closed) { CompositionTarget.Rendering -= tick; return; }
                if (landed) return; // окно закрывается — лишний кадр не должен «приземлить» чёлку второй раз
                double t = clock.Elapsed.TotalSeconds;
                double ks = EaseInOutCubic(Clamp01(t / Morph));       // сжатие в пилюлю
                double u = Clamp01((t - FlyFrom) / (FlyTo - FlyFrom)); // полёт
                double kf = EaseInOutBack(u);

                // содержимое гаснет первым, фон темнеет до чистого чёрного, рамка растворяется
                if (!contentGone)
                {
                    double fo = Clamp01(t / Fade);
                    if (inner != null)
                    {
                        inner.Opacity = 1 - fo;
                        double sc = Lerp(1, 0.85, fo);
                        innerScale.ScaleX = sc; innerScale.ScaleY = sc;
                        if (fo >= 1) inner.Visibility = Visibility.Collapsed;
                    }
                    if (fo >= 1) contentGone = true;
                }
                double kd = Clamp01(t / Dark);
                bg.Color = LerpColor(c0, Colors.Black, kd);
                edge.Opacity = 1 - kd;

                if (u >= 1)
                {
                    // 3) на месте: настоящая чёлка проявляется и пружинит, пилюля гаснет, вспыхивает свечение
                    landed = true;
                    CompositionTarget.Rendering -= tick;
                    Place(goal, pw, ph);
                    card.CornerRadius = new CornerRadius(0, 0, pr, pr);
                    _landed = true;
                    main.LandFromWelcome();
                    card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(110)));
                    Flash(stage, new Point(goal.X - box.X, goal.Y - box.Y), pw, ph);
                    After(320, CloseNow, stepBound: false);
                    return;
                }

                // 1) размер → пилюля; 2) на лету чуть вытягивается вдоль пути, как капля
                double stretch = Math.Sin(Math.PI * u) * 0.10 * ks;
                double w = Lerp(w0, pw, ks) * (1 - stretch * 0.5), h = Lerp(h0, ph, ks) * (1 + stretch);
                Place(Bezier(kf), w, h);
                // углы: от 28 к полукругу, а у самой цели верхние выпрямляются, нижние — как у чёлки
                double round = Math.Min(Lerp(28, h / 2, ks), h / 2);
                double kc = EaseInOutCubic(Clamp01((u - 0.72) / 0.28));
                double topR = Lerp(round, 0, kc), botR = Lerp(round, pr, kc);
                card.CornerRadius = new CornerRadius(topR, topR, botR, botR);
            };
            CompositionTarget.Rendering += tick;
        }

        /// <summary>Мягкая вспышка цвета акцента на месте чёлки в момент посадки.</summary>
        private void Flash(Canvas stage, Point center, double pw, double ph)
        {
            var c = AccentColor();
            var fill = new RadialGradientBrush();
            fill.GradientStops.Add(new GradientStop(Color.FromArgb(200, c.R, c.G, c.B), 0));
            fill.GradientStops.Add(new GradientStop(Color.FromArgb(80, c.R, c.G, c.B), 0.45));
            fill.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
            double w = pw * 1.9, h = ph * 3.4;
            var glow = new Ellipse { Width = w, Height = h, Fill = fill, IsHitTestVisible = false, RenderTransformOrigin = new Point(0.5, 0.5) };
            Canvas.SetLeft(glow, center.X - w / 2);
            Canvas.SetTop(glow, center.Y - h / 2);
            var st = new ScaleTransform(0.4, 0.4);
            glow.RenderTransform = st;
            stage.Children.Add(glow);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var d = TimeSpan.FromMilliseconds(300);
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.4, 1.5, d) { EasingFunction = ease });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.4, 1.5, d) { EasingFunction = ease });
            glow.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, d) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
        }
    }
}
