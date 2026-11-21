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
                    Journal.Debug("clipboard", what + ": буфер занят другой программой, " + tries + " попыток за " + sw.ElapsedMilliseconds + " мс (0x" + hr.ToString("X8") + ")");
                    return false;
                }
                Thread.Sleep(40);
            }
        }

        public static bool Text(string text) => !string.IsNullOrEmpty(text) && Set(new DataObject(DataFormats.UnicodeText, text), "текст");
        public static bool Image(System.Windows.Media.Imaging.BitmapSource bmp) => bmp != null && Set(new DataObject(DataFormats.Bitmap, bmp), "картинка");
    }
}
