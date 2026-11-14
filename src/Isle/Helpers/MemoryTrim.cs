using System;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Isle.Services;

namespace Isle.Helpers
{
    /// <summary>
    /// Вернуть память системе после тяжёлого дела (снимок 4K — это десятки мегабайт на каждую копию картинки).
    /// .NET сам не спешит убирать большие массивы, пока памяти хватает: Isle «висел» на 400–600 МБ.
    /// Полная сборка с уплотнением больших объектов + обрезка рабочего набора — и Isle снова лёгкий.
    /// </summary>
    public static class MemoryTrim
    {
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

        private static DispatcherTimer _later;

        /// <summary>Убрать мусор через delayMs (повторные вызовы сдвигают срок — одна уборка после серии снимков).</summary>
        public static void Soon(string why, int delayMs = 3000)
        {
            _later?.Stop();
            _later = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(delayMs) };
            _later.Tick += (_, __) => { _later.Stop(); Now(why); };
            _later.Start();
        }

        public static void Now(string why)
        {
            try
            {
                long before = Environment.WorkingSet, managed = GC.GetTotalMemory(false);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
                Journal.Info("memory", "уборка после «" + why + "»: память " + (before >> 20) + " → " + (Environment.WorkingSet >> 20) + " МБ, управляемая "
                    + (managed >> 20) + " → " + (GC.GetTotalMemory(false) >> 20) + " МБ, за " + sw.ElapsedMilliseconds + " мс");
            }
            catch (Exception ex) { Journal.Warn("memory", "уборка не удалась: " + ex.Message); }
        }
    }
}
