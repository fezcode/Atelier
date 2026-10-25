using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Atelier.Theme
{
    /// <summary>
    /// Shortcut labels as people write them. Avalonia prints punctuation keys by their
    /// enum names, so Ctrl+, showed in the menu as "Ctrl+OemComma".
    /// </summary>
    public static class ShortcutText
    {
        public static string Format(KeyGesture gesture) =>
            gesture.ToString()
                .Replace("OemComma", ",")
                .Replace("OemPeriod", ".")
                .Replace("OemPlus", "+")
                .Replace("OemMinus", "-")
                .Replace("OemOpenBrackets", "[")
                .Replace("OemCloseBrackets", "]")
                .Replace("OemQuestion", "/")
                .Replace("OemSemicolon", ";");

        private static bool _installed;

        /// <summary>
        /// Rewrites the gesture column of every menu item once its template is up. The
        /// template binds that text straight to <see cref="KeyGesture.ToString()"/>; a local
        /// value on the TextBlock outranks the template binding.
        /// </summary>
        public static void Install()
        {
            if (_installed) return;
            _installed = true;

            TemplatedControl.TemplateAppliedEvent.AddClassHandler<MenuItem>((item, _) =>
            {
                if (item.InputGesture is not { } gesture) return;
                var raw = gesture.ToString();
                var friendly = Format(gesture);
                if (raw == friendly) return;

                // The parts only exist once the template has been laid out.
                Dispatcher.UIThread.Post(() =>
                {
                    foreach (var text in item.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text == raw))
                        text.Text = friendly;
                }, DispatcherPriority.Loaded);
            });
        }
    }
}
