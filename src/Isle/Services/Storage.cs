using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Isle.Services
{
    public static class Storage
    {
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Isle");

        private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

        public static T Load<T>(string name) where T : new()
        {
            try
            {
                var path = Path.Combine(Dir, name);
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? new T();
            }
            catch { }
            return new T();
        }

        public static void Save<T>(string name, T value)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var path = Path.Combine(Dir, name);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(value, Opts));
                File.Move(tmp, path, true);
            }
            catch { }
        }
    }

    public class AppSettings
    {
        // Модули
        public bool MediaEnabled { get; set; } = true;
        public bool LyricsEnabled { get; set; } = true;
        public bool VolumeToasts { get; set; } = true;
        public bool LayoutToasts { get; set; } = true;
        public bool CapsToasts { get; set; } = true;
        public bool BatteryToasts { get; set; } = true;
        public bool NotificationToasts { get; set; } = true;
        public bool ClipboardHistory { get; set; } = true;
        public int ClipboardLimit { get; set; } = 50;
        public bool HideInFullscreen { get; set; } = true;
        public bool ExpandOnHover { get; set; } = true;
        public bool DoNotDisturb { get; set; } = false;

        // OLED-режим: чёлка полностью скрыта, появляется при наведении на верхний край экрана
        public bool OledMode { get; set; } = false;
        public int OledRevealSeconds { get; set; } = 10;
        public double Scale { get; set; } = 1.0;
        /// <summary>По умолчанию остров не попадает на скриншоты, запись и трансляции.</summary>
        public bool ShowInCaptures { get; set; } = false;
        public bool VpnStatus { get; set; } = true;
        public bool ClipboardImages { get; set; } = true;
        public List<string> Launcher { get; set; } = new();

        // Погода
        public string City { get; set; } = "Москва";
        public double Latitude { get; set; } = 55.7558;
        public double Longitude { get; set; } = 37.6173;

        // ИИ
        /// <summary>Сервис ИИ: groq | gemini | openrouter | polza | openai | anthropic | ollama | custom.</summary>
        public string AiPreset { get; set; } = "groq";
        /// <summary>Базовый адрес OpenAI-совместимого API (…/v1). Для Anthropic не используется.</summary>
        public string AiBaseUrl { get; set; } = "https://api.groq.com/openai/v1";
        [System.Text.Json.Serialization.JsonIgnore]
        public string AiProvider => AiPreset == "anthropic" ? "anthropic" : "openai";
        /// <summary>Ключ хранится зашифрованным через Windows DPAPI (расшифровать может только этот пользователь Windows).</summary>
        public string AiKeyProtected { get; set; } = "";

        [System.Text.Json.Serialization.JsonIgnore]
        public string AiKey
        {
            get
            {
                if (string.IsNullOrEmpty(AiKeyProtected)) return "";
                try
                {
                    var raw = System.Security.Cryptography.ProtectedData.Unprotect(
                        Convert.FromBase64String(AiKeyProtected), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                    return System.Text.Encoding.UTF8.GetString(raw);
                }
                catch { return ""; }
            }
            set
            {
                if (string.IsNullOrEmpty(value)) { AiKeyProtected = ""; return; }
                var enc = System.Security.Cryptography.ProtectedData.Protect(
                    System.Text.Encoding.UTF8.GetBytes(value), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                AiKeyProtected = Convert.ToBase64String(enc);
            }
        }
        public string AiModel { get; set; } = "llama-3.3-70b-versatile";

        // Голос в текст (Whisper)
        public string VoicePreset { get; set; } = "groq"; // groq | polza | openai | custom
        public string VoiceBaseUrl { get; set; } = "https://api.groq.com/openai/v1";
        public string VoiceModel { get; set; } = "whisper-large-v3";
        public string VoiceKeyProtected { get; set; } = "";
        public string VoiceLanguage { get; set; } = ""; // "" — сам определит, ru, en
        public bool VoiceAutoPaste { get; set; } = true;
        /// <summary>После распознавания ИИ расставляет знаки препинания (слова не меняет).</summary>
        public bool VoicePunctuate { get; set; } = true;
        [System.Text.Json.Serialization.JsonIgnore]
        public string VoiceKey { get => Unprotect(VoiceKeyProtected); set => VoiceKeyProtected = Protect(value); }

        // OBS (запись экрана через obs-websocket)
        public int ObsPort { get; set; } = 4455;
        public string ObsPasswordProtected { get; set; } = "";
        [System.Text.Json.Serialization.JsonIgnore]
        public string ObsPassword { get => Unprotect(ObsPasswordProtected); set => ObsPasswordProtected = Protect(value); }

        // Внешний вид
        public string AccentHex { get; set; } = "#ECECF1";
        public double IslandOpacity { get; set; } = 1.0;
        /// <summary>Частота анимаций, кадров/с: 30, 60, 120 или 0 — как у монитора.</summary>
        public int MaxFps { get; set; } = 60;
        public string Character { get; set; } = "pixel"; // eyes | pixel | none
        public bool FirstRunDone { get; set; } = false;
        /// <summary>Подсказка из трея «Isle запущен» уже показывалась.</summary>
        public bool TrayHintShown { get; set; } = false;
        public bool AutoUpdateCheck { get; set; } = true;
        public bool ScaleFixV2 { get; set; } = false;
        public bool ObsStatus { get; set; } = true;
        /// <summary>Брать скорость, объём и остаток загрузки прямо из Steam (Isle сам включает это в Steam).</summary>
        public bool SteamExactSpeed { get; set; } = true;
        public bool VoiceDefaultsV2 { get; set; } = false;

        // Музыка
        /// <summary>Пилюля во время музыки: bars (обычная) | title (с названием) | mini (мини-плеер) | lyrics (с текстом).</summary>
        public string MusicPill { get; set; } = "title";
        /// <summary>Плеер на «Главной»: mirror (мягкое зеркало) | fill (заливка) | compact (компактный).</summary>
        public string HomePlayer { get; set; } = "mirror";
        public bool LyricsOnHome { get; set; } = true;
        public bool ShowClock { get; set; } = true;
        public bool ClockSeconds { get; set; } = false;

        public static string Protect(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var enc = System.Security.Cryptography.ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(value), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(enc);
        }

        public static string Unprotect(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            try
            {
                return System.Text.Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(
                    Convert.FromBase64String(value), null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
            }
            catch { return ""; }
        }

        // Прочее
        public string Hotkey { get; set; } = "Ctrl+Alt+Space";
        public string HotkeyCommand { get; set; } = "Ctrl+Alt+K";
        public string HotkeyClipboard { get; set; } = "Ctrl+Alt+V";
        public string HotkeyScreenshot { get; set; } = "Ctrl+Alt+S";
        public string HotkeyVoice { get; set; } = "Ctrl+Alt+D";
        public string HotkeyVoiceCommand { get; set; } = "Ctrl+Alt+A";
        public string HotkeyCollapse { get; set; } = "Escape";
        /// <summary>Клавиша PrintScreen делает снимок через Isle (выделение области, карточка в чёлке).</summary>
        public bool PrintScreenIsle { get; set; }
        /// <summary>Сохранять снимки Isle в «Изображения\\Isle» (а не только в буфер обмена).</summary>
        public bool SaveScreenshots { get; set; } = true;
        public bool Use24h { get; set; } = true;

        private static AppSettings _current;
        public static AppSettings Current => _current ??= Migrate(Storage.Load<AppSettings>("settings.json"));

        /// <summary>Старые значения по умолчанию → новые (облегчённый turbo хуже ставит знаки препинания).</summary>
        private static AppSettings Migrate(AppSettings s)
        {
            if (s != null && s.VoiceModel == "whisper-large-v3-turbo") s.VoiceModel = "whisper-large-v3";
            if (s != null && s.AiPreset is "deepseek" or "pollinations") s.AiPreset = "custom"; // убранные из списка сервисы — адрес и ключ остаются
            if (s != null && s.VoicePreset == "pollinations") s.VoicePreset = "custom";
            return s;
        }
        public void Save() => Storage.Save("settings.json", this);
    }

    public class Note
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; } = "Заметка";
        public string Text { get; set; } = "";
        /// <summary>Форматированный текст (XAML FlowDocument). Text — его простая копия для заголовков и поиска.</summary>
        public string Xaml { get; set; } = "";
        public DateTime Updated { get; set; } = DateTime.Now;
    }

    public class NotesData
    {
        public List<Note> Notes { get; set; } = new();
    }
}
