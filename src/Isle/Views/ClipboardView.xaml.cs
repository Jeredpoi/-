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
        private bool _refreshQueued;

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
            // очистка или отмена на N записей — одна перестройка, а не N
            Svc.Items.CollectionChanged += (_, __) =>
            {
                if (_refreshQueued) return;
                _refreshQueued = true;
                Dispatcher.InvokeAsync(() => { _refreshQueued = false; RefreshAll(); }, DispatcherPriority.Background);
            };
            Svc.Folders.CollectionChanged += (_, __) => RebuildFolders();
            Svc.ActiveFolderChanged += () => { RebuildFolders(); UpdateRecordBar(); };
            Svc.FolderAppsChanged += () => { RebuildFolders(); UpdateAppsBar(); };
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
            UpdateAppsBar();
            UpdateEmpty();
        }

        private void Apply()
        {
            _view.Refresh();
            RebuildTypes();
            UpdateEmpty();
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
            else
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                // у папки есть приложения — значок снимка: их скриншоты попадают сюда сами
                if (key != AllKey && key != PinnedKey && Svc.AppsOf(key).Count > 0)
                    sp.Children.Add(new Icon { Kind = "Crop", Width = 11, Height = 11, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 0, count > 0 ? 6 : 0, 0), VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(new TextBlock { Text = count > 0 ? count.ToString() : "", FontSize = 11, Foreground = (Brush)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center });
                right = sp;
            }
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
            // сюда можно перетащить запись из списка
            row.AllowDrop = true;
            row.DragEnter += (_, e) => { if (IsOurDrag(e)) { row.Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x33)); e.Effects = DragDropEffects.Copy; } e.Handled = true; };
            row.DragOver += (_, e) => { e.Effects = IsOurDrag(e) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
            row.DragLeave += (_, __) => row.Background = sel ? new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x21)) : Brushes.Transparent;
            row.Drop += (_, e) => { e.Handled = true; if (IsOurDrag(e) && _dragItem != null) DropInto(_dragItem, key); };
            var apps = key != AllKey && key != PinnedKey ? Svc.AppsOf(key) : Array.Empty<string>();
            if (apps.Count > 0) row.ToolTip = "Снимки из " + string.Join(", ", apps.Select(CachedTitle)) + " попадают сюда";
            if (key != AllKey && key != PinnedKey) row.MouseRightButtonUp += (_, e) => { e.Handled = true; FolderContextMenu(key, row); };
            return row;
        }

        private void SelectFolder(string key)
        {
            if (_folder == key) return;
            _folder = key;
            _type = null;
            RebuildFolders();
            UpdateAppsBar();
            Apply();
        }

        private void FolderContextMenu(string folder, FrameworkElement target)
        {
            var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom };
            bool rec = Svc.ActiveFolder == folder;
            var recItem = new MenuItem { Header = rec ? "Перестать сохранять сюда" : "Сохранять сюда новые копии" };
            recItem.Click += (_, __) => Svc.ActiveFolder = rec ? null : folder;
            menu.Items.Add(recItem);
            var apps = Svc.AppsOf(folder);
            var appsItem = new MenuItem { Header = apps.Count == 0 ? "Снимки из приложений…" : "Снимки из приложений (" + apps.Count + ")…" };
            appsItem.Click += (_, __) => Dispatcher.InvokeAsync(() => AppsMenu(folder, target), DispatcherPriority.Background);
            menu.Items.Add(appsItem);
            var ren = new MenuItem { Header = "Переименовать" };
            ren.Click += (_, __) => StartFolderName(folder);
            menu.Items.Add(ren);
            menu.Items.Add(new Separator());
            int n = Svc.Items.Count(x => x.Folder == folder);
            var del = new MenuItem { Header = n > 0 ? "Удалить папку (записи останутся в истории)" : "Удалить папку" };
            del.Click += (_, __) => DeleteFolderWithUndo(folder);
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
            else
            {
                // открытая папка остаётся открытой: новое имя ставим до переименования (оно сразу перестраивает колонку)
                bool wasSel = _folder == _renaming;
                if (wasSel) _folder = name;
                if (!Svc.RenameFolder(_renaming, name) && wasSel) _folder = _renaming;
                RebuildFolders(); UpdateAppsBar(); Apply();
            }
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

        // ---------- снимки из приложений → в папку ----------
        private void UpdateAppsBar()
        {
            bool user = _folder != AllKey && _folder != PinnedKey;
            var apps = user ? Svc.AppsOf(_folder) : Array.Empty<string>();
            AppsBar.Visibility = apps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (apps.Count > 0) AppsText.Text = "Снимки из " + string.Join(", ", apps.Select(CachedTitle)) + " сами попадают сюда";
        }

        private void OnEditApps(object s, RoutedEventArgs e) => AppsMenu(_folder, AppsBar);

        /// <summary>
        /// Список приложений с окнами (и уже выбранных для этой папки): отметил — снимки, сделанные в нём,
        /// сами попадают в папку. У приложения одна папка: выбор здесь снимает его с другой.
        /// </summary>
        private async void AppsMenu(string folder, FrameworkElement target)
        {
            // перебор процессов и чтение описаний exe — не в потоке интерфейса
            var running = await System.Threading.Tasks.Task.Run(RunningApps);
            if (!Svc.Folders.Contains(folder)) return;
            var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom, MaxHeight = 440, MinWidth = 280 };
            menu.Items.Add(new MenuItem { Header = "Снимки из этих приложений попадут в «" + folder + "»", IsEnabled = false });
            menu.Items.Add(new Separator());
            var mine = Svc.AppsOf(folder);
            var all = mine.Select(a => (name: a, path: running.FirstOrDefault(r => r.name.Equals(a, StringComparison.OrdinalIgnoreCase)).path))
                .Concat(running.Where(r => !mine.Contains(r.name, StringComparer.OrdinalIgnoreCase)))
                .ToList();
            if (all.Count == 0) menu.Items.Add(new MenuItem { Header = "Открой нужное приложение — оно появится здесь", IsEnabled = false });
            foreach (var (name, path) in all)
            {
                var item = new MenuItem { StaysOpenOnClick = true };
                var row = new Grid { MinWidth = 260 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                row.Children.Add(new Image { Source = path != null ? Isle.Native.Win32.ShellIcon(path) : null, Width = 18, Height = 18, Margin = new Thickness(0, 0, 10, 0) });
                var other = Svc.FolderApps.FirstOrDefault(kv => kv.Key != folder && kv.Value.Contains(name, StringComparer.OrdinalIgnoreCase)).Key;
                var title = new TextBlock { Text = AppTitle(name) + (other != null ? "  · сейчас в «" + other + "»" : ""), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 320, TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(title, 1); row.Children.Add(title);
                bool on = mine.Contains(name, StringComparer.OrdinalIgnoreCase);
                var mark = new TextBlock { Text = "✓", FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Visibility = on ? Visibility.Visible : Visibility.Hidden };
                Grid.SetColumn(mark, 2); row.Children.Add(mark);
                item.Header = row;
                var n = name;
                item.Click += (_, __) =>
                {
                    bool now = !Svc.AppsOf(folder).Contains(n, StringComparer.OrdinalIgnoreCase);
                    Svc.SetFolderApp(folder, n, now);
                    mark.Visibility = now ? Visibility.Visible : Visibility.Hidden;
                    title.Text = AppTitle(n);
                };
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Titles = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Имя без обращения к процессам (для колонки, которая перестраивается часто).</summary>
        private static string CachedTitle(string process) => Titles.TryGetValue(process, out var t) ? t : process;

        /// <summary>Понятное имя приложения: описание из exe («Google Chrome»), иначе имя процесса. Запоминается и неудача.</summary>
        private static string AppTitle(string process)
        {
            if (Titles.TryGetValue(process, out var t)) return t;
            t = process;
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(process))
                {
                    using (p)
                    {
                        var d = p.MainModule?.FileVersionInfo.FileDescription;
                        if (!string.IsNullOrWhiteSpace(d)) { t = d.Trim(); break; }
                    }
                }
            }
            catch { }
            Titles[process] = t;
            return t;
        }

        /// <summary>Приложения с открытыми окнами (без самого Isle и системных), по алфавиту.</summary>
        private static List<(string name, string path)> RunningApps()
        {
            var own = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { own, "explorer", "ApplicationFrameHost", "TextInputHost", "SystemSettings", "ShellExperienceHost", "SearchHost", "StartMenuExperienceHost", "LockApp" };
            var list = new List<(string, string)>();
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        if (p.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(p.MainWindowTitle) || skip.Contains(p.ProcessName)) continue;
                        if (list.Any(x => x.Item1.Equals(p.ProcessName, StringComparison.OrdinalIgnoreCase))) continue;
                        string path = null;
                        try { path = p.MainModule?.FileName; } catch { }
                        list.Add((p.ProcessName, path));
                    }
                    catch { }
                }
            }
            return list.OrderBy(x => AppTitle(x.Item1), StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        // ---------- перетаскивание записей ----------
        private const string DragFormat = "IsleClipItem";
        private Point _downAt;
        private ClipItem _pressed, _dragItem;
        private bool _dragged;

        private void OnRowDown(object sender, MouseButtonEventArgs e)
        {
            _dragged = false;
            _downAt = e.GetPosition(this);
            _pressed = (sender as FrameworkElement)?.Tag as ClipItem;
        }

        private void OnRowMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _pressed == null || _dragItem != null) return;
            if (!ReferenceEquals((sender as FrameworkElement)?.Tag, _pressed)) return; // нажали на другой строке или мимо списка
            if (e.OriginalSource is DependencyObject d && IsInsideButton(d)) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _downAt.X) < 6 && Math.Abs(p.Y - _downAt.Y) < 6) return;
            var item = _pressed;
            var data = new DataObject();
            data.SetData(DragFormat, "1");
            // и наружу: в Телеграм, Проводник, редактор — как файл или текст
            if (item.IsImage && File.Exists(item.ImagePath)) data.SetFileDropList(new System.Collections.Specialized.StringCollection { item.ImagePath });
            else if (item.IsFiles) { var sc = new System.Collections.Specialized.StringCollection(); sc.AddRange(item.Files.Where(File.Exists).ToArray()); if (sc.Count > 0) data.SetFileDropList(sc); }
            else data.SetText(item.Text);
            _dragItem = item; _dragged = true;
            AppHost.Window.SuppressAutoCollapse = true;
            // только «копировать»: с «переместить» Проводник забирал сам файл — у картинки ломалась запись,
            // а у записи «Файлы» уезжали оригиналы пользователя
            try { DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy); }
            catch (Exception ex) { App.Log("перетаскивание записи: " + ex.Message); }
            finally
            {
                _dragItem = null; _pressed = null;
                AppHost.Window.SuppressAutoCollapse = false;
                RebuildFolders();
            }
        }

        private bool IsOurDrag(DragEventArgs e) => _dragItem != null && e.Data.GetDataPresent(DragFormat);

        /// <summary>Запись брошена на папку слева.</summary>
        private void DropInto(ClipItem item, string key)
        {
            if (key == PinnedKey) item.Pinned = true;
            else item.Folder = key == AllKey ? null : key;
            Svc.Save();
            Journal.Info("clipboard", "перетащено в " + (key == AllKey ? "историю" : key == PinnedKey ? "закреплённые" : "папку"));
            RebuildFolders();
            Apply();
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
            _pressed = null; // кнопку отпустили — следующее движение мыши уже не перетаскивание этой записи
            if (e.OriginalSource is DependencyObject d && IsInsideButton(d)) return;
            if (_dragged) { _dragged = false; return; } // это было перетаскивание, а не клик
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

        private ClipboardService.FolderUndo _folderUndo;

        /// <summary>Удаление папки — с «Отменить», как и очистка: записи уходят в историю, лишние старые — с возвратом.</summary>
        private void DeleteFolderWithUndo(string folder)
        {
            FinishUndo();
            var u = Svc.DeleteFolder(folder);
            _folderUndo = u;
            UndoText.Text = "Папка «" + folder + "» удалена" + (u.Evicted.Count > 0 ? " · " + u.Evicted.Count + " старых записей не поместились в историю" : "");
            UndoBar.Visibility = Visibility.Visible;
            Hint.Visibility = Visibility.Hidden;
            _undoTimer.Stop(); _undoTimer.Start();
            RebuildFolders();
            Apply();
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
            if (_folderUndo != null)
            {
                var fu = _folderUndo; _folderUndo = null;
                _undoTimer.Stop();
                Svc.UndoDeleteFolder(fu);
                HideUndo(); RebuildFolders(); UpdateAppsBar(); Apply();
                return;
            }
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
            if (_folderUndo != null) { Svc.Purge(_folderUndo.Evicted); _folderUndo = null; }
            HideUndo();
        }

        private void HideUndo()
        {
            UndoBar.Visibility = Visibility.Collapsed;
            Hint.Visibility = Visibility.Visible;
        }
    }
}
