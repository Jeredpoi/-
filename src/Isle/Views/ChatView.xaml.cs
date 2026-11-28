using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Isle.Services;

namespace Isle.Views
{
    public partial class ChatView : UserControl, IIslandView
    {
        private readonly ObservableCollection<ChatMessage> _msgs = new();
        private CancellationTokenSource _cts;
        private bool _busy;
        /// <summary>Вложения, которые уйдут со следующим сообщением.</summary>
        private readonly List<string> _attach = new();

        private static readonly string[] Ideas =
        {
            "Объясни простыми словами, как работает VPN",
            "Помоги написать вежливое письмо о переносе встречи",
            "Придумай пять идей, чем заняться в выходные"
        };

        public ChatView()
        {
            InitializeComponent();
            Messages.ItemsSource = _msgs;
            // Клоди в чате одет по погоде, ночью — в колпаке и зевает
            ChatBuddy.IsVisibleChanged += (_, __) =>
            {
                if (!ChatBuddy.IsVisible) return;
                int hour = DateTime.Now.Hour;
                bool night = hour >= 23 || hour < 6;
                ChatBuddy.Night = night;
                ChatBuddy.Outfit = MainWindow.OutfitForWeather();
                ChatBuddy.Mood = night ? Isle.Helpers.Mood.Drowsy : Isle.Helpers.Mood.Idle;
            };
            // в чате по Клоди тоже можно тыкнуть
            ChatBuddy.Cursor = System.Windows.Input.Cursors.Hand;
            ChatBuddy.MouseLeftButtonUp += (_, e) => { ChatBuddy.Poke(); e.Handled = true; };
            foreach (var idea in Ideas)
            {
                var b = new Button { Content = idea, Style = (Style)FindResource("Flat"), Margin = new Thickness(0, 0, 0, 6), HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(14, 6, 14, 6) };
                b.Click += (_, __) => Ask(idea);
                Suggestions.Children.Add(b);
            }
            UpdateModelLine();
            AppHost.Voice.RecordingChanged += rec => Dispatcher.InvokeAsync(() => SetMicState(rec));
            ChatHistory.Changed += () => Dispatcher.InvokeAsync(() => { BuildRecent(); UpdateBadge(); }); // очистка/удаление сразу убирают «Недавние разговоры»
            BuildRecent();
            UpdateBadge();
            // волна в поле ввода — под голос, пока идёт диктовка
            AppHost.Voice.Level += lvl => Dispatcher.InvokeAsync(() => { if (RecordBar.Visibility == Visibility.Visible) ChatWave.Push(lvl); });
            _recTick.Tick += (_, __) =>
            {
                var t = DateTime.Now - _recStart;
                RecTimer.Text = (int)t.TotalMinutes + ":" + t.Seconds.ToString("00");
                RecDotChat.Opacity = (DateTime.Now.Millisecond / 450) % 2 == 0 ? 1 : 0.35;
            };
            // перетаскивание файлов и картинок на чат
            DragEnter += OnDragOver; DragOver += OnDragOver;
            DragLeave += (_, __) => DropOverlay.Visibility = Visibility.Collapsed;
            Drop += OnDrop;
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; OnNew(null, null); }
                else if (e.Key == Key.Escape && AppHost.Voice.Recording && RecordBar.Visibility == Visibility.Visible) { e.Handled = true; OnRecCancel(null, null); }
            };
        }

        /// <summary>Последние 4 разговора прямо на пустом экране чата — продолжить в один клик.</summary>
        private void BuildRecent()
        {
            RecentChats.Children.Clear();
            var recent = ChatHistory.All().Take(3).ToList();
            RecentTitle.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            // с недавними разговорами пустой экран не помещался (Клоди уезжал за верх) — подсказки тогда прячем
            Suggestions.Visibility = recent.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            foreach (var c in recent)
            {
                var b = new Button
                {
                    Style = (Style)FindResource("Ghost"), Margin = new Thickness(0, 0, 0, 2), HorizontalAlignment = HorizontalAlignment.Center,
                    Content = new TextBlock { Text = c.Title + "  ·  " + c.Updated.ToString("dd.MM HH:mm"), FontSize = 12, Foreground = (System.Windows.Media.Brush)FindResource("Muted") }
                };
                var chat = c;
                b.Click += (_, __) => OpenSession(chat);
                // рядом — крестик: убрать разговор из истории
                var del = new Button
                {
                    Style = (Style)FindResource("Ghost"), Width = 20, Height = 20, Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 2),
                    ToolTip = "Удалить этот разговор", VerticalAlignment = VerticalAlignment.Center,
                    Content = new Isle.Helpers.Icon { Kind = "Close", Width = 9, Height = 9 }
                };
                del.Click += (_, __) => DeleteChat(chat);
                var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                row.Children.Add(b);
                row.Children.Add(del);
                RecentChats.Children.Add(row);
            }
        }

        public void OnShown()
        {
            if (_msgs.Count == 0) BuildRecent();
            UpdateModelLine();
            UpdateBadge();
            _ = WarmVision();
            // фокус не забираем сами — только если окно уже активно (иначе пользователь кликнет в поле)
            if (AppHost.Window.IsActive) Dispatcher.InvokeAsync(() => Input.Focus(), DispatcherPriority.Input);
        }

        private void UpdateModelLine()
        {
            var s = AppSettings.Current;
            var preset = AiService.Preset(s.AiPreset);
            ModelLine.Text = (string.IsNullOrWhiteSpace(s.AiKey) && s.AiPreset != "ollama")
                ? "Подключи ИИ: шестерёнка справа вверху → «ИИ» (Groq — бесплатно)"
                : (preset?.Title ?? "ИИ") + " · " + s.AiModel + " · помнит весь разговор · Shift+Enter — новая строка";
        }

        private void OnKey(object s, KeyEventArgs e)
        {
            // Ctrl+V с картинкой или файлами в буфере — прикрепляем (текст вставляется как обычно)
            if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
            {
                try
                {
                    if (Clipboard.ContainsImage() && Clipboard.GetImage() is System.Windows.Media.Imaging.BitmapSource img)
                    { e.Handled = true; AddImage(img); return; }
                    if (Clipboard.ContainsFileDropList())
                    { e.Handled = true; foreach (var f in Clipboard.GetFileDropList()) AddAttachment(f); return; }
                }
                catch (Exception ex) { Journal.Debug("chat", "вставка вложения: " + ex.Message); }
            }
            if (e.Key == Key.Back && Input.Text.Length == 0 && _attach.Count > 0) { e.Handled = true; RemoveAttachment(_attach[^1]); return; }
            if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                e.Handled = true;
                OnSend(null, null);
            }
        }

        private void OnSend(object s, RoutedEventArgs e)
        {
            var text = Input.Text.Trim();
            if ((text.Length == 0 && _attach.Count == 0) || _busy) return;
            Input.Text = "";
            var files = _attach.ToList();
            _attach.Clear();
            RenderAttachments();
            Ask(text, files);
        }

        /// <summary>Ошибки сервиса и подсказки Isle — не реплики ИИ, в разговор их не отправляем.</summary>
        public static bool IsServiceError(string text)
        {
            var t = text ?? "";
            return t.StartsWith("Ошибка сети:") || t.StartsWith("Ошибка:") || t.StartsWith("Нужен API-ключ") || t == "(пустой ответ)";
        }

        /// <summary>
        /// Реплики должны чередоваться и начинаться с пользователя (Claude иначе отвечает ошибкой 400):
        /// подряд идущие сообщения одной роли склеиваем, ведущие ответы ИИ отбрасываем.
        /// </summary>
        public static System.Collections.Generic.List<ChatMessage> BuildHistory(System.Collections.Generic.IEnumerable<ChatMessage> src)
        {
            var list = new System.Collections.Generic.List<ChatMessage>();
            foreach (var m in src)
            {
                if (list.Count == 0 && m.Role != "user") continue;
                if (list.Count > 0 && list[^1].Role == m.Role)
                    list[^1] = new ChatMessage { Role = m.Role, Text = list[^1].Text + "\n\n" + m.Text,
                        Attachments = (list[^1].Attachments ?? new List<string>()).Concat(m.Attachments ?? new List<string>()).ToList() };
                else list.Add(new ChatMessage { Role = m.Role, Text = m.Text, Attachments = m.Attachments });
            }
            return list;
        }

        /// <summary>Голосовая команда, которой нужно подтверждение: показываем её в чате с кнопками «Выполнить / Отмена».</summary>
        public void AddExchange(string userText, string answer, System.Collections.Generic.IEnumerable<AiAction> confirm)
        {
            Welcome.Visibility = Visibility.Collapsed;
            Scroll.Visibility = Visibility.Visible;
            _msgs.Add(new ChatMessage { Role = "user", Text = "🎙 " + userText });
            if (!string.IsNullOrWhiteSpace(answer)) _msgs.Add(new ChatMessage { Role = "assistant", Text = answer });
            foreach (var a in confirm) _msgs.Add(new ChatMessage { Role = "action", Text = "Нужно подтверждение", Pending = a });
            Scroll.ScrollToEnd();
            SaveSession();
        }

        public async void Ask(string text) => Ask(text, null);

        public async void Ask(string text, List<string> files)
        {
            // ИИ ещё отвечает — не теряем вопрос: кладём его в поле ввода, отправить можно следом
            if (_busy) { if (!string.IsNullOrWhiteSpace(text)) { Input.Text = text; Input.CaretIndex = text.Length; } return; }
            _busy = true;
            SendBtn.IsEnabled = false;
            Welcome.Visibility = Visibility.Collapsed;
            Scroll.Visibility = Visibility.Visible;
            // файлы, кроме картинок, — строкой «📎 имя» (картинки видны миниатюрами над текстом)
            var shownText = text;
            foreach (var f in files ?? new List<string>())
                if (!ChatFiles.IsImage(f)) shownText += (shownText.Length > 0 ? "\n" : "") + "📎 " + Path.GetFileName(f);
            _msgs.Add(new ChatMessage { Role = "user", Text = shownText, Attachments = files is { Count: > 0 } ? files : null });
            var thinking = new ChatMessage { Role = "assistant", Text = "…" };
            _msgs.Add(thinking);
            Scroll.ScrollToEnd();
            var requestCts = new CancellationTokenSource();
            _cts = requestCts;
            string answer;
            try
            {
                var history = BuildHistory(_msgs.Where(m => !ReferenceEquals(m, thinking) && !m.HasPending && (m.Role == "user" || m.Role == "assistant")
                                               && !IsServiceError(m.Text))); // свои ошибки ИИ не показываем как его ответы
                answer = await AiService.AskAsync(history, requestCts.Token);
            }
            // отмену сделал сам пользователь (другой чат, очистка) — молча выходим; иначе это тайм-аут запроса
            catch (OperationCanceledException) when (requestCts.IsCancellationRequested) { return; }
            catch (OperationCanceledException) { answer = "Ошибка сети: сервис не ответил вовремя — попробуй ещё раз"; }
            catch (Exception ex) { answer = "Ошибка сети: " + ex.Message; }
            // A cleared or switched conversation must not be repopulated by a late API response.
            if (requestCts.IsCancellationRequested) return;
            var (clean, actions) = AiActions.Extract(answer ?? "");
            var reports = new System.Collections.Generic.List<string>();
            var confirm = new System.Collections.Generic.List<AiAction>();
            foreach (var a in actions)
            {
                if (a.NeedsConfirm) confirm.Add(a);
                else reports.Add(AiActions.Run(a));
            }
            var shown = clean + (reports.Count > 0 ? (clean.Length > 0 ? "\n\n" : "") + string.Join("\n", reports) : "");
            var idx = _msgs.IndexOf(thinking);
            if (idx >= 0) _msgs[idx] = new ChatMessage { Role = "assistant", Text = string.IsNullOrWhiteSpace(shown) ? "(пустой ответ)" : shown };
            NameChatAsync(string.IsNullOrWhiteSpace(text) ? "Картинка" : text, shown);
            foreach (var a in confirm) _msgs.Add(new ChatMessage { Role = "action", Text = "Нужно подтверждение", Pending = a });
            Scroll.ScrollToEnd();
            _busy = false;
            SendBtn.IsEnabled = true;
            SaveSession();
        }

        private void OnRunPending(object s, RoutedEventArgs e)
        {
            if ((s as FrameworkElement)?.Tag is not ChatMessage m || m.Pending == null) return;
            var report = AiActions.Run(m.Pending);
            var i = _msgs.IndexOf(m);
            if (i >= 0) _msgs[i] = new ChatMessage { Role = "assistant", Text = report };
            SaveSession();
        }

        private void OnSkipPending(object s, RoutedEventArgs e)
        {
            if ((s as FrameworkElement)?.Tag is not ChatMessage m) return;
            var i = _msgs.IndexOf(m);
            if (i >= 0) _msgs[i] = new ChatMessage { Role = "assistant", Text = "Отменено: " + m.PendingLabel };
            SaveSession();
        }

        // ---------- голос в поле ввода ----------
        private readonly DispatcherTimer _recTick = new() { Interval = TimeSpan.FromMilliseconds(100) };
        private DateTime _recStart;
        private bool _recMine;   // запись начата из чата — полоса записи в поле ввода
        private DispatcherTimer _busyHide;

        private void InsertVoice(string text)
        {
            _busyHide?.Stop();
            HideRecordBar();
            Input.Text = (Input.Text.Length > 0 ? Input.Text.TrimEnd() + " " : "") + text;
            Input.CaretIndex = Input.Text.Length;
            Input.Focus();
        }

        private void HideRecordBar()
        {
            RecordBar.Visibility = Visibility.Collapsed;
            Input.Visibility = AttachBtn.Visibility = MicBtn.Visibility = Visibility.Visible;
        }

        private void OnMic(object s, RoutedEventArgs e)
        {
            if (AppHost.Voice.Recording) { OnRecDone(null, null); return; }
            _recMine = true;
            AppHost.Voice.Start("Говори — текст появится в поле ввода чата");
        }

        private void OnRecDone(object s, RoutedEventArgs e)
        {
            if (!AppHost.Voice.Recording) return;
            // пока распознаётся — «Распознаю…» вместо волны; текст придёт в поле. Не пришёл (тишина, ошибка) — полоса уйдёт сама
            RecBusy.Visibility = Visibility.Visible; ChatWave.Visibility = Visibility.Collapsed; RecDone.IsEnabled = false;
            _ = AppHost.Voice.StopAsync(InsertVoice);
            _busyHide?.Stop();
            _busyHide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
            _busyHide.Tick += (_, __) => { _busyHide.Stop(); HideRecordBar(); };
            _busyHide.Start();
        }

        private void OnRecCancel(object s, RoutedEventArgs e)
        {
            _busyHide?.Stop();
            AppHost.Voice.Cancel();
            HideRecordBar();
        }

        /// <summary>Запись идёт — поле ввода становится полосой записи: ✕, красная точка, таймер, волна, ✓.</summary>
        private void SetMicState(bool rec)
        {
            if (rec && _recMine)
            {
                _recStart = DateTime.Now;
                RecTimer.Text = "0:00";
                ChatWave.Clear(); ChatWave.Fill = (Brush)FindResource("Accent");
                ChatWave.Visibility = Visibility.Visible; RecBusy.Visibility = Visibility.Collapsed; RecDone.IsEnabled = true;
                RecordBar.Visibility = Visibility.Visible;
                Input.Visibility = AttachBtn.Visibility = MicBtn.Visibility = Visibility.Hidden; // полоса записи вместо поля (без своей подложки — углы поля круглые)
                Composer.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xF8, 0x71, 0x71));
                _recTick.Start();
            }
            if (!rec)
            {
                _recTick.Stop();
                _recMine = false;
                Composer.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
                if (RecBusy.Visibility != Visibility.Visible) HideRecordBar();
            }
        }

        // ---------- вложения ----------
        private void OnAttach(object s, RoutedEventArgs e)
        {
            var menu = new ContextMenu { PlacementTarget = AttachBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Top, VerticalOffset = -6 };
            AppHost.Window.SuppressAutoCollapse = true;
            menu.Closed += (_, __) => AppHost.Window.SuppressAutoCollapse = false;
            MenuItem Item(string text, string icon, Action act)
            {
                var it = new MenuItem { Header = text, Icon = new Isle.Helpers.Icon { Kind = icon, Width = 13, Height = 13 } };
                it.Click += (_, __) => act();
                return it;
            }
            menu.Items.Add(Item(ChatFiles.Vision() ? "Изображение…" : "Изображение… (эта модель картинки не видит)", "Image", () => Pick(true)));
            menu.Items.Add(Item("Файл…", "File", () => Pick(false)));
            menu.IsOpen = true;
        }

        private void Pick(bool images)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Filter = images ? "Картинки|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp" : "Все файлы|*.*|Текст и код|*.txt;*.md;*.csv;*.json;*.xml;*.html;*.py;*.cs;*.js;*.ts;*.log"
            };
            AppHost.Window.SuppressAutoCollapse = true;
            try { if (dlg.ShowDialog(Window.GetWindow(this)) == true) foreach (var f in dlg.FileNames) AddAttachment(f); }
            finally { AppHost.Window.SuppressAutoCollapse = false; }
        }

        private void OnDragOver(object s, DragEventArgs e)
        {
            bool ok = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Bitmap);
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            DropOverlay.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
            e.Handled = true;
        }

        private void OnDrop(object s, DragEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            try
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files) foreach (var f in files) AddAttachment(f);
                else if (e.Data.GetData(DataFormats.Bitmap) is System.Windows.Media.Imaging.BitmapSource b) AddImage(b);
            }
            catch (Exception ex) { Journal.Debug("chat", "перетаскивание: " + ex.Message); }
        }

        private void AddImage(System.Windows.Media.Imaging.BitmapSource img)
        {
            try { _attach.Add(ChatFiles.SaveImage(img)); RenderAttachments(); }
            catch (Exception ex) { Journal.Debug("chat", "картинка не сохранилась: " + ex.Message); }
        }

        private void AddAttachment(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path) || _attach.Count >= 8) return;
            try
            {
                // картинки — уменьшенной копией в папку чата: история не зависит от исходного файла, запрос меньше
                var p = ChatFiles.IsImage(path) ? ChatFiles.SaveImageFile(path) : path;
                if (!_attach.Contains(p)) _attach.Add(p);
            }
            catch (Exception ex) { Journal.Debug("chat", "вложение: " + ex.Message); return; }
            RenderAttachments();
            Journal.Info("chat", "вложение: " + (ChatFiles.IsImage(path) ? "картинка" : "файл " + Path.GetExtension(path)));
        }

        private void RemoveAttachment(string p) { _attach.Remove(p); RenderAttachments(); }

        /// <summary>Карточки вложений над полем ввода: картинка — миниатюра 56×56, файл — значок, имя и размер; ✕ — убрать.</summary>
        private void RenderAttachments()
        {
            AttachStrip.Children.Clear();
            bool vision = ChatFiles.Vision();
            foreach (var a in _attach)
            {
                var path = a;
                FrameworkElement body;
                bool img = ChatFiles.IsImage(path);
                if (img)
                    body = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(10), ClipToBounds = true,
                        Child = new Image { Source = ChatFiles.Thumb(path), Stretch = Stretch.UniformToFill }, Opacity = vision ? 1 : 0.45 };
                else
                {
                    long size = 0; try { size = new FileInfo(path).Length; } catch { }
                    var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    sp.Children.Add(new Isle.Helpers.Icon { Kind = "File", Width = 16, Height = 16, Foreground = (Brush)FindResource("Muted") });
                    var names = new StackPanel { Margin = new Thickness(8, 0, 0, 0), MaxWidth = 130 };
                    names.Children.Add(new TextBlock { Text = Path.GetFileName(path), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
                    names.Children.Add(new TextBlock { Text = size >= 1048576 ? (size / 1048576.0).ToString("0.#") + " МБ" : Math.Max(1, size / 1024) + " КБ", FontSize = 10, Foreground = (Brush)FindResource("Muted") });
                    sp.Children.Add(names);
                    body = new Border { Height = 56, Padding = new Thickness(10, 0, 12, 0), CornerRadius = new CornerRadius(10), Child = sp };
                    ((Border)body).SetResourceReference(Border.BackgroundProperty, "ChipBg");
                }
                var x = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, -5, -5, 0), Cursor = Cursors.Hand, Visibility = Visibility.Collapsed, ToolTip = "Убрать",
                    Child = new Isle.Helpers.Icon { Kind = "Close", Width = 8, Height = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                x.SetResourceReference(Border.BackgroundProperty, "SelBg");
                x.MouseLeftButtonUp += (_, ev) => { ev.Handled = true; RemoveAttachment(path); };
                var card = new Grid { Margin = new Thickness(0, 0, 8, 0), ToolTip = img ? (vision ? null : "Текущая модель не видит картинки — отправится только текст") : Path.GetFileName(path) };
                card.Children.Add(body);
                card.Children.Add(x);
                card.MouseEnter += (_, __) => x.Visibility = Visibility.Visible;
                card.MouseLeave += (_, __) => x.Visibility = Visibility.Collapsed;
                // появление: лёгкий «рост» карточки
                var sc = new ScaleTransform(0.85, 0.85);
                card.RenderTransform = sc; card.RenderTransformOrigin = new Point(0.5, 0.5);
                var grow = new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                sc.BeginAnimation(ScaleTransform.ScaleXProperty, grow); sc.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
                AttachStrip.Children.Add(card);
            }
            AttachStrip.Visibility = _attach.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            bool imgs = _attach.Any(ChatFiles.IsImage);
            VisionHint.Visibility = imgs && !vision ? Visibility.Visible : Visibility.Collapsed;
            VisionHint.Text = "Модель не видит картинки — уйдёт только текст";
            VisionHint.ToolTip = "Сейчас: " + AppSettings.Current.AiModel + ". Сменить модель: шестерёнка → «ИИ».";
        }

        private async System.Threading.Tasks.Task WarmVision()
        {
            await ChatFiles.WarmAsync();
            if (_attach.Count > 0) RenderAttachments();
        }

        // ---------- История разговоров ----------

        private ChatSession _session = new();

        /// <summary>После первого ответа ИИ придумывает разговору короткое красивое название (в фоне, не мешая чату).</summary>
        private async void NameChatAsync(string question, string answer)
        {
            var session = _session;
            if (session == null || session.AiTitle || IsServiceError(answer ?? "")) return;
            if (_msgs.Count(m => m.IsUser) != 1) return; // только по первому вопросу разговора
            var title = await AiService.TitleAsync(question, answer);
            if (title == null || !ReferenceEquals(session, _session)) return;
            session.Title = title;
            session.AiTitle = true;
            SaveSession();
        }

        private void SaveSession()
        {
            // «…» — заглушка, пока ИИ думает; в историю её не пишем (иначе она уйдёт модели как ответ)
            var real = _msgs.Where(m => (m.Role == "user" || m.Role == "assistant") && !(m.Role == "assistant" && m.Text == "…")).ToList();
            if (real.Count == 0) return;
            _session.Messages = real.Select(m => new ChatMessage { Role = m.Role, Text = m.Text, Attachments = m.Attachments }).ToList();
            if (!_session.AiTitle)
            {
                _session.Title = real.FirstOrDefault(m => m.IsUser)?.Text ?? "Разговор";
                if (_session.Title.Length > 48) _session.Title = _session.Title.Substring(0, 48) + "…";
            }
            _session.Updated = DateTime.Now;
            ChatHistory.Save(_session);
            UpdateBadge();
        }

        /// <summary>Кружок истории: первая буква текущего разговора; новый (пустой) — значок часов.</summary>
        private void UpdateBadge()
        {
            var title = _msgs.Count > 0 ? (_session.Title ?? "").Trim() : "";
            var letter = title.FirstOrDefault(char.IsLetterOrDigit);
            bool has = letter != default;
            BadgeLetter.Text = has ? char.ToUpper(letter).ToString() : "";
            BadgeLetter.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            BadgeIcon.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            HistoryBadge.ToolTip = (has ? "«" + title + "» · " : "") + "Разговоры · Ctrl+N — новый";
        }

        private void OnHistory(object s, MouseButtonEventArgs e)
        {
            HistorySearch.Text = "";
            BuildHistoryList("");
            HistoryScroll.MaxHeight = Math.Clamp(SpaceAbove(HistoryBadge) - 150, 100, 300);
            AppHost.Window.SuppressAutoCollapse = true;
            HistoryPopup.Closed -= OnHistoryClosed;
            HistoryPopup.Closed += OnHistoryClosed;
            HistoryPopup.IsOpen = true;
            HistoryBadge.SetResourceReference(Border.BackgroundProperty, "SelBg");
            Dispatcher.InvokeAsync(() => HistorySearch.Focus(), DispatcherPriority.Input);
        }

        private void OnHistoryClosed(object s, EventArgs e)
        {
            AppHost.Window.SuppressAutoCollapse = false;
            HistoryBadge.SetResourceReference(Border.BackgroundProperty, "ChipBg");
        }

        private void OnHistorySearch(object s, TextChangedEventArgs e) => BuildHistoryList(HistorySearch.Text.Trim());

        private void BuildHistoryList(string q)
        {
            HistoryList.Children.Clear();
            var all = ChatHistory.All();
            ClearHistoryBtn.Visibility = all.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            var list = q.Length == 0 ? all : all.Where(c => (c.Title ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
                                                         || c.Messages.Any(m => (m.Text ?? "").Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
            if (list.Count == 0)
                HistoryList.Children.Add(new TextBlock { Text = all.Count == 0 ? "Пока нет сохранённых разговоров" : "Ничего не нашлось", Margin = new Thickness(10, 8, 10, 8),
                    Foreground = (Brush)FindResource("Muted"), FontSize = 12 });
            foreach (var c in list)
            {
                var chat = c;
                bool current = c.Id == _session.Id && _msgs.Count > 0;
                var letter = (c.Title ?? "").FirstOrDefault(char.IsLetterOrDigit);
                var row = new Grid { Height = 40 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var dot = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Margin = new Thickness(8, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = letter == default ? "•" : char.ToUpper(letter).ToString(), FontSize = 11, FontWeight = FontWeights.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                dot.SetResourceReference(Border.BackgroundProperty, "SelBg");
                row.Children.Add(dot);
                var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                names.Children.Add(new TextBlock { Text = c.Title, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal });
                names.Children.Add(new TextBlock { Text = c.Updated.ToString("dd.MM HH:mm"), FontSize = 10.5, Foreground = (Brush)FindResource("Muted") });
                Grid.SetColumn(names, 1); row.Children.Add(names);
                var del = new Button { Style = (Style)FindResource("Ghost"), Width = 24, Height = 24, Padding = new Thickness(0), Margin = new Thickness(4, 0, 6, 0), Visibility = Visibility.Hidden,
                    ToolTip = "Удалить этот разговор", Content = new Isle.Helpers.Icon { Kind = "Close", Width = 9, Height = 9 } };
                del.Click += (_, ev) => { ev.Handled = true; DeleteChat(chat); BuildHistoryList(HistorySearch.Text.Trim()); };
                Grid.SetColumn(del, 2); row.Children.Add(del);
                var item = new Border { CornerRadius = new CornerRadius(10), Child = row, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 2),
                    BorderThickness = new Thickness(current ? 2 : 0, 0, 0, 0) };
                if (current) { item.SetResourceReference(Border.BackgroundProperty, "SelBg"); item.SetResourceReference(Border.BorderBrushProperty, "Accent"); }
                else item.Background = Brushes.Transparent;
                item.MouseEnter += (_, __) => { del.Visibility = Visibility.Visible; if (!current) item.SetResourceReference(Border.BackgroundProperty, "ChipHover"); };
                item.MouseLeave += (_, __) => { del.Visibility = Visibility.Hidden; if (!current) item.Background = Brushes.Transparent; };
                item.MouseLeftButtonUp += (_, __) => { HistoryPopup.IsOpen = false; OpenSession(chat); };
                HistoryList.Children.Add(item);
            }
        }

        private void OnClearHistory(object s, RoutedEventArgs e) { HistoryPopup.IsOpen = false; ClearHistory(); }

        /// <summary>Сколько места (в пикселях окна) над элементом — чтобы меню не выходило за остров.</summary>
        private static double SpaceAbove(FrameworkElement el)
        {
            try
            {
                var w = Window.GetWindow(el);
                return w == null ? 380 : el.TranslatePoint(new Point(0, 0), w).Y;
            }
            catch { return 380; }
        }

        /// <summary>Удалить один разговор; если он открыт — вернуться к приветствию.</summary>
        private void DeleteChat(ChatSession chat)
        {
            if (chat.Id == _session.Id)
            {
                _cts?.Cancel();
                _busy = false;
                SendBtn.IsEnabled = true;
                _session = new ChatSession();
                _msgs.Clear();
                Welcome.Visibility = Visibility.Visible;
                Scroll.Visibility = Visibility.Collapsed;
            }
            ChatHistory.Delete(chat.Id);
        }

        private void ClearHistory()
        {
            _cts?.Cancel();
            _cts = null;
            _busy = false;
            SendBtn.IsEnabled = true;
            ChatHistory.Clear();
            _session = new ChatSession();
            _msgs.Clear();
            BuildRecent();
            Welcome.Visibility = Visibility.Visible;
            Scroll.Visibility = Visibility.Collapsed;
        }

        private void OpenSession(ChatSession c)
        {
            _cts?.Cancel();
            _busy = false;
            SendBtn.IsEnabled = true;
            _session = c;
            _msgs.Clear();
            foreach (var m in c.Messages) _msgs.Add(new ChatMessage { Role = m.Role, Text = m.Text, Attachments = m.Attachments });
            Welcome.Visibility = _msgs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Scroll.Visibility = _msgs.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            Scroll.ScrollToEnd();
            UpdateBadge();
        }

        private void OnNew(object s, RoutedEventArgs e)
        {
            HistoryPopup.IsOpen = false;
            SaveSession();
            _session = new ChatSession();
            _cts?.Cancel();
            _msgs.Clear();
            _busy = false;
            SendBtn.IsEnabled = true;
            Welcome.Visibility = Visibility.Visible;
            BuildRecent();
            Scroll.Visibility = Visibility.Collapsed;
            UpdateBadge();
        }
    }
}
