using System;
using System.Globalization;
using Atelier.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Atelier.Views
{
    /// <summary>
    /// Drawn on top of the canvas in edit mode: the selection's marching ants, the
    /// brush footprint under the pointer and, zoomed right in, a pixel grid.
    ///
    /// It sits inside the zoomed content, so it works in canvas pixels like everything
    /// else there -- and divides its line widths by the zoom so the outlines stay one
    /// screen pixel wide however far in or out the view is.
    /// </summary>
    public class PaintOverlay : Control
    {
        public static readonly StyledProperty<double> ZoomProperty =
            AvaloniaProperty.Register<PaintOverlay, double>(nameof(Zoom), 1.0);

        static PaintOverlay()
        {
            AffectsRender<PaintOverlay>(ZoomProperty);
        }

        public double Zoom
        {
            get => GetValue(ZoomProperty);
            set => SetValue(ZoomProperty, value);
        }

        public MainWindowViewModel? Source { get; set; }

        /// <summary>Past this many lines the grid costs more than it shows.</summary>
        private const int MaxGridLines = 8000;

        private static readonly IBrush GridBrush = new SolidColorBrush(Color.FromArgb(0x50, 0x80, 0x80, 0x80));
        private static readonly IBrush AntsLight = Brushes.White;
        private static readonly IBrush AntsDark = Brushes.Black;

        public override void Render(DrawingContext context)
        {
            var vm = Source;
            if (vm == null || !vm.IsEditMode || vm.Document is not { } doc) return;

            double t = 1.0 / Math.Max(Zoom, 0.01);

            if (vm.ShowGridlines && Zoom >= 4 && doc.Width + doc.Height <= MaxGridLines)
            {
                var pen = new Pen(GridBrush, t);
                for (int x = 0; x <= doc.Width; x++) context.DrawLine(pen, new Point(x, 0), new Point(x, doc.Height));
                for (int y = 0; y <= doc.Height; y++) context.DrawLine(pen, new Point(0, y), new Point(doc.Width, y));
            }

            if (vm.SelectionGeometry is { } outline)
            {
                context.DrawGeometry(null, new Pen(AntsLight, t), outline);
                context.DrawGeometry(null, new Pen(AntsDark, t, new DashStyle(new double[] { 4, 4 }, 0)), outline);
            }

            if (vm.BrushCursor is { } r)
            {
                var light = new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)), t);
                var dark = new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)), t);
                var inner = r.Deflate(t);
                if (vm.BrushCursorIsSquare)
                {
                    context.DrawRectangle(null, dark, r);
                    context.DrawRectangle(null, light, inner);
                }
                else
                {
                    context.DrawEllipse(null, dark, r);
                    context.DrawEllipse(null, light, inner);
                }
            }
        }
    }

    /// <summary>
    /// Binds a radio button to one value of an enum property: checked when the property
    /// holds the value named by the parameter, and setting it when clicked.
    /// </summary>
    public sealed class EnumEqualsConverter : IValueConverter
    {
        public static readonly EnumEqualsConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value != null && parameter is string name && string.Equals(value.ToString(), name, StringComparison.Ordinal);

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not true || parameter is not string name) return BindingOperations.DoNothing;
            var type = Nullable.GetUnderlyingType(targetType) ?? targetType;
            return type.IsEnum && Enum.TryParse(type, name, out var result) ? result : BindingOperations.DoNothing;
        }
    }
}
