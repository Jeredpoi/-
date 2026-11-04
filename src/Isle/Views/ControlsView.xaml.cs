using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Isle.Services;
using Radios = Windows.Devices.Radios;

namespace Isle.Views
{
    public partial class ControlsView : UserControl, IIslandView
    {
        private readonly Dictionary<string, DateTime> _armed = new();
        private readonly DispatcherTimer _reset = new() { Interval = TimeSpan.FromSeconds(3) };
        private static readonly Brush On = new SolidColorBrush(Color.FromRgb(0x5B, 0x6C, 0xF0));
        private static readonly Brush Off = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x33));

        public ControlsView()
        {
            InitializeComponent();
            _reset.Tick += (_, __) => { _reset.Stop(); ResetLabels(); };
        }

        public async void OnShown()
        {
            var net = SystemControls.Network();
            NetName.Text = net.name;
            NetStatus.Text = net.status;
            NetDot.Background = net.up ? On : Off;

            ThemeStatus.Text = SystemControls.IsDarkTheme ? "Включена" : "Выключена";
            ThemeDot.Background = SystemControls.IsDarkTheme ? On : Off;

            var bt = await SystemControls.GetRadio(Radios.RadioKind.Bluetooth);
            BtStatus.Text = bt == null ? "Нет адаптера" : bt.Value ? "Включён" : "Выключен";
            BtDot.Background = bt == true ? On : Off;
        }

        private void OnNetwork(object s, RoutedEventArgs e) => SystemControls.OpenSettings("ms-settings:network");

        private async void OnBluetooth(object s, RoutedEventArgs e)
        {
            var r = await SystemControls.ToggleRadio(Radios.RadioKind.Bluetooth);
            if (r == null) SystemControls.OpenSettings("ms-settings:bluetooth");
            OnShown();
        }

        private void OnTheme(object s, RoutedEventArgs e)
        {
            SystemControls.SetDarkTheme(!SystemControls.IsDarkTheme);
            OnShown();
        }

        private void OnTaskMgr(object s, RoutedEventArgs e) => SystemControls.TaskManager();
        private void OnSettings(object s, RoutedEventArgs e) => SystemControls.OpenSettings();
        private void OnSound(object s, RoutedEventArgs e) => SystemControls.OpenSettings("ms-settings:sound");
        private void OnShots(object s, RoutedEventArgs e)
        {
            System.IO.Directory.CreateDirectory(ScreenTools.ShotsDir);
            SystemControls.Shell(ScreenTools.ShotsDir);
        }

        private void OnLock(object s, RoutedEventArgs e) { AppHost.Window.Collapse(); SystemControls.Lock(); }

        // Опасные действия — только со второго нажатия
        private bool Confirm(string key, TextBlock label)
        {
            if (_armed.TryGetValue(key, out var t) && (DateTime.Now - t).TotalSeconds < 3)
            {
                _armed.Remove(key);
                return true;
            }
            ResetLabels();
            _armed[key] = DateTime.Now;
            label.Text = "Ещё раз";
            label.Foreground = (Brush)FindResource("Accent");
            _reset.Stop(); _reset.Start();
            return false;
        }

        private void ResetLabels()
        {
            var fg = (Brush)FindResource("Fg");
            SleepText.Text = "Сон"; RestartText.Text = "Перезагрузка"; ShutdownText.Text = "Выключение";
            SleepText.Foreground = RestartText.Foreground = ShutdownText.Foreground = fg;
        }

        private void OnSleep(object s, RoutedEventArgs e) { if (Confirm("sleep", SleepText)) { AppHost.Window.Collapse(); SystemControls.Sleep(); } }
        private void OnRestart(object s, RoutedEventArgs e) { if (Confirm("restart", RestartText)) SystemControls.Restart(); }
        private void OnShutdown(object s, RoutedEventArgs e) { if (Confirm("shutdown", ShutdownText)) SystemControls.Shutdown(); }
    }
}
