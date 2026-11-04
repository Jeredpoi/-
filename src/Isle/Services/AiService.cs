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
        /// <summary>Действие, которое ждёт подтверждения пользователя (кнопка «Выполнить»).</summary>
        public AiAction Pending { get; set; }
        public bool HasPending => Pending != null;
        public string PendingLabel => Pending?.Label ?? "";
    }

    public class AiPreset
    {
        public string Id, Title, BaseUrl, Model, KeyUrl, Note;
        public bool Free;
    }

    /// <summary>
    /// Чат с ИИ: Anthropic (Claude) или любой OpenAI-совместимый сервис (Groq, Gemini, OpenRouter, DeepSeek, OpenAI, Ollama…).
    /// Ключ хранится только у пользователя в %AppData%\Isle\settings.json, зашифрованным.
    /// </summary>
    public static class AiService
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

        public static readonly AiPreset[] Presets =
        {
            new() { Id = "groq", Title = "Groq", Free = true, BaseUrl = "https://api.groq.com/openai/v1", Model = "llama-3.3-70b-versatile",
                    KeyUrl = "https://console.groq.com/keys", Note = "Бесплатно, без карты, очень быстро. Тот же ключ подходит для голоса в текст." },
            new() { Id = "gemini", Title = "Google Gemini", Free = true, BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai", Model = "gemini-2.5-flash",
                    KeyUrl = "https://aistudio.google.com/apikey", Note = "Бесплатный лимит, хорошо понимает русский." },
            new() { Id = "openrouter", Title = "OpenRouter", Free = true, BaseUrl = "https://openrouter.ai/api/v1", Model = "meta-llama/llama-3.3-70b-instruct:free",
                    KeyUrl = "https://openrouter.ai/keys", Note = "Сотни моделей через один ключ, есть бесплатные (с пометкой :free)." },
            new() { Id = "deepseek", Title = "DeepSeek", BaseUrl = "https://api.deepseek.com/v1", Model = "deepseek-chat",
                    KeyUrl = "https://platform.deepseek.com/api_keys", Note = "Дёшево и умно." },
            new() { Id = "openai", Title = "OpenAI", BaseUrl = "https://api.openai.com/v1", Model = "gpt-4o-mini",
                    KeyUrl = "https://platform.openai.com/api-keys", Note = "Платно, по факту использования." },
            new() { Id = "anthropic", Title = "Claude", BaseUrl = "https://api.anthropic.com/v1", Model = "claude-haiku-5-5",
                    KeyUrl = "https://console.anthropic.com/settings/keys", Note = "Модели Claude от Anthropic. Платно, по факту использования." },
            new() { Id = "ollama", Title = "Ollama (на этом ПК)", Free = true, BaseUrl = "http://localhost:11434/v1", Model = "llama3.2",
                    KeyUrl = "https://ollama.com/download", Note = "Модель работает прямо на компьютере, ключ не нужен." },
        };

        public static AiPreset Preset(string id) => Presets.FirstOrDefault(p => p.Id == id);

        private static string SystemPrompt => BasePrompt + AiActions.Prompt;
        private const string BasePrompt =
            "Ты — помощник, встроенный в панель Isle на рабочем столе Windows. Отвечай кратко и по делу, на языке пользователя. " +
            "Используй простой текст без лишней разметки.";

        public static event Action<bool> BusyChanged;

        public static async Task<string> AskAsync(IReadOnlyList<ChatMessage> history, CancellationToken ct = default)
        {
            var s = AppSettings.Current;
            if (string.IsNullOrWhiteSpace(s.AiKey) && s.AiPreset != "ollama")
                return "Нужен API-ключ: открой Настройки → ИИ, выбери сервис (например, бесплатный Groq), вставь ключ и нажми «Проверить».";
            BusyChanged?.Invoke(true);
            try
            {
                return s.AiProvider == "anthropic"
                    ? await AnthropicAsync(history, s.AiKey, s.AiModel, ct)
                    : await OpenAiAsync(history, s.AiBaseUrl, s.AiKey, s.AiModel, ct);
            }
            finally { BusyChanged?.Invoke(false); }
        }

        /// <summary>Проверка ключа и модели одним коротким запросом.</summary>
        public static async Task<string> TestAsync(string preset, string baseUrl, string key, string model)
        {
            var hist = new List<ChatMessage> { new() { Role = "user", Text = "Ответь одним словом: работает?" } };
            try
            {
                var r = preset == "anthropic" ? await AnthropicAsync(hist, key, model, default) : await OpenAiAsync(hist, baseUrl, key, model, default);
                return r.StartsWith("Ошибка") ? r : "Работает: «" + Trim(r, 60) + "»";
            }
            catch (Exception ex) { return "Ошибка: " + ex.Message; }
        }

        /// <summary>Список моделей, которые доступны этому ключу.</summary>
        public static async Task<List<string>> ModelsAsync(string preset, string baseUrl, string key)
        {
            var url = (preset == "anthropic" ? "https://api.anthropic.com/v1" : baseUrl.TrimEnd('/')) + "/models";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            AddAuth(req, preset, key);
            using var resp = await Http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!resp.IsSuccessStatusCode) throw new Exception(ErrorText(doc.RootElement, (int)resp.StatusCode));
            var list = new List<string>();
            if (doc.RootElement.TryGetProperty("data", out var data))
                foreach (var m in data.EnumerateArray())
                    if (m.TryGetProperty("id", out var id)) list.Add(id.GetString());
            return list.Where(x => !string.IsNullOrEmpty(x)).Select(x => x.StartsWith("models/") ? x.Substring(7) : x).OrderBy(x => x).ToList();
        }

        private static void AddAuth(HttpRequestMessage req, string preset, string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            if (preset == "anthropic")
            {
                req.Headers.Add("x-api-key", key.Trim());
                req.Headers.Add("anthropic-version", "2023-06-01");
            }
            else req.Headers.Add("Authorization", "Bearer " + key.Trim());
        }

        private static async Task<string> AnthropicAsync(IReadOnlyList<ChatMessage> history, string key, string model, CancellationToken ct)
        {
            var body = new
            {
                model = string.IsNullOrWhiteSpace(model) ? "claude-haiku-5-5" : model,
                max_tokens = 2048,
                system = SystemPrompt,
                messages = history.Select(m => new { role = m.Role, content = m.Text }).ToArray()
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            AddAuth(req, "anthropic", key);
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

        private static async Task<string> OpenAiAsync(IReadOnlyList<ChatMessage> history, string baseUrl, string key, string model, CancellationToken ct)
        {
            var msgs = new List<object> { new { role = "system", content = SystemPrompt } };
            msgs.AddRange(history.Select(m => (object)new { role = m.Role, content = m.Text }));
            var body = new { model, messages = msgs };
            using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/chat/completions");
            AddAuth(req, "openai", key);
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var resp = await Http.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!resp.IsSuccessStatusCode) return "Ошибка: " + ErrorText(doc.RootElement, (int)resp.StatusCode);
            var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            // некоторые модели (qwen, deepseek-r1) присылают рассуждения в <think>…</think> — пользователю они не нужны
            content = System.Text.RegularExpressions.Regex.Replace(content, @"<think>[\s\S]*?</think>", "").Trim();
            return content;
        }

        public static string ErrorText(JsonElement root, int code)
        {
            try
            {
                if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) root = root[0];
                if (root.TryGetProperty("error", out var e))
                {
                    if (e.ValueKind == JsonValueKind.String) return e.GetString();
                    if (e.TryGetProperty("message", out var m)) return m.GetString();
                }
            }
            catch { }
            return code == 401 ? "неверный ключ (HTTP 401)" : code == 403 ? "доступ запрещён — возможно, сервис недоступен в вашей стране, попробуйте VPN (HTTP 403)" : "HTTP " + code;
        }

        private static string Trim(string s, int n) => s.Length > n ? s.Substring(0, n) + "…" : s;
    }
}
