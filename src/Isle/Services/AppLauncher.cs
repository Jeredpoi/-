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
        };

        public static bool Open(string what) => Launch(what, null);

        /// <summary>Запуск с аргументами (например, OBS с --startrecording).</summary>
        public static bool Launch(string what, string extraArgs)
        {
            what = (what ?? "").Trim().Trim('"', '«', '»', '.');
            if (what.Length == 0) return false;
            var key = Aliases.TryGetValue(what, out var alias) ? alias : what;

            // If VK is already the active tab in a browser window, switch to that window
            // instead of asking the default browser to create another tab.
            if ((key.Equals("https://vk.com", StringComparison.OrdinalIgnoreCase) || key.Equals("http://vk.com", StringComparison.OrdinalIgnoreCase)) && FocusExistingVk()) return true;

            // сайты и протоколы
            if (key.Contains("://") || (key.EndsWith(":") && !key.Contains('\\')) || key.StartsWith("ms-"))
                return Start(key, null);
            if (!key.Contains(' ') && key.Contains('.') && !File.Exists(key) && !key.EndsWith(".exe"))
                return Start("https://" + key, null);
            // путь к файлу/папке
            if (File.Exists(key) || Directory.Exists(key)) return Start(key, extraArgs);

            var exe = Find(key);
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
                            if (File.Exists(icon)) return icon;
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
                { "chrome", "msedge", "firefox", "opera", "opera_gx", "browser", "yandex", "vivaldi" };
            IntPtr found = IntPtr.Zero;
            Win32.EnumWindows((hwnd, _) =>
            {
                if (!Win32.IsWindowVisible(hwnd)) return true;
                Win32.GetWindowThreadProcessId(hwnd, out uint pid);
                try
                {
                    var process = Process.GetProcessById((int)pid);
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
            var n = name.ToLowerInvariant();
            string best = null; int bestScore = int.MaxValue;
            foreach (var d in dirs.Where(Directory.Exists))
            {
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(d, "*.lnk", SearchOption.AllDirectories).ToList(); } catch { continue; }
                foreach (var f in files)
                {
                    var t = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                    if (t.Contains("uninstall") || t.Contains("удал")) continue;
                    int score = t == n ? 0 : t.StartsWith(n) ? 1 + t.Length - n.Length : t.Contains(n) ? 50 + t.Length : int.MaxValue;
                    if (score < bestScore) { bestScore = score; best = f; }
                }
            }
            return best;
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
