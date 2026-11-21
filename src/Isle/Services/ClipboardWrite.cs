using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using IComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Isle.Services
{
    /// <summary>
    /// Запись в буфер обмена с коротким ожиданием. Стандартный Clipboard.SetText/SetImage в WPF, если буфер держит
    /// другая программа, сам ждёт ~1 с (10 попыток по 100 мс), а мы вызывали его до 5 раз — интерфейс замирал на 5 секунд.
    /// Здесь пробуем напрямую через OLE каждые 40 мс, но не дольше ~0,7 с.
    /// </summary>
    public static class ClipboardWrite
    {
        [DllImport("ole32.dll")] private static extern int OleSetClipboard(IComDataObject data);
        [DllImport("ole32.dll")] private static extern int OleFlushClipboard();

        public static bool Set(DataObject data, string what, int budgetMs = 700)
        {
            var sw = Stopwatch.StartNew();
            for (int tries = 1; ; tries++)
            {
                int hr = OleSetClipboard(data);
                if (hr == 0)
                {
                    OleFlushClipboard(); // данные остаются в буфере и после выхода из Isle
                    if (tries > 1) Journal.Debug("clipboard", what + ": буфер освободился с " + tries + "-й попытки за " + sw.ElapsedMilliseconds + " мс");
                    return true;
                }
                if (sw.ElapsedMilliseconds >= budgetMs)
                {
                    if (budgetMs > 0) Journal.Debug("clipboard", what + ": буфер занят другой программой, " + tries + " попыток за " + sw.ElapsedMilliseconds + " мс (0x" + hr.ToString("X8") + ")");
                    return false;
                }
                Thread.Sleep(40);
            }
        }

        /// <summary>
        /// Короткая попытка сразу (до 0,3 с), а если буфер всё ещё занят — дописываем в фоне: ещё до 5 с, не замораживая интерфейс.
        /// Возвращает true, если записалось сразу. done(true/false) — когда стало ясно окончательно.
        /// </summary>
        public static bool SetOrLater(Func<DataObject> make, string what, Action<bool> done = null)
        {
            if (Set(make(), what, 300)) { done?.Invoke(true); return true; }
            var started = DateTime.Now;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            timer.Tick += (_, __) =>
            {
                bool ok = Set(make(), what, 0);
                if (!ok && DateTime.Now - started < TimeSpan.FromSeconds(5)) return;
                timer.Stop();
                Journal.Debug("clipboard", what + (ok ? ": записано, когда буфер освободился, через " : ": так и не записано за ") + (int)(DateTime.Now - started).TotalMilliseconds + " мс");
                done?.Invoke(ok);
            };
            timer.Start();
            return false;
        }

        public static bool Text(string text, Action<bool> done = null) => !string.IsNullOrEmpty(text) && SetOrLater(() => new DataObject(DataFormats.UnicodeText, text), "текст", done);
        public static bool Image(System.Windows.Media.Imaging.BitmapSource bmp, Action<bool> done = null) => bmp != null && SetOrLater(() => new DataObject(DataFormats.Bitmap, bmp), "картинка", done);
    }
}
