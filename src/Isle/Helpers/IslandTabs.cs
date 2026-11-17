using System;
using System.Collections.Generic;
using System.Linq;
using Isle.Services;

namespace Isle.Helpers
{
    /// <summary>Вкладки раскрытой чёлки: какие есть, в каком порядке и какие спрятаны (настраивается во «Внешнем виде»).</summary>
    public static class IslandTabs
    {
        public record Tab(string Id, string Title, string Icon);

        /// <summary>Все вкладки в порядке по умолчанию.</summary>
        public static readonly Tab[] All =
        {
            new("home", "Главная", "Home"),
            new("clip", "Буфер", "Clipboard"),
            new("shelf", "Полка", "Tray"),
            new("phone", "Телефон", "Phone"),
            new("text", "Текст", "Text"),
            new("chat", "Чат", "Chat"),
            new("notes", "Заметка", "Note"),
            new("weather", "Погода", "Sun"),
            new("controls", "Управление", "Sliders"),
        };

        public static Tab Get(string id) => All.FirstOrDefault(t => t.Id == id);

        /// <summary>
        /// Порядок из настроек; вкладки, которых там нет (появились в обновлении), встают на своё место
        /// по умолчанию — после той, что стоит перед ними в списке по умолчанию.
        /// </summary>
        public static List<string> Order()
        {
            var saved = (AppSettings.Current.TabOrder ?? new List<string>()).Where(id => Get(id) != null).Distinct().ToList();
            if (saved.Count == 0) return All.Select(t => t.Id).ToList();
            for (int i = 0; i < All.Length; i++)
            {
                var id = All[i].Id;
                if (saved.Contains(id)) continue;
                int after = -1;
                for (int j = i - 1; j >= 0 && after < 0; j--) after = saved.IndexOf(All[j].Id);
                saved.Insert(after + 1, id);
            }
            return saved;
        }

        public static bool IsHidden(string id) => id != "home" && (AppSettings.Current.HiddenTabs?.Contains(id) ?? false);

        public static List<string> Visible() => Order().Where(id => !IsHidden(id)).ToList();

        public static void Save(List<string> order, IEnumerable<string> hidden)
        {
            AppSettings.Current.TabOrder = order.ToList();
            AppSettings.Current.HiddenTabs = hidden.Where(h => h != "home").Distinct().ToList();
            AppSettings.Current.Save();
        }
    }
}
