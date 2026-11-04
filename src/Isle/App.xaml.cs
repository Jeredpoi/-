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
            Trace("запуск " + typeof(App).Assembly.GetName().Version + ", " + Environment.OSVersion + ", " + Environment.ProcessPath, first);
            if (!first)
            {
                // Isle уже работает — просим его показаться и выходим.
                Trace("уже запущен — прошу показаться");
                bool signalled = false;
                try { EventWaitHandle.OpenExisting(ShowEventName).Set(); signalled = true; } catch { }
                if (!signalled)
                    MessageBox.Show("Isle уже запущен, но не отвечает (скорее всего, это старая версия).\n\n" +
                                    "Открой Диспетчер задач (Ctrl+Shift+Esc), заверши все процессы Isle.exe и запусти Isle ещё раз.",
                                    "Isle", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            DispatcherUnhandledException += OnError;
            AppDomain.CurrentDomain.UnhandledException += (_, a) => Log(a.ExceptionObject?.ToString());
            base.OnStartup(e);

            try
            {
                var w = new MainWindow();
                Trace("окно создано");
                AppHost.Window = w;
                w.Show();
                Trace("окно показано: " + w.Left + "," + w.Top + " " + w.Width + "×" + w.Height);
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
            Trace("ОШИБКА ЗАПУСКА: " + ex.Message);
            MessageBox.Show(
                "Isle не смог запуститься.\n\n" + ex.GetType().Name + ": " + ex.Message +
                "\n\nПодробности записаны в " + Path.Combine(Storage.Dir, "errors.log"),
                "Isle", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }

        /// <summary>Журнал запуска %AppData%\Isle\startup.log — по нему видно, на каком шаге что-то сломалось.</summary>
        public static void Trace(string text, bool fresh = false)
        {
            try
            {
                Directory.CreateDirectory(Storage.Dir);
                var line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + text + Environment.NewLine;
                var path = Path.Combine(Storage.Dir, "startup.log");
                if (fresh) File.WriteAllText(path, line); else File.AppendAllText(path, line);
            }
            catch { }
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
