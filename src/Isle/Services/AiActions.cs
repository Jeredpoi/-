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
        public bool NeedsConfirm => Name == "cmd" || Name == "lock" || Name == "sleep" || (Name == "open" && IsRunnableFile(Arg));

        /// <summary>
        /// Сразу открываем только сайты (http/https) и программы простым названием («блокнот», «obs»).
        /// Всё остальное — путь к файлу, file://, любой другой протокол — только после «Выполнить»:
        /// ответ ИИ мог прийти из чужого текста, который его попросили обработать.
        /// </summary>
        private static bool IsRunnableFile(string arg)
        {
            var p = (arg ?? "").Trim().Trim('"', '«', '»', '.');
            if (p.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || p.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return p.Contains('"') || p.Contains(' '); // адрес с кавычками/пробелами — подозрительно
            if (AppLauncher.IsAlias(p)) return false;
            var ext = System.IO.Path.GetExtension(p).ToLowerInvariant();
            if (p.Contains("://")) return true;                       // file://, smb:// и прочие протоколы
            if (p.Contains('\\') || p.Contains('/'))
            {
                // папка (без расширения) и обычные документы/картинки открываются сразу, всё остальное — по кнопке
                if (ext.Length == 0) return false;
                return !(ext is ".txt" or ".pdf" or ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".mp4" or ".mkv" or ".mov"
                    or ".mp3" or ".wav" or ".flac" or ".md" or ".csv" or ".log");
            }
            if (p.Contains(':')) return true;                          // неизвестный протокол вида «xxx:»
            return ext is ".exe" or ".bat" or ".cmd" or ".ps1" or ".vbs" or ".js" or ".jse" or ".wsf" or ".msi" or ".scr" or ".com" or ".lnk" or ".reg" or ".hta"
                or ".cpl" or ".msc" or ".pif" or ".jar" or ".url" or ".application" or ".appref-ms" or ".settingcontent-ms" or ".library-ms";
        }
        public string Label => Name switch
        {
            "open" => "Открыть " + Arg,
            "search" => "Найти в интернете: " + Arg,
            "timer" => "Таймер на " + Arg,
            "remind" => "Напоминание: " + Arg,
            "edit" => "Переписать текст в поле: " + Arg,
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
            "[[timer: 10m]] — таймер (s — секунды, m — минуты, h — часы; можно вместе: [[timer: 4h40m]]);\n" +
            "[[remind: позвонить маме завтра в 9]] — напоминание обычной фразой, ВСЕГДА со временем («через 4 часа 40 минут …», «в 19:00 …»); " +
            "если просят таймер и напоминание без своего времени — напоминание ставь на то же время, что и таймер;\n" +
            "[[volume: 0-100]] — громкость;\n" +
            "[[media: play | pause | next | prev]] — музыка;\n" +
            "[[copy: текст]] — положить текст в буфер обмена;\n" +
            "[[screenshot]] — снимок экрана;\n" +
            "[[lock]], [[sleep]] — блокировка и сон (выполнятся после подтверждения пользователем);\n" +
            "[[cmd: команда cmd.exe]] — команда в консоли (только после подтверждения пользователем; не предлагай удаляющих или опасных команд).\n" +
            "Если пользователь просит открыть OBS, используй [[open: OBS]] даже если точное имя исполняемого файла неизвестно. " +
            "«Открой браузер» — это [[open: браузер]] (откроется браузер по умолчанию). Сайт открывай сразу адресом — " +
            "одной командой [[open: vk.com]], БЕЗ отдельной команды открыть браузер: одна просьба — одна команда. " +
            "Для ВКонтакте используй [[open: vk.com]]: Isle сначала попробует переключиться на уже открытое окно браузера с VK. " +
            "Не утверждай, что найдено существующее окно или вкладка, пока Isle не вернул успешный результат. " +
            "Не пиши команды, если пользователь просто спрашивает.";

        private static readonly Regex Rx = new(@"\[\[\s*(\w+)\s*(?::\s*([^\]]*?))?\s*\]\]", RegexOptions.Compiled);
        private static readonly HashSet<string> Known = new() { "edit", "open", "search", "timer", "remind", "volume", "media", "copy", "screenshot", "lock", "sleep", "cmd" };

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

        private static (DateTime at, TimeSpan span) _lastTimer;

        /// <summary>Выполняет действие и возвращает короткий отчёт для чата.</summary>
        public static string Run(AiAction a)
        {
            try
            {
                switch (a.Name)
                {
                    case "edit":
                        return "✗ Править набранный текст можно голосовой командой Ctrl+Alt+A";
                    case "open":
                        return AppLauncher.Open(a.Arg) ? "✓ Открыл " + a.Arg : "✗ Не нашёл «" + a.Arg + "» — проверь, что программа установлена";
                    case "search":
                        SystemControls.Shell("https://www.google.com/search?q=" + Uri.EscapeDataString(a.Arg));
                        return "✓ Ищу: " + a.Arg;
                    case "timer":
                    {
                        // «4h40m», «1ч 20м», «90s», «1:30» — все части складываем (раньше бралась только первая: 4h40m → 4:00:00)
                        var parsed = TimerService.ParseDuration(a.Arg.Replace(" ", ""));
                        TimeSpan span;
                        if (parsed.HasValue) span = parsed.Value;
                        else
                        {
                            var m = Regex.Match(a.Arg.ToLowerInvariant(), @"(\d+(?:[.,]\d+)?)\s*(s|с|m|м|h|ч)?");
                            if (!m.Success) return "✗ Не понял время таймера";
                            double n = double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                            var u = m.Groups[2].Value;
                            span = u == "s" || u == "с" ? TimeSpan.FromSeconds(n) : u == "h" || u == "ч" ? TimeSpan.FromHours(n) : TimeSpan.FromMinutes(n);
                        }
                        if (span < TimeSpan.FromSeconds(1) || span > TimeSpan.FromHours(100)) return "✗ Таймер — от 1 секунды до 100 часов";
                        AppHost.Timer.StartNew(span);
                        _lastTimer = (DateTime.Now, span);
                        return "✓ Таймер на " + TimerService.Format(span);
                    }
                    case "remind":
                    {
                        var r = AppHost.Reminders.Add(a.Arg);
                        // «поставь таймер на 4:40 и напомни собираться в театр» — у напоминания нет времени,
                        // но в этом же ответе только что поставили таймер: напоминаем, когда он закончится
                        if (r == null && DateTime.Now - _lastTimer.at < TimeSpan.FromSeconds(10) && _lastTimer.span > TimeSpan.Zero)
                            r = AppHost.Reminders.Add("через " + Math.Max(1, (int)Math.Round(_lastTimer.span.TotalMinutes)) + " минут " + a.Arg);
                        return r == null ? "✗ Не понял, когда напомнить" : "✓ Напомню " + r.WhenText + ": " + r.Text;
                    }
                    case "volume":
                        if (!int.TryParse(Regex.Match(a.Arg, @"\d+").Value, out var v)) return "✗ Не понял громкость";
                        AppHost.Audio.Volume = Math.Clamp(v, 0, 100) / 100f;
                        return "✓ Громкость " + Math.Clamp(v, 0, 100) + "%";
                    case "media":
                        if (a.Arg.StartsWith("next")) AppHost.Media.Next();
                        else if (a.Arg.StartsWith("prev")) AppHost.Media.Previous();
                        // «пауза» не должна включать музыку, если она уже на паузе (и наоборот)
                        else if (a.Arg.StartsWith("pause") || a.Arg.StartsWith("stop")) AppHost.Media.Pause();
                        else if (a.Arg.StartsWith("play")) AppHost.Media.Play();
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
