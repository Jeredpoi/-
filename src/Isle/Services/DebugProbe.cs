using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Isle.Services
{
    /// <summary>
    /// Подробный журнал (временная отладка): всё, что помогает понять лаги и баги по одному файлу.
    /// — мониторы (разрешение, масштаб, Гц) и видеокарты при запуске;
    /// — зависания потока интерфейса (сторож раз в 50 мс: если тик опоздал больше чем на 120 мс — пишем);
    /// — раз в 30 с: процессор и память Isle, кадры общего такта анимаций, как рисуется чёлка, что на переднем плане;
    /// — все исключения, даже пойманные (не больше 30 в минуту, без текста буфера обмена и ключей).
    /// Ничего личного: ни содержимого буфера, ни нажатых клавиш, ни паролей.
    /// </summary>
    public static class DebugProbe
    {
        private static DispatcherTimer _watchdog, _stats;
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static double _lastTick;
        private static TimeSpan _lastCpu;
        private static double _lastCpuAt;
        private static int _exInMinute; private static long _exMinute;
        private static bool _started;
        [ThreadStatic] private static bool _inHandler;

        public static void Start()
        {
            if (!Journal.Verbose || _started) return;
            _started = true;
            Journal.Info("debug", "подробный журнал включён (временная отладка) — его можно выключить в Настройках → Обновления");
            LogDisplays();

            // сторож потока интерфейса
            _lastTick = Clock.Elapsed.TotalMilliseconds;
            _watchdog = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(50) };
            _watchdog.Tick += (_, __) =>
            {
                double now = Clock.Elapsed.TotalMilliseconds, late = now - _lastTick - 50;
                _lastTick = now;
                if (Journal.Verbose && late > 120) Journal.Debug("ui", "поток интерфейса был занят ~" + Math.Round(late) + " мс" + Context());
            };
            _watchdog.Start();

            // сводка раз в 30 с
            using (var p = Process.GetCurrentProcess()) { _lastCpu = p.TotalProcessorTime; _lastCpuAt = Clock.Elapsed.TotalSeconds; }
            Helpers.FrameClock.TakeStats();
            _stats = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
            _stats.Tick += (_, __) => { if (Journal.Verbose) Stats(); };
            _stats.Start();

            // все исключения, даже пойманные: по ним видно, что «тихо» не сработало
            AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
            {
                if (_inHandler) return; // исключение внутри самого обработчика не должно зациклить его
                _inHandler = true;
                try
                {
                    if (!Journal.Verbose) return;
                    var ex = e.Exception;
                    if (ex is OperationCanceledException || ex is ThreadAbortException) return;
                    long minute = Environment.TickCount64 / 60000;
                    if (minute != _exMinute) { _exMinute = minute; _exInMinute = 0; }
                    if (++_exInMinute > 30) return;
                    var st = new StackTrace(ex, false);
                    var where = st.FrameCount > 0 ? st.GetFrame(0)?.GetMethod() : ex.TargetSite;
                    Journal.Debug("exception", ex.GetType().Name + ": " + Journal.Short(ex.Message, 200)
                        + (where != null ? " @ " + where.DeclaringType?.Name + "." + where.Name : "") + " t" + Environment.CurrentManagedThreadId);
                }
                catch { }
                finally { _inHandler = false; }
            };
        }

        private static string Context()
        {
            try
            {
                var w = AppHost.Window;
                return w == null ? "" : " (чёлка " + w.State + ", " + (w.SoftRender ? "процессор" : "видеокарта") + ")";
            }
            catch { return ""; }
        }

        private static void Stats()
        {
            try
            {
                using var p = Process.GetCurrentProcess();
                double now = Clock.Elapsed.TotalSeconds;
                double cpu = (p.TotalProcessorTime - _lastCpu).TotalSeconds / Math.Max(0.001, now - _lastCpuAt) / Environment.ProcessorCount * 100;
                _lastCpu = p.TotalProcessorTime; _lastCpuAt = now;
                var (fps, worst, running) = Helpers.FrameClock.TakeStats();
                string fg = "";
                try
                {
                    var h = Native.Win32.GetForegroundWindow();
                    Native.Win32.GetWindowThreadProcessId(h, out uint pid);
                    using var fp = Process.GetProcessById((int)pid);
                    fg = fp.ProcessName;
                }
                catch { }
                Journal.Debug("stats", "процессор Isle " + Math.Round(cpu, 1) + "%, память " + (p.WorkingSet64 >> 20) + " МБ (управляемая " + (GC.GetTotalMemory(false) >> 20)
                    + " МБ, сборок " + GC.CollectionCount(0) + "/" + GC.CollectionCount(1) + "/" + GC.CollectionCount(2) + "), потоков " + p.Threads.Count
                    + "; такт анимаций " + (running ? Math.Round(fps) + " к/с, худший кадр " + Math.Round(worst) + " мс" : "стоит")
                    + ", ограничение " + (Helpers.FrameClock.MaxFps == 0 ? "как у монитора" : Helpers.FrameClock.MaxFps + " к/с")
                    + Context() + ", полноэкранное " + (AppHost.Watchers?.IsFullscreen ?? false) + ", впереди " + fg);
            }
            catch (Exception ex) { Journal.Debug("stats", "сводка не собралась: " + ex.Message); }
        }

        // ---------- мониторы и видеокарты ----------

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string device, int i, ref DISPLAY_DEVICE dd, int flags);

        private static void LogDisplays()
        {
            try
            {
                var sb = new StringBuilder();
                var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                for (int i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++, dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>())
                {
                    if ((dd.StateFlags & 1) == 0) continue; // не подключён к рабочему столу
                    sb.Append(i > 0 ? "; " : "").Append(dd.DeviceName).Append(" — ").Append(dd.DeviceString)
                      .Append((dd.StateFlags & 4) != 0 ? " (основной)" : "");
                }
                Journal.Debug("display", "выходы: " + sb);
                foreach (var s in System.Windows.Forms.Screen.AllScreens)
                {
                    int hz = Helpers.Display.RefreshHz(s.Bounds.X + s.Bounds.Width / 2, s.Bounds.Y + s.Bounds.Height / 2);
                    Journal.Debug("display", "монитор " + s.DeviceName + (s.Primary ? " (основной)" : "") + ": " + s.Bounds.Width + "×" + s.Bounds.Height
                        + " в " + s.Bounds.X + "," + s.Bounds.Y + ", рабочая область " + s.WorkingArea.Width + "×" + s.WorkingArea.Height + ", " + hz + " Гц, " + s.BitsPerPixel + " бит");
                }
                Journal.Debug("display", "WPF: tier " + (System.Windows.Media.RenderCapability.Tier >> 16) + ", аппаратная отрисовка "
                    + (System.Windows.Media.RenderCapability.IsPixelShaderVersionSupported(2, 0) ? "есть" : "нет")
                    + ", макс. текстура " + System.Windows.Media.RenderCapability.MaxHardwareTextureSize);
            }
            catch (Exception ex) { Journal.Debug("display", "не удалось: " + ex.Message); }
        }
    }
}
