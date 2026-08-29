using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Atelier.Hoswl
{
    /// <summary>
    /// Turns the window's real <see cref="Menu"/> into the hoswl "menus" JSON array,
    /// so Hisashi shows exactly what the in-app strip shows — same headers, gestures,
    /// enabled/checked state, separators and submenus — with no second definition to
    /// keep in step. Ids are positional (<c>m0.3</c>, <c>m0.4.1</c>) and map back to
    /// the live <see cref="MenuItem"/>s for dispatch.
    /// </summary>
    public static class HoswlMenuBuilder
    {
        public static string Build(Menu menu, IDictionary<string, MenuItem> map)
        {
            map.Clear();
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartArray();
                int i = 0;
                foreach (var top in menu.Items)
                {
                    if (top is not MenuItem m || !m.IsVisible) continue;
                    var id = "m" + i++;
                    w.WriteStartObject();
                    w.WriteString("id", id);
                    w.WriteString("label", Label(m));
                    w.WritePropertyName("items");
                    WriteItems(w, m, id, map);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        private static void WriteItems(Utf8JsonWriter w, MenuItem parent, string parentId, IDictionary<string, MenuItem> map)
        {
            w.WriteStartArray();
            int j = 0;
            foreach (var child in parent.Items)
            {
                if (child is Separator sep)
                {
                    if (!sep.IsVisible) continue;
                    w.WriteStartObject();
                    w.WriteBoolean("sep", true);
                    w.WriteEndObject();
                    j++;
                    continue;
                }
                if (child is not MenuItem item || !item.IsVisible) continue;
                var id = parentId + "." + j++;
                map[id] = item;
                w.WriteStartObject();
                w.WriteString("id", id);
                w.WriteString("label", Label(item));
                if (HasChildren(item))
                {
                    w.WritePropertyName("items");
                    WriteItems(w, item, id, map);
                }
                else
                {
                    var key = item.InputGesture?.ToString();
                    if (!string.IsNullOrEmpty(key)) w.WriteString("key", key);
                    if (item.ToggleType == MenuItemToggleType.CheckBox) w.WriteBoolean("check", item.IsChecked);
                }
                if (!item.IsEnabled) w.WriteBoolean("enabled", false);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        private static bool HasChildren(MenuItem item)
        {
            foreach (var c in item.Items) if (c is MenuItem) return true;
            return false;
        }

        /// <summary>Header text without the access-key underscore ("_Open..." → "Open...", "Fi_t" → "Fit").</summary>
        public static string Label(MenuItem item)
        {
            var s = item.Header?.ToString() ?? "";
            var sb = new StringBuilder(s.Length);
            for (int k = 0; k < s.Length; k++)
            {
                if (s[k] == '_')
                {
                    if (k + 1 < s.Length && s[k + 1] == '_') { sb.Append('_'); k++; }
                    continue;
                }
                sb.Append(s[k]);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Act on a click that came back from Hisashi: what a real click on the item
        /// would do — toggle a checkable item, then raise its Click so the XAML
        /// handler runs. Disabled and submenu-header rows are ignored.
        /// </summary>
        public static bool Dispatch(IDictionary<string, MenuItem> map, string id)
        {
            if (!map.TryGetValue(id, out var item) || !item.IsEnabled || HasChildren(item)) return false;
            if (item.ToggleType == MenuItemToggleType.CheckBox) item.IsChecked = !item.IsChecked;
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            return true;
        }
    }
}
