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
        private static Mutex _single;

        protected override void OnStartup(StartupEventArgs e)
        {
            _single = new Mutex(true, "Isle.SingleInstance.7f1c", out bool first);
            if (!first)
            {
                Shutdown();
                return;
            }

            DispatcherUnhandledException += OnError;
            AppDomain.CurrentDomain.UnhandledException += (_, a) => Log(a.ExceptionObject?.ToString());
            base.OnStartup(e);

            var w = new MainWindow();
            AppHost.Window = w;
            w.Show();
        }

        private void OnError(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log(e.Exception.ToString());
            e.Handled = true; // остров не должен падать из-за одного модуля
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
