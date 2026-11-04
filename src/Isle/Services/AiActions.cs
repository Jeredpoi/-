using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Isle.Services
{
    public class AiAction
    {
        public string Name;
        public string Arg;
        /// <summary>Опасные действия выполняются только после нажатия «Выполнить».</summary>
        public bool NeedsConfirm => Name == "cmd" || Name == "lock" || Name == "sleep";
        public string Label => Name switch
        {
            "open" => "Открыть " + Arg,
            "search" => "Найти в интернете: " + Arg,
            "timer" => "Таймер на " + Arg,
            "remind" => "Напоминание: " + Arg,
            "volume" => "Громкость " + Arg + "%",
            "media" => Arg switch { "next" => "Следующий трек", "prev" => "Предыдущий трек", _ => "Музыка: пауза / играть" },
            "copy" => "Скопировать в буфер",
            "screenshot" => "Снимок экрана",
            "lock" => "Заблокировать компьютер",
            "sleep" => "Перевести в сон",
            "cmd" => "Выполнить в консоли: " + Arg,
            _ => Name + " " + Arg
        };
    }

    /// <summary>
    /// ИИ может управлять компьютером: в ответе он пишет строки вида [[open: notepad]],
    /// Isle их выполняет. Безопасные — сразу, опасные (консоль, блокировка, сон) — по кнопке.
    /// </summary>
    public static class AiActions
    {
        public const string Prompt =
            "\n\nТы можешь управлять компьютером пользователя (Windows). Когда пользователь просит что-то СДЕЛАТЬ, вставь в ответ " +
            "отдельные строки-команды в двойных квадратных скобках, а рядом коротко скажи, что делаешь. Команды:\n" +
            "[[open: что открыть]] — сайт, файл, папку или программу ПРОСТЫМ названием, Isle сам найдёт её в меню «Пуск» " +
            "(например [[open: калькулятор]], [[open: obs]], [[open: telegram]], [[open: блокнот]], [[open: https://youtube.com]]). " +
            "Не придумывай пути и не открывай магазин приложений;\n" +
            "[[search: запрос]] — поиск в интернете;\n" +
            "[[timer: 10m]] — таймер (s — секунды, m — минуты, h — часы);\n" +
            "[[remind: позвонить маме завтра в 9]] — напоминание обычной фразой;\n" +
            "[[volume: 0-100]] — громкость;\n" +
            "[[media: play | pause | next | prev]] — музыка;\n" +
            "[[copy: текст]] — положить текст в буфер обмена;\n" +
            "[[screenshot]] — снимок экрана;\n" +
            "[[lock]], [[sleep]] — блокировка и сон (выполнятся после подтверждения пользователем);\n" +
            "[[cmd: команда cmd.exe]] — команда в консоли (только после подтверждения пользователем; не предлагай удаляющих или опасных команд).\n" +
            "Не пиши команды, если пользователь просто спрашивает.";

        private static readonly Regex Rx = new(@"\[\[\s*(\w+)\s*(?::\s*([^\]]*?))?\s*\]\]", RegexOptions.Compiled);
        private static readonly HashSet<string> Known = new() { "open", "search", "timer", "remind", "volume", "media", "copy", "screenshot", "lock", "sleep", "cmd" };

        /// <summary>Достаёт команды из ответа и возвращает текст без них.</summary>
        public static (string text, List<AiAction> actions) Extract(string answer)
        {
            var list = new List<AiAction>();
            var text = Rx.Replace(answer, m =>
            {
                var name = m.Groups[1].Value.ToLowerInvariant();
                if (!Known.Contains(name)) return m.Value;
                list.Add(new AiAction { Name = name, Arg = m.Groups[2].Success ? m.Groups[2].Value.Trim() : "" });
                return "";
            });
            text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
            return (text, list);
        }

        /// <summary>Выполняет действие и возвращает короткий отчёт для чата.</summary>
        public static string Run(AiAction a)
        {
            try
            {
                switch (a.Name)
                {
                    case "open":
                        return AppLauncher.Open(a.Arg) ? "✓ Открыл " + a.Arg : "✗ Не нашёл «" + a.Arg + "» — проверь, что программа установлена";
                    case "search":
                        SystemControls.Shell("https://www.google.com/search?q=" + Uri.EscapeDataString(a.Arg));
                        return "✓ Ищу: " + a.Arg;
                    case "timer":
                    {
                        var m = Regex.Match(a.Arg.ToLowerInvariant(), @"(\d+(?:[.,]\d+)?)\s*(s|с|m|м|h|ч)?");
                        if (!m.Success) return "✗ Не понял время таймера";
                        double n = double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                        var u = m.Groups[2].Value;
                        var span = u == "s" || u == "с" ? TimeSpan.FromSeconds(n) : u == "h" || u == "ч" ? TimeSpan.FromHours(n) : TimeSpan.FromMinutes(n);
                        AppHost.Timer.StartNew(span);
                        return "✓ Таймер на " + TimerService.Format(span);
                    }
                    case "remind":
                    {
                        var r = AppHost.Reminders.Add(a.Arg);
                        return r == null ? "✗ Не понял, когда напомнить" : "✓ Напомню " + r.WhenText + ": " + r.Text;
                    }
                    case "volume":
                        if (!int.TryParse(Regex.Match(a.Arg, @"\d+").Value, out var v)) return "✗ Не понял громкость";
                        AppHost.Audio.Volume = Math.Clamp(v, 0, 100) / 100f;
                        return "✓ Громкость " + Math.Clamp(v, 0, 100) + "%";
                    case "media":
                        if (a.Arg.StartsWith("next")) AppHost.Media.Next();
                        else if (a.Arg.StartsWith("prev")) AppHost.Media.Previous();
                        else AppHost.Media.PlayPause();
                        return "✓ " + a.Label;
                    case "copy":
                        ClipboardService.SetText(a.Arg);
                        return "✓ Скопировал в буфер";
                    case "screenshot":
                        _ = Tools.FullShot();
                        return "✓ Снимок экрана в буфере";
                    case "lock":
                        SystemControls.Lock();
                        return "✓ Заблокировал";
                    case "sleep":
                        SystemControls.Sleep();
                        return "✓ Сон";
                    case "cmd":
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/k " + a.Arg) { UseShellExecute = true });
                        return "✓ Запустил в консоли: " + a.Arg;
                }
            }
            catch (Exception ex) { return "✗ " + ex.Message; }
            return "✗ Неизвестная команда " + a.Name;
        }
    }
}
