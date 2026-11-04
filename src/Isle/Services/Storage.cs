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

        // Погода
        public string City { get; set; } = "Москва";
        public double Latitude { get; set; } = 55.7558;
        public double Longitude { get; set; } = 37.6173;

        // ИИ
        public string AiProvider { get; set; } = "anthropic"; // anthropic | openai
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
        public string AiModel { get; set; } = "claude-haiku-5-5";
        public string AiEndpoint { get; set; } = "https://api.openai.com/v1/chat/completions";

        // Прочее
        public string Hotkey { get; set; } = "Ctrl+Alt+Space";
        public bool Use24h { get; set; } = true;

        private static AppSettings _current;
        public static AppSettings Current => _current ??= Storage.Load<AppSettings>("settings.json");
        public void Save() => Storage.Save("settings.json", this);
    }

    public class Note
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; } = "Заметка";
        public string Text { get; set; } = "";
        public DateTime Updated { get; set; } = DateTime.Now;
    }

    public class NotesData
    {
        public List<Note> Notes { get; set; } = new();
    }
}
