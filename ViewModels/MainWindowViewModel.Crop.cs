using System;
using System.Collections.Generic;
using Atelier.Painting;
using Avalonia;
using ReactiveUI;
using SkiaSharp;

namespace Atelier.ViewModels
{
    /// <summary>Which part of the crop frame the pointer has hold of.</summary>
    public enum CropHandle
    {
        None,
        Move,
        New,
        Top,
        Bottom,
        Left,
        Right,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
    }

    /// <summary>An entry in the crop tool's aspect-ratio list. A ratio of 0 is freeform.</summary>
    public sealed class CropAspectOption
    {
        public CropAspectOption(string name, double ratio, bool isOriginal = false)
        {
            Name = name;
            Ratio = ratio;
            IsOriginal = isOriginal;
        }

        public string Name { get; }
        public double Ratio { get; }
        public bool IsOriginal { get; }
        public override string ToString() => Name;
    }

    /// <summary>
    /// The crop tool, done the way Preview does it: picking the tool puts a frame round
    /// the whole picture, the edges and corners are dragged in, everything outside is
    /// dimmed, a rule-of-thirds grid helps line things up, and Enter crops.
    ///
    /// The frame lives in canvas pixels and is snapped to whole pixels as it moves, so
    /// the size shown is exactly the size the crop will produce.
    /// </summary>
    public partial class MainWindowViewModel
    {
        private SKRect? _cropRect;
        private CropHandle _cropHandle;
        private SKRect _cropStartRect;
        private double _cropStartRatio;
        private SKSizeI _cropCanvas;

        public bool IsCropTool => _tool == PaintTool.Crop;

        /// <summary>The frame in canvas pixels, while the crop tool is in hand.</summary>
        public Rect? CropFrame => IsCropTool && _cropRect is { } r ? new Rect(r.Left, r.Top, r.Width, r.Height) : null;

        /// <summary>True while an edge, corner or the whole frame is being dragged.</summary>
        public bool IsCropDragging => _gesture == Gesture.Crop;

        public string CropSizeText => _cropRect is { } r ? $"{(int)Math.Round(r.Width)} × {(int)Math.Round(r.Height)}" : string.Empty;

        /// <summary>Crop only does something once the frame is smaller than the picture.</summary>
        public bool CanApplyCrop => IsCropTool && _doc != null && _cropRect is { } r
                                    && SKRectI.Round(r) != _doc.Bounds;

        public IReadOnlyList<CropAspectOption> CropAspects { get; } = new[]
        {
            new CropAspectOption("Freeform", 0),
            new CropAspectOption("Original", 0, isOriginal: true),
            new CropAspectOption("Square (1:1)", 1),
            new CropAspectOption("4:3", 4.0 / 3),
            new CropAspectOption("3:2", 3.0 / 2),
            new CropAspectOption("16:9", 16.0 / 9),
            new CropAspectOption("3:4", 3.0 / 4),
            new CropAspectOption("2:3", 2.0 / 3),
            new CropAspectOption("9:16", 9.0 / 16),
        };

        private CropAspectOption? _cropAspect;
        public CropAspectOption CropAspect
        {
            get => _cropAspect ??= CropAspects[0];
            set
            {
                if (value == null || ReferenceEquals(value, _cropAspect)) return;
                _cropAspect = value;
                this.RaisePropertyChanged();
                FitFrameToAspect();
            }
        }

        /// <summary>The ratio the frame is held to, or 0 for none.</summary>
        private double LockedRatio
        {
            get
            {
                var a = CropAspect;
                if (a.IsOriginal) return _doc == null || _doc.Height == 0 ? 0 : (double)_doc.Width / _doc.Height;
                return a.Ratio;
            }
        }

        /// <summary>Puts a fresh frame round the picture, or round the selection if there was one.</summary>
        private void BeginCrop(SKRectI? seed)
        {
            if (_doc == null) { _cropRect = null; return; }
            _cropCanvas = new SKSizeI(_doc.Width, _doc.Height);
            var full = new SKRect(0, 0, _doc.Width, _doc.Height);
            if (seed is { } s)
            {
                var r = new SKRect(s.Left, s.Top, s.Right, s.Bottom);
                r.Intersect(full);
                _cropRect = r.Width >= 1 && r.Height >= 1 ? r : full;
            }
            else _cropRect = full;
            if (LockedRatio > 0) FitFrameToAspect(); else RaiseCrop();
        }

        /// <summary>Esc: the frame goes back round the whole picture.</summary>
        public void ResetCrop()
        {
            if (!IsCropTool) return;
            _gesture = _gesture == Gesture.Crop ? Gesture.None : _gesture;
            BeginCrop(null);
        }

        /// <summary>Enter or the Crop button: cut the picture down to the frame.</summary>
        public void ApplyCrop()
        {
            if (!CanApplyCrop || _doc == null || _cropRect is not { } r) return;
            var area = SKRectI.Round(r);
            area.Intersect(_doc.Bounds);
            if (area.Width < 1 || area.Height < 1) return;

            _gesture = Gesture.None;
            var cropped = PaintDocument.Cropped(_doc.Bitmap, area, null, Sk(_secondary));
            _doc.Commit(cropped);
            // The new picture is the whole canvas: start a fresh frame round it.
            BeginCrop(null);
            StatusMessage = $"Cropped to {area.Width} × {area.Height}";
        }

        /// <summary>Called when the bitmap is swapped (undo, redo, resize) so the frame still fits.</summary>
        private void OnCropDocumentReplaced()
        {
            if (!IsCropTool || _doc == null) return;
            // A new canvas size (crop, undo of one, resize) gets a fresh frame round it;
            // anything else -- undoing a stroke -- leaves the frame where it was.
            if (_cropRect is { } && _cropCanvas == new SKSizeI(_doc.Width, _doc.Height)) { RaiseCrop(); return; }
            BeginCrop(null);
        }

        private void RaiseCrop()
        {
            this.RaisePropertyChanged(nameof(CropFrame));
            this.RaisePropertyChanged(nameof(CropSizeText));
            this.RaisePropertyChanged(nameof(CanApplyCrop));
            this.RaisePropertyChanged(nameof(IsCropDragging));
            OverlayInvalidated?.Invoke();
        }

        // ---- Hit testing ------------------------------------------------------------

        /// <summary>How close, in screen pixels, the pointer must be to grab an edge.</summary>
        private const double CropGrabScreenPx = 10;

        /// <summary>What a press at <paramref name="p"/> (canvas pixels) would take hold of.</summary>
        public CropHandle HitCrop(double x, double y)
        {
            if (!IsCropTool || _cropRect is not { } r) return CropHandle.None;
            double tol = CropGrabScreenPx / Math.Max(ZoomLevel, 0.01);
            // A small frame keeps some room in the middle to grab and move.
            double inner = Math.Min(tol, Math.Min(r.Width, r.Height) / 3);

            bool nearL = Math.Abs(x - r.Left) <= tol, nearR = Math.Abs(x - r.Right) <= tol;
            bool nearT = Math.Abs(y - r.Top) <= tol, nearB = Math.Abs(y - r.Bottom) <= tol;
            bool inX = x >= r.Left - tol && x <= r.Right + tol;
            bool inY = y >= r.Top - tol && y <= r.Bottom + tol;

            // Inside but clear of the edges: move.
            if (x > r.Left + inner && x < r.Right - inner && y > r.Top + inner && y < r.Bottom - inner)
                return CropHandle.Move;

            if (nearT && nearL) return CropHandle.TopLeft;
            if (nearT && nearR) return CropHandle.TopRight;
            if (nearB && nearL) return CropHandle.BottomLeft;
            if (nearB && nearR) return CropHandle.BottomRight;
            if (nearT && inX) return CropHandle.Top;
            if (nearB && inX) return CropHandle.Bottom;
            if (nearL && inY) return CropHandle.Left;
            if (nearR && inY) return CropHandle.Right;
            return CropHandle.New;
        }

        // ---- Dragging ---------------------------------------------------------------

        private void CropDown(SKPoint p)
        {
            if (_doc == null) return;
            _cropRect ??= new SKRect(0, 0, _doc.Width, _doc.Height);
            _cropHandle = HitCrop(p.X, p.Y);
            if (_cropHandle == CropHandle.None) return;
            _cropStartRect = _cropRect.Value;
            _cropStartRatio = _cropStartRect.Height > 0 ? _cropStartRect.Width / _cropStartRect.Height : 1;
            _start = ClampToCanvas(p);
            _gesture = Gesture.Crop;
            RaiseCrop();
        }

        private void CropMove(SKPoint p, bool shift)
        {
            if (_doc == null) return;
            p = ClampToCanvas(p);
            float W = _doc.Width, H = _doc.Height;
            var s = _cropStartRect;
            // Shift holds the frame's own proportions when no ratio is chosen.
            double ratio = LockedRatio > 0 ? LockedRatio : shift ? _cropStartRatio : 0;
            SKRect r;

            switch (_cropHandle)
            {
                case CropHandle.Move:
                {
                    float dx = (float)Math.Round(p.X - _start.X), dy = (float)Math.Round(p.Y - _start.Y);
                    dx = Math.Clamp(dx, -s.Left, W - s.Right);
                    dy = Math.Clamp(dy, -s.Top, H - s.Bottom);
                    r = new SKRect(s.Left + dx, s.Top + dy, s.Right + dx, s.Bottom + dy);
                    break;
                }
                case CropHandle.New:
                    r = FromAnchor(_start, p, ratio, W, H);
                    break;
                case CropHandle.TopLeft: r = FromAnchor(new SKPoint(s.Right, s.Bottom), p, ratio, W, H); break;
                case CropHandle.TopRight: r = FromAnchor(new SKPoint(s.Left, s.Bottom), p, ratio, W, H); break;
                case CropHandle.BottomLeft: r = FromAnchor(new SKPoint(s.Right, s.Top), p, ratio, W, H); break;
                case CropHandle.BottomRight: r = FromAnchor(new SKPoint(s.Left, s.Top), p, ratio, W, H); break;
                case CropHandle.Left:
                case CropHandle.Right:
                {
                    float anchor = _cropHandle == CropHandle.Left ? s.Right : s.Left;
                    float w = Math.Max(1, Math.Abs(Snap(p.X) - anchor));
                    float maxW = _cropHandle == CropHandle.Left ? anchor : W - anchor;
                    w = Math.Min(w, maxW);
                    float top = s.Top, bottom = s.Bottom;
                    if (ratio > 0)
                    {
                        float cy = s.MidY;
                        float maxH = 2 * Math.Min(cy, H - cy);
                        float h = (float)(w / ratio);
                        if (h > maxH) { h = maxH; w = (float)(h * ratio); }
                        top = Snap(cy - h / 2);
                        bottom = top + Math.Max(1, Snap(h));
                    }
                    r = _cropHandle == CropHandle.Left
                        ? new SKRect(anchor - Snap(w), top, anchor, bottom)
                        : new SKRect(anchor, top, anchor + Snap(w), bottom);
                    break;
                }
                case CropHandle.Top:
                case CropHandle.Bottom:
                {
                    float anchor = _cropHandle == CropHandle.Top ? s.Bottom : s.Top;
                    float h = Math.Max(1, Math.Abs(Snap(p.Y) - anchor));
                    float maxH = _cropHandle == CropHandle.Top ? anchor : H - anchor;
                    h = Math.Min(h, maxH);
                    float left = s.Left, right = s.Right;
                    if (ratio > 0)
                    {
                        float cx = s.MidX;
                        float maxW = 2 * Math.Min(cx, W - cx);
                        float w = (float)(h * ratio);
                        if (w > maxW) { w = maxW; h = (float)(w / ratio); }
                        left = Snap(cx - w / 2);
                        right = left + Math.Max(1, Snap(w));
                    }
                    r = _cropHandle == CropHandle.Top
                        ? new SKRect(left, anchor - Snap(h), right, anchor)
                        : new SKRect(left, anchor, right, anchor + Snap(h));
                    break;
                }
                default:
                    return;
            }

            _cropRect = Clamp(r, W, H);
            RaiseCrop();
        }

        private void CropUp()
        {
            _gesture = Gesture.None;
            // A click that drew nothing leaves the old frame where it was.
            if (_cropHandle == CropHandle.New && _cropRect is { } r && (r.Width < 2 || r.Height < 2))
                _cropRect = _cropStartRect;
            _cropHandle = CropHandle.None;
            RaiseCrop();
        }

        /// <summary>
        /// A frame from a fixed corner to the pointer, held to <paramref name="ratio"/>
        /// if there is one, and never running off the canvas.
        /// </summary>
        private static SKRect FromAnchor(SKPoint anchor, SKPoint p, double ratio, float W, float H)
        {
            float sx = p.X >= anchor.X ? 1 : -1, sy = p.Y >= anchor.Y ? 1 : -1;
            float w = Math.Abs(p.X - anchor.X), h = Math.Abs(p.Y - anchor.Y);
            float maxW = sx > 0 ? W - anchor.X : anchor.X;
            float maxH = sy > 0 ? H - anchor.Y : anchor.Y;

            if (ratio > 0)
            {
                // Follow whichever way the pointer has gone furthest.
                if (w / Math.Max(h, 0.001f) > ratio) h = (float)(w / ratio); else w = (float)(h * ratio);
                if (w > maxW) { w = maxW; h = (float)(w / ratio); }
                if (h > maxH) { h = maxH; w = (float)(h * ratio); }
            }
            else
            {
                w = Math.Min(w, maxW);
                h = Math.Min(h, maxH);
            }

            w = Math.Max(1, Snap(w));
            h = Math.Max(1, Snap(h));
            float x0 = sx > 0 ? anchor.X : anchor.X - w;
            float y0 = sy > 0 ? anchor.Y : anchor.Y - h;
            return new SKRect(x0, y0, x0 + w, y0 + h);
        }

        /// <summary>Picking a ratio: the largest frame of that shape inside the current one, kept centred.</summary>
        private void FitFrameToAspect()
        {
            if (!IsCropTool || _doc == null) { RaiseCrop(); return; }
            double ratio = LockedRatio;
            var r = _cropRect ?? new SKRect(0, 0, _doc.Width, _doc.Height);
            if (ratio > 0)
            {
                float w = r.Width, h = r.Height;
                if (w / h > ratio) w = (float)(h * ratio); else h = (float)(w / ratio);
                w = Math.Max(1, Snap(w));
                h = Math.Max(1, Snap(h));
                float left = Snap(r.MidX - w / 2), top = Snap(r.MidY - h / 2);
                r = new SKRect(left, top, left + w, top + h);
            }
            _cropRect = Clamp(r, _doc.Width, _doc.Height);
            RaiseCrop();
        }

        private static float Snap(float v) => (float)Math.Round(v);

        private SKPoint ClampToCanvas(SKPoint p) => _doc == null
            ? p
            : new SKPoint(Math.Clamp(p.X, 0, _doc.Width), Math.Clamp(p.Y, 0, _doc.Height));

        /// <summary>Slides a frame back onto the canvas without changing its size where it can.</summary>
        private static SKRect Clamp(SKRect r, float W, float H)
        {
            float w = Math.Min(Math.Max(1, r.Width), W), h = Math.Min(Math.Max(1, r.Height), H);
            float left = Math.Clamp(r.Left, 0, W - w), top = Math.Clamp(r.Top, 0, H - h);
            return new SKRect(left, top, left + w, top + h);
        }
    }
}
