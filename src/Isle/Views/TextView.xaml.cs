using System;
using System.Windows;
using System.Windows.Controls;
using Isle.Helpers;
using Isle.Services;

namespace Isle.Views
{
    public partial class TextView : UserControl, IIslandView
    {
        public TextView()
        {
            InitializeComponent();
            Add("Раскладка", TextTools.SwitchLayout);
            Add("ВЕРХНИЙ", TextTools.Upper);
            Add("нижний", TextTools.Lower);
            Add("Каждое Слово", TextTools.TitleCase);
            Add("Предложение", TextTools.SentenceCase);
            Add("Транслит", TextTools.Translit);
            Add("Убрать переносы", TextTools.RemoveLineBreaks);
            Add("Лишние пробелы", TextTools.CollapseSpaces);
            Add("JSON красиво", TextTools.JsonPretty);
            Add("JSON в строку", TextTools.JsonToString);
            Add("→ Base64", TextTools.ToBase64);
            Add("Base64 →", TextTools.FromBase64);
            Add("→ URL", TextTools.UrlEncode);
            Add("URL →", TextTools.UrlDecode);
            Add("Сортировать строки", TextTools.SortLines);
            Add("Без повторов", TextTools.UniqueLines);
            Add("Задом наперёд", TextTools.Reverse);
            Add("Спросить ИИ", t => { AskAi(t); return t; });
            OnChanged(null, null);
        }

        public void OnShown() { }

        private void Add(string title, Func<string, string> fn)
        {
            var b = new Button { Content = title, Style = (Style)FindResource("Flat"), Margin = new Thickness(0, 0, 6, 6), Height = 34 };
            b.Click += (_, __) =>
            {
                Error.Text = "";
                try
                {
                    var src = Input.SelectionLength > 0 ? Input.SelectedText : Input.Text;
                    var res = fn(src);
                    if (res == src) return;
                    if (Input.SelectionLength > 0) Input.SelectedText = res;
                    else Input.Text = res;
                }
                catch (Exception ex) { Error.Text = "Не получилось: " + ex.Message; }
            };
            ToolGrid.Children.Add(b);
        }

        private void AskAi(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            AppHost.Window.SelectTab("chat");
            (AppHost.Window.GetView("chat") as ChatView)?.Ask(text);
        }

        private void OnChanged(object s, TextChangedEventArgs e) => Stats.Text = TextTools.Stats(Input.Text);

        private void OnPaste(object s, RoutedEventArgs e)
        {
            try { if (Clipboard.ContainsText()) Input.Text = Clipboard.GetText(); } catch { }
        }

        private void OnCopy(object s, RoutedEventArgs e)
        {
            if (Input.Text.Length == 0) return;
            ClipboardService.SetText(Input.Text);
            ToastBus.Show(new Toast { Kind = ToastKind.Success, Title = "Скопировано", Icon = Icons.Copy, Important = true, Duration = TimeSpan.FromSeconds(1.2) });
        }

        private void OnClear(object s, RoutedEventArgs e) => Input.Text = "";
    }
}
