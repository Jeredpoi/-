using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Isle.Services
{
    public class ChatMessage
    {
        public string Role { get; set; } // user | assistant
        public string Text { get; set; }
        public bool IsUser => Role == "user";
    }

    /// <summary>
    /// Чат с ИИ. Поддерживает Anthropic (Claude) и любой OpenAI-совместимый API
    /// (OpenAI, Gemini через OpenAI-эндпоинт, OpenRouter, локальная Ollama и т.д.).
    /// Ключ хранится только у пользователя в %AppData%\Isle\settings.json.
    /// </summary>
    public static class AiService
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

        private const string SystemPrompt =
            "Ты — помощник, встроенный в панель Isle на рабочем столе Windows. Отвечай кратко и по делу, на языке пользователя. " +
            "Используй простой текст без лишней разметки.";

        public static async Task<string> AskAsync(IReadOnlyList<ChatMessage> history, CancellationToken ct = default)
        {
            var s = AppSettings.Current;
            if (string.IsNullOrWhiteSpace(s.AiKey) && s.AiProvider == "anthropic")
                return "Добавь API-ключ во вкладке «Настройки» → «ИИ», и я смогу отвечать.";

            return s.AiProvider == "anthropic"
                ? await AnthropicAsync(history, s, ct)
                : await OpenAiAsync(history, s, ct);
        }

        private static async Task<string> AnthropicAsync(IReadOnlyList<ChatMessage> history, AppSettings s, CancellationToken ct)
        {
            var body = new
            {
                model = string.IsNullOrWhiteSpace(s.AiModel) ? "claude-haiku-5-5" : s.AiModel,
                max_tokens = 2048,
                system = SystemPrompt,
                messages = history.Select(m => new { role = m.Role, content = m.Text }).ToArray()
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            req.Headers.Add("x-api-key", s.AiKey.Trim());
            req.Headers.Add("anthropic-version", "2023-06-01");
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var resp = await Http.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!resp.IsSuccessStatusCode) return "Ошибка: " + ErrorText(doc.RootElement, (int)resp.StatusCode);
            var sb = new StringBuilder();
            foreach (var block in doc.RootElement.GetProperty("content").EnumerateArray())
                if (block.TryGetProperty("type", out var t) && t.GetString() == "text")
                    sb.Append(block.GetProperty("text").GetString());
            return sb.ToString().Trim();
        }

        private static async Task<string> OpenAiAsync(IReadOnlyList<ChatMessage> history, AppSettings s, CancellationToken ct)
        {
            var msgs = new List<object> { new { role = "system", content = SystemPrompt } };
            msgs.AddRange(history.Select(m => (object)new { role = m.Role, content = m.Text }));
            var body = new { model = s.AiModel, messages = msgs };
            using var req = new HttpRequestMessage(HttpMethod.Post, s.AiEndpoint);
            if (!string.IsNullOrWhiteSpace(s.AiKey)) req.Headers.Add("Authorization", "Bearer " + s.AiKey.Trim());
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var resp = await Http.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!resp.IsSuccessStatusCode) return "Ошибка: " + ErrorText(doc.RootElement, (int)resp.StatusCode);
            return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()?.Trim() ?? "";
        }

        private static string ErrorText(JsonElement root, int code)
        {
            try
            {
                if (root.TryGetProperty("error", out var e))
                {
                    if (e.ValueKind == JsonValueKind.String) return e.GetString();
                    if (e.TryGetProperty("message", out var m)) return m.GetString();
                }
            }
            catch { }
            return "HTTP " + code;
        }
    }
}
