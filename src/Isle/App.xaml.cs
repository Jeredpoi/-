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

        /// <summary>Стенд проверки снимков (--bench папка): сценарий настоящей мышью, после него выходим.</summary>
        private string _benchDir;

        protected override void OnStartup(StartupEventArgs e)
        {
            int bi = Array.IndexOf(e.Args, "--bench");
            if (bi >= 0 && bi + 1 < e.Args.Length) _benchDir = Path.GetFullPath(e.Args[bi + 1]);
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

            // «покажись» от второго запуска слушаем сразу: пока первый ещё грузится, второй иначе решал, что Isle «не отвечает»
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            var t = new Thread(() =>
            {
                while (_showEvent.WaitOne())
                    Dispatcher.InvokeAsync(() => AppHost.Window?.BringUp());
            }) { IsBackground = true, Name = "Isle.ShowListener" };
            t.Start();

            if (_benchDir == null && SwitchToInstalledCopy()) { Shutdown(); return; }

            try { SystemControls.RepairAutostartPath(); }
            catch (Exception ex) { Log("autostart repair: " + ex.Message); }

            DispatcherUnhandledException += OnError;
            AppDomain.CurrentDomain.UnhandledException += (_, a) => { Log(a.ExceptionObject?.ToString()); Journal.Flush(); };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, a) => { Journal.Error("task", a.Exception); a.SetObserved(); };
            Journal.Verbose = AppSettings.Current.DebugLog;
            LogSession();
            base.OnStartup(e);

            try
            {
                var w = new MainWindow();
                Trace("окно создано");
                AppHost.Window = w;
                w.Show();
                Trace("окно показано: " + w.Left + "," + w.Top + " " + w.Width + "×" + w.Height);
                _started = true;
                try { DebugProbe.Start(); } catch (Exception ex) { Log("debug probe: " + ex.Message); }
                Helpers.MemoryTrim.Soon("запуск", 25000); // после запуска остаётся мусор загрузки модулей
                if (_benchDir != null) _ = Bench.RunAsync(w, _benchDir);
            }
            catch (Exception ex)
            {
                Fatal(ex);
                return;
            }

            try { _tray = new TrayIcon(); } catch (Exception ex) { Log("tray: " + ex); }

        }

        /// <summary>Шапка сессии в подробном журнале: версия, система, экраны, язык, настройки (без ключей).</summary>
        private static void LogSession()
        {
            try
            {
                Journal.Info("session", "==================== запуск Isle " + typeof(App).Assembly.GetName().Version + " ====================");
                Journal.Info("session", "ОС " + Environment.OSVersion + ", .NET " + Environment.Version + ", 64-бит " + Environment.Is64BitProcess
                    + ", ядер " + Environment.ProcessorCount + ", культура " + System.Globalization.CultureInfo.CurrentCulture.Name
                    + ", UI " + System.Globalization.CultureInfo.CurrentUICulture.Name + ", путь " + Environment.ProcessPath);
                Journal.Info("session", "экран (DIP) " + SystemParameters.PrimaryScreenWidth + "×" + SystemParameters.PrimaryScreenHeight
                    + ", рабочая область " + SystemParameters.WorkArea + ", виртуальный " + SystemParameters.VirtualScreenWidth + "×" + SystemParameters.VirtualScreenHeight
                    + ", рендер tier " + (System.Windows.Media.RenderCapability.Tier >> 16));
                var json = System.Text.Json.JsonSerializer.Serialize(AppSettings.Current);
                // ключи и пароли в журнал не попадают
                json = System.Text.RegularExpressions.Regex.Replace(json, "\"([A-Za-z]*(Key|Password|Token|Protected)[A-Za-z]*)\":\"[^\"]*\"", "\"$1\":\"***\"");
                Journal.Info("session", "настройки " + json);
                AppSettings.Current.LogBaseline();
            }
            catch (Exception ex) { Journal.Error("session", ex); }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Journal.Info("session", "выход, код " + e.ApplicationExitCode);
            Journal.Flush();
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

        /// <summary>
        /// Если запущена старая копия exe (например, скачанная из Actions), а установщик уже поставил
        /// более новую версию в %LocalAppData%\Programs\Isle — запускаем установленную и выходим.
        /// </summary>
        private static bool SwitchToInstalledCopy()
        {
            try
            {
                var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Isle", "Isle.exe");
                var me = Environment.ProcessPath ?? "";
                if (!File.Exists(installed) || string.Equals(Path.GetFullPath(installed), Path.GetFullPath(me), StringComparison.OrdinalIgnoreCase)) return false;
                var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(installed);
                if (!Version.TryParse(vi.FileVersion ?? "", out var instVer)) return false;
                var myVer = typeof(App).Assembly.GetName().Version ?? new Version(0, 0);
                if (instVer <= myVer) return false;
                Trace("запущена старая копия " + myVer + " (" + me + "), переключаюсь на установленную " + instVer, true);
                // автозапуск — на установленную версию
                try { using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); if (k.GetValue("Isle") != null) k.SetValue("Isle", "\"" + installed + "\""); } catch { }
                _single?.ReleaseMutex();
                _single?.Dispose();
                _single = null;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installed) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(installed) });
                return true;
            }
            catch { return false; }
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
            Journal.Info("trace", text);
        }

        public static void Log(string text)
        {
            try
            {
                Directory.CreateDirectory(Storage.Dir);
                File.AppendAllText(Path.Combine(Storage.Dir, "errors.log"), DateTime.Now + "  " + text + Environment.NewLine + Environment.NewLine);
            }
            catch { }
            Journal.Error("error", text);
        }
    }
}
