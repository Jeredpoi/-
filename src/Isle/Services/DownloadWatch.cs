using System;
using System.Collections.Generic;
using System.IO;

namespace Isle.Services
{
    /// <summary>
    /// Следит за файлами, в которые Steam и браузеры пишут о загрузках, и сообщает об изменении сразу —
    /// вместо того чтобы постоянно перечитывать их по таймеру. Файлы не читаются здесь, только «что-то поменялось».
    /// </summary>
    public static class DownloadWatch
    {
        private static readonly List<FileSystemWatcher> Watchers = new();

        /// <summary>Что-то изменилось (приходит из фонового потока).</summary>
        public static event Action Changed;

        public static void Start()
        {
            Stop();
            var targets = new List<(string Dir, string Filter)>();
            try { targets.AddRange(SteamDownloadService.WatchTargets()); } catch { }
            try { targets.AddRange(BrowserDownloadService.WatchTargets()); } catch { }
            foreach (var (dir, filter) in targets)
            {
                try
                {
                    var w = new FileSystemWatcher(dir, filter)
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                        IncludeSubdirectories = false,
                        InternalBufferSize = 16384
                    };
                    w.Changed += (_, __) => Changed?.Invoke();
                    w.Created += (_, __) => Changed?.Invoke();
                    w.Renamed += (_, __) => Changed?.Invoke();
                    w.Error += (_, __) => Changed?.Invoke();
                    w.EnableRaisingEvents = true;
                    Watchers.Add(w);
                }
                catch (Exception ex) { App.Log("download watch " + dir + ": " + ex.Message); }
            }
        }

        public static void Stop()
        {
            foreach (var w in Watchers) { try { w.Dispose(); } catch { } }
            Watchers.Clear();
        }
    }
}
