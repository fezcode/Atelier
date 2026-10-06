using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Atelier.Painting
{
    /// <summary>
    /// The pixels edit mode paints on, together with their undo history.
    ///
    /// Premultiplied BGRA because that is the only layout Skia will draw into; the
    /// conversions to and from Magick's straight alpha happen at the two edges
    /// (<see cref="FromUnpremulBgra"/> and <see cref="ToUnpremulBgra"/>) and nowhere else.
    ///
    /// History is whole-bitmap snapshots rather than per-operation deltas. Every Paint
    /// operation -- a stroke, a fill, a crop that changes the size -- undoes the same
    /// way, and the snapshot taken at the start of an operation doubles as the clean
    /// base that shape and marker previews are redrawn over while the mouse is down.
    /// </summary>
    public sealed unsafe class PaintDocument : IDisposable
    {
        /// <summary>Paint's own limit. Large photos hit <see cref="UndoBudgetBytes"/> first.</summary>
        public const int MaxUndoSteps = 50;

        /// <summary>
        /// A 24 MP photo is ~96 MB a snapshot, so a step count alone would let fifty of
        /// them take the machine down. A few steps are always kept, whatever their size.
        /// </summary>
        public static long UndoBudgetBytes = 1024L * 1024 * 1024;

        private const int MinUndoSteps = 3;

        private readonly LinkedList<SKBitmap> _undo = new();
        private readonly Stack<SKBitmap> _redo = new();

        public SKBitmap Bitmap { get; private set; }

        public int Width => Bitmap.Width;
        public int Height => Bitmap.Height;
        public SKRectI Bounds => new(0, 0, Width, Height);

        /// <summary>Some pixels inside the rectangle changed; the size did not.</summary>
        public event Action<SKRectI>? Changed;

        /// <summary>The whole bitmap was swapped -- undo, crop, resize -- and may be a new size.</summary>
        public event Action? Replaced;

        public event Action? HistoryChanged;

        public PaintDocument(int width, int height, SKColor fill)
        {
            Bitmap = NewBitmap(width, height);
            Bitmap.Erase(fill);
        }

        private PaintDocument(SKBitmap bitmap) => Bitmap = bitmap;

        public static SKBitmap NewBitmap(int width, int height)
        {
            var bitmap = new SKBitmap(new SKImageInfo(
                Math.Max(1, width), Math.Max(1, height), SKColorType.Bgra8888, SKAlphaType.Premul));
            bitmap.Erase(SKColors.Transparent);
            return bitmap;
        }

        private static SKImageInfo Straight(int width, int height) =>
            new(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);

        /// <summary>Builds a document from straight-alpha BGRA bytes, as Magick hands them out.</summary>
        public static PaintDocument FromUnpremulBgra(byte[] pixels, int width, int height) =>
            new(BitmapFromUnpremulBgra(pixels, width, height));

        public static SKBitmap BitmapFromUnpremulBgra(byte[] pixels, int width, int height)
        {
            if (pixels.Length < width * height * 4)
                throw new ArgumentException("Pixel buffer is smaller than the image it describes.");

            var bitmap = NewBitmap(width, height);
            fixed (byte* p = pixels)
            {
                using var src = new SKPixmap(Straight(width, height), (IntPtr)p, width * 4);
                src.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0);
            }
            return bitmap;
        }

        /// <summary>Straight-alpha BGRA, tightly packed -- what Magick's pixel reader expects.</summary>
        public byte[] ToUnpremulBgra() => ToUnpremulBgra(Bitmap);

        public static byte[] ToUnpremulBgra(SKBitmap bitmap)
        {
            var bytes = new byte[bitmap.Width * bitmap.Height * 4];
            fixed (byte* p = bytes)
            {
                using var src = bitmap.PeekPixels();
                src.ReadPixels(Straight(bitmap.Width, bitmap.Height), (IntPtr)p, bitmap.Width * 4, 0, 0);
            }
            return bytes;
        }

        // ---- History ---------------------------------------------------------------

        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;
        public int UndoCount => _undo.Count;

        /// <summary>
        /// The state before the operation in progress -- the clean canvas previews are
        /// redrawn over. Only meaningful between <see cref="BeginOperation"/> and the
        /// next one.
        /// </summary>
        public SKBitmap? OperationBase => _undo.Last?.Value;

        /// <summary>Snapshots the canvas so the change about to be made can be undone.</summary>
        public void BeginOperation()
        {
            _undo.AddLast(Bitmap.Copy());
            AfterPush();
        }

        /// <summary>
        /// Swaps in a whole new bitmap as one undoable step. The current bitmap becomes
        /// the undo entry itself rather than a copy of it, since it is about to be
        /// dropped anyway.
        /// </summary>
        public void Commit(SKBitmap next)
        {
            _undo.AddLast(Bitmap);
            Bitmap = next;
            AfterPush();
            Replaced?.Invoke();
        }

        private void AfterPush()
        {
            ClearRedo();
            long size = (long)Bitmap.ByteCount;
            long total = size;
            foreach (var b in _undo) total += b.ByteCount;

            while (_undo.Count > MinUndoSteps && (_undo.Count > MaxUndoSteps || total > UndoBudgetBytes))
            {
                var oldest = _undo.First!.Value;
                total -= oldest.ByteCount;
                _undo.RemoveFirst();
                oldest.Dispose();
            }
            HistoryChanged?.Invoke();
        }

        /// <summary>
        /// Forgets the step just begun, restoring the canvas to it. For gestures that
        /// turned out to change nothing -- a click that never became a stroke.
        /// </summary>
        public void CancelOperation()
        {
            if (_undo.Last is not { } last) return;
            _undo.RemoveLast();
            var current = Bitmap;
            Bitmap = last.Value;
            current.Dispose();
            HistoryChanged?.Invoke();
            Replaced?.Invoke();
        }

        public bool Undo()
        {
            if (_undo.Last is not { } last) return false;
            _undo.RemoveLast();
            _redo.Push(Bitmap);
            Bitmap = last.Value;
            HistoryChanged?.Invoke();
            Replaced?.Invoke();
            return true;
        }

        public bool Redo()
        {
            if (_redo.Count == 0) return false;
            _undo.AddLast(Bitmap);
            Bitmap = _redo.Pop();
            HistoryChanged?.Invoke();
            Replaced?.Invoke();
            return true;
        }

        private void ClearRedo()
        {
            while (_redo.Count > 0) _redo.Pop().Dispose();
        }

        // ---- Drawing ---------------------------------------------------------------

        /// <summary>Runs <paramref name="draw"/> on a canvas over the document and reports <paramref name="dirty"/>.</summary>
        public void Draw(Action<SKCanvas> draw, SKRectI dirty)
        {
            using (var canvas = new SKCanvas(Bitmap)) draw(canvas);
            Invalidate(dirty);
        }

        /// <summary>For draws that only know what they touched once they are done -- text, mostly.</summary>
        public void Draw(Func<SKCanvas, SKRectI> draw)
        {
            SKRectI dirty;
            using (var canvas = new SKCanvas(Bitmap)) dirty = draw(canvas);
            Invalidate(dirty);
        }

        public void Invalidate(SKRectI dirty)
        {
            dirty.Intersect(Bounds);
            if (!dirty.IsEmpty) Changed?.Invoke(dirty);
        }

        public void InvalidateAll() => Changed?.Invoke(Bounds);

        /// <summary>Copies <paramref name="area"/> back from the operation base, wiping a preview.</summary>
        public void RestoreFromBase(SKRectI area) => RestoreFrom(OperationBase, area);

        public void RestoreFrom(SKBitmap? source, SKRectI area)
        {
            if (source == null || source.Width != Width || source.Height != Height) return;
            area.Intersect(Bounds);
            if (area.IsEmpty) return;

            byte* src = (byte*)source.GetPixels();
            byte* dst = (byte*)Bitmap.GetPixels();
            int rowBytes = Bitmap.RowBytes;
            int span = area.Width * 4;
            for (int y = area.Top; y < area.Bottom; y++)
            {
                long offset = (long)y * rowBytes + area.Left * 4L;
                Buffer.MemoryCopy(src + offset, dst + offset, span, span);
            }
        }

        // ---- Pixels ----------------------------------------------------------------

        /// <summary>A straight-alpha colour as the premultiplied word Bgra8888 stores.</summary>
        public static uint Premultiply(SKColor c)
        {
            uint a = c.Alpha;
            uint r = (c.Red * a + 127) / 255;
            uint g = (c.Green * a + 127) / 255;
            uint b = (c.Blue * a + 127) / 255;
            return (a << 24) | (r << 16) | (g << 8) | b;
        }

        public static SKColor Unpremultiply(uint px)
        {
            uint a = px >> 24;
            if (a == 0) return SKColors.Transparent;
            byte Channel(int shift) => (byte)Math.Min(255, (((px >> shift) & 0xFF) * 255 + a / 2) / a);
            return new SKColor(Channel(16), Channel(8), Channel(0), (byte)a);
        }

        public SKColor GetPixel(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height) return SKColors.Transparent;
            return Unpremultiply(Row(Bitmap, y)[x]);
        }

        private static uint* Row(SKBitmap bitmap, int y) =>
            (uint*)((byte*)bitmap.GetPixels() + (long)y * bitmap.RowBytes);

        private static bool Near(uint a, uint b, int tolerance)
        {
            if (tolerance == 0) return a == b;
            for (int shift = 0; shift < 32; shift += 8)
            {
                int d = (int)((a >> shift) & 0xFF) - (int)((b >> shift) & 0xFF);
                if (d > tolerance || d < -tolerance) return false;
            }
            return true;
        }

        /// <summary>
        /// The paint bucket. A scanline fill over the region of pixels within
        /// <paramref name="tolerance"/> (0-255 per channel) of the one clicked.
        /// Returns false, recording nothing, when the click would change nothing.
        /// </summary>
        public bool FloodFill(int x, int y, SKColor color, int tolerance)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height) return false;

            uint fill = Premultiply(color);
            uint target = Row(Bitmap, y)[x];
            if (target == fill && tolerance == 0) return false;

            BeginOperation();

            int w = Width, h = Height;
            var seen = new bool[w * h];
            var stack = new Stack<(int X, int Y)>();
            stack.Push((x, y));
            int minX = x, maxX = x, minY = y, maxY = y;

            while (stack.Count > 0)
            {
                var (sx, sy) = stack.Pop();
                uint* row = Row(Bitmap, sy);
                if (seen[sy * w + sx] || !Near(row[sx], target, tolerance)) continue;

                int left = sx;
                while (left > 0 && !seen[sy * w + left - 1] && Near(row[left - 1], target, tolerance)) left--;
                int right = sx;
                while (right < w - 1 && !seen[sy * w + right + 1] && Near(row[right + 1], target, tolerance)) right++;

                for (int i = left; i <= right; i++)
                {
                    row[i] = fill;
                    seen[sy * w + i] = true;
                }

                minX = Math.Min(minX, left); maxX = Math.Max(maxX, right);
                minY = Math.Min(minY, sy); maxY = Math.Max(maxY, sy);

                foreach (int ny in stackalloc[] { sy - 1, sy + 1 })
                {
                    if (ny < 0 || ny >= h) continue;
                    uint* next = Row(Bitmap, ny);
                    bool inRun = false;
                    for (int i = left; i <= right; i++)
                    {
                        bool match = !seen[ny * w + i] && Near(next[i], target, tolerance);
                        if (match && !inRun) stack.Push((i, ny));
                        inRun = match;
                    }
                }
            }

            Invalidate(new SKRectI(minX, minY, maxX + 1, maxY + 1));
            return true;
        }

        /// <summary>
        /// Right-dragging the eraser: swaps every pixel of exactly <paramref name="from"/>
        /// inside the area for <paramref name="to"/>, leaving everything else alone.
        /// </summary>
        public void ReplaceColor(SKRectI area, SKColor from, SKColor to)
        {
            area.Intersect(Bounds);
            if (area.IsEmpty) return;

            uint match = Premultiply(from), with = Premultiply(to);
            for (int y = area.Top; y < area.Bottom; y++)
            {
                uint* row = Row(Bitmap, y);
                for (int x = area.Left; x < area.Right; x++)
                    if (row[x] == match) row[x] = with;
            }
            Invalidate(area);
        }

        // ---- Whole-bitmap transforms ---------------------------------------------
        // Static so a floating selection goes through exactly the same code as the
        // whole picture does.

        private static SKPaint CopyPaint(bool smooth = false) => new()
        {
            BlendMode = SKBlendMode.Src,
            FilterQuality = smooth ? SKFilterQuality.High : SKFilterQuality.None,
            IsAntialias = smooth,
        };

        /// <summary>Turns clockwise by 90, 180 or 270 degrees.</summary>
        public static SKBitmap Rotated(SKBitmap src, int degrees)
        {
            degrees = ((degrees % 360) + 360) % 360;
            bool swap = degrees is 90 or 270;
            var result = NewBitmap(swap ? src.Height : src.Width, swap ? src.Width : src.Height);
            using var canvas = new SKCanvas(result);
            switch (degrees)
            {
                case 90: canvas.Translate(result.Width, 0); break;
                case 180: canvas.Translate(result.Width, result.Height); break;
                case 270: canvas.Translate(0, result.Height); break;
            }
            canvas.RotateDegrees(degrees);
            using var paint = CopyPaint();
            canvas.DrawBitmap(src, 0, 0, paint);
            return result;
        }

        public static SKBitmap Flipped(SKBitmap src, bool horizontal)
        {
            var result = NewBitmap(src.Width, src.Height);
            using var canvas = new SKCanvas(result);
            if (horizontal) canvas.Scale(-1, 1, src.Width / 2f, 0);
            else canvas.Scale(1, -1, 0, src.Height / 2f);
            using var paint = CopyPaint();
            canvas.DrawBitmap(src, 0, 0, paint);
            return result;
        }

        public static SKBitmap Resized(SKBitmap src, int width, int height)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            var result = NewBitmap(width, height);
            using var canvas = new SKCanvas(result);
            using var paint = CopyPaint(smooth: true);
            canvas.DrawBitmap(src, new SKRect(0, 0, src.Width, src.Height), new SKRect(0, 0, width, height), paint);
            return result;
        }

        /// <summary>
        /// Paint's skew: rows slide sideways by <paramref name="horizontalDegrees"/> and
        /// columns slide vertically by <paramref name="verticalDegrees"/>. The canvas grows
        /// to hold the slanted picture and the new corners are filled with <paramref name="background"/>.
        /// </summary>
        public static SKBitmap Skewed(SKBitmap src, float horizontalDegrees, float verticalDegrees, SKColor background)
        {
            float kx = (float)Math.Tan(horizontalDegrees * Math.PI / 180);
            float ky = (float)Math.Tan(verticalDegrees * Math.PI / 180);
            var matrix = SKMatrix.CreateSkew(kx, ky);
            var bounds = matrix.MapRect(new SKRect(0, 0, src.Width, src.Height));

            var result = NewBitmap((int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height));
            result.Erase(background);
            using var canvas = new SKCanvas(result);
            canvas.Translate(-bounds.Left, -bounds.Top);
            canvas.Concat(ref matrix);
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
            canvas.DrawBitmap(src, 0, 0, paint);
            return result;
        }

        /// <summary>Inverts colour but not alpha. In premultiplied terms that is simply a - c.</summary>
        public static SKBitmap Inverted(SKBitmap src)
        {
            var result = src.Copy();
            for (int y = 0; y < result.Height; y++)
            {
                uint* row = Row(result, y);
                for (int x = 0; x < result.Width; x++)
                {
                    uint px = row[x];
                    uint a = px >> 24;
                    uint r = a - ((px >> 16) & 0xFF);
                    uint g = a - ((px >> 8) & 0xFF);
                    uint b = a - (px & 0xFF);
                    row[x] = (a << 24) | (r << 16) | (g << 8) | b;
                }
            }
            return result;
        }

        /// <summary>
        /// The part of <paramref name="src"/> inside <paramref name="area"/>. With a mask,
        /// everything outside it is left transparent -- a free-form selection's corners.
        /// </summary>
        public static SKBitmap Extract(SKBitmap src, SKRectI area, SKPath? mask)
        {
            var result = NewBitmap(area.Width, area.Height);
            using var canvas = new SKCanvas(result);
            canvas.Translate(-area.Left, -area.Top);
            if (mask != null) canvas.ClipPath(mask, antialias: false);
            using var paint = CopyPaint();
            canvas.DrawBitmap(src, 0, 0, paint);
            return result;
        }

        /// <summary>Keeps <paramref name="area"/>; outside a free-form mask becomes <paramref name="background"/>.</summary>
        public static SKBitmap Cropped(SKBitmap src, SKRectI area, SKPath? mask, SKColor background)
        {
            var result = NewBitmap(area.Width, area.Height);
            if (mask != null) result.Erase(background);
            using var canvas = new SKCanvas(result);
            canvas.Translate(-area.Left, -area.Top);
            if (mask != null) canvas.ClipPath(mask, antialias: false);
            using var paint = CopyPaint();
            canvas.DrawBitmap(src, 0, 0, paint);
            return result;
        }

        /// <summary>Changes the canvas size without scaling, anchored top-left; new space is <paramref name="background"/>.</summary>
        public static SKBitmap WithCanvasSize(SKBitmap src, int width, int height, SKColor background)
        {
            var result = NewBitmap(width, height);
            result.Erase(background);
            using var canvas = new SKCanvas(result);
            using var paint = CopyPaint();
            canvas.DrawBitmap(src, 0, 0, paint);
            return result;
        }

        /// <summary>
        /// A transparent-selection copy: every pixel of the background colour drops out,
        /// so a lifted selection can be laid over a picture without its white box.
        /// </summary>
        public static SKBitmap WithoutColor(SKBitmap src, SKColor color)
        {
            var result = src.Copy();
            uint match = Premultiply(color);
            for (int y = 0; y < result.Height; y++)
            {
                uint* row = Row(result, y);
                for (int x = 0; x < result.Width; x++)
                    if (row[x] == match) row[x] = 0;
            }
            return result;
        }

        public void Dispose()
        {
            ClearRedo();
            foreach (var b in _undo) b.Dispose();
            _undo.Clear();
            Bitmap.Dispose();
        }
    }
}
