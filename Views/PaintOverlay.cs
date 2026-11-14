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

            if (vm.CropFrame is { } crop) DrawCrop(context, vm, crop, new Rect(0, 0, doc.Width, doc.Height), t);

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

        private static readonly IBrush CropShade = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0));
        private static readonly IBrush CropShadeDrag = new SolidColorBrush(Color.FromArgb(0xB8, 0, 0, 0));
        private static readonly IBrush CropGrid = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF));
        private static readonly IBrush CropGridDrag = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF));
        private static readonly IBrush CropEdgeShadow = new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0));
        private static readonly IBrush BadgeBack = new SolidColorBrush(Color.FromArgb(0xD0, 0x14, 0x14, 0x14));
        private static readonly Typeface BadgeFace = new("Segoe UI", FontStyle.Normal, FontWeight.SemiBold);

        /// <summary>
        /// The crop frame, as Preview draws it: the rest of the picture dimmed, a thin white
        /// border, rule-of-thirds lines inside and heavy corner brackets to grab. All sizes
        /// are in screen pixels, scaled by <paramref name="t"/> (one screen pixel in canvas units).
        /// </summary>
        private static void DrawCrop(DrawingContext context, MainWindowViewModel vm, Rect f, Rect canvas, double t)
        {
            bool dragging = vm.IsCropDragging;
            var shade = dragging ? CropShadeDrag : CropShade;

            // Four bands round the frame, so nothing is drawn twice over the corners.
            context.FillRectangle(shade, new Rect(canvas.Left, canvas.Top, canvas.Width, Math.Max(0, f.Top - canvas.Top)));
            context.FillRectangle(shade, new Rect(canvas.Left, f.Bottom, canvas.Width, Math.Max(0, canvas.Bottom - f.Bottom)));
            context.FillRectangle(shade, new Rect(canvas.Left, f.Top, Math.Max(0, f.Left - canvas.Left), f.Height));
            context.FillRectangle(shade, new Rect(f.Right, f.Top, Math.Max(0, canvas.Right - f.Right), f.Height));

            // Rule of thirds.
            var grid = new Pen(dragging ? CropGridDrag : CropGrid, t);
            for (int i = 1; i <= 2; i++)
            {
                double x = f.Left + f.Width * i / 3, y = f.Top + f.Height * i / 3;
                context.DrawLine(grid, new Point(x, f.Top), new Point(x, f.Bottom));
                context.DrawLine(grid, new Point(f.Left, y), new Point(f.Right, y));
            }

            // Border, with a soft dark line outside so it reads on white pictures too.
            context.DrawRectangle(null, new Pen(CropEdgeShadow, 3 * t), f.Inflate(t));
            context.DrawRectangle(null, new Pen(Brushes.White, 1.5 * t), f);

            // Corner brackets and edge ticks, sitting just inside the frame.
            double thick = 4 * t;
            double len = Math.Min(22 * t, Math.Min(f.Width, f.Height) / 3);
            double h = thick / 2;
            var bracket = new Pen(Brushes.White, thick, lineCap: PenLineCap.Square);
            void Corner(double x, double y, double dx, double dy)
            {
                double cx = x + dx * h, cy = y + dy * h;
                context.DrawLine(bracket, new Point(cx, cy), new Point(cx + dx * len, cy));
                context.DrawLine(bracket, new Point(cx, cy), new Point(cx, cy + dy * len));
            }
            Corner(f.Left, f.Top, 1, 1);
            Corner(f.Right, f.Top, -1, 1);
            Corner(f.Left, f.Bottom, 1, -1);
            Corner(f.Right, f.Bottom, -1, -1);

            double tick = len * 0.8;
            if (f.Width > 3 * len)
            {
                context.DrawLine(bracket, new Point(f.Center.X - tick / 2, f.Top + h), new Point(f.Center.X + tick / 2, f.Top + h));
                context.DrawLine(bracket, new Point(f.Center.X - tick / 2, f.Bottom - h), new Point(f.Center.X + tick / 2, f.Bottom - h));
            }
            if (f.Height > 3 * len)
            {
                context.DrawLine(bracket, new Point(f.Left + h, f.Center.Y - tick / 2), new Point(f.Left + h, f.Center.Y + tick / 2));
                context.DrawLine(bracket, new Point(f.Right - h, f.Center.Y - tick / 2), new Point(f.Right - h, f.Center.Y + tick / 2));
            }

            // The size, in a dark pill under the frame (or inside it, at the bottom of the picture).
            var text = new FormattedText(vm.CropSizeText, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                BadgeFace, 12 * t, Brushes.White);
            double padX = 8 * t, padY = 4 * t, gap = 8 * t;
            double bw = text.Width + 2 * padX, bh = text.Height + 2 * padY;
            double bx = f.Center.X - bw / 2;
            double by = f.Bottom + gap + bh <= canvas.Bottom ? f.Bottom + gap : f.Bottom - gap - bh;
            bx = Math.Clamp(bx, canvas.Left, Math.Max(canvas.Left, canvas.Right - bw));
            if (bh < f.Height || by > f.Bottom)
            {
                context.DrawRectangle(BadgeBack, null, new Rect(bx, by, bw, bh), bh / 2, bh / 2);
                context.DrawText(text, new Point(bx + padX, by + padY));
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
