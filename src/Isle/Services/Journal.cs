using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace Isle.Services
{
    /// <summary>
    /// Подробный журнал работы: %AppData%\Isle\isle.log. Пишет всё — что показалось в чёлке, куда она
    /// развернулась, какие хоткеи сработали, что ответили погода/ИИ/голос/обновления, какие настройки поменялись,
    /// что делали снимки экрана, загрузки, буфер обмена и т. д. По нему можно восстановить, что было перед багом.
    /// Запись — в фоне пачками (не тормозит интерфейс). Файл > 8 МБ — уходит в isle.old.log, начинается новый.
    /// </summary>
    public static class Journal
    {
        private static readonly ConcurrentQueue<string> Queue = new();
        private static readonly Timer Flusher = new(_ => Flush(), null, 400, 400);
        private static readonly object FileLock = new();
        private static long MaxBytes => Verbose ? 32L * 1024 * 1024 : 8L * 1024 * 1024;

        /// <summary>Подробный (отладочный) журнал: пишет и строки DEBUG. Включается в Настройках → Обновления.</summary>
        public static volatile bool Verbose = true;
        /// <summary>Отладочная строка — пишется, только когда включён подробный журнал.</summary>
        public static void Debug(string area, string text) { if (Verbose) Write("DEBUG", area, text); }

        public static string PathNow => Path.Combine(Storage.Dir, "isle.log");

        public static void Info(string area, string text) => Write("INFO ", area, text);
        public static void Warn(string area, string text) => Write("WARN ", area, text);
        public static void Error(string area, string text) => Write("ERROR", area, text);
        public static void Error(string area, Exception ex) => Write("ERROR", area, ex?.ToString() ?? "null");

        private static void Write(string level, string area, string text)
        {
            try
            {
                var sb = new StringBuilder(96 + (text?.Length ?? 0));
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ').Append(level)
                  .Append(" [").Append(area).Append("] t").Append(Environment.CurrentManagedThreadId).Append("  ")
                  .Append((text ?? "").Replace("\r", "").Replace("\n", "\n        "));
                Queue.Enqueue(sb.ToString());
            }
            catch { }
        }

        /// <summary>Сбросить очередь на диск (вызывается таймером и при выходе).</summary>
        public static void Flush()
        {
            if (Queue.IsEmpty) return;
            try
            {
                lock (FileLock)
                {
                    Directory.CreateDirectory(Storage.Dir);
                    var path = PathNow;
                    try
                    {
                        var fi = new FileInfo(path);
                        if (fi.Exists && fi.Length > MaxBytes)
                        {
                            var old = Path.Combine(Storage.Dir, "isle.old.log");
                            File.Copy(path, old, true);
                            File.WriteAllText(path, "");
                        }
                    }
                    catch { }
                    var sb = new StringBuilder();
                    while (Queue.TryDequeue(out var line)) sb.Append(line).Append(Environment.NewLine);
                    File.AppendAllText(path, sb.ToString());
                }
            }
            catch { }
        }

        /// <summary>Короткое описание, чтобы не тащить в журнал огромные тексты.</summary>
        public static string Short(string s, int max = 160)
        {
            if (string.IsNullOrEmpty(s)) return "«»";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return "«" + (s.Length > max ? s.Substring(0, max) + "…(" + s.Length + ")" : s) + "»";
        }
    }
}
