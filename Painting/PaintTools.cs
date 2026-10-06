using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Atelier.Painting
{
    public enum PaintTool
    {
        Select,
        FreeSelect,
        Pencil,
        Brush,
        Fill,
        Text,
        Eraser,
        Picker,
        Magnifier,
        Shape,
    }

    /// <summary>The nine brushes in Paint's Brushes gallery, in its order.</summary>
    public enum BrushKind
    {
        Brush,
        Calligraphy1,
        Calligraphy2,
        Airbrush,
        Oil,
        Crayon,
        Marker,
        NaturalPencil,
        Watercolor,
    }

    /// <summary>Paint's Shapes gallery, in its order.</summary>
    public enum ShapeKind
    {
        Line,
        Curve,
        Oval,
        Rectangle,
        RoundedRectangle,
        Polygon,
        Triangle,
        RightTriangle,
        Diamond,
        Pentagon,
        Hexagon,
        RightArrow,
        LeftArrow,
        UpArrow,
        DownArrow,
        FourPointStar,
        FivePointStar,
        SixPointStar,
        RoundedCallout,
        OvalCallout,
        CloudCallout,
        Heart,
        Lightning,
    }

    /// <summary>
    /// Every closed shape in the gallery as a path fitted to a rectangle. Built from a
    /// unit-square outline and scaled, rather than drawn with a scaled canvas, so the
    /// outline keeps the width the user picked however the shape is stretched.
    /// </summary>
    public static class ShapeGeometry
    {
        public static string DisplayName(ShapeKind kind) => kind switch
        {
            ShapeKind.RoundedRectangle => "Rounded rectangle",
            ShapeKind.RightTriangle => "Right triangle",
            ShapeKind.RightArrow => "Right arrow",
            ShapeKind.LeftArrow => "Left arrow",
            ShapeKind.UpArrow => "Up arrow",
            ShapeKind.DownArrow => "Down arrow",
            ShapeKind.FourPointStar => "Four-point star",
            ShapeKind.FivePointStar => "Five-point star",
            ShapeKind.SixPointStar => "Six-point star",
            ShapeKind.RoundedCallout => "Rounded rectangular callout",
            ShapeKind.OvalCallout => "Oval callout",
            ShapeKind.CloudCallout => "Cloud callout",
            _ => kind.ToString(),
        };

        /// <summary>Line, Curve and Polygon are built point by point, not fitted to a box.</summary>
        public static bool IsBoxed(ShapeKind kind) =>
            kind is not (ShapeKind.Line or ShapeKind.Curve or ShapeKind.Polygon);

        public static SKPath Build(ShapeKind kind, SKRect r)
        {
            r = r.Standardized;
            var unit = Unit(kind);
            var matrix = SKMatrix.CreateScale(Math.Max(r.Width, 0.01f), Math.Max(r.Height, 0.01f));
            matrix = matrix.PostConcat(SKMatrix.CreateTranslation(r.Left, r.Top));

            if (kind == ShapeKind.RoundedRectangle)
            {
                // A fixed proportion of the shorter side, so a long thin bar does not end
                // up with corners rounder than it is tall.
                var path = new SKPath();
                float radius = Math.Min(r.Width, r.Height) / 6f;
                path.AddRoundRect(r, radius, radius);
                return path;
            }

            unit.Transform(matrix);
            return unit;
        }

        private static SKPath Polygon(params float[] xy)
        {
            var path = new SKPath();
            path.MoveTo(xy[0], xy[1]);
            for (int i = 2; i < xy.Length; i += 2) path.LineTo(xy[i], xy[i + 1]);
            path.Close();
            return path;
        }

        private static SKPath Star(int points, float inner)
        {
            var path = new SKPath();
            for (int i = 0; i < points * 2; i++)
            {
                double angle = -Math.PI / 2 + i * Math.PI / points;
                float radius = i % 2 == 0 ? 0.5f : 0.5f * inner;
                float x = 0.5f + radius * (float)Math.Cos(angle);
                float y = 0.5f + radius * (float)Math.Sin(angle);
                if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
            }
            path.Close();
            return path;
        }

        private static SKPath Union(SKPath a, SKPath b)
        {
            var result = a.Op(b, SKPathOp.Union) ?? a;
            if (!ReferenceEquals(result, a)) a.Dispose();
            b.Dispose();
            return result;
        }

        private static SKPath Rotate(SKPath path, float degrees)
        {
            path.Transform(SKMatrix.CreateRotationDegrees(degrees, 0.5f, 0.5f));
            return path;
        }

        private static SKPath Unit(ShapeKind kind)
        {
            switch (kind)
            {
                case ShapeKind.Oval:
                {
                    var p = new SKPath();
                    p.AddOval(new SKRect(0, 0, 1, 1));
                    return p;
                }
                case ShapeKind.Triangle: return Polygon(0.5f, 0, 1, 1, 0, 1);
                case ShapeKind.RightTriangle: return Polygon(0, 0, 1, 1, 0, 1);
                case ShapeKind.Diamond: return Polygon(0.5f, 0, 1, 0.5f, 0.5f, 1, 0, 0.5f);
                case ShapeKind.Pentagon: return Polygon(0.5f, 0, 1, 0.38f, 0.81f, 1, 0.19f, 1, 0, 0.38f);
                case ShapeKind.Hexagon: return Polygon(0.25f, 0, 0.75f, 0, 1, 0.5f, 0.75f, 1, 0.25f, 1, 0, 0.5f);
                case ShapeKind.RightArrow: return Arrow();
                case ShapeKind.LeftArrow: return Rotate(Arrow(), 180);
                case ShapeKind.UpArrow: return Rotate(Arrow(), 270);
                case ShapeKind.DownArrow: return Rotate(Arrow(), 90);
                case ShapeKind.FourPointStar: return Star(4, 0.38f);
                case ShapeKind.FivePointStar: return Star(5, 0.4f);
                case ShapeKind.SixPointStar: return Star(6, 0.58f);
                case ShapeKind.RoundedCallout:
                {
                    var body = new SKPath();
                    body.AddRoundRect(new SKRect(0, 0, 1, 0.75f), 0.12f, 0.12f);
                    return Union(body, Polygon(0.18f, 0.7f, 0.12f, 1, 0.4f, 0.7f));
                }
                case ShapeKind.OvalCallout:
                {
                    var body = new SKPath();
                    body.AddOval(new SKRect(0, 0, 1, 0.78f));
                    return Union(body, Polygon(0.22f, 0.62f, 0.12f, 1, 0.42f, 0.74f));
                }
                case ShapeKind.CloudCallout: return Cloud();
                case ShapeKind.Heart:
                {
                    var p = new SKPath();
                    p.MoveTo(0.5f, 0.25f);
                    p.CubicTo(0.5f, 0.05f, 0f, 0f, 0f, 0.35f);
                    p.CubicTo(0f, 0.65f, 0.4f, 0.8f, 0.5f, 1f);
                    p.CubicTo(0.6f, 0.8f, 1f, 0.65f, 1f, 0.35f);
                    p.CubicTo(1f, 0f, 0.5f, 0.05f, 0.5f, 0.25f);
                    p.Close();
                    return p;
                }
                case ShapeKind.Lightning:
                    return Polygon(0.38f, 0, 0.62f, 0.3f, 0.52f, 0.36f, 0.8f, 0.6f, 0.7f, 0.65f, 1, 1,
                        0.45f, 0.72f, 0.55f, 0.67f, 0.18f, 0.47f, 0.3f, 0.41f, 0, 0.18f);
                default:
                {
                    var p = new SKPath();
                    p.AddRect(new SKRect(0, 0, 1, 1));
                    return p;
                }
            }
        }

        private static SKPath Arrow() => Polygon(0, 0.28f, 0.58f, 0.28f, 0.58f, 0, 1, 0.5f, 0.58f, 1, 0.58f, 0.72f, 0, 0.72f);

        private static SKPath Cloud()
        {
            var cloud = new SKPath();
            cloud.AddOval(new SKRect(0.05f, 0.12f, 0.45f, 0.5f));
            foreach (var r in new[]
                     {
                         new SKRect(0.3f, 0f, 0.7f, 0.4f),
                         new SKRect(0.55f, 0.08f, 0.98f, 0.5f),
                         new SKRect(0.5f, 0.32f, 0.92f, 0.72f),
                         new SKRect(0.15f, 0.34f, 0.6f, 0.75f),
                         new SKRect(0f, 0.3f, 0.3f, 0.62f),
                     })
            {
                var puff = new SKPath();
                puff.AddOval(r);
                cloud = Union(cloud, puff);
            }

            foreach (var r in new[] { new SKRect(0.16f, 0.8f, 0.26f, 0.9f), new SKRect(0.08f, 0.92f, 0.14f, 0.98f) })
                cloud.AddOval(r);
            return cloud;
        }

        /// <summary>
        /// The outline the toolbar draws for each shape -- the real geometry, so the
        /// gallery can never disagree with what gets drawn.
        /// </summary>
        public static string IconData(ShapeKind kind)
        {
            var box = new SKRect(1, 2, 17, 16);
            using var path = kind switch
            {
                ShapeKind.Line => LinePath(new SKPoint(2, 15), new SKPoint(16, 3)),
                ShapeKind.Curve => CurvePath(new SKPoint(2, 14), new SKPoint(6, 0), new SKPoint(12, 18), new SKPoint(16, 4)),
                ShapeKind.Polygon => PolygonIcon(),
                _ => Build(kind, box),
            };
            return path.ToSvgPathData();
        }

        private static SKPath PolygonIcon()
        {
            var p = Polygon(3, 3, 12, 2, 16, 9, 9, 16, 2, 12, 6, 8);
            return p;
        }

        public static SKPath LinePath(SKPoint a, SKPoint b)
        {
            var p = new SKPath();
            p.MoveTo(a);
            p.LineTo(b);
            return p;
        }

        public static SKPath CurvePath(SKPoint start, SKPoint c1, SKPoint c2, SKPoint end)
        {
            var p = new SKPath();
            p.MoveTo(start);
            p.CubicTo(c1, c2, end);
            return p;
        }

        public static SKPath PolylinePath(IReadOnlyList<SKPoint> points, bool close)
        {
            var p = new SKPath();
            if (points.Count == 0) return p;
            p.MoveTo(points[0]);
            for (int i = 1; i < points.Count; i++) p.LineTo(points[i]);
            if (close) p.Close();
            return p;
        }
    }

    /// <summary>
    /// How each brush lays paint down between two pointer positions.
    ///
    /// Most brushes are incremental: each move adds a segment on top of what is there.
    /// Marker and Watercolor are not -- their translucency must not build up where the
    /// stroke crosses itself -- so they are redrawn whole from the operation base on
    /// every move; see <see cref="IsRedrawn"/>.
    /// </summary>
    public static class BrushRenderer
    {
        public static string DisplayName(BrushKind kind) => kind switch
        {
            BrushKind.Calligraphy1 => "Calligraphy brush 1",
            BrushKind.Calligraphy2 => "Calligraphy brush 2",
            BrushKind.Oil => "Oil brush",
            BrushKind.NaturalPencil => "Natural pencil",
            BrushKind.Watercolor => "Watercolour brush",
            _ => kind.ToString(),
        };

        public static bool IsRedrawn(BrushKind kind) => kind is BrushKind.Marker or BrushKind.Watercolor;

        public static SKRectI Bounds(SKPoint a, SKPoint b, float size)
        {
            float pad = size + 3;
            return SKRectI.Ceiling(new SKRect(
                Math.Min(a.X, b.X) - pad, Math.Min(a.Y, b.Y) - pad,
                Math.Max(a.X, b.X) + pad, Math.Max(a.Y, b.Y) + pad));
        }

        /// <summary>Paint's pencil: hard-edged, no anti-aliasing, a single pixel at size 1.</summary>
        public static void Pencil(SKCanvas canvas, SKPoint a, SKPoint b, SKColor color, float size)
        {
            using var paint = new SKPaint
            {
                Color = color,
                IsAntialias = false,
                StrokeWidth = Math.Max(1, size),
                StrokeCap = size <= 1 ? SKStrokeCap.Butt : SKStrokeCap.Round,
                Style = SKPaintStyle.Stroke,
            };
            if (a == b || size <= 1)
            {
                // A 1px aliased line in Skia skips its last pixel, and a click with no
                // movement would draw nothing at all.
                foreach (var p in Steps(a, b, 1)) DotPencil(canvas, p, color, size);
                return;
            }
            canvas.DrawLine(a, b, paint);
        }

        private static void DotPencil(SKCanvas canvas, SKPoint p, SKColor color, float size)
        {
            using var paint = new SKPaint { Color = color, IsAntialias = false, Style = SKPaintStyle.Fill };
            if (size <= 1) canvas.DrawRect((float)Math.Floor(p.X), (float)Math.Floor(p.Y), 1, 1, paint);
            else canvas.DrawCircle(p, size / 2f, paint);
        }

        /// <summary>The eraser paints the background colour in a hard square.</summary>
        public static void Eraser(SKCanvas canvas, SKPoint a, SKPoint b, SKColor color, float size)
        {
            using var paint = new SKPaint { Color = color, IsAntialias = false, Style = SKPaintStyle.Fill };
            float half = Math.Max(1, size) / 2f;
            foreach (var p in Steps(a, b, Math.Max(1, half / 2)))
                canvas.DrawRect(p.X - half, p.Y - half, half * 2, half * 2, paint);
        }

        /// <summary>Points along a segment no further apart than <paramref name="spacing"/>, ends included.</summary>
        public static IEnumerable<SKPoint> Steps(SKPoint a, SKPoint b, float spacing)
        {
            float dx = b.X - a.X, dy = b.Y - a.Y;
            float length = (float)Math.Sqrt(dx * dx + dy * dy);
            int count = Math.Max(1, (int)Math.Ceiling(length / Math.Max(0.5f, spacing)));
            for (int i = 0; i <= count; i++)
                yield return new SKPoint(a.X + dx * i / count, a.Y + dy * i / count);
        }

        public static void Segment(SKCanvas canvas, BrushKind kind, SKPoint a, SKPoint b, SKColor color, float size, Random random)
        {
            size = Math.Max(1, size);
            switch (kind)
            {
                case BrushKind.Brush:
                {
                    using var paint = Stroke(color, size, SKStrokeCap.Round);
                    if (a == b) { paint.Style = SKPaintStyle.Fill; canvas.DrawCircle(a, size / 2, paint); }
                    else canvas.DrawLine(a, b, paint);
                    break;
                }
                case BrushKind.Calligraphy1:
                case BrushKind.Calligraphy2:
                    Calligraphy(canvas, a, b, color, size, kind == BrushKind.Calligraphy1 ? 1 : -1);
                    break;
                case BrushKind.Airbrush:
                    Spray(canvas, b, color, size, random);
                    break;
                case BrushKind.Oil:
                    Oil(canvas, a, b, color, size, random);
                    break;
                case BrushKind.Crayon:
                    Crayon(canvas, a, b, color, size, random);
                    break;
                case BrushKind.NaturalPencil:
                    NaturalPencil(canvas, a, b, color, size, random);
                    break;
            }
        }

        /// <summary>The airbrush: a spatter of single pixels inside a circle. Also run on a timer while held still.</summary>
        public static void Spray(SKCanvas canvas, SKPoint centre, SKColor color, float size, Random random)
        {
            float radius = Math.Max(2, size * 1.5f);
            int dots = (int)Math.Clamp(radius * radius / 6, 6, 400);
            using var paint = new SKPaint { Color = color, IsAntialias = false, Style = SKPaintStyle.Fill };
            for (int i = 0; i < dots; i++)
            {
                double angle = random.NextDouble() * Math.PI * 2;
                double distance = Math.Sqrt(random.NextDouble()) * radius;
                canvas.DrawRect((float)(centre.X + Math.Cos(angle) * distance),
                    (float)(centre.Y + Math.Sin(angle) * distance), 1, 1, paint);
            }
        }

        public static SKRectI SprayBounds(SKPoint centre, float size) =>
            Bounds(centre, centre, Math.Max(2, size * 1.5f));

        private static SKPaint Stroke(SKColor color, float size, SKStrokeCap cap) => new()
        {
            Color = color,
            IsAntialias = true,
            StrokeWidth = size,
            StrokeCap = cap,
            StrokeJoin = SKStrokeJoin.Round,
            Style = SKPaintStyle.Stroke,
        };

        /// <summary>A flat nib held at 45 degrees: thick one way, a hairline the other.</summary>
        private static void Calligraphy(SKCanvas canvas, SKPoint a, SKPoint b, SKColor color, float size, int direction)
        {
            float half = size / 2f * 0.7071f;
            var nib = new SKPoint(half, -half * direction);
            using var path = new SKPath();
            path.MoveTo(a - nib);
            path.LineTo(a + nib);
            path.LineTo(b + nib);
            path.LineTo(b - nib);
            path.Close();
            using var paint = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.StrokeAndFill, StrokeWidth = 1 };
            canvas.DrawPath(path, paint);
        }

        /// <summary>Parallel bristles at varying strength, so the stroke has streaks in it.</summary>
        private static void Oil(SKCanvas canvas, SKPoint a, SKPoint b, SKColor color, float size, Random random)
        {
            float dx = b.X - a.X, dy = b.Y - a.Y;
            float length = Math.Max(0.001f, (float)Math.Sqrt(dx * dx + dy * dy));
            var normal = new SKPoint(-dy / length, dx / length);
            int bristles = Math.Clamp((int)size, 3, 24);

            using var paint = Stroke(color, Math.Max(1, size / bristles * 1.6f), SKStrokeCap.Round);
            for (int i = 0; i < bristles; i++)
            {
                float offset = (i / (float)(bristles - 1) - 0.5f) * size;
                // Seeded by bristle so a streak stays a streak along the whole stroke.
                paint.Color = color.WithAlpha((byte)(color.Alpha * (0.35 + 0.65 * Hash(i))));
                var shift = new SKPoint(normal.X * offset, normal.Y * offset);
                canvas.DrawLine(a + shift, b + shift, paint);
            }
        }

        private static double Hash(int i) => ((i * 2654435761u) % 1000) / 1000.0;

        /// <summary>Waxy and broken: random single pixels across the nib, most of them kept.</summary>
        private static void Crayon(SKCanvas canvas, SKPoint a, SKPoint b, SKColor color, float size, Random random)
        {
            using var paint = new SKPaint { Color = color, IsAntialias = false, Style = SKPaintStyle.Fill };
            float radius = size / 2f;
            foreach (var p in Steps(a, b, Math.Max(1, radius / 2)))
            {
                int dots = (int)Math.Clamp(radius * radius * 1.2, 4, 600);
                for (int i = 0; i < dots; i++)
                {
                    double angle = random.NextDouble() * Math.PI * 2;
                    double distance = Math.Sqrt(random.NextDouble()) * radius;
                    paint.Color = color.WithAlpha((byte)(color.Alpha * (0.4 + 0.6 * random.NextDouble())));
                    canvas.DrawRect((float)(p.X + Math.Cos(angle) * distance), (float)(p.Y + Math.Sin(angle) * distance), 1, 1, paint);
                }
            }
        }

        /// <summary>Graphite: a thin, slightly uneven line with grain.</summary>
        private static void NaturalPencil(SKCanvas canvas, SKPoint a, SKPoint b, SKColor color, float size, Random random)
        {
            float width = Math.Max(1, size / 2f);
            using var paint = Stroke(color.WithAlpha((byte)(color.Alpha * 0.55)), width, SKStrokeCap.Round);
            canvas.DrawLine(a, b, paint);

            using var grain = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };
            foreach (var p in Steps(a, b, 1))
            {
                grain.Color = color.WithAlpha((byte)(color.Alpha * random.NextDouble() * 0.6));
                canvas.DrawRect(p.X + (float)(random.NextDouble() - 0.5) * width,
                    p.Y + (float)(random.NextDouble() - 0.5) * width, 1, 1, grain);
            }
        }

        /// <summary>Marker and Watercolour, drawn as one path so overlaps do not darken.</summary>
        public static void WholeStroke(SKCanvas canvas, BrushKind kind, IReadOnlyList<SKPoint> points, SKColor color, float size)
        {
            if (points.Count == 0) return;
            size = Math.Max(1, size);

            using var path = ShapeGeometry.PolylinePath(points, close: false);
            if (points.Count == 1) path.LineTo(points[0].X + 0.01f, points[0].Y);

            if (kind == BrushKind.Marker)
            {
                using var paint = Stroke(color.WithAlpha((byte)(color.Alpha * 0.5)), size, SKStrokeCap.Square);
                canvas.DrawPath(path, paint);
            }
            else
            {
                using var paint = Stroke(color.WithAlpha((byte)(color.Alpha * 0.4)), size, SKStrokeCap.Round);
                paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(0.5f, size / 6f));
                canvas.DrawPath(path, paint);
            }
        }

        public static SKRectI Bounds(IReadOnlyList<SKPoint> points, float size)
        {
            if (points.Count == 0) return SKRectI.Empty;
            float minX = points[0].X, maxX = minX, minY = points[0].Y, maxY = minY;
            foreach (var p in points)
            {
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            }
            return Bounds(new SKPoint(minX, minY), new SKPoint(maxX, maxY), size * 1.5f);
        }
    }

    /// <summary>The Text tool's settings, as one value so a commit can capture them all at once.</summary>
    public readonly record struct TextStyle(
        string FontFamily, float SizePx, bool Bold, bool Italic, bool Underline, bool Strikethrough);

    public static class TextRenderer
    {
        private static SKPaint Paint(TextStyle style, SKColor color)
        {
            var typeface = SKTypeface.FromFamilyName(style.FontFamily,
                style.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
                SKFontStyleWidth.Normal,
                style.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright) ?? SKTypeface.Default;
            return new SKPaint
            {
                Typeface = typeface,
                TextSize = Math.Max(1, style.SizePx),
                Color = color,
                IsAntialias = true,
                SubpixelText = true,
            };
        }

        /// <summary>
        /// Draws <paramref name="text"/> with its top-left at (<paramref name="x"/>, <paramref name="y"/>),
        /// one line per newline, over an optional opaque <paramref name="background"/>.
        /// Returns the area touched.
        /// </summary>
        public static SKRectI Draw(SKCanvas canvas, string text, float x, float y, TextStyle style, SKColor color, SKColor? background)
        {
            using var paint = Paint(style, color);
            var metrics = paint.FontMetrics;
            float lineHeight = paint.FontSpacing;
            var lines = text.Replace("\r\n", "\n").Split('\n');

            float width = 0;
            foreach (var line in lines) width = Math.Max(width, paint.MeasureText(line));
            var area = new SKRect(x, y, x + width, y + lineHeight * lines.Length);

            if (background is { } bg)
            {
                using var fill = new SKPaint { Color = bg, Style = SKPaintStyle.Fill };
                canvas.DrawRect(area, fill);
            }

            float thickness = metrics.UnderlineThickness is > 0 ? metrics.UnderlineThickness.Value : Math.Max(1, style.SizePx / 14f);
            using var rule = new SKPaint { Color = color, Style = SKPaintStyle.Fill, IsAntialias = true };

            for (int i = 0; i < lines.Length; i++)
            {
                float baseline = y + i * lineHeight - metrics.Ascent;
                canvas.DrawText(lines[i], x, baseline, paint);
                float lineWidth = paint.MeasureText(lines[i]);
                if (lineWidth <= 0) continue;

                if (style.Underline)
                {
                    float pos = metrics.UnderlinePosition is > 0 ? metrics.UnderlinePosition.Value : style.SizePx / 10f;
                    canvas.DrawRect(x, baseline + pos, lineWidth, thickness, rule);
                }
                if (style.Strikethrough)
                {
                    float pos = metrics.StrikeoutPosition is < 0 ? metrics.StrikeoutPosition.Value : metrics.Ascent * 0.3f;
                    canvas.DrawRect(x, baseline + pos, lineWidth, thickness, rule);
                }
            }

            // Italic overhang and descenders run outside the measured box.
            area.Inflate(style.SizePx / 2, style.SizePx / 2);
            return SKRectI.Ceiling(area);
        }

        public static IReadOnlyList<string> SystemFamilies()
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var family in SKFontManager.Default.FontFamilies)
                if (!string.IsNullOrWhiteSpace(family) && !family.StartsWith("@")) names.Add(family);
            return new List<string>(names);
        }
    }
}
