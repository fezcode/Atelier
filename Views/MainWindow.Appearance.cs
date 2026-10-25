using System;
using System.Linq;
using System.Threading.Tasks;
using Atelier.Theme;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Layout;
using Avalonia.Media;

namespace Atelier.Views
{
    public partial class MainWindow
    {
        /// <summary>Puts the window on the saved glass. Called once from the constructor, after settings load.</summary>
        private void InitAppearance()
        {
            ShortcutText.Install();
            GlassTheme.Apply(_userSettings.Appearance);
            GlassTheme.Attach(this);
        }

        /// <summary>View &gt; Appearance and the top bar's Appearance button.</summary>
        public void Appearance_Click(object? sender, RoutedEventArgs e) => _ = ShowAppearanceAsync();

        private bool _appearanceOpen;

        /// <summary>
        /// Material, tint, glassiness, transparency and colour level. Every control applies as
        /// it moves -- the main window behind the dialog is the preview -- and the result is
        /// saved when the dialog closes.
        /// </summary>
        private async Task ShowAppearanceAsync()
        {
            if (_appearanceOpen) return;
            _appearanceOpen = true;
            try
            {
                var working = GlassTheme.Current.Clone();
                bool syncing = false;

                void Push()
                {
                    if (syncing) return;
                    GlassTheme.Apply(working);
                }

                // ---- Material ----
                var materials = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
                var materialButtons = Enum.GetValues<BackdropMaterial>().Select(m =>
                {
                    var rb = new RadioButton
                    {
                        Classes = { "Segment" },
                        Content = m.ToString(),
                        GroupName = "AppearanceMaterial",
                        IsChecked = working.Material == m,
                        MinWidth = 78,
                        Tag = m,
                    };
                    rb.IsCheckedChanged += (_, _) =>
                    {
                        if (rb.IsChecked != true) return;
                        working.Material = m;
                        Push();
                    };
                    materials.Children.Add(rb);
                    return rb;
                }).ToList();
                var materialTrack = new Border { Classes = { "Card" }, CornerRadius = new CornerRadius(10), Padding = new Thickness(3), Child = materials, HorizontalAlignment = HorizontalAlignment.Left };

                // ---- Sliders ----
                var glass = PercentSlider(working.Glassiness, v => { working.Glassiness = v; Push(); }, out var glassRow,
                    "GLASSINESS", "Frost and edge light on the panes.");
                var transparency = PercentSlider(working.Transparency, v => { working.Transparency = v; Push(); }, out var transparencyRow,
                    "TRANSPARENCY", "How much of the backdrop shows through.");
                var colorLevel = PercentSlider(working.ColorLevel, v => { working.ColorLevel = v; Push(); }, out var colorRow,
                    "COLOUR LEVEL", "How strongly the tint colours the glass.");

                // ---- Tint ----
                var hue = new Slider { Minimum = 0, Maximum = 360, Value = working.TintColor.ToHsv().H, Margin = new Thickness(0, -6, 0, 0) };
                var swatches = new WrapPanel { Orientation = Orientation.Horizontal };
                var swatchButtons = new System.Collections.Generic.List<(Button Button, Border Ring, Color Color)>();
                foreach (var p in GlassTheme.Presets)
                {
                    var ring = new Border
                    {
                        Width = 30, Height = 30, CornerRadius = new CornerRadius(15),
                        BorderThickness = new Thickness(2), Padding = new Thickness(3),
                        Child = new Ellipse { Fill = new SolidColorBrush(p.Color), Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1 },
                    };
                    var b = new Button
                    {
                        Classes = { "Icon" }, Padding = new Thickness(2), Margin = new Thickness(0, 0, 4, 4),
                        Content = ring, Tag = p.Color,
                    };
                    ToolTip.SetTip(b, p.Name);
                    b.Click += (_, _) =>
                    {
                        working.Tint = p.Color.ToString();
                        syncing = true;
                        hue.Value = p.Color.ToHsv().H;
                        syncing = false;
                        Push();
                        MarkSwatch();
                    };
                    swatches.Children.Add(b);
                    swatchButtons.Add((b, ring, p.Color));
                }

                void MarkSwatch()
                {
                    var current = working.TintColor;
                    foreach (var s in swatchButtons)
                        s.Ring.BorderBrush = s.Color == current
                            ? new SolidColorBrush(Colors.White)
                            : Brushes.Transparent;
                }
                MarkSwatch();

                hue.PropertyChanged += (_, e) =>
                {
                    if (e.Property != RangeBase.ValueProperty || syncing) return;
                    var hsv = working.TintColor.ToHsv();
                    // A grey tint has no hue to turn; give the slider some colour to work with.
                    double s = hsv.S < 0.2 ? 0.55 : hsv.S;
                    double v = hsv.V < 0.5 ? 0.94 : hsv.V;
                    working.Tint = HsvColor.ToRgb(hue.Value, s, v).ToString();
                    Push();
                    MarkSwatch();
                };

                // A rainbow under the hue slider, so it reads as a hue control at a glance.
                var rainbow = new Border
                {
                    Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 4, 0, 0),
                    Background = new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                        EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                        GradientStops = new GradientStops
                        {
                            new GradientStop(Color.Parse("#F07070"), 0), new GradientStop(Color.Parse("#F0D070"), 0.17),
                            new GradientStop(Color.Parse("#80E090"), 0.33), new GradientStop(Color.Parse("#70D8F0"), 0.5),
                            new GradientStop(Color.Parse("#7080F0"), 0.67), new GradientStop(Color.Parse("#D070E8"), 0.83),
                            new GradientStop(Color.Parse("#F07070"), 1),
                        },
                    },
                };

                // Transparency means nothing on a solid backdrop.
                void SyncEnabled() => transparencyRow.IsEnabled = working.Material != BackdropMaterial.Solid;
                foreach (var rb in materialButtons) rb.IsCheckedChanged += (_, _) => SyncEnabled();
                SyncEnabled();

                var reset = DialogButton("Reset");
                var done = DialogButton("Done", isDefault: true);

                var content = new DockPanel
                {
                    LastChildFill = true,
                    Children =
                    {
                        new Grid
                        {
                            [DockPanel.DockProperty] = Dock.Bottom,
                            Margin = new Thickness(0, 18, 0, 0),
                            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                            Children = { reset, WithColumn(done, 2) },
                        },
                        new StackPanel
                            {
                                Spacing = 14,
                                Children =
                                {
                                    new StackPanel
                                    {
                                        Spacing = 4,
                                        Children =
                                        {
                                            new TextBlock { Text = "Appearance", FontSize = 22, FontWeight = FontWeight.SemiBold },
                                            new TextBlock { Text = "Tune the glass Atelier sits on. Changes show as you go.", FontSize = 12.5, TextWrapping = TextWrapping.Wrap, [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Atelier.TextMutedBrush") },
                                        },
                                    },
                                    new StackPanel { Spacing = 8, Children = { Eyebrow("MATERIAL"), materialTrack } },
                                    new StackPanel { Spacing = 8, Children = { Eyebrow("TINT"), swatches, rainbow, hue } },
                                    glassRow,
                                    transparencyRow,
                                    colorRow,
                                },
                            },
                    },
                };

                var dialog = Dialog("Appearance", 440, 760, content);

                reset.Click += (_, _) =>
                {
                    var d = new Appearance();
                    working = d;
                    syncing = true;
                    foreach (var rb in materialButtons) rb.IsChecked = (BackdropMaterial)rb.Tag! == d.Material;
                    glass.Value = d.Glassiness * 100;
                    transparency.Value = d.Transparency * 100;
                    colorLevel.Value = d.ColorLevel * 100;
                    hue.Value = d.TintColor.ToHsv().H;
                    syncing = false;
                    GlassTheme.Apply(working);
                    MarkSwatch();
                    SyncEnabled();
                };
                done.Click += (_, _) => dialog.Close();
                dialog.KeyDown += (_, k) => { if (k.Key == Key.Escape) dialog.Close(); };

                await dialog.ShowDialog(this);

                _userSettings.Appearance = GlassTheme.Current.Clone();
                _userSettings.Save();
            }
            finally
            {
                _appearanceOpen = false;
            }
        }

        private static Control WithColumn(Control c, int column)
        {
            Grid.SetColumn(c, column);
            return c;
        }

        private static TextBlock Eyebrow(string text) => new() { Text = text, Classes = { "Eyebrow" } };

        /// <summary>A labelled 0-100% slider; <paramref name="set"/> receives 0..1.</summary>
        private static Slider PercentSlider(double value, Action<double> set, out Control row, string label, string hint)
        {
            var slider = new Slider { Minimum = 0, Maximum = 100, Value = Math.Round(value * 100) };
            var readout = new TextBlock
            {
                Text = $"{slider.Value:0}%", FontSize = 12, FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Right,
                [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Atelier.AccentBrush"),
            };
            slider.PropertyChanged += (_, e) =>
            {
                if (e.Property != RangeBase.ValueProperty) return;
                readout.Text = $"{slider.Value:0}%";
                set(slider.Value / 100);
            };

            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            header.Children.Add(Eyebrow(label));
            header.Children.Add(WithColumn(readout, 1));

            row = new StackPanel
            {
                Spacing = 0,
                Children =
                {
                    header,
                    new TextBlock { Text = hint, FontSize = 12, Margin = new Thickness(0, 3, 0, 0), [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Atelier.TextFaintBrush") },
                    slider,
                },
            };
            return slider;
        }
    }
}
