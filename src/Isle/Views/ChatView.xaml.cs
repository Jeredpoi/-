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
            ModelLine.Text = (string.IsNullOrWhiteSpace(s.AiKey) && s.AiProvider == "anthropic")
                ? "Нужен API-ключ: Настройки → ИИ"
                : s.AiModel + " помнит весь разговор · Shift+Enter — новая строка";
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
                var history = _msgs.Where(m => !ReferenceEquals(m, thinking)).ToList();
                answer = await AiService.AskAsync(history, _cts.Token);
            }
            catch (Exception ex) { answer = "Ошибка сети: " + ex.Message; }
            var idx = _msgs.IndexOf(thinking);
            if (idx >= 0) _msgs[idx] = new ChatMessage { Role = "assistant", Text = string.IsNullOrWhiteSpace(answer) ? "(пустой ответ)" : answer };
            Scroll.ScrollToEnd();
            _busy = false;
            SendBtn.IsEnabled = true;
        }

        private void OnNew(object s, RoutedEventArgs e)
        {
            _cts?.Cancel();
            _msgs.Clear();
            _busy = false;
            SendBtn.IsEnabled = true;
            Welcome.Visibility = Visibility.Visible;
            Scroll.Visibility = Visibility.Collapsed;
        }
    }
}
