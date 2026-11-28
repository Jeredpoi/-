using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Isle.Services;

namespace Isle
{
    public static class Bench
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadKeyboardLayout(string id, uint flags);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern int GetKeyboardLayoutList(int n, IntPtr[] list);

        private static object Call(object o, string name, params object[] args) =>
            o.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.Invoke(o, args);

        private static string Lang(IntPtr hwnd) => ((int)((long)Win32.LayoutOf(Win32.FocusWindowOf(hwnd)) & 0xFFFF)).ToString("X4");

        private static async Task<IntPtr> StartAndWait(string exe, string args, StringBuilder log)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
                for (int i = 0; i < 60; i++)
                {
                    await Task.Delay(250);
                    var fg = GetForegroundWindow();
                    Win32.GetWindowThreadProcessId(fg, out uint pid);
                    var name = "";
                    try { name = Process.GetProcessById((int)pid).ProcessName; } catch { }
                    if (name.Equals(Path.GetFileNameWithoutExtension(exe), StringComparison.OrdinalIgnoreCase)) { await Task.Delay(1500); return GetForegroundWindow(); }
                }
                log.AppendLine("не дождались окна " + exe);
            }
            catch (Exception ex) { log.AppendLine("старт " + exe + ": " + ex.Message); }
            return IntPtr.Zero;
        }

        private static async Task Case(MainWindow w, IntPtr app, string title, bool islandActive, StringBuilder log)
        {
            SetForegroundWindow(app);
            await Task.Delay(600);
            string before = Lang(app);
            if (islandActive) { w.Activate(); await Task.Delay(500); }
            log.AppendLine($"[{title}] до: {before}, активное окно — {(GetForegroundWindow() == app ? "приложение" : "Isle/другое")}");
            Call(w, "OnLangClick", null, new RoutedEventArgs());
            await Task.Delay(1800);
            string after = Lang(app);
            log.AppendLine($"[{title}] сразу после: {after}, активное — {(GetForegroundWindow() == app ? "приложение" : "Isle/другое")}");
            // «откатывается ли»: уходим в Isle и возвращаемся в окно
            w.Activate(); await Task.Delay(500);
            SetForegroundWindow(app); await Task.Delay(800);
            string later = Lang(app);
            log.AppendLine($"[{title}] после возврата в окно: {later} → {(later != before && after != before ? "OK" : "НЕ ПЕРЕКЛЮЧИЛОСЬ/ОТКАТ")}");
        }

        public static async Task RunAsync(MainWindow w, string dir)
        {
            Directory.CreateDirectory(dir);
            var log = new StringBuilder();
            try
            {
                AppSettings.Current.WelcomeShown = true;
                AppSettings.Current.AutoUpdateCheck = false;
                LoadKeyboardLayout("00000409", 0);
                LoadKeyboardLayout("00000419", 0);
                int n = GetKeyboardLayoutList(0, null); var list = new IntPtr[n]; GetKeyboardLayoutList(n, list);
                log.AppendLine("раскладки: " + string.Join(", ", Array.ConvertAll(list, h => ((long)h & 0xFFFF).ToString("X4"))));
                log.AppendLine("hotkey включен: " + Win32.LayoutHotkeyEnabled());
                await Task.Delay(1500);

                var np = await StartAndWait("notepad.exe", "", log);
                if (np != IntPtr.Zero)
                {
                    await Case(w, np, "Блокнот, активен Блокнот", false, log);
                    await Case(w, np, "Блокнот, активен Isle", true, log);
                }
                var chromeExe = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
                if (File.Exists(chromeExe))
                {
                    var tmp = Path.Combine(Path.GetTempPath(), "chr" + Environment.TickCount);
                    var ch = await StartAndWait(chromeExe, $"--user-data-dir=\"{tmp}\" --no-first-run --no-default-browser-check --new-window data:text/html,<input autofocus>", log);
                    if (ch != IntPtr.Zero)
                    {
                        await Task.Delay(2000);
                        await Case(w, ch, "Chrome, активен Chrome", false, log);
                        await Case(w, ch, "Chrome, активен Isle", true, log);
                    }
                }
                else log.AppendLine("Chrome не найден");
            }
            catch (Exception ex) { log.AppendLine("ОШИБКА " + ex); }
            File.WriteAllText(Path.Combine(dir, "layout.txt"), log.ToString(), Encoding.UTF8);
            try
            {
                var j = Journal.PathNow;
                if (File.Exists(j)) File.Copy(j, Path.Combine(dir, "isle.log"), true);
            }
            catch { }
            Application.Current.Shutdown();
        }
    }
}
