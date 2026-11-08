using System;
using System.IO;

namespace Isle.Services
{
    /// <summary>
    /// Подробный журнал загрузок Steam: steam-download.log в папке данных Isle.
    /// Всё, что Isle получил от Steam (сырая сводка), что показал в чёлке и почему не подключился.
    /// Больше 5 МБ — старый журнал переименовывается в steam-download.old.log.
    /// </summary>
    public static class Diag
    {
        private static readonly object Lock = new();

        public static string PathToLog => Path.Combine(Storage.Dir, "steam-download.log");

        public static void Write(string text)
        {
            try
            {
                lock (Lock)
                {
                    Directory.CreateDirectory(Storage.Dir);
                    var path = PathToLog;
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 5_000_000)
                        File.Move(path, Path.Combine(Storage.Dir, "steam-download.old.log"), true);
                    File.AppendAllText(path, DateTime.Now.ToString("dd.MM HH:mm:ss.fff") + "  " + text + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}
