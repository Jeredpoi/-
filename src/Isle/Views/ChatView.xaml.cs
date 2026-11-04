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
            foreach (var idea in Ideas)
            {
                var b = new Button { Content = idea, Style = (Style)FindResource("Flat"), Margin = new Thickness(0, 0, 0, 6), HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(14, 6, 14, 6) };
                b.Click += (_, __) => Ask(idea);
                Suggestions.Children.Add(b);
            }
            UpdateModelLine();
            AppHost.Voice.RecordingChanged += rec => Dispatcher.InvokeAsync(() => SetMicState(rec));
        }

        public void OnShown()
        {
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
            _cts = new CancellationTokenSource();
            string answer;
            try
            {
                var history = _msgs.Where(m => !ReferenceEquals(m, thinking) && !m.HasPending && (m.Role == "user" || m.Role == "assistant")).ToList();
                answer = await AiService.AskAsync(history, _cts.Token);
            }
            catch (Exception ex) { answer = "Ошибка сети: " + ex.Message; }
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
        }

        private void OnSkipPending(object s, RoutedEventArgs e)
        {
            if ((s as FrameworkElement)?.Tag is not ChatMessage m) return;
            var i = _msgs.IndexOf(m);
            if (i >= 0) _msgs[i] = new ChatMessage { Role = "assistant", Text = "Отменено: " + m.PendingLabel };
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

        private void SaveSession()
        {
            var real = _msgs.Where(m => m.Role == "user" || m.Role == "assistant").ToList();
            if (real.Count == 0) return;
            _session.Messages = real.Select(m => new ChatMessage { Role = m.Role, Text = m.Text }).ToList();
            _session.Title = real.FirstOrDefault(m => m.IsUser)?.Text ?? "Разговор";
            if (_session.Title.Length > 48) _session.Title = _session.Title.Substring(0, 48) + "…";
            _session.Updated = DateTime.Now;
            ChatHistory.Save(_session);
        }

        private void OnHistory(object s, RoutedEventArgs e)
        {
            var menu = new ContextMenu { PlacementTarget = HistoryBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Top, MaxHeight = 380 };
            AppHost.Window.SuppressAutoCollapse = true;
            menu.Closed += (_, __) => AppHost.Window.SuppressAutoCollapse = false;
            var all = ChatHistory.All();
            if (all.Count == 0) menu.Items.Add(new MenuItem { Header = "Пока нет сохранённых разговоров", IsEnabled = false });
            foreach (var c in all)
            {
                var item = new MenuItem { Header = c.Title + "   ·   " + c.Updated.ToString("dd.MM HH:mm"), IsChecked = c.Id == _session.Id };
                var chat = c;
                item.Click += (_, __) => OpenSession(chat);
                menu.Items.Add(item);
            }
            if (all.Count > 0)
            {
                menu.Items.Add(new Separator());
                var clear = new MenuItem { Header = "Очистить историю" };
                clear.Click += (_, __) => ChatHistory.Clear();
                menu.Items.Add(clear);
            }
            menu.IsOpen = true;
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
            Scroll.Visibility = Visibility.Collapsed;
        }
    }
}
