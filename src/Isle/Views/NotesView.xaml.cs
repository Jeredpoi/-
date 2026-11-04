using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Isle.Services;

namespace Isle.Views
{
    public partial class NotesView : UserControl, IIslandView
    {
        private readonly NotesData _data = Storage.Load<NotesData>("notes.json");
        private Note _current;
        private bool _loading;
        private DateTime _deleteArmed = DateTime.MinValue;
        private readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(600) };

        public NotesView()
        {
            InitializeComponent();
            if (_data.Notes.Count == 0) _data.Notes.Add(new Note());
            _save.Tick += (_, __) => { _save.Stop(); SaveNow(); };

            foreach (var m in new[] { 5, 10, 15, 25, 45 })
            {
                var b = new Button { Content = m.ToString(), Style = (Style)FindResource("Flat"), Margin = new Thickness(0, 0, 4, 0), Height = 28, Padding = new Thickness(0) };
                b.Click += (_, __) => AppHost.Timer.Set(TimeSpan.FromMinutes(m));
                Presets.Children.Add(b);
            }

            RemList.ItemsSource = AppHost.Reminders.Items;
            AppHost.Reminders.Items.CollectionChanged += (_, __) => UpdateRemHint();
            AppHost.Timer.Changed += UpdateTimer;
            UpdateRemHint();
            UpdateTimer();
            Open(_data.Notes[0]);
        }

        public void OnShown() { UpdateTimer(); BuildTabs(); }

        // ---------- Заметки ----------

        private void BuildTabs()
        {
            NoteTabs.Items.Clear();
            foreach (var n in _data.Notes)
            {
                var title = string.IsNullOrWhiteSpace(n.Text) ? "Заметка" : n.Text.Split('\n')[0].Trim();
                if (title.Length > 16) title = title.Substring(0, 16) + "…";
                var t = new ToggleButton { Content = title, Style = (Style)FindResource("Chip"), Margin = new Thickness(0, 0, 6, 0), IsChecked = n == _current };
                var note = n;
                t.Click += (_, __) => { SaveNow(); Open(note); };
                NoteTabs.Items.Add(t);
            }
        }

        private void Open(Note n)
        {
            _current = n;
            _loading = true;
            Editor.Text = n.Text;
            _loading = false;
            BuildTabs();
        }

        private void OnEdit(object s, TextChangedEventArgs e)
        {
            if (_loading || _current == null) return;
            _current.Text = Editor.Text;
            _current.Updated = DateTime.Now;
            Saved.Text = "Сохраняю…";
            _save.Stop(); _save.Start();
        }

        private void SaveNow()
        {
            Storage.Save("notes.json", _data);
            Saved.Text = "Сохранено " + DateTime.Now.ToString("HH:mm");
            BuildTabs();
        }

        private void OnAddNote(object s, RoutedEventArgs e)
        {
            SaveNow();
            var n = new Note();
            _data.Notes.Add(n);
            Open(n);
            SaveNow();
        }

        private void OnDeleteNote(object s, RoutedEventArgs e)
        {
            if (_current == null) return;
            // удаление в два нажатия, чтобы не стереть заметку случайно
            if (!string.IsNullOrWhiteSpace(_current.Text) && (DateTime.Now - _deleteArmed).TotalSeconds > 3)
            {
                _deleteArmed = DateTime.Now;
                Saved.Text = "Нажми ещё раз, чтобы удалить";
                return;
            }
            _deleteArmed = DateTime.MinValue;
            _data.Notes.Remove(_current);
            if (_data.Notes.Count == 0) _data.Notes.Add(new Note());
            Open(_data.Notes[0]);
            SaveNow();
        }

        private void OnCopy(object s, RoutedEventArgs e)
        {
            if (Editor.Text.Length > 0) ClipboardService.SetText(Editor.Text);
        }

        private void OnRemindLine(object s, RoutedEventArgs e)
        {
            int line = Editor.GetLineIndexFromCharacterIndex(Editor.CaretIndex);
            var text = line >= 0 ? Editor.GetLineText(line).Trim() : "";
            if (text.Length == 0) return;
            RemInput.Text = text;
            AddReminder();
        }

        // ---------- Таймер ----------

        private void UpdateTimer()
        {
            var t = AppHost.Timer;
            TimeText.Text = TimerService.Format(t.Left);
            StartIcon.Kind = t.Running ? "Pause" : "Play";
            StartText.Text = t.Running ? "Пауза" : (t.Paused ? "Дальше" : "Старт");
            DrawRing(t.Fraction);
        }

        private void DrawRing(double f)
        {
            const double r = 39.5, c = 42;
            f = Math.Clamp(f, 0, 0.9999);
            if (f <= 0.0001) { Ring.Data = null; return; }
            double a = f * 2 * Math.PI;
            var start = new Point(c, c - r);
            var end = new Point(c + r * Math.Sin(a), c - r * Math.Cos(a));
            var fig = new PathFigure { StartPoint = start, IsClosed = false };
            fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, f > 0.5, SweepDirection.Clockwise, true));
            Ring.Data = new PathGeometry(new[] { fig });
        }

        private void OnStart(object s, RoutedEventArgs e)
        {
            var t = AppHost.Timer;
            if (t.Running) t.Pause();
            else t.Start();
        }

        private void OnCustomKey(object s, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            if (double.TryParse(Custom.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m) && m > 0 && m < 24 * 60)
            {
                AppHost.Timer.StartNew(TimeSpan.FromMinutes(m));
                Custom.Text = "";
            }
            e.Handled = true;
        }

        // ---------- Напоминания ----------

        private void UpdateRemHint() => RemHint.Visibility = AppHost.Reminders.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        private void OnRemKey(object s, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            AddReminder();
        }

        private void AddReminder()
        {
            RemError.Text = "";
            var r = AppHost.Reminders.Add(RemInput.Text);
            if (r == null) { RemError.Text = "Не понял, когда. Пример: «через 20 минут», «завтра в 9», «в 18:30»"; return; }
            RemInput.Text = "";
        }

        private void OnRemDelete(object s, RoutedEventArgs e)
        {
            if ((s as FrameworkElement)?.Tag is Reminder r) AppHost.Reminders.Remove(r);
        }
    }
}
