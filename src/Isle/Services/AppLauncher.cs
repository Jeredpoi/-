using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;
using Isle.Native;

namespace Isle.Services
{
    /// <summary>
    /// Находит и запускает программы по-человечески: «калькулятор», «OBS», «телеграм», «блокнот».
    /// Ищет в известных названиях, ярлыках меню «Пуск», реестре App Paths и типичных папках.
    /// </summary>
    public static class AppLauncher
    {
        private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["калькулятор"] = "calc", ["calculator"] = "calc", ["calc"] = "calc",
            ["блокнот"] = "notepad", ["notepad"] = "notepad",
            ["paint"] = "mspaint", ["пэинт"] = "mspaint", ["паинт"] = "mspaint",
            ["проводник"] = "explorer", ["explorer"] = "explorer",
            ["диспетчер задач"] = "taskmgr", ["task manager"] = "taskmgr",
            ["командная строка"] = "cmd", ["консоль"] = "cmd", ["терминал"] = "wt",
            ["параметры"] = "ms-settings:", ["настройки windows"] = "ms-settings:", ["settings"] = "ms-settings:",
            ["панель управления"] = "control", ["ножницы"] = "ms-screenclip:", ["snipping tool"] = "ms-screenclip:",
            ["камера"] = "microsoft.windows.camera:", ["часы"] = "ms-clock:", ["будильник"] = "ms-clock:",
            ["почта"] = "outlookmail:", ["магазин"] = "ms-windows-store:", ["microsoft store"] = "ms-windows-store:",
            ["word"] = "winword", ["ворд"] = "winword", ["excel"] = "excel", ["эксель"] = "excel", ["powerpoint"] = "powerpnt",
            ["obs"] = "obs", ["обс"] = "obs", ["obs studio"] = "obs",
            ["телеграм"] = "telegram", ["telegram"] = "telegram", ["дискорд"] = "discord", ["discord"] = "discord",
            ["спотифай"] = "spotify", ["spotify"] = "spotify", ["стим"] = "steam", ["steam"] = "steam",
            ["хром"] = "chrome", ["chrome"] = "chrome", ["гугл хром"] = "chrome", ["edge"] = "msedge", ["эдж"] = "msedge",
            ["comet"] = "comet", ["комет"] = "comet", ["perplexity"] = "comet", ["перплексити"] = "comet",
            ["браузер"] = "default-browser", ["веб-браузер"] = "default-browser", ["интернет"] = "default-browser",
            ["web browser"] = "default-browser", ["мой браузер"] = "default-browser",
            ["яндекс"] = "browser", ["яндекс браузер"] = "browser", ["firefox"] = "firefox", ["фаерфокс"] = "firefox",
            ["вконтакте"] = "https://vk.com", ["вк"] = "https://vk.com", ["vk"] = "https://vk.com", ["vk.com"] = "https://vk.com",
            ["ютуб"] = "https://youtube.com", ["youtube"] = "https://youtube.com",
        };

        /// <summary>Специальные места установки, которые не всегда видны через меню «Пуск».</summary>
        private static readonly Dictionary<string, string[]> KnownPaths = new()
        {
            ["obs"] = new[] { @"%ProgramFiles%\obs-studio\bin\64bit\obs64.exe", @"%ProgramFiles(x86)%\obs-studio\bin\64bit\obs64.exe" },
            ["telegram"] = new[] { @"%AppData%\Telegram Desktop\Telegram.exe", @"%LocalAppData%\Telegram Desktop\Telegram.exe" },
            ["discord"] = new[] { @"%LocalAppData%\Discord\Update.exe|--processStart Discord.exe" },
            ["spotify"] = new[] { @"%AppData%\Spotify\Spotify.exe", "spotify:" },
            ["steam"] = new[] { @"%ProgramFiles(x86)%\Steam\steam.exe" },
            ["browser"] = new[] { @"%LocalAppData%\Yandex\YandexBrowser\Application\browser.exe" },
            ["comet"] = new[] { @"%LocalAppData%\Perplexity\Comet\Application\comet.exe", @"%LocalAppData%\Comet\Application\comet.exe" },
        };

        public static bool Open(string what) => Launch(what, null);

        /// <summary>Известное простое название («блокнот», «ютуб») — открывается без подтверждения.</summary>
        public static bool IsAlias(string what) => Aliases.ContainsKey((what ?? "").Trim());

        /// <summary>Запуск с аргументами (например, OBS с --startrecording).</summary>
        public static bool Launch(string what, string extraArgs)
        {
            what = (what ?? "").Trim().Trim('"', '«', '»', '.');
            if (what.Length == 0) return false;
            var key = Aliases.TryGetValue(what, out var alias) ? alias : what;

            // If VK is already the active tab in a browser window, switch to that window
            // instead of asking the default browser to create another tab.
            if ((key.Equals("https://vk.com", StringComparison.OrdinalIgnoreCase) || key.Equals("http://vk.com", StringComparison.OrdinalIgnoreCase)) && FocusExistingVk()) return true;

            // «открой браузер» — браузер по умолчанию (Chrome, Яндекс, Comet…), а не случайный
            if (key == "default-browser")
            {
                var browser = DefaultBrowserExe();
                if (browser != null && FocusRunning(browser)) return true;
                return browser != null ? Start(browser, null) : Start("https://www.google.com", null);
            }

            // сайты и протоколы
            if (key.Contains("://") || (key.EndsWith(":") && !key.Contains('\\')) || key.StartsWith("ms-"))
                return Start(key, null);
            if (!key.Contains(' ') && key.Contains('.') && !File.Exists(key) && !key.EndsWith(".exe"))
                return Start("https://" + key, null);
            // путь к файлу/папке
            if (File.Exists(key) || Directory.Exists(key)) return Start(key, extraArgs);

            var exe = Find(key);
            // программа уже запущена (OBS, Telegram, Discord…) — просто показываем её окно, а не второй экземпляр
            if (exe != null && string.IsNullOrEmpty(extraArgs) && !exe.Contains('|') && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && FocusRunning(exe)) return true;
            if (exe != null) return Start(exe, extraArgs);
            // системные команды (calc, notepad, mspaint…) находятся через PATH
            return Start(key, extraArgs);
        }

        public static string Find(string name)
        {
            name = Aliases.TryGetValue(name, out var a) ? a : name;
            if (KnownPaths.TryGetValue(name.ToLowerInvariant(), out var paths))
                foreach (var p in paths)
                {
                    var file = Environment.ExpandEnvironmentVariables(p.Split('|')[0]);
                    if (File.Exists(file)) return p.Contains('|') ? file + "|" + p.Split('|')[1] : file;
                    if (p.EndsWith(":")) return p;
                }
            // App Paths в реестре
            foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using var k = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + (name.EndsWith(".exe") ? name : name + ".exe"));
                    if (k?.GetValue(null) is string v && File.Exists(v.Trim('"'))) return v.Trim('"');
                }
                catch { }
            }
            // OBS из Steam: ...\steamapps\common\OBS Studio\bin\64bit\obs64.exe в любой библиотеке Steam
            if (name.Equals("obs", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    foreach (var lib in SteamDownloadService.SteamLibraries())
                    {
                        var steamObs = Path.Combine(lib, "steamapps", "common", "OBS Studio", "bin", "64bit", "obs64.exe");
                        if (File.Exists(steamObs)) return steamObs;
                    }
                }
                catch { }
            }
            // ярлыки меню «Пуск»
            var lnk = FindShortcut(name);
            if (lnk != null) return lnk;
            var uninstall = FindInstalledApplication(name);
            if (uninstall != null) return uninstall;
            return null;
        }

        private static string FindInstalledApplication(string name)
        {
            var needles = new List<string> { name };
            if (name.Equals("obs", StringComparison.OrdinalIgnoreCase))
                needles.AddRange(new[] { "obs studio", "open broadcaster software" });
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstall = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                    foreach (var subName in uninstall?.GetSubKeyNames() ?? Array.Empty<string>())
                    {
                        using var app = uninstall.OpenSubKey(subName);
                        var display = app?.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(display) || !needles.Any(n => display.Contains(n, StringComparison.OrdinalIgnoreCase))) continue;
                        var icon = app.GetValue("DisplayIcon") as string;
                        if (!string.IsNullOrWhiteSpace(icon))
                        {
                            icon = Environment.ExpandEnvironmentVariables(icon.Trim().Trim('"'));
                            int comma = icon.LastIndexOf(',');
                            if (comma > 2 && int.TryParse(icon.Substring(comma + 1), out _)) icon = icon.Substring(0, comma).Trim('"');
                            // DisplayIcon часто указывает на деинсталлятор или .ico — такое не запускаем
                            var iconName = Path.GetFileName(icon).ToLowerInvariant();
                            if (File.Exists(icon) && iconName.EndsWith(".exe") && !iconName.Contains("unins") && !iconName.Contains("uninstall")
                                && !iconName.Contains("setup") && !iconName.Contains("update")) return icon;
                        }
                        var install = app.GetValue("InstallLocation") as string;
                        if (string.IsNullOrWhiteSpace(install) || !Directory.Exists(install)) continue;
                        var direct = Directory.EnumerateFiles(install, "*.exe", SearchOption.TopDirectoryOnly)
                            .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Contains(name, StringComparison.OrdinalIgnoreCase));
                        if (direct != null) return direct;
                        // OBS has a stable subfolder layout even when the user selected a custom root.
                        if (name.Equals("obs", StringComparison.OrdinalIgnoreCase))
                        {
                            var obs = Directory.EnumerateFiles(install, "obs64.exe", SearchOption.AllDirectories).FirstOrDefault();
                            if (obs != null) return obs;
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        private static bool FocusExistingVk()
        {
            var browsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "chrome", "msedge", "firefox", "opera", "opera_gx", "browser", "yandex", "vivaldi", "comet" };
            IntPtr found = IntPtr.Zero;
            Win32.EnumWindows((hwnd, _) =>
            {
                if (!Win32.IsWindowVisible(hwnd)) return true;
                Win32.GetWindowThreadProcessId(hwnd, out uint pid);
                try
                {
                    using var process = Process.GetProcessById((int)pid);
                    if (!browsers.Contains(process.ProcessName)) return true;
                    var title = new StringBuilder(512);
                    Win32.GetWindowText(hwnd, title, title.Capacity);
                    var text = title.ToString();
                    if (text.Contains("ВКонтакте", StringComparison.OrdinalIgnoreCase) || text.Contains("vk.com", StringComparison.OrdinalIgnoreCase)
                        || text.StartsWith("VK", StringComparison.OrdinalIgnoreCase) || text.Contains(" - VK", StringComparison.OrdinalIgnoreCase))
                    { found = hwnd; return false; }
                }
                catch { }
                return true;
            }, IntPtr.Zero);
            if (found == IntPtr.Zero) return false;
            Win32.ShowWindow(found, 9);
            Win32.ForceForeground(found);
            return Win32.GetForegroundWindow() == found;
        }

        private static string FindShortcut(string name)
        {
            var dirs = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            };
            var n = NormalizeAppName(name);
            string best = null; int bestScore = int.MaxValue;
            foreach (var d in dirs.Where(Directory.Exists))
            {
                IEnumerable<string> files;
                // .lnk — обычные ярлыки, .url — ярлыки Steam и интернет-ярлыки (OBS из Steam, игры)
                try { files = Directory.EnumerateFiles(d, "*.*", SearchOption.AllDirectories)
                        .Where(f => f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".url", StringComparison.OrdinalIgnoreCase)).ToList(); }
                catch { continue; }
                foreach (var f in files)
                {
                    var rawTitle = Path.GetFileNameWithoutExtension(f);
                    var t = NormalizeAppName(rawTitle);
                    if (t.Contains("uninstall") || t.Contains("удал")) continue;
                    int score = t == n ? 0 : t.StartsWith(n) ? 1 + t.Length - n.Length : t.Contains(n) ? 50 + t.Length : int.MaxValue;
                    // Handle small spelling/voice-recognition errors without launching
                    // an unrelated app: fuzzy matches must be close and unambiguous.
                    if (score == int.MaxValue && n.Length >= 4)
                    {
                        int distance = EditDistance(n, t);
                        int limit = Math.Max(1, Math.Min(2, n.Length / 5));
                        if (distance <= limit) score = 100 + distance * 10 + Math.Abs(t.Length - n.Length);
                    }
                    if (score < bestScore) { bestScore = score; best = f; }
                }
            }
            return bestScore < 100 || (bestScore >= 100 && bestScore < 130) ? best : null;
        }

        private static string NormalizeAppName(string value)
        {
            var chars = (value ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray();
            return new string(chars);
        }

        private static int EditDistance(string a, string b)
        {
            if (a.Length > b.Length) (a, b) = (b, a);
            var previous = Enumerable.Range(0, a.Length + 1).ToArray();
            var current = new int[a.Length + 1];
            for (int j = 1; j <= b.Length; j++)
            {
                current[0] = j;
                for (int i = 1; i <= a.Length; i++)
                    current[i] = Math.Min(Math.Min(current[i - 1] + 1, previous[i] + 1), previous[i - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                (previous, current) = (current, previous);
            }
            return previous[a.Length];
        }

        /// <summary>Путь к браузеру по умолчанию из настроек Windows (UserChoice для http → команда открытия).</summary>
        public static string DefaultBrowserExe()
        {
            try
            {
                using var choice = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice")
                                   ?? Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice");
                var progId = choice?.GetValue("ProgId") as string;
                if (string.IsNullOrEmpty(progId)) return null;
                using var cmd = Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command");
                var line = cmd?.GetValue(null) as string;
                if (string.IsNullOrWhiteSpace(line)) return null;
                line = line.Trim();
                string exe = line.StartsWith("\"") ? line.Substring(1, line.IndexOf('"', 1) - 1) : line.Split(' ')[0];
                exe = Environment.ExpandEnvironmentVariables(exe);
                return File.Exists(exe) ? exe : null;
            }
            catch { return null; }
        }

        /// <summary>Если программа с этим exe уже запущена и у неё есть окно — вывести его вперёд.</summary>
        private static bool FocusRunning(string exe)
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(exe);
                var procs = Process.GetProcessesByName(name);
                try
                {
                    foreach (var p in procs)
                    {
                        var h = p.MainWindowHandle;
                        if (h == IntPtr.Zero) continue;
                        Win32.ShowWindow(h, 9); // SW_RESTORE — если свёрнуто
                        Win32.ForceForeground(h);
                        return true;
                    }
                }
                finally { foreach (var p in procs) p.Dispose(); }
            }
            catch { }
            return false;
        }

        private static bool Start(string target, string args)
        {
            try
            {
                string file = target, a = args;
                if (target.Contains('|')) { var parts = target.Split('|'); file = parts[0]; a = (parts[1] + " " + args).Trim(); }
                var psi = new ProcessStartInfo(file, a ?? "") { UseShellExecute = true };
                if (File.Exists(file)) psi.WorkingDirectory = Path.GetDirectoryName(file); // OBS без этого не стартует
                Process.Start(psi);
                return true;
            }
            catch { return false; }
        }
    }
}
