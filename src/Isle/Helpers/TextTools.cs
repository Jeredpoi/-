using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Isle.Helpers
{
    public static class TextTools
    {
        private const string En = "`qwertyuiop[]asdfghjkl;'zxcvbnm,./~QWERTYUIOP{}ASDFGHJKL:\"ZXCVBNM<>?@#$^&";
        private const string Ru = "ёйцукенгшщзхъфывапролджэячсмитьбю.ЁЙЦУКЕНГШЩЗХЪФЫВАПРОЛДЖЭЯЧСМИТЬБЮ,\"№;:?";

        /// <summary>«ghbdtn» → «привет» и обратно (определяет направление по большинству букв).</summary>
        public static string SwitchLayout(string s)
        {
            int lat = s.Count(c => c < 128 && char.IsLetter(c));
            int cyr = s.Count(c => c >= 0x400 && c <= 0x4FF);
            bool toRu = lat >= cyr;
            var from = toRu ? En : Ru;
            var to = toRu ? Ru : En;
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                int i = from.IndexOf(c);
                sb.Append(i >= 0 && i < to.Length ? to[i] : c);
            }
            return sb.ToString();
        }

        public static string Upper(string s) => s.ToUpper(CultureInfo.CurrentCulture);
        public static string Lower(string s) => s.ToLower(CultureInfo.CurrentCulture);

        public static string TitleCase(string s) =>
            Regex.Replace(s.ToLower(CultureInfo.CurrentCulture), @"(^|[\s\-«""'(\[])(\p{L})", m => m.Groups[1].Value + m.Groups[2].Value.ToUpper(CultureInfo.CurrentCulture));

        public static string SentenceCase(string s) =>
            Regex.Replace(s.ToLower(CultureInfo.CurrentCulture), @"(^\s*|[.!?…]\s+)(\p{L})", m => m.Groups[1].Value + m.Groups[2].Value.ToUpper(CultureInfo.CurrentCulture));

        private static readonly Dictionary<char, string> Tr = new()
        {
            ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "yo", ['ж'] = "zh",
            ['з'] = "z", ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o",
            ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f", ['х'] = "kh", ['ц'] = "ts",
            ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "shch", ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu",
            ['я'] = "ya", ['і'] = "i", ['ї'] = "yi", ['є'] = "ye", ['ә'] = "a", ['ғ'] = "gh", ['қ'] = "q", ['ң'] = "ng",
            ['ө'] = "o", ['ұ'] = "u", ['ү'] = "u", ['һ'] = "h"
        };

        public static string Translit(string s)
        {
            var sb = new StringBuilder(s.Length * 2);
            foreach (var c in s)
            {
                var lower = char.ToLowerInvariant(c);
                if (Tr.TryGetValue(lower, out var t))
                {
                    if (c != lower && t.Length > 0) t = char.ToUpperInvariant(t[0]) + t.Substring(1);
                    sb.Append(t);
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public static string RemoveLineBreaks(string s) => Regex.Replace(s, @"\s*\r?\n\s*", " ").Trim();
        public static string CollapseSpaces(string s) => Regex.Replace(Regex.Replace(s, @"[ \t ]+", " "), @" *\r?\n *", "\n").Trim();

        public static string JsonPretty(string s)
        {
            using var doc = JsonDocument.Parse(s);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
            {
                WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        }

        /// <summary>Текст → JSON-строка с экранированием (удобно вставлять в код).</summary>
        public static string JsonToString(string s)
        {
            var t = s.Trim();
            if (t.StartsWith("{") || t.StartsWith("["))
            {
                try
                {
                    using var doc = JsonDocument.Parse(t);
                    return JsonSerializer.Serialize(JsonSerializer.Serialize(doc.RootElement), new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                }
                catch { }
            }
            return JsonSerializer.Serialize(s, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }

        public static string ToBase64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
        public static string FromBase64(string s) => Encoding.UTF8.GetString(Convert.FromBase64String(s.Trim()));
        public static string UrlEncode(string s) => Uri.EscapeDataString(s);
        public static string UrlDecode(string s) => Uri.UnescapeDataString(s);
        public static string Reverse(string s) => new string(s.Reverse().ToArray());
        public static string SortLines(string s) => string.Join("\n", s.Replace("\r", "").Split('\n').OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase));
        public static string UniqueLines(string s) => string.Join("\n", s.Replace("\r", "").Split('\n').Distinct());

        public static string Stats(string s)
        {
            int chars = s.Length;
            int noSpaces = s.Count(c => !char.IsWhiteSpace(c));
            int words = Regex.Matches(s, @"[\p{L}\p{N}][\p{L}\p{N}\-']*").Count;
            int lines = s.Length == 0 ? 0 : s.Split('\n').Length;
            return $"{chars} симв. · {noSpaces} без пробелов · {words} {Plural(words, "слово", "слова", "слов")} · {lines} стр.";
        }

        public static string Plural(int n, string one, string few, string many)
        {
            n = Math.Abs(n) % 100;
            int n1 = n % 10;
            if (n > 10 && n < 20) return many;
            if (n1 > 1 && n1 < 5) return few;
            if (n1 == 1) return one;
            return many;
        }
    }
}
