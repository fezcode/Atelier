using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Atelier.Theme
{
    /// <summary>What the OS paints behind the window before Atelier's own glass goes on top.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum BackdropMaterial
    {
        /// <summary>Windows 11 Mica: the wallpaper, heavily blurred and tinted. Falls back to acrylic.</summary>
        Mica,
        /// <summary>Acrylic: whatever is behind the window, frosted.</summary>
        Acrylic,
        /// <summary>A plain blur of what is behind the window, lighter than acrylic.</summary>
        Blur,
        /// <summary>No see-through at all; the tint is painted opaque.</summary>
        Solid,
    }

    /// <summary>
    /// The user's View &gt; Appearance choices. Every slider is 0..1; <see cref="GlassTheme"/>
    /// turns them into brushes.
    /// </summary>
    public sealed class Appearance
    {
        [JsonPropertyName("material")]     public BackdropMaterial Material { get; set; } = BackdropMaterial.Mica;
        /// <summary>Frost and edge light on the glass: 0 is flat tinted panes, 1 is a strong sheen.</summary>
        [JsonPropertyName("glassiness")]   public double Glassiness   { get; set; } = 0.6;
        /// <summary>How much of the backdrop shows through: 0 is opaque, 1 is barely there.</summary>
        [JsonPropertyName("transparency")] public double Transparency { get; set; } = 0.55;
        /// <summary>How strongly the tint colours the dark glass.</summary>
        [JsonPropertyName("colorLevel")]   public double ColorLevel   { get; set; } = 0.5;
        [JsonPropertyName("tint")]         public string Tint         { get; set; } = GlassTheme.DefaultTint;

        public Appearance Clone() => (Appearance)MemberwiseClone();

        /// <summary>Clamps everything into range, so a hand-edited settings file cannot break the theme.</summary>
        public Appearance Normalised()
        {
            var a = Clone();
            a.Glassiness = Clamp01(a.Glassiness);
            a.Transparency = Clamp01(a.Transparency);
            a.ColorLevel = Clamp01(a.ColorLevel);
            if (!Enum.IsDefined(a.Material)) a.Material = BackdropMaterial.Mica;
            if (string.IsNullOrWhiteSpace(a.Tint) || !Color.TryParse(a.Tint, out _)) a.Tint = GlassTheme.DefaultTint;
            return a;
        }

        public Color TintColor => Color.TryParse(Tint, out var c) ? c : Color.Parse(GlassTheme.DefaultTint);

        private static double Clamp01(double v) => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0.5;
    }

    /// <summary>
    /// Atelier's look, taken from its icon: dark iridescent glass tinted violet, with sky and
    /// peach in the sheen. All of the app's chrome reads its colours from the resources this
    /// writes, through DynamicResource, so moving a slider restyles every open window live.
    /// </summary>
    public static class GlassTheme
    {
        public const string DefaultTint = "#8B6CF0";

        /// <summary>Tints lifted from the icon, plus a neutral for people who want no colour.</summary>
        public static readonly IReadOnlyList<(string Name, Color Color)> Presets = new[]
        {
            ("Amethyst", Color.Parse("#8B6CF0")),
            ("Lavender", Color.Parse("#B7A4F5")),
            ("Iris",     Color.Parse("#6F7DF7")),
            ("Sky",      Color.Parse("#6CC3F2")),
            ("Rose",     Color.Parse("#EC8DCB")),
            ("Peach",    Color.Parse("#FFA97E")),
            ("Graphite", Color.Parse("#8E93A3")),
        };

        private static readonly Color Ink = Color.Parse("#0C0A12");
        private static readonly Color Sky = Color.Parse("#8FD3FF");
        private static readonly Color Peach = Color.Parse("#FFB38A");
        private static readonly Color Pink = Color.Parse("#F4A6D7");

        public static Appearance Current { get; private set; } = new();

        /// <summary>Raised after <see cref="Apply"/>, so windows can re-pick their OS backdrop.</summary>
        public static event Action? Changed;

        public static void Apply(Appearance appearance)
        {
            Current = appearance.Normalised();
            if (Application.Current is { } app)
                Write(app);
            Changed?.Invoke();
        }

        /// <summary>
        /// Puts a window on the glass: asks the OS for the chosen backdrop and keeps it in step
        /// with later changes. Without composition (or with Solid) the base colour is painted
        /// opaque instead, since a transparent window there would render black.
        /// </summary>
        public static void Attach(Window window)
        {
            void Sync()
            {
                window.TransparencyLevelHint = Hints(Current.Material);
                // ClearType assumes an opaque background; on a see-through one it leaves
                // red and blue fringes on every glyph. Greyscale stays clean.
                RenderOptions.SetTextRenderingMode(window, TextRenderingMode.Antialias);
                bool seeThrough = Current.Material != BackdropMaterial.Solid
                    && window.ActualTransparencyLevel != WindowTransparencyLevel.None;
                window.Background = seeThrough ? Brushes.Transparent : new SolidColorBrush(Base(Current));
            }

            void OnChanged() => Sync();
            window.PropertyChanged += (_, e) =>
            {
                if (e.Property == TopLevel.ActualTransparencyLevelProperty) Sync();
            };
            Changed += OnChanged;
            window.Closed += (_, _) => Changed -= OnChanged;
            Sync();
        }

        /// <summary>
        /// The two glass layers every window sits on -- the tinted pane and the iridescent
        /// sheen -- with <paramref name="content"/> on top.
        /// </summary>
        public static Panel Host(Control content)
        {
            var backdrop = new Border { Classes = { "GlassBackdrop", "Dialog" }, IsHitTestVisible = false };
            var sheen = new Border { Classes = { "GlassSheen" }, IsHitTestVisible = false };
            return new Panel { Children = { backdrop, sheen, content } };
        }

        private static IReadOnlyList<WindowTransparencyLevel> Hints(BackdropMaterial m) => m switch
        {
            BackdropMaterial.Mica => new[] { WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur },
            BackdropMaterial.Acrylic => new[] { WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur },
            BackdropMaterial.Blur => new[] { WindowTransparencyLevel.Blur, WindowTransparencyLevel.AcrylicBlur },
            _ => new[] { WindowTransparencyLevel.None },
        };

        // ---- Colour maths -----------------------------------------------------------

        internal static Color Mix(Color a, Color b, double t)
        {
            t = Math.Clamp(t, 0, 1);
            byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
            return Color.FromArgb(L(a.A, b.A), L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
        }

        internal static Color Alpha(Color c, double a) =>
            Color.FromArgb((byte)Math.Round(Math.Clamp(a, 0, 1) * 255), c.R, c.G, c.B);

        private static Color White(double a) => Alpha(Colors.White, a);

        /// <summary>The tinted near-black everything is built on.</summary>
        internal static Color Base(Appearance a) => Mix(Ink, a.TintColor, 0.04 + 0.26 * a.ColorLevel);

        /// <summary>The accent as text and icon colour: the tint, lifted enough to read on dark glass.</summary>
        internal static Color AccentOf(Appearance a) => Mix(a.TintColor, Colors.White, 0.28);

        private static void Write(Application app)
        {
            var a = Current;
            double g = a.Glassiness, t = a.Transparency, c = a.ColorLevel;
            var tint = a.TintColor;
            var baseColor = Base(a);
            bool solid = a.Material == BackdropMaterial.Solid;

            // How much of the OS backdrop is let through. Solid keeps the panes opaque, and
            // so does the window itself (see Attach), whatever the slider says.
            double see = solid ? 0 : t;

            var accent = AccentOf(a);
            var r = app.Resources;

            void Brush(string key, Color color) => r[key] = new SolidColorBrush(color);

            // Never fully clear: past about 75% a bright wallpaper or window behind washes the
            // text out, and the slider stops being useful well before that anyway.
            Brush("Atelier.BackdropBrush", Alpha(baseColor, 1 - 0.75 * see));
            // Dialogs are for reading and deciding, so they keep most of their body whatever
            // the slider says -- they float over arbitrary content, often a bright photo.
            Brush("Atelier.DialogBackdropBrush", Alpha(baseColor, 1 - 0.28 * see));
            Brush("Atelier.ChromeBrush", Alpha(Mix(baseColor, Colors.White, 0.03), 0.62 - 0.34 * see));
            Brush("Atelier.PaneBrush", Alpha(Mix(baseColor, Colors.White, 0.02), 0.55 - 0.28 * see));
            Brush("Atelier.CardBrush", White(0.04 + 0.05 * g));
            Brush("Atelier.CardHoverBrush", White(0.08 + 0.07 * g));
            Brush("Atelier.CardPressedBrush", White(0.03 + 0.03 * g));
            Brush("Atelier.StrokeBrush", White(0.06 + 0.07 * g));
            Brush("Atelier.StrokeStrongBrush", White(0.12 + 0.10 * g));
            Brush("Atelier.DividerBrush", White(0.05 + 0.04 * g));
            Brush("Atelier.PopupBrush", Alpha(Mix(Ink, tint, 0.08 + 0.18 * c), 0.97));
            Brush("Atelier.PopupStrokeBrush", White(0.10 + 0.06 * g));

            Brush("Atelier.TextBrush", Color.Parse("#F5F2FF"));
            Brush("Atelier.TextSecondaryBrush", Mix(Color.Parse("#D2CDE2"), tint, 0.12 * c));
            Brush("Atelier.TextMutedBrush", Mix(Color.Parse("#A6A0BA"), tint, 0.15 * c));
            Brush("Atelier.TextFaintBrush", Mix(Color.Parse("#807A94"), tint, 0.15 * c));

            Brush("Atelier.AccentBrush", accent);
            // Primary buttons: the tint as one flat fill, deep enough for white text, like a
            // Windows accent button. A gradient here read as decoration rather than a control.
            Brush("Atelier.AccentSolidBrush", Mix(tint, Ink, 0.22));
            Brush("Atelier.AccentSolidHoverBrush", Mix(tint, Ink, 0.08));
            Brush("Atelier.AccentSolidPressedBrush", Mix(tint, Ink, 0.34));
            Brush("Atelier.AccentSoftBrush", Alpha(tint, 0.18 + 0.14 * c));
            Brush("Atelier.AccentStrokeBrush", Alpha(Mix(tint, Colors.White, 0.18), 0.85));
            Brush("Atelier.DangerBrush", Color.Parse("#FF8FA3"));
            Brush("Atelier.DangerSoftBrush", Color.Parse("#33FF6F8A"));
            Brush("Atelier.SuccessBrush", Color.Parse("#86E3B5"));
            Brush("Atelier.WarmBrush", Mix(Peach, tint, 0.1));

            // The icon's iridescence: pink at one end, sky at the other, the tint between.
            r["Atelier.AccentGradientBrush"] = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Mix(tint, Pink, 0.40), 0),
                    new GradientStop(Mix(tint, Colors.White, 0.12), 0.5),
                    new GradientStop(Mix(tint, Sky, 0.50), 1),
                },
            };

            // Frost across the whole window: a white glint top-left fading out, then a
            // faint sky-and-peach caustic in the far corner, scaled by glassiness.
            r["Atelier.SheenBrush"] = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(White(0.10 * g), 0),
                    new GradientStop(Alpha(Mix(tint, Colors.White, 0.4), 0.05 * g * (0.4 + c)), 0.25),
                    new GradientStop(White(0), 0.55),
                    new GradientStop(Alpha(Sky, 0.05 * g * (0.3 + c)), 0.82),
                    new GradientStop(Alpha(Peach, 0.07 * g * (0.3 + c)), 1),
                },
            };

            // Edge light on cards and pills: bright along the top, as glass catches it.
            r["Atelier.RimBrush"] = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(White(0.10 + 0.22 * g), 0),
                    new GradientStop(White(0.04 + 0.04 * g), 0.6),
                    new GradientStop(White(0.03 + 0.03 * g), 1),
                },
            };

            // Fluent's own controls (combo boxes, text boxes, flyouts, sliders) pick these up.
            var card = r["Atelier.CardBrush"]!;
            var hover = r["Atelier.CardHoverBrush"]!;
            var pressed = r["Atelier.CardPressedBrush"]!;
            var stroke = r["Atelier.StrokeBrush"]!;
            var strong = r["Atelier.StrokeStrongBrush"]!;
            var popup = r["Atelier.PopupBrush"]!;
            var popupStroke = r["Atelier.PopupStrokeBrush"]!;
            var soft = r["Atelier.AccentSoftBrush"]!;
            var accentBrush = r["Atelier.AccentBrush"]!;
            var text = r["Atelier.TextBrush"]!;

            foreach (var (key, value) in new (string, object)[]
            {
                ("ButtonBackground", card), ("ButtonBackgroundPointerOver", hover), ("ButtonBackgroundPressed", pressed),
                ("ButtonBorderBrush", stroke), ("ButtonBorderBrushPointerOver", strong), ("ButtonBorderBrushPressed", stroke),
                ("ButtonForeground", text), ("ButtonForegroundPointerOver", text), ("ButtonForegroundPressed", text),
                ("ToggleButtonBackground", card), ("ToggleButtonBackgroundPointerOver", hover), ("ToggleButtonBackgroundPressed", pressed),
                ("ToggleButtonBackgroundChecked", soft), ("ToggleButtonBackgroundCheckedPointerOver", soft), ("ToggleButtonBackgroundCheckedPressed", soft),
                ("ToggleButtonBorderBrushChecked", r["Atelier.AccentStrokeBrush"]!), ("ToggleButtonForegroundChecked", text),
                ("ToggleButtonForegroundCheckedPointerOver", text),
                ("TextControlBackground", card), ("TextControlBackgroundPointerOver", hover), ("TextControlBackgroundFocused", popup),
                ("TextControlBorderBrush", stroke), ("TextControlBorderBrushPointerOver", strong), ("TextControlBorderBrushFocused", accentBrush),
                ("TextControlForeground", text), ("TextControlForegroundFocused", text), ("TextControlForegroundPointerOver", text),
                ("ComboBoxBackground", card), ("ComboBoxBackgroundPointerOver", hover), ("ComboBoxBackgroundPressed", pressed),
                ("ComboBoxBorderBrush", stroke), ("ComboBoxBorderBrushPointerOver", strong),
                ("ComboBoxDropDownBackground", popup), ("ComboBoxDropDownBorderBrush", popupStroke),
                ("MenuFlyoutPresenterBackground", popup), ("MenuFlyoutPresenterBorderBrush", popupStroke),
                ("MenuFlyoutItemBackgroundPointerOver", hover), ("MenuFlyoutItemBackgroundPressed", pressed),
                ("MenuFlyoutSubItemBackgroundPointerOver", hover), ("MenuFlyoutSubItemBackgroundSubMenuOpened", hover),
                ("FlyoutPresenterBackground", popup), ("FlyoutBorderThemeBrush", popupStroke),
                ("ToolTipBackground", popup), ("ToolTipBorderBrush", popupStroke), ("ToolTipForeground", text),
                ("SystemControlHighlightListAccentLowBrush", soft), ("SystemControlHighlightListAccentMediumBrush", soft),
                ("SystemControlHighlightListAccentHighBrush", soft), ("SystemControlHighlightListLowBrush", hover),
                ("SystemControlHighlightListMediumBrush", pressed),
            })
                r[key] = value;

            // Sliders, check boxes and focus rings come off the Fluent accent.
            if (app.Styles.OfType<FluentTheme>().FirstOrDefault() is { } fluent
                && fluent.Palettes.TryGetValue(ThemeVariant.Dark, out var palette))
                palette.Accent = Mix(tint, Colors.White, 0.12);
        }
    }
}
