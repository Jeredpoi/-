using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Isle.Services;

namespace Isle.Views
{
    public partial class ChatView : UserControl, IIslandView
    {
        private readonly ObservableCollection<ChatMessage> _msgs = new();
        private CancellationTokenSource _cts;
        private bool _busy;

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
            foreach (var idea in Ideas)
            {
                var b = new Button { Content = idea, Style = (Style)FindResource("Flat"), Margin = new Thickness(0, 0, 0, 6), HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(14, 6, 14, 6) };
                b.Click += (_, __) => Ask(idea);
                Suggestions.Children.Add(b);
            }
            UpdateModelLine();
            AppHost.Voice.RecordingChanged += rec => Dispatcher.InvokeAsync(() => SetMicState(rec));
            ChatHistory.Changed += () => Dispatcher.InvokeAsync(BuildRecent); // очистка/удаление сразу убирают «Недавние разговоры»
            BuildRecent();
        }

        /// <summary>Последние 4 разговора прямо на пустом экране чата — продолжить в один клик.</summary>
        private void BuildRecent()
        {
            RecentChats.Children.Clear();
            var recent = ChatHistory.All().Take(4).ToList();
            RecentTitle.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
            // фокус не забираем сами — только если окно уже активно (иначе пользователь кликнет в поле)
            if (AppHost.Window.IsActive) Dispatcher.InvokeAsync(() => Input.Focus(), DispatcherPriority.Input);
        }

        private void UpdateModelLine()
        {
            var s = AppSettings.Current;
            var preset = AiService.Preset(s.AiPreset);
            ModelLine.Text = (string.IsNullOrWhiteSpace(s.AiKey) && s.AiPreset != "ollama")
                ? "Нужен API-ключ: шестерёнка → ИИ (бесплатно — Groq)"
                : (preset?.Title ?? "ИИ") + " · " + s.AiModel + " · помнит весь разговор · Shift+Enter — новая строка";
        }

        private void OnKey(object s, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                e.Handled = true;
                OnSend(null, null);
            }
        }

        private void OnSend(object s, RoutedEventArgs e)
        {
            var text = Input.Text.Trim();
            if (text.Length == 0 || _busy) return;
            Input.Text = "";
            Ask(text);
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
                    list[^1] = new ChatMessage { Role = m.Role, Text = list[^1].Text + "\n\n" + m.Text };
                else list.Add(new ChatMessage { Role = m.Role, Text = m.Text });
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

        public async void Ask(string text)
        {
            if (_busy) return;
            _busy = true;
            SendBtn.IsEnabled = false;
            Welcome.Visibility = Visibility.Collapsed;
            Scroll.Visibility = Visibility.Visible;
            _msgs.Add(new ChatMessage { Role = "user", Text = text });
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
            NameChatAsync(text, shown);
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

        private void OnMic(object s, RoutedEventArgs e)
        {
            AppHost.Voice.Toggle(text =>
            {
                Input.Text = (Input.Text.Length > 0 ? Input.Text.TrimEnd() + " " : "") + text;
                Input.CaretIndex = Input.Text.Length;
            });
            SetMicState(AppHost.Voice.Recording);
        }

        /// <summary>Во время записи кнопка микрофона краснеет.</summary>
        private void SetMicState(bool rec)
        {
            MicBtn.Background = rec ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x12, 0x14)) : null;
            if (!rec) MicBtn.ClearValue(BackgroundProperty);
            MicIcon.Foreground = rec ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF8, 0x71, 0x71))
                                     : (System.Windows.Media.Brush)FindResource("Fg");
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
            var real = _msgs.Where(m => m.Role == "user" || m.Role == "assistant").ToList();
            if (real.Count == 0) return;
            _session.Messages = real.Select(m => new ChatMessage { Role = m.Role, Text = m.Text }).ToList();
            if (!_session.AiTitle)
            {
                _session.Title = real.FirstOrDefault(m => m.IsUser)?.Text ?? "Разговор";
                if (_session.Title.Length > 48) _session.Title = _session.Title.Substring(0, 48) + "…";
            }
            _session.Updated = DateTime.Now;
            ChatHistory.Save(_session);
        }

        private void OnHistory(object s, RoutedEventArgs e)
        {
            var menu = new ContextMenu
            {
                PlacementTarget = HistoryBtn,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Top,
                VerticalOffset = -6,
                // меню не выше, чем место над кнопкой внутри острова — иначе оно уезжает за край экрана
                MaxHeight = Math.Clamp(SpaceAbove(HistoryBtn) - 14, 120, 380)
            };
            AppHost.Window.SuppressAutoCollapse = true;
            menu.Closed += (_, __) => AppHost.Window.SuppressAutoCollapse = false;
            var all = ChatHistory.All();
            if (all.Count == 0) menu.Items.Add(new MenuItem { Header = "Пока нет сохранённых разговоров", IsEnabled = false });
            foreach (var c in all)
            {
                // строка истории: название и дата, справа — крестик «удалить этот разговор»
                var row = new DockPanel { LastChildFill = true, MinWidth = 260 };
                var del = new Button
                {
                    Style = (Style)FindResource("Ghost"), Width = 22, Height = 22, Padding = new Thickness(0), Margin = new Thickness(10, 0, 0, 0),
                    ToolTip = "Удалить этот разговор", Content = new Isle.Helpers.Icon { Kind = "Close", Width = 10, Height = 10 }
                };
                DockPanel.SetDock(del, Dock.Right);
                row.Children.Add(del);
                row.Children.Add(new TextBlock { Text = c.Title + "   ·   " + c.Updated.ToString("dd.MM HH:mm"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                var item = new MenuItem { Header = row, IsChecked = c.Id == _session.Id };
                var chat = c;
                item.Click += (_, __) => OpenSession(chat);
                del.PreviewMouseLeftButtonDown += (_, ev) =>
                {
                    ev.Handled = true; // не открывать разговор, который удаляем
                    DeleteChat(chat);
                    menu.Items.Remove(item);
                    if (!menu.Items.OfType<MenuItem>().Any(m => m.Header is DockPanel)) menu.IsOpen = false;
                };
                menu.Items.Add(item);
            }
            if (all.Count > 0)
            {
                menu.Items.Add(new Separator());
                var clear = new MenuItem { Header = "Очистить историю" };
                clear.Click += (_, __) => ClearHistory();
                menu.Items.Add(clear);
            }
            menu.IsOpen = true;
        }

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
            foreach (var m in c.Messages) _msgs.Add(new ChatMessage { Role = m.Role, Text = m.Text });
            Welcome.Visibility = _msgs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Scroll.Visibility = _msgs.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            Scroll.ScrollToEnd();
        }

        private void OnNew(object s, RoutedEventArgs e)
        {
            SaveSession();
            _session = new ChatSession();
            _cts?.Cancel();
            _msgs.Clear();
            _busy = false;
            SendBtn.IsEnabled = true;
            Welcome.Visibility = Visibility.Visible;
            BuildRecent();
            Scroll.Visibility = Visibility.Collapsed;
        }
    }
}
