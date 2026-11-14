using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Isle.Helpers;
using Isle.Services;

namespace Isle.Views
{
    public partial class ClipboardView : UserControl, IIslandView
    {
        private const string AllKey = "\u0001all", PinnedKey = "\u0001pinned";

        private readonly ICollectionView _view;
        /// <summary>Выбранная слева папка: вся история, закреплённые или своя (имя).</summary>
        private string _folder = AllKey;
        /// <summary>Фильтр по типу (null — все типы).</summary>
        private ClipCategory? _type;

        private List<(int index, ClipItem item)> _undo;
        private readonly DispatcherTimer _undoTimer = new() { Interval = TimeSpan.FromSeconds(6) };
        private string _renaming;

        private static readonly (ClipCategory cat, string name, string icon)[] Types =
        {
            (ClipCategory.Text, "Текст", "Text"), (ClipCategory.Link, "Ссылки", "Link"), (ClipCategory.Image, "Фото", "Image"),
            (ClipCategory.Video, "Видео", "Film"), (ClipCategory.Files, "Файлы", "File"), (ClipCategory.Code, "Код", "Code"),
            (ClipCategory.Color, "Цвета", "Pipette"),
        };

        private static ClipboardService Svc => AppHost.Clipboard;

        public ClipboardView()
        {
            InitializeComponent();
            _view = CollectionViewSource.GetDefaultView(Svc.Items);
            _view.Filter = o => o is ClipItem c && InFolder(c) && (_type == null || c.Category == _type) && Matches(c);
            List.ItemsSource = _view;
            Svc.Items.CollectionChanged += (_, __) => Dispatcher.InvokeAsync(RefreshAll, DispatcherPriority.Background);
            Svc.Folders.CollectionChanged += (_, __) => RebuildFolders();
            Svc.ActiveFolderChanged += () => { RebuildFolders(); UpdateRecordBar(); };
            _undoTimer.Tick += (_, __) => FinishUndo();
            RefreshAll();
        }

        public void OnShown()
        {
            _view.Refresh(); // обновить «N мин назад»
            RefreshAll();
        }

        public void FocusSearch() => Dispatcher.InvokeAsync(() => { Search.Focus(); Search.SelectAll(); }, DispatcherPriority.Input);

        // ---------- фильтры ----------
        private bool InFolder(ClipItem c) => _folder switch
        {
            AllKey => true,
            PinnedKey => c.Pinned,
            _ => c.Folder == _folder,
        };

        private bool Matches(ClipItem c)
        {
            var q = Search.Text.Trim();
            if (q.Length == 0) return true;
            return c.Text.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0
                || (c.Source ?? "").IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        private void RefreshAll()
        {
            RebuildFolders();
            RebuildTypes();
            UpdateRecordBar();
            UpdateEmpty();
        }

        private void Apply()
        {
            _view.Refresh();
            RebuildTypes();
            UpdateEmpty();
            if (List.Items.Count > 0 && List.SelectedIndex >= List.Items.Count) List.SelectedIndex = -1;
        }

        // ---------- колонка папок ----------
        private void RebuildFolders()
        {
            FoldersPanel.Children.Clear();
            FoldersPanel.Children.Add(FolderRow(AllKey, "Вся история", "History", Svc.Items.Count));
            int pinned = Svc.Items.Count(x => x.Pinned);
            FoldersPanel.Children.Add(FolderRow(PinnedKey, "Закреплённые", "Pin", pinned));
            if (Svc.Folders.Count > 0)
                FoldersPanel.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x28)), Margin = new Thickness(10, 6, 10, 6) });
            foreach (var f in Svc.Folders) FoldersPanel.Children.Add(FolderRow(f, f, "Folder", Svc.Items.Count(x => x.Folder == f)));
            if (_folder != AllKey && _folder != PinnedKey && !Svc.Folders.Contains(_folder)) SelectFolder(AllKey);
        }

        private FrameworkElement FolderRow(string key, string title, string icon, int count)
        {
            bool sel = key == _folder, rec = key == Svc.ActiveFolder;
            var fg = sel ? (Brush)FindResource("Fg") : (Brush)FindResource("Muted");
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new Icon { Kind = icon, Width = 14, Height = 14, Foreground = fg, VerticalAlignment = VerticalAlignment.Center });
            var name = new TextBlock { Text = title, FontSize = 12.5, Foreground = fg, Margin = new Thickness(9, 0, 6, 0), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            if (sel) name.FontWeight = FontWeights.SemiBold;
            Grid.SetColumn(name, 1); g.Children.Add(name);
            FrameworkElement right;
            if (rec)
            {
                // сюда сейчас сохраняются копии — красная точка, как «идёт запись»
                right = new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x5F)), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Новые копии сохраняются сюда" };
            }
            else right = new TextBlock { Text = count > 0 ? count.ToString() : "", FontSize = 11, Foreground = (Brush)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(right, 2); g.Children.Add(right);

            var row = new Border
            {
                Child = g, Padding = new Thickness(10, 7, 10, 7), CornerRadius = new CornerRadius(9), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 2),
                Background = sel ? new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x21)) : Brushes.Transparent,
            };
            if (!sel)
            {
                row.MouseEnter += (_, __) => row.Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x18));
                row.MouseLeave += (_, __) => row.Background = Brushes.Transparent;
            }
            row.MouseLeftButtonUp += (_, __) => SelectFolder(key);
            if (key != AllKey && key != PinnedKey) row.MouseRightButtonUp += (_, e) => { e.Handled = true; FolderContextMenu(key, row); };
            return row;
        }

        private void SelectFolder(string key)
        {
            if (_folder == key) return;
            _folder = key;
            _type = null;
            RebuildFolders();
            Apply();
        }

        private void FolderContextMenu(string folder, FrameworkElement target)
        {
            var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom };
            bool rec = Svc.ActiveFolder == folder;
            var recItem = new MenuItem { Header = rec ? "Перестать сохранять сюда" : "Сохранять сюда новые копии" };
            recItem.Click += (_, __) => Svc.ActiveFolder = rec ? null : folder;
            menu.Items.Add(recItem);
            var ren = new MenuItem { Header = "Переименовать" };
            ren.Click += (_, __) => StartFolderName(folder);
            menu.Items.Add(ren);
            menu.Items.Add(new Separator());
            int n = Svc.Items.Count(x => x.Folder == folder);
            var del = new MenuItem { Header = n > 0 ? "Удалить папку (записи останутся в истории)" : "Удалить папку" };
            del.Click += (_, __) => Svc.DeleteFolder(folder);
            menu.Items.Add(del);
            menu.IsOpen = true;
        }

        // создание и переименование папки — прямо в колонке, без отдельного окна
        private void OnNewFolder(object s, RoutedEventArgs e) => StartFolderName(null);

        private void StartFolderName(string renaming)
        {
            _renaming = renaming;
            FolderName.Text = renaming ?? "";
            FolderName.Tag = renaming == null ? "Название и Enter" : "Новое название";
            NewFolderBtn.Visibility = Visibility.Collapsed;
            FolderName.Visibility = Visibility.Visible;
            AppHost.Window.SuppressAutoCollapse = true;
            Dispatcher.InvokeAsync(() => { FolderName.Focus(); FolderName.SelectAll(); }, DispatcherPriority.Input);
        }

        private void EndFolderName(bool commit)
        {
            if (FolderName.Visibility != Visibility.Visible) return;
            var name = FolderName.Text.Trim();
            FolderName.Visibility = Visibility.Collapsed;
            NewFolderBtn.Visibility = Visibility.Visible;
            AppHost.Window.SuppressAutoCollapse = false;
            if (!commit || name.Length == 0) return;
            if (_renaming == null)
            {
                if (Svc.AddFolder(name)) SelectFolder(name);
            }
            else if (Svc.RenameFolder(_renaming, name) && _folder == _renaming) { _folder = name; RebuildFolders(); Apply(); }
        }

        private void OnFolderNameKey(object s, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { e.Handled = true; EndFolderName(true); }
            else if (e.Key == Key.Escape) { e.Handled = true; EndFolderName(false); }
        }

        private void OnFolderNameLost(object s, KeyboardFocusChangedEventArgs e) => EndFolderName(true);

        // ---------- чипы типов ----------
        private void RebuildTypes()
        {
            TypesPanel.Children.Clear();
            var inFolder = Svc.Items.Where(InFolder).ToList();
            var counts = inFolder.GroupBy(x => x.Category).ToDictionary(g => g.Key, g => g.Count());
            // показываем фильтры, только когда есть из чего выбирать
            if (counts.Count < 2 && _type == null) { TypesPanel.Visibility = Visibility.Collapsed; return; }
            TypesPanel.Visibility = Visibility.Visible;
            TypesPanel.Children.Add(Chip(null, "Все", null, inFolder.Count));
            foreach (var (cat, name, icon) in Types)
                if (counts.TryGetValue(cat, out int n) || _type == cat) TypesPanel.Children.Add(Chip(cat, name, icon, n));
        }

        private ToggleButton Chip(ClipCategory? cat, string name, string icon, int count)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            if (icon != null) sp.Children.Add(new Icon { Kind = icon, Width = 12, Height = 12, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            if (count > 0) sp.Children.Add(new TextBlock { Text = count.ToString(), Opacity = 0.55, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var b = new ToggleButton { Style = (Style)FindResource("Chip"), Content = sp, IsChecked = _type == cat, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(11, 0, 11, 0) };
            b.Click += (_, __) => { _type = _type == cat ? null : cat; Apply(); };
            return b;
        }

        // ---------- полоса «сохраняется в папку» ----------
        private void UpdateRecordBar()
        {
            var f = Svc.ActiveFolder;
            if (f == null) { RecordBar.Visibility = Visibility.Collapsed; return; }
            RecordBar.Visibility = Visibility.Visible;
            RecordText.Text = "Всё, что копируешь, сохраняется в «" + f + "»";
        }

        private void OnStopRecord(object s, RoutedEventArgs e) => Svc.ActiveFolder = null;

        // ---------- пусто ----------
        private Action _emptyAction;

        private void UpdateEmpty()
        {
            bool none = _view.IsEmpty;
            Empty.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
            _emptyAction = null;
            if (!none) return;
            string q = Search.Text.Trim();
            if (!AppSettings.Current.ClipboardHistory)
            {
                EmptyText.Text = "История буфера выключена";
                _emptyAction = () => { AppSettings.Current.ClipboardHistory = true; AppSettings.Current.Save(); UpdateEmpty(); };
                EmptyBtnText.Text = "Включить";
            }
            else if (q.Length > 0) EmptyText.Text = "Ничего не найдено по «" + q + "»";
            else if (_type != null) EmptyText.Text = "Здесь пока нет: " + Types.First(t => t.cat == _type).name.ToLowerInvariant();
            else if (_folder == PinnedKey) EmptyText.Text = "Закреплённых пока нет — наведи на запись и нажми булавку";
            else if (_folder != AllKey)
            {
                EmptyText.Text = "Папка «" + _folder + "» пустая.\nПеренеси сюда записи кнопкой с папкой или включи сохранение — тогда всё скопированное попадёт сюда.";
                if (Svc.ActiveFolder != _folder)
                {
                    var f = _folder;
                    _emptyAction = () => Svc.ActiveFolder = f;
                    EmptyBtnText.Text = "Сохранять сюда новые копии";
                }
            }
            else EmptyText.Text = "Пока пусто — скопируй что-нибудь (Ctrl+C)";
            EmptyBtn.Visibility = _emptyAction != null ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnEmptyAction(object s, RoutedEventArgs e) => _emptyAction?.Invoke();

        // ---------- поиск и клавиатура ----------
        private void OnSearch(object s, TextChangedEventArgs e) => Apply();

        private void OnSearchKey(object s, KeyEventArgs e)
        {
            int n = List.Items.Count;
            switch (e.Key)
            {
                case Key.Down when n > 0:
                    List.SelectedIndex = Math.Min(n - 1, List.SelectedIndex + 1); List.ScrollIntoView(List.SelectedItem); e.Handled = true; break;
                case Key.Up when n > 0:
                    List.SelectedIndex = Math.Max(0, List.SelectedIndex - 1); List.ScrollIntoView(List.SelectedItem); e.Handled = true; break;
                case Key.Enter when n > 0:
                    var it = List.SelectedItem as ClipItem ?? List.Items[0] as ClipItem;
                    if (it != null) Use(it, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                    e.Handled = true; break;
                case Key.Delete when List.SelectedItem is ClipItem sel && Search.Text.Length == 0:
                    int i = List.SelectedIndex; DeleteWithUndo(new[] { sel }, "Удалено");
                    if (List.Items.Count > 0) List.SelectedIndex = Math.Min(i, List.Items.Count - 1);
                    e.Handled = true; break;
                case Key.Escape when Search.Text.Length > 0:
                    Search.Clear(); e.Handled = true; break;
            }
        }

        // ---------- записи ----------
        private void OnItemClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && IsInsideButton(d)) return;
            if ((sender as FrameworkElement)?.Tag is not ClipItem item) return;
            Use(item, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        }

        private static void Use(ClipItem item, bool copyOnly)
        {
            var target = AppHost.Window.PreviousForeground;
            AppHost.Window.Collapse();
            AppHost.Clipboard.Paste(item, target, !copyOnly);
            if (copyOnly) ToastBus.Show(new Toast { Kind = ToastKind.Success, Title = "Скопировано", Icon = Icons.Copy, Important = true, Duration = TimeSpan.FromSeconds(1.2) });
        }

        private static bool IsInsideButton(DependencyObject d)
        {
            while (d != null && d is not Border { Name: "row" })
            {
                if (d is ButtonBase) return true;
                d = VisualTreeHelper.GetParent(d);
            }
            return false;
        }

        private void OnPin(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not ClipItem item) return;
            item.Pinned = !item.Pinned;
            Svc.Save();
            RebuildFolders();
            Apply();
        }

        private void OnDelete(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not ClipItem item) return;
            DeleteWithUndo(new[] { item }, "Удалено");
        }

        private void OnFolderMenu(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not ClipItem item) return;
            var menu = new ContextMenu { PlacementTarget = sender as UIElement, Placement = PlacementMode.Bottom };
            foreach (var f in Svc.Folders)
            {
                var mi = new MenuItem { Header = f, IsCheckable = true, IsChecked = item.Folder == f };
                var name = f;
                mi.Click += (_, __) => { Svc.MoveToFolder(item, item.Folder == name ? null : name); RebuildFolders(); Apply(); };
                menu.Items.Add(mi);
            }
            if (Svc.Folders.Count > 0) menu.Items.Add(new Separator());
            if (item.Folder != null)
            {
                var off = new MenuItem { Header = "Убрать из папки" };
                off.Click += (_, __) => { Svc.MoveToFolder(item, null); RebuildFolders(); Apply(); };
                menu.Items.Add(off);
            }
            var add = new MenuItem { Header = "Новая папка…" };
            add.Click += (_, __) => StartFolderName(null);
            menu.Items.Add(add);
            menu.IsOpen = true;
        }

        /// <summary>Быстрое действие по типу записи.</summary>
        private void OnAction(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not ClipItem item) return;
            try
            {
                switch (item.Kind)
                {
                    case ClipKind.Link:
                        var url = item.Text.Trim();
                        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
                        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https"))
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
                            AppHost.Window.Collapse();
                        }
                        break;
                    case ClipKind.Files:
                        var first = item.Files.FirstOrDefault(f => File.Exists(f) || Directory.Exists(f));
                        if (first == null) { ToastBus.Show(new Toast { Kind = ToastKind.Error, Title = "Файлов больше нет", Icon = Icons.Folder, Important = true }); break; }
                        System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + first + "\"");
                        AppHost.Window.Collapse();
                        break;
                    case ClipKind.Image:
                        SaveImage(item);
                        break;
                }
            }
            catch (Exception ex) { App.Log("clip action: " + ex.Message); }
        }

        private static void SaveImage(ClipItem item)
        {
            if (!File.Exists(item.ImagePath)) return;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = "Isle " + item.Time.ToString("yyyy-MM-dd HH.mm.ss") + ".png",
                Filter = "PNG|*.png", InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            };
            AppHost.Window.SuppressAutoCollapse = true;
            try
            {
                if (dlg.ShowDialog(AppHost.Window) != true) return;
                File.Copy(item.ImagePath, dlg.FileName, true);
                ToastBus.Show(new Toast { Kind = ToastKind.Success, Title = "Сохранено", Subtitle = System.IO.Path.GetFileName(dlg.FileName), Icon = Icons.Download, FilePath = dlg.FileName, Important = true });
            }
            finally { AppHost.Window.SuppressAutoCollapse = false; }
        }

        // ---------- меню «⋯» и очистка с отменой ----------
        private void OnMore(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu { PlacementTarget = MoreBtn, Placement = PlacementMode.Bottom };
            // очищается то, что сейчас видно; закреплённые и разложенные по папкам — только внутри своей папки
            var visible = _view.Cast<ClipItem>().ToList();
            bool inUserFolder = _folder != AllKey && _folder != PinnedKey;
            var victims = visible.Where(x => !x.Pinned && (inUserFolder || x.Folder == null)).ToList();
            string what = inUserFolder ? "папку «" + _folder + "»" : _type != null || Search.Text.Trim().Length > 0 ? "найденное" : "историю";
            var clear = new MenuItem { Header = "Очистить " + what + (victims.Count > 0 ? " (" + victims.Count + ")" : ""), IsEnabled = victims.Count > 0 };
            clear.Click += (_, __) => DeleteWithUndo(victims, "Очищено");
            menu.Items.Add(clear);
            var images = Svc.Items.Where(x => x.IsImage && !x.Pinned && x.Folder == null).ToList();
            if (images.Count > 0 && !inUserFolder)
            {
                var ci = new MenuItem { Header = "Удалить только картинки (" + images.Count + ")" };
                ci.Click += (_, __) => DeleteWithUndo(images, "Удалены картинки");
                menu.Items.Add(ci);
            }
            if (!inUserFolder && _folder == AllKey) menu.Items.Add(new MenuItem { Header = "Закреплённые и папки не трогаются", IsEnabled = false });
            menu.Items.Add(new Separator());
            var hist = new MenuItem { Header = "Запоминать историю", IsCheckable = true, IsChecked = AppSettings.Current.ClipboardHistory };
            hist.Click += (_, __) => { AppSettings.Current.ClipboardHistory = !AppSettings.Current.ClipboardHistory; AppSettings.Current.Save(); UpdateEmpty(); };
            menu.Items.Add(hist);
            menu.IsOpen = true;
        }

        private void DeleteWithUndo(IEnumerable<ClipItem> items, string verb)
        {
            FinishUndo();
            var removed = Svc.SoftRemove(items);
            if (removed.Count == 0) return;
            _undo = removed;
            UndoText.Text = verb + (removed.Count > 1 ? ": " + removed.Count : "");
            UndoBar.Visibility = Visibility.Visible;
            Hint.Visibility = Visibility.Hidden;
            _undoTimer.Stop(); _undoTimer.Start();
            RebuildFolders();
            Apply();
        }

        private void OnUndo(object s, RoutedEventArgs e)
        {
            if (_undo == null) return;
            var u = _undo; _undo = null;
            _undoTimer.Stop();
            Svc.Restore(u);
            HideUndo();
            RebuildFolders();
            Apply();
        }

        /// <summary>Время на отмену вышло — удаляем файлы картинок окончательно.</summary>
        private void FinishUndo()
        {
            _undoTimer.Stop();
            if (_undo != null) { Svc.Purge(_undo); _undo = null; }
            HideUndo();
        }

        private void HideUndo()
        {
            UndoBar.Visibility = Visibility.Collapsed;
            Hint.Visibility = Visibility.Visible;
        }
    }
}
