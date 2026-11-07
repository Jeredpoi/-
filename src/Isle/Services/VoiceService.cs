using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Isle.Services
{
    /// <summary>
    /// Голос в текст: запись с микрофона → Whisper (Groq бесплатно или OpenAI) → текст в буфер.
    /// Первое нажатие Ctrl+Alt+D начинает запись, второе — останавливает и распознаёт.
    /// </summary>
    public class VoiceService
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };
        private WasapiCapture _capture;
        private MemoryStream _pcm;
        private WaveFormat _srcFormat;
        private DateTime _started;

        public bool Recording => _capture != null;
        public event Action<bool> RecordingChanged;
        public event Action<float> Level;

        public void Toggle(Action<string> onText = null)
        {
            if (Recording) _ = StopAsync(onText);
            else Start();
        }

        public void Start(string hint = null)
        {
            if (Recording) return;
            try
            {
                var dev = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                _capture = new WasapiCapture(dev);
                _srcFormat = _capture.WaveFormat;
                _pcm = new MemoryStream();
                _capture.DataAvailable += OnData;
                _capture.StartRecording();
                _started = DateTime.Now;
                RecordingChanged?.Invoke(true);
                ToastBus.Show(new Toast
                {
                    Kind = ToastKind.Info, Title = "Слушаю…", Subtitle = hint ?? "Говори. Ctrl+Alt+D ещё раз — вставить текст",
                    Icon = Icons.Mic, Accent = System.Windows.Media.Color.FromRgb(0xF8, 0x71, 0x71), Progress = 0.03,
                    ReplaceKey = "voice", Duration = TimeSpan.FromMinutes(5), Important = true
                });
            }
            catch (Exception ex)
            {
                _capture = null;
                ToastBus.Show(new Toast { Kind = ToastKind.Error, Title = "Микрофон недоступен", Subtitle = ex.Message, Icon = Icons.Mic, Important = true, Duration = TimeSpan.FromSeconds(4) });
            }
        }

        /// <summary>На лету переводим звук в 16 кГц, моно, 16 бит — так файл в десятки раз меньше.</summary>
        private void OnData(object s, WaveInEventArgs e)
        {
            var buf = _pcm;
            if (buf == null) return; // запись уже остановлена
            var f = _srcFormat;
            int ch = f.Channels;
            bool isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat || (f.Encoding == WaveFormatEncoding.Extensible && f.BitsPerSample == 32);
            int bytesPerSample = f.BitsPerSample / 8;
            int frame = bytesPerSample * ch;
            int frames = e.BytesRecorded / frame;
            double step = f.SampleRate / 16000.0;
            float peak = 0;
            for (double i = 0; i < frames; i += step)
            {
                int idx = (int)i * frame;
                float sum = 0;
                for (int c = 0; c < ch; c++)
                {
                    int o = idx + c * bytesPerSample;
                    float v = isFloat ? BitConverter.ToSingle(e.Buffer, o)
                        : bytesPerSample == 2 ? BitConverter.ToInt16(e.Buffer, o) / 32768f
                        : bytesPerSample == 4 ? BitConverter.ToInt32(e.Buffer, o) / 2147483648f
                        : bytesPerSample == 3 ? ((e.Buffer[o] | e.Buffer[o + 1] << 8 | (sbyte)e.Buffer[o + 2] << 16) / 8388608f) // 24-битные микрофоны
                        : 0;
                    sum += v;
                }
                float m = Math.Clamp(sum / ch, -1f, 1f);
                peak = Math.Max(peak, Math.Abs(m));
                short sv = (short)(m * 32767);
                buf.WriteByte((byte)(sv & 0xFF));
                buf.WriteByte((byte)((sv >> 8) & 0xFF));
            }
            Level?.Invoke(peak);
            if (DateTime.Now - _started > TimeSpan.FromMinutes(5)) // защита от забытой записи
                System.Windows.Application.Current?.Dispatcher.InvokeAsync(() => _ = StopAsync(null));
        }

        /// <param name="command">Голосовая команда ИИ (Ctrl+Alt+A): распознаём бесплатным Groq, если есть его ключ.</param>
        public async Task StopAsync(Action<string> onText, bool command = false)
        {
            if (!Recording) return;
            var cap = _capture;
            _capture = null;
            try { cap.StopRecording(); } catch { }
            await Task.Delay(150); // последние куски звука
            cap.DataAvailable -= OnData;
            cap.Dispose();
            RecordingChanged?.Invoke(false);
            var pcm = TrimSilence(_pcm.ToArray());
            _pcm = null;
            if (pcm == null)
            {
                ToastBus.Show(new Toast { Kind = ToastKind.Error, Title = "Тишина — ничего не услышал", Icon = Icons.Mic, ReplaceKey = "voice", Important = true });
                return;
            }
            if (pcm.Length < 16000) // меньше половины секунды
            {
                ToastBus.Show(new Toast { Kind = ToastKind.Error, Title = "Слишком коротко", Icon = Icons.Mic, ReplaceKey = "voice", Important = true });
                return;
            }
            ToastBus.Show(new Toast { Title = "Распознаю…", Icon = Icons.Mic, ReplaceKey = "voice", Important = true, Duration = TimeSpan.FromSeconds(30) });
            string text;
            try { text = await TranscribeAsync(Wav(pcm), command); }
            catch (Exception ex)
            {
                ToastBus.Show(new Toast { Kind = ToastKind.Error, Title = "Не удалось распознать", Subtitle = ex.Message, Icon = Icons.Mic, ReplaceKey = "voice", Important = true, Duration = TimeSpan.FromSeconds(6) });
                return;
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                ToastBus.Show(new Toast { Kind = ToastKind.Error, Title = "Тишина — ничего не услышал", Icon = Icons.Mic, ReplaceKey = "voice", Important = true });
                return;
            }
            if (AppSettings.Current.VoicePunctuate && !command) text = await AiService.PunctuateAsync(text); // команде знаки не нужны — экономим запрос
            if (onText != null)
            {
                // текст нужен программе (чат, голосовая команда) — буфер не трогаем
                ToastBus.Show(new Toast { Kind = ToastKind.Success, Title = "Распознано", Subtitle = text, Icon = Icons.Check, ReplaceKey = "voice", Important = true, Duration = TimeSpan.FromSeconds(1.2) });
                onText(text);
                return;
            }
            ClipboardService.SetText(text);
            if (AppSettings.Current.VoiceAutoPaste)
            {
                await Task.Delay(100);
                Native.Win32.SendKeyCombo(Native.Win32.VK_CONTROL, Native.Win32.VK_V);
            }
            ToastBus.Show(new Toast
            {
                Kind = ToastKind.Success, Title = "Текст в буфере · " + text.Length + " симв.", Subtitle = text,
                Icon = Icons.Check, ReplaceKey = "voice", Important = true, Duration = TimeSpan.FromSeconds(4)
            });
        }

        private static byte[] Wav(byte[] pcm)
        {
            using var ms = new MemoryStream();
            using (var w = new WaveFileWriter(ms, new WaveFormat(16000, 16, 1))) w.Write(pcm, 0, pcm.Length);
            return ms.ToArray();
        }

        public static string KeyFor(AppSettings s)
        {
            if (!string.IsNullOrWhiteSpace(s.VoiceKey)) return s.VoiceKey;
            // один ключ Groq/OpenAI подходит и для чата, и для диктовки
            try
            {
                if (new Uri(s.VoiceBaseUrl).Host == new Uri(s.AiBaseUrl).Host) return s.AiKey;
            }
            catch { }
            return "";
        }

        private const string GroqBase = "https://api.groq.com/openai/v1";

        /// <summary>Ключ Groq: из раздела «ИИ» (если там Groq) или из «Голос в текст» (если там Groq). Пусто — ключа нет.</summary>
        public static string GroqKey(AppSettings s)
        {
            static bool IsGroq(string url) { try { return new Uri(url).Host.EndsWith("groq.com", StringComparison.OrdinalIgnoreCase); } catch { return false; } }
            if (IsGroq(s.AiBaseUrl) && !string.IsNullOrWhiteSpace(s.AiKey)) return s.AiKey;
            if (IsGroq(s.VoiceBaseUrl)) return KeyFor(s);
            return "";
        }

        public static async Task<string> TranscribeAsync(byte[] wav, bool command = false)
        {
            var s = AppSettings.Current;
            var key = KeyFor(s);
            string baseUrl = s.VoiceBaseUrl, model = string.IsNullOrWhiteSpace(s.VoiceModel) ? "whisper-large-v3" : s.VoiceModel;
            // Голосовые команды — всегда через бесплатный Groq (платный сервис из настроек — только для диктовки Ctrl+Alt+D)
            if (command && GroqKey(s) is { Length: > 0 } groq)
            {
                key = groq; baseUrl = GroqBase; model = "whisper-large-v3";
            }
            if (string.IsNullOrWhiteSpace(key)) throw new Exception("нужен ключ: Настройки → Голос в текст");
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(wav);
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(file, "file", "voice.wav");
            form.Add(new StringContent(model), "model");
            if (!string.IsNullOrEmpty(s.VoiceLanguage)) form.Add(new StringContent(s.VoiceLanguage), "language");
            form.Add(new StringContent("json"), "response_format");
            // подсказка-образец с пунктуацией: Whisper подстраивается под стиль и сам ставит точки, запятые и вопросы
            var hint = s.VoiceLanguage == "en"
                ? "Hello! How are you? I'm fine, thanks. Let's meet tomorrow at nine, okay?"
                : "Привет! Как дела? У меня всё хорошо, спасибо. Давай встретимся завтра в девять, хорошо?";
            form.Add(new StringContent(hint), "prompt");
            using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/audio/transcriptions") { Content = form };
            req.Headers.Add("Authorization", "Bearer " + key.Trim());
            using var resp = await Http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            AiService.CheckLimits(resp); // у Groq лимит распознавания тоже кончается — предупредим
            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch { throw new Exception("сервис ответил не по делу (код " + (int)resp.StatusCode + ") — попробуй ещё раз или включи VPN"); }
            using (doc)
            {
                if (!resp.IsSuccessStatusCode) throw new Exception(AiService.ErrorText(doc.RootElement, (int)resp.StatusCode));
                var text = doc.RootElement.TryGetProperty("text", out var t) ? (t.GetString() ?? "").Trim() : "";
                text = StripJunk(text);
                return IsHallucination(text, hint) ? "" : text;
            }
        }

        /// <summary>
        /// На тишине Whisper любит «выдумывать»: повторяет подсказку или пишет титры из видео.
        /// Такое считаем пустой записью, а не вставляем в чужое окно.
        /// </summary>
        public static bool IsHallucination(string text, string hint)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            string Norm(string x) => NormText(x);
            var t = Norm(text);
            if (t.Length == 0) return true;
            if (!string.IsNullOrEmpty(hint) && (t.Length >= 15 && Norm(hint).Contains(t) || t.Contains(Norm(hint)))) return true; // короткое «привет» — это ты, а не подсказка
            return t.Length < 60 && Junk.Any(t.Contains);
        }

        // «титры» из роликов, которые Whisper дописывает на тишине (часто в конце настоящей фразы)
        private static readonly string[] Junk =
        {
            "продолжениеследует", "субтитр", "дубровск", "dimatorzok", "диматорзок", "редакторсубтитров",
            "спасибозапросмотр", "подписывайтесь", "ставьтелайк", "amaraorg", "thanksforwatching", "subtitles", "pleasesubscribe"
        };

        private static string NormText(string x) => new string(x.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        /// <summary>Убирает из текста предложения-«титры», оставляя то, что ты сказал.</summary>
        public static string StripJunk(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var parts = System.Text.RegularExpressions.Regex.Split(text, @"(?<=[.!?…])\s+");
            return string.Join(" ", parts.Where(p => { var n = NormText(p); return !Junk.Any(n.Contains); })).Trim();
        }

        /// <summary>
        /// Обрезает тишину в начале и в конце записи (16 кГц, 16 бит): Whisper на тишине выдумывает фразы.
        /// Если громкой речи нет совсем — null.
        /// </summary>
        private static byte[] TrimSilence(byte[] pcm)
        {
            const int win = 320 * 2; // окна по 20 мс
            int n = pcm.Length / win;
            if (n == 0) return null;
            var lvl = new double[n];
            double max = 0;
            for (int w = 0; w < n; w++)
            {
                double sum = 0;
                for (int i = w * win; i < (w + 1) * win; i += 2)
                {
                    double v = (short)(pcm[i] | pcm[i + 1] << 8) / 32768.0;
                    sum += v * v;
                }
                lvl[w] = Math.Sqrt(sum / 320);
                max = Math.Max(max, lvl[w]);
            }
            if (max < 0.012) return null; // только шум микрофона
            double gate = Math.Max(0.008, max * 0.08);
            int first = Array.FindIndex(lvl, v => v >= gate), last = Array.FindLastIndex(lvl, v => v >= gate);
            first = Math.Max(0, first - 15); last = Math.Min(n - 1, last + 20); // запас 0,3 с до и 0,4 с после
            int len = (last - first + 1) * win;
            if (len < 16000) len = Math.Min(pcm.Length - first * win, 16000); // Whisper не любит совсем короткие куски
            var res = new byte[len];
            Array.Copy(pcm, first * win, res, 0, len);
            return res;
        }

        public sealed record VoiceModelInfo(string Id, string Title, double Price, string PriceText, bool PaidOnly);

        /// <summary>
        /// Модели распознавания у выбранного сервиса: только «голос → текст» (вход — звук, выход — текст),
        /// от дешёвых к дорогим. У агрегаторов (Polza.ai, OpenRouter) сотни моделей — остальные (картинки, чат, озвучка) отбрасываем.
        /// </summary>
        public static async Task<System.Collections.Generic.List<VoiceModelInfo>> ModelsAsync(string baseUrl)
        {
            var s = AppSettings.Current;
            using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + "/models");
            var key = KeyFor(s);
            if (!string.IsNullOrWhiteSpace(key)) req.Headers.Add("Authorization", "Bearer " + key.Trim());
            using var resp = await Http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch { throw new Exception("сервис ответил не по делу (код " + (int)resp.StatusCode + ")"); }
            using (doc)
            {
                if (!resp.IsSuccessStatusCode) throw new Exception(AiService.ErrorText(doc.RootElement, (int)resp.StatusCode));
                var list = new System.Collections.Generic.List<VoiceModelInfo>();
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return list;
                foreach (var m in data.EnumerateArray())
                {
                    var id = m.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                    if (id.Length == 0 || !IsSpeechToText(m, id)) continue;
                    var title = m.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : "";
                    bool paid = m.TryGetProperty("paid_only", out var p) && p.ValueKind == JsonValueKind.True;
                    var (price, text) = Price(m);
                    list.Add(new VoiceModelInfo(id, title, price, text, paid));
                }
                // сначала с известной ценой — от дешёвых к дорогим, без цены — в конце по алфавиту
                return list.OrderBy(x => double.IsNaN(x.Price) ? 1 : 0).ThenBy(x => double.IsNaN(x.Price) ? 0 : x.Price).ThenBy(x => x.Id).ToList();
            }
        }

        /// <summary>Вход — звук, выход — текст: по списку поддерживаемых адресов, по модальностям или по имени (Whisper и т. п.).</summary>
        internal static bool IsSpeechToText(JsonElement m, string id)
        {
            static bool Has(JsonElement el, string prop, string value) =>
                el.TryGetProperty(prop, out var a) && a.ValueKind == JsonValueKind.Array &&
                a.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && (x.GetString() ?? "").Contains(value, StringComparison.OrdinalIgnoreCase));
            if (Has(m, "supported_endpoints", "audio/transcriptions")) return true;
            if (m.TryGetProperty("supported_endpoints", out _)) return false; // сервис сам перечислил, что модель умеет — распознавания нет
            // у агрегаторов в стиле OpenRouter (Polza.ai) модальности лежат в «architecture»
            if (m.TryGetProperty("architecture", out var arch) && arch.ValueKind == JsonValueKind.Object
                && Has(arch, "input_modalities", "audio") && Has(arch, "output_modalities", "text") && !Has(arch, "output_modalities", "audio")
                && !Has(arch, "input_modalities", "text")) return true;
            if (Has(m, "input_modalities", "audio") && Has(m, "output_modalities", "text") && !Has(m, "output_modalities", "audio")
                && !(m.TryGetProperty("category", out var c) && c.GetString() == "text")) return true;
            var low = id.ToLowerInvariant();
            return low.Contains("whisper") || low.Contains("transcribe") || low.Contains("speech-to-text") || low.Contains("stt");
        }

        /// <summary>Цена модели из её «pricing» (у агрегаторов — цена за единицу). NaN — сервис цену не сообщил.</summary>
        private static (double, string) Price(JsonElement m)
        {
            if (!m.TryGetProperty("pricing", out var pr) || pr.ValueKind != JsonValueKind.Object) return (double.NaN, "");
            double sum = 0; bool any = false;
            string currency = pr.TryGetProperty("currency", out var cur) && cur.ValueKind == JsonValueKind.String ? cur.GetString() : "";
            foreach (var f in pr.EnumerateObject())
            {
                double v = f.Value.ValueKind == JsonValueKind.Number ? f.Value.GetDouble()
                    : f.Value.ValueKind == JsonValueKind.String && double.TryParse(f.Value.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
                if (v > 0) { sum += v; any = true; }
            }
            if (!any) return (0, "бесплатно");
            var unit = string.IsNullOrEmpty(currency) ? "" : currency.Equals("RUB", StringComparison.OrdinalIgnoreCase) ? "₽" : currency;
            return (sum, (sum.ToString(sum < 0.001 ? "0.#######" : "0.####", System.Globalization.CultureInfo.InvariantCulture) + " " + unit).Trim());
        }

        /// <summary>Проверка: отправляем секунду тишины — если ключ верный, сервис ответит.</summary>
        public static async Task<string> TestAsync()
        {
            try
            {
                await TranscribeAsync(Wav(new byte[32000]));
                return "Работает: модель " + AppSettings.Current.VoiceModel + " приняла запись";
            }
            catch (Exception ex)
            {
                var msg = ex.Message;
                if (msg.Contains("model", StringComparison.OrdinalIgnoreCase) || msg.Contains("модел", StringComparison.OrdinalIgnoreCase))
                    msg += " — нажми «Модели» и выбери из списка";
                return "Ошибка: " + msg;
            }
        }
    }
}
