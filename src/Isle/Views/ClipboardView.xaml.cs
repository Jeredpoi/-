using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Isle.Services;

namespace Isle.Views
{
    public partial class ClipboardView : UserControl, IIslandView
    {
        private readonly ICollectionView _view;

        public ClipboardView()
        {
            InitializeComponent();
            _view = CollectionViewSource.GetDefaultView(AppHost.Clipboard.Items);
            _view.Filter = o => o is ClipItem c && (string.IsNullOrEmpty(Search.Text) || c.Text.IndexOf(Search.Text, StringComparison.CurrentCultureIgnoreCase) >= 0);
            List.ItemsSource = _view;
            AppHost.Clipboard.Items.CollectionChanged += (_, __) => UpdateEmpty();
            UpdateEmpty();
        }

        public void OnShown()
        {
            _view.Refresh(); // обновить «N мин назад»
            UpdateEmpty();
        }

        public void FocusSearch() => Dispatcher.InvokeAsync(() => { Search.Focus(); Search.SelectAll(); }, DispatcherPriority.Input);

        private void UpdateEmpty()
        {
            bool none = AppHost.Clipboard.Items.Count == 0;
            bool notFound = !none && _view != null && _view.IsEmpty;
            Empty.Text = none ? "Пока пусто — скопируй что-нибудь (Ctrl+C)" : "Ничего не найдено по «" + Search.Text.Trim() + "»";
            Empty.Visibility = none || notFound ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnSearch(object s, TextChangedEventArgs e) { _view?.Refresh(); UpdateEmpty(); }

        private void OnItemClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && IsInsideButton(d)) return;
            if ((sender as FrameworkElement)?.Tag is not ClipItem item) return;
            bool copyOnly = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            var target = AppHost.Window.PreviousForeground;
            AppHost.Window.Collapse();
            AppHost.Clipboard.Paste(item, target, !copyOnly);
            if (copyOnly) ToastBus.Show(new Toast { Kind = ToastKind.Success, Title = "Скопировано", Icon = Icons.Copy, Important = true, Duration = TimeSpan.FromSeconds(1.2) });
        }

        private static bool IsInsideButton(DependencyObject d)
        {
            while (d != null && d is not Border { Name: "row" })
            {
                if (d is Button) return true;
                d = System.Windows.Media.VisualTreeHelper.GetParent(d);
            }
            return false;
        }

        private void OnPin(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not ClipItem item) return;
            item.Pinned = !item.Pinned;
            AppHost.Clipboard.Save();
            _view.Refresh();
        }

        private void OnDelete(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not ClipItem item) return;
            AppHost.Clipboard.Remove(item);
        }

        private void OnClear(object sender, RoutedEventArgs e) => AppHost.Clipboard.Clear();
    }
}
