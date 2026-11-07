using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using Isle.Native;

namespace Isle.Services
{
    /// <summary>
    /// ИИ правит текст прямо в поле ввода (ВКонтакте, Telegram, почта…): зажал Ctrl+Alt+A и сказал
    /// «сделай этот текст официальнее» — Isle берёт выделенный текст (или всё поле), отдаёт ИИ и вставляет результат на место.
    /// Отменить — обычный Ctrl+Z.
    /// </summary>
    public static class TextEditor
    {
        // что сделать…
        private static readonly Regex Verb = new(
            @"\b(сделай|перепиши|переделай|исправь|поправь|оформи|переведи|сократи|расширь|улучши|перефразируй|отредактируй|смягчи|допиши|убери|замени|дополни|вежливее|официальн|короче|грамотн)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // …и с чем: с текстом / сообщением в поле
        private static readonly Regex Target = new(
            @"(текст|сообщени|написанн|набранн|напечатанн|абзац|предложени|письм|фраз|пост|комментари|в поле|выделенн)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool IsEditRequest(string command) =>
            !string.IsNullOrWhiteSpace(command) && Verb.IsMatch(command) && Target.IsMatch(command);

        private const ushort Ctrl = 0x11, KeyA = 0x41, KeyC = 0x43, KeyV = 0x56;

        public static async Task EditFocusedAsync(string request)
        {
            Show("«" + request + "»", "Беру текст из поля…", 30);
            await WaitModifiersUp(); // Ctrl+Alt ещё могут быть зажаты после команды — иначе Ctrl+C превратится в Ctrl+Alt+C

            string saved = null;
            try { if (Clipboard.ContainsText()) saved = Clipboard.GetText(); } catch { }

            ClipboardService.IgnoreUntil = DateTime.Now.AddSeconds(4);
            // 1) выделенный кусок
            string text = await CopyAsync();
            // 2) ничего не выделено — берём всё поле
            if (string.IsNullOrWhiteSpace(text))
            {
                Win32.SendKeyCombo(Ctrl, KeyA);
                await Task.Delay(80);
                text = await CopyAsync();
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                Restore(saved);
                Show("Не нашёл текст", "Поставь курсор в поле с текстом (или выдели кусок) и повтори команду", 5, failed: true);
                return;
            }
            if (text.Length > 8000)
            {
                Restore(saved);
                Show("Слишком длинный текст", "До 8000 символов — выдели часть", 5, failed: true);
                return;
            }

            Show("«" + request + "»", "Переписываю " + text.Length + " симв.…", 40);
            string result;
            try { result = await AiService.RewriteAsync(request, text); }
            catch (Exception ex)
            {
                Restore(saved);
                Show("Не удалось переписать", ex.Message, 6, failed: true);
                return;
            }

            // текст в поле всё ещё выделен — вставка заменит его
            ClipboardService.IgnoreUntil = DateTime.Now.AddSeconds(4);
            ClipboardService.SetText(result);
            await Task.Delay(60);
            Win32.SendKeyCombo(Ctrl, KeyV);
            await Task.Delay(350);
            Restore(saved);
            Show("Текст заменён", "Не понравилось — Ctrl+Z вернёт как было", 4);
        }

        private static async Task<string> CopyAsync()
        {
            try { Clipboard.Clear(); } catch { }
            Win32.SendKeyCombo(Ctrl, KeyC);
            for (int i = 0; i < 8; i++) // приложения отдают буфер не мгновенно
            {
                await Task.Delay(40);
                try { if (Clipboard.ContainsText()) return Clipboard.GetText(); } catch { }
            }
            return null;
        }

        private static void Restore(string saved)
        {
            ClipboardService.IgnoreUntil = DateTime.Now.AddSeconds(1);
            if (saved != null) ClipboardService.SetText(saved);
        }

        private static async Task WaitModifiersUp()
        {
            for (int i = 0; i < 40; i++)
            {
                bool down = (Win32.GetAsyncKeyState(0x11) & 0x8000) != 0 || (Win32.GetAsyncKeyState(0x12) & 0x8000) != 0
                         || (Win32.GetAsyncKeyState(0x10) & 0x8000) != 0 || (Win32.GetAsyncKeyState(0x5B) & 0x8000) != 0;
                if (!down) return;
                await Task.Delay(50);
            }
        }

        private static void Show(string title, string sub, double seconds, bool failed = false) =>
            ToastBus.Show(new Toast
            {
                Kind = failed ? ToastKind.Info : ToastKind.Success, Title = title, Subtitle = sub,
                Icon = failed ? Icons.Info : Icons.Chat, ReplaceKey = "voice", Important = true, Duration = TimeSpan.FromSeconds(seconds)
            });
    }
}
