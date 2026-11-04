using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Isle.Services;

namespace Isle
{
    public partial class App : Application
    {
        private const string MutexName = "Isle.SingleInstance.7f1c";
        private const string ShowEventName = "Isle.ShowIsland.7f1c";
        private static Mutex _single;
        private EventWaitHandle _showEvent;
        private TrayIcon _tray;
        private bool _started;

        protected override void OnStartup(StartupEventArgs e)
        {
            _single = new Mutex(true, MutexName, out bool first);
            if (!first)
            {
                // Isle уже работает — просим его показаться и выходим.
                try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
                Shutdown();
                return;
            }

            DispatcherUnhandledException += OnError;
            AppDomain.CurrentDomain.UnhandledException += (_, a) => Log(a.ExceptionObject?.ToString());
            base.OnStartup(e);

            try
            {
                var w = new MainWindow();
                AppHost.Window = w;
                w.Show();
                _started = true;
            }
            catch (Exception ex)
            {
                Fatal(ex);
                return;
            }

            try { _tray = new TrayIcon(); } catch (Exception ex) { Log("tray: " + ex); }

            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            var t = new Thread(() =>
            {
                while (_showEvent.WaitOne())
                    Dispatcher.InvokeAsync(() => AppHost.Window?.BringUp());
            }) { IsBackground = true, Name = "Isle.ShowListener" };
            t.Start();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _tray?.Dispose();
            base.OnExit(e);
        }

        private void OnError(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log(e.Exception.ToString());
            e.Handled = true; // один сломанный модуль не должен ронять весь остров
            if (!_started) Fatal(e.Exception);
        }

        /// <summary>Ошибка при запуске: показываем её, а не висим невидимым процессом.</summary>
        private void Fatal(Exception ex)
        {
            Log("FATAL: " + ex);
            MessageBox.Show(
                "Isle не смог запуститься.\n\n" + ex.GetType().Name + ": " + ex.Message +
                "\n\nПодробности записаны в " + Path.Combine(Storage.Dir, "errors.log"),
                "Isle", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }

        public static void Log(string text)
        {
            try
            {
                Directory.CreateDirectory(Storage.Dir);
                File.AppendAllText(Path.Combine(Storage.Dir, "errors.log"), DateTime.Now + "  " + text + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }
    }
}
