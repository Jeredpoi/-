using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Isle.Services;

namespace Isle.Views
{
    public partial class ShelfView : UserControl, IIslandView
    {
        private Point _downAt;
        private ShelfItem _downItem;

        public ShelfView()
        {
            InitializeComponent();
            List.ItemsSource = AppHost.Shelf.Items;
            AppHost.Shelf.Items.CollectionChanged += (_, __) => Update();
            Update();
        }

        public void OnShown() => Update();

        private void Update()
        {
            bool any = AppHost.Shelf.Items.Count > 0;
            Empty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            Filled.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnDragEnter(object s, DragEventArgs e) => Zone.BorderBrush = (System.Windows.Media.Brush)FindResource("Accent");
        private void OnDragLeave(object s, DragEventArgs e) => Zone.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x34, 0x34, 0x3C));

        private void OnDrop(object s, DragEventArgs e)
        {
            OnDragLeave(s, e);
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) AppHost.Shelf.Add(files);
            e.Handled = true;
        }

        private void OnTileDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && FindButton(d)) return;
            _downItem = (sender as FrameworkElement)?.Tag as ShelfItem;
            _downAt = e.GetPosition(this);
            if (e.ClickCount == 2 && _downItem != null)
            {
                SystemControls.Shell(_downItem.Path);
                _downItem = null;
            }
        }

        private void OnTileMove(object sender, MouseEventArgs e)
        {
            if (_downItem == null || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(this);
            if (System.Math.Abs(p.X - _downAt.X) < 6 && System.Math.Abs(p.Y - _downAt.Y) < 6) return;
            var item = _downItem;
            _downItem = null;
            var data = new DataObject(DataFormats.FileDrop, new[] { item.Path });
            AppHost.Window.SuppressAutoCollapse = true;
            try { DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link); }
            finally { AppHost.Window.SuppressAutoCollapse = false; }
        }

        private static bool FindButton(DependencyObject d)
        {
            while (d != null)
            {
                if (d is Button) return true;
                if (d is Border b && b.Tag is ShelfItem) return false;
                d = System.Windows.Media.VisualTreeHelper.GetParent(d);
            }
            return false;
        }

        private void OnRemove(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is ShelfItem i) AppHost.Shelf.Remove(i);
        }

        private void OnClear(object sender, RoutedEventArgs e) => AppHost.Shelf.Clear();
    }
}
