using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Atelier.Painting;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ImageMagick;
using ReactiveUI;
using SkiaSharp;
using FontStyle = Avalonia.Media.FontStyle;
using FontWeight = Avalonia.Media.FontWeight;

namespace Atelier.ViewModels
{
    /// <summary>One square in the colour palette. Custom slots start out empty.</summary>
    public class PaletteSwatch : ReactiveObject
    {
        private Color _color;
        private bool _isEmpty;

        public PaletteSwatch(Color color, string name, bool isEmpty = false)
        {
            _color = color;
            _isEmpty = isEmpty;
            Name = name;
        }

        public string Name { get; set; }

        public Color Color
        {
            get => _color;
            set
            {
                this.RaiseAndSetIfChanged(ref _color, value);
                this.RaisePropertyChanged(nameof(Brush));
            }
        }

        public bool IsEmpty
        {
            get => _isEmpty;
            set => this.RaiseAndSetIfChanged(ref _isEmpty, value);
        }

        public IBrush Brush => new SolidColorBrush(Color);
    }

    /// <summary>A shape in the ribbon's gallery, with an icon drawn from its real geometry.</summary>
    public sealed class ShapeOption
    {
        public ShapeOption(ShapeKind kind, string name, Geometry? icon)
        {
            Kind = kind;
            Name = name;
            Icon = icon;
        }

        public ShapeKind Kind { get; }
        public string Name { get; }
        public Geometry? Icon { get; }
    }

    public sealed class BrushOption
    {
        public BrushOption(BrushKind kind, string name)
        {
            Kind = kind;
            Name = name;
        }

        public BrushKind Kind { get; }
        public string Name { get; }
    }

    /// <summary>
    /// Edit mode, Paint-style.
    ///
    /// Entering edit mode decodes the picture -- turned the way it is on screen -- into a
    /// <see cref="PaintDocument"/>. Every tool draws into that document. The colour
    /// sliders and filters stay what they always were: a non-destructive pass applied on
    /// top, previewed after a pause and baked in only on save. While they are all at rest
    /// the screen is a straight copy of the document, updated rectangle by rectangle as
    /// the brush moves; once any is moved, the adjusted render takes over and catches up
    /// with the brush a moment after it stops.
    /// </summary>
    public partial class MainWindowViewModel
    {
        private PaintDocument? _doc;
        private WriteableBitmap? _display;

        /// <summary>The document being painted, while in edit mode. Exposed for tests.</summary>
        public PaintDocument? Document => _doc;

        /// <summary>The pixels on screen changed but the bitmap object did not; the view must redraw.</summary>
        public event Action? CanvasInvalidated;

        /// <summary>The selection outline or brush cursor moved.</summary>
        public event Action? OverlayInvalidated;

        /// <summary>The canvas changed size -- crop, resize, rotate -- so the view should refit.</summary>
        public event Action? CanvasResized;

        /// <summary>A text box was opened and wants the keyboard.</summary>
        public event Action? TextEditStarted;

        /// <summary>The picture was turned before Edit was pressed, so even an untouched document differs from disk.</summary>
        private bool _editBaselineDirty;

        public bool HasEditChanges => IsEditMode && (_editBaselineDirty || (_doc?.CanUndo ?? false) || IsAdjusted);

        public bool CanUndo => IsEditMode && (_doc?.CanUndo ?? false);
        public bool CanRedo => IsEditMode && (_doc?.CanRedo ?? false);

        // ---- Entering and leaving -------------------------------------------------

        public void EnterEditMode()
        {
            if (string.IsNullOrEmpty(ImagePath) || Path.GetExtension(ImagePath).ToLowerInvariant() == ".svg") return;

            try
            {
                var (pixels, width, height) = DecodeForEditing(ImagePath, Orientation);
                var doc = PaintDocument.FromUnpremulBgra(pixels, width, height);

                // The turn on screen is now part of the pixels, so the view must stop
                // applying it. If it was a turn the file does not have yet, that is an
                // unsaved change the editor now owns.
                _editBaselineDirty = Orientation != _storedOrientation;
                _storedOrientation = ImageOrientation.Identity;
                Orientation = ImageOrientation.Identity;

                ResetEditParameters();
                IsEditMode = true;
                AttachDocument(doc);
                ErrorMessage = null;
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Cannot edit this format: {ex.Message}";
            }
        }

        /// <summary>
        /// The picture as straight-alpha BGRA, turned the way the viewer shows it.
        ///
        /// Mirrors <see cref="Decode"/>: the HEIF family carries its rotation in the
        /// container and is auto-oriented, everything else arrives as the sensor frame
        /// and gets the viewer's own orientation applied.
        /// </summary>
        internal static (byte[] Pixels, int Width, int Height) DecodeForEditing(string path, ImageOrientation orientation)
        {
            using var image = new MagickImage(path);
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".heic" or ".heif" or ".avif") image.AutoOrient();

            if (orientation.Mirrored) image.Flop();
            if (orientation.Angle != 0) image.Rotate(orientation.Angle);

            if (image.ColorSpace == ColorSpace.CMYK) image.ColorSpace = ColorSpace.sRGB;
            if (!image.HasAlpha) image.Alpha(AlphaOption.Opaque);

            using var px = image.GetPixelsUnsafe();
            var bytes = px.ToByteArray(PixelMapping.BGRA)
                        ?? throw new InvalidOperationException("The decoder returned no pixels.");
            return (bytes, (int)image.Width, (int)image.Height);
        }

        private void ResetEditParameters()
        {
            _brightness = 100;
            _saturation = 100;
            _hue = 100;
            _contrast = 0;
            _blur = 0;
            _currentFilter = null;
            this.RaisePropertyChanged(nameof(Brightness));
            this.RaisePropertyChanged(nameof(Saturation));
            this.RaisePropertyChanged(nameof(Hue));
            this.RaisePropertyChanged(nameof(Contrast));
            this.RaisePropertyChanged(nameof(BlurValue));
            this.RaisePropertyChanged(nameof(ActiveFilter));
        }

        public async Task ExitEditMode(bool discard)
        {
            if (discard && !string.IsNullOrEmpty(ImagePath))
            {
                // Reloading ends the session itself; see LoadImageAsync.
                await LoadImageAsync(ImagePath);
                return;
            }
            EndEditSession();
        }

        /// <summary>Drops the document and every piece of in-progress tool state.</summary>
        private void EndEditSession()
        {
            _previewToken++;
            _previewTimer?.Stop();
            _gesture = Gesture.None;
            _curveStage = 0;
            _polygonActive = false;
            DropFloat();
            _selPath?.Dispose();
            _selPath = null;
            _selOffset = default;
            _marquee = null;
            _isTextEditing = false;
            this.RaisePropertyChanged(nameof(IsTextEditing));

            if (_doc != null)
            {
                _doc.Changed -= OnDocumentChanged;
                _doc.Replaced -= OnDocumentReplaced;
                _doc.HistoryChanged -= OnHistoryChanged;
                _doc.Dispose();
                _doc = null;
            }
            _editBaselineDirty = false;
            _display = null;

            IsEditMode = false;
            RaiseHistory();
            RaiseSelection();
        }

        private void AttachDocument(PaintDocument doc)
        {
            _doc = doc;
            doc.Changed += OnDocumentChanged;
            doc.Replaced += OnDocumentReplaced;
            doc.HistoryChanged += OnHistoryChanged;
            RebuildDisplay();
            RaiseHistory();
            RaiseSelection();
        }

        // ---- The bitmap on screen -------------------------------------------------

        private void RebuildDisplay()
        {
            if (_doc == null) return;
            _display = new WriteableBitmap(new PixelSize(_doc.Width, _doc.Height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Premul);
            CopyToDisplay(_doc.Bounds);
            ImageSource = _display;
            ImageWidth = _doc.Width;
            ImageHeight = _doc.Height;
            this.RaisePropertyChanged(nameof(CanvasSizeText));
            if (IsAdjusted) SchedulePreview();
        }

        private unsafe void CopyToDisplay(SKRectI area)
        {
            if (_doc == null || _display == null) return;
            if (_display.PixelSize.Width != _doc.Width || _display.PixelSize.Height != _doc.Height) return;
            area.Intersect(_doc.Bounds);
            if (area.IsEmpty) return;

            using var fb = _display.Lock();
            byte* src = (byte*)_doc.Bitmap.GetPixels();
            byte* dst = (byte*)fb.Address;
            int srcRow = _doc.Bitmap.RowBytes, dstRow = fb.RowBytes, span = area.Width * 4;
            for (int y = area.Top; y < area.Bottom; y++)
                Buffer.MemoryCopy(src + (long)y * srcRow + area.Left * 4L, dst + (long)y * dstRow + area.Left * 4L, span, span);
        }

        private unsafe void WriteAdjustedToDisplay(byte[] straight, int width, int height)
        {
            if (_display == null || _display.PixelSize.Width != width || _display.PixelSize.Height != height) return;
            using var fb = _display.Lock();
            fixed (byte* p = straight)
            {
                using var src = new SKPixmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul), (IntPtr)p, width * 4);
                src.ReadPixels(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), fb.Address, fb.RowBytes, 0, 0);
            }
        }

        private void OnDocumentChanged(SKRectI area)
        {
            // Straight to the screen even when adjustments are on: the stroke shows
            // immediately, and the adjusted render replaces it once the brush rests.
            CopyToDisplay(area);
            CanvasInvalidated?.Invoke();
            if (IsAdjusted) SchedulePreview();
        }

        private void OnDocumentReplaced()
        {
            if (_doc == null) return;
            if (_display == null || _display.PixelSize.Width != _doc.Width || _display.PixelSize.Height != _doc.Height)
            {
                RebuildDisplay();
                CanvasResized?.Invoke();
            }
            else
            {
                CopyToDisplay(_doc.Bounds);
                if (IsAdjusted) SchedulePreview();
            }
            CanvasInvalidated?.Invoke();
        }

        private void OnHistoryChanged() => RaiseHistory();

        private void RaiseHistory()
        {
            this.RaisePropertyChanged(nameof(CanUndo));
            this.RaisePropertyChanged(nameof(CanRedo));
            this.RaisePropertyChanged(nameof(HasEditChanges));
            this.RaisePropertyChanged(nameof(IsDirty));
        }

        // ---- Adjustments (the original Edit Image controls) ------------------------

        private double _brightness = 100;
        public double Brightness
        {
            get => _brightness;
            set { this.RaiseAndSetIfChanged(ref _brightness, value); OnAdjustmentChanged(); }
        }

        private double _saturation = 100;
        public double Saturation
        {
            get => _saturation;
            set { this.RaiseAndSetIfChanged(ref _saturation, value); OnAdjustmentChanged(); }
        }

        private double _hue = 100;
        public double Hue
        {
            get => _hue;
            set { this.RaiseAndSetIfChanged(ref _hue, value); OnAdjustmentChanged(); }
        }

        private double _contrast;
        public double Contrast
        {
            get => _contrast;
            set { this.RaiseAndSetIfChanged(ref _contrast, value); OnAdjustmentChanged(); }
        }

        private double _blur;
        public double BlurValue
        {
            get => _blur;
            set { this.RaiseAndSetIfChanged(ref _blur, value); OnAdjustmentChanged(); }
        }

        private string? _currentFilter;
        public string? ActiveFilter => _currentFilter;

        public void ApplyFilter(string filterName)
        {
            _currentFilter = _currentFilter == filterName ? null : filterName;
            this.RaisePropertyChanged(nameof(ActiveFilter));
            OnAdjustmentChanged();
        }

        public void ResetAdjustments()
        {
            ResetEditParameters();
            OnAdjustmentChanged();
        }

        public bool IsAdjusted =>
            _brightness != 100 || _saturation != 100 || _hue != 100 || _contrast != 0 || _blur > 0 || _currentFilter != null;

        private void OnAdjustmentChanged()
        {
            this.RaisePropertyChanged(nameof(IsAdjusted));
            this.RaisePropertyChanged(nameof(HasEditChanges));
            this.RaisePropertyChanged(nameof(IsDirty));
            SchedulePreview();
        }

        private readonly record struct Adjustments(double Brightness, double Saturation, double Hue, double Contrast, double Blur, string? Filter);

        private Adjustments CurrentAdjustments() => new(_brightness, _saturation, _hue, _contrast, _blur, _currentFilter);

        private static void ApplyAdjustments(MagickImage image, Adjustments a)
        {
            // The document always carries alpha, and several of these -- Negate above all --
            // treat it as one more channel to process: an inverted opaque photo came out
            // fully transparent. A picture with no real transparency sets alpha aside for
            // the duration; one with transparency keeps it but is negated colour-only.
            bool opaque = image.IsOpaque;
            if (opaque) image.Alpha(AlphaOption.Off);

            image.Modulate(new Percentage(a.Brightness), new Percentage(a.Saturation), new Percentage(a.Hue));
            if (a.Contrast != 0) image.BrightnessContrast(new Percentage(0), new Percentage(a.Contrast));
            if (a.Blur > 0) image.Blur(0, a.Blur);

            switch (a.Filter)
            {
                case "Grayscale": image.Grayscale(); break;
                case "Sepia": image.SepiaTone(); break;
                case "Negate": image.Negate(Channels.RGB); break;
                case "Charcoal": image.Charcoal(); break;
                case "Edge": image.Edge(1); break;
            }

            if (opaque) image.Alpha(AlphaOption.Opaque);
        }

        private static MagickImage FromBgra(byte[] pixels, int width, int height)
        {
            var image = new MagickImage();
            image.ReadPixels(pixels, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.BGRA));
            return image;
        }

        private static byte[] ToBgra(MagickImage image)
        {
            using var px = image.GetPixelsUnsafe();
            return px.ToByteArray(PixelMapping.BGRA) ?? Array.Empty<byte>();
        }

        private int _previewToken;
        private DispatcherTimer? _previewTimer;

        /// <summary>
        /// Debounced: the sliders fire on every pixel of travel, and a full Magick pass
        /// over a large photo takes long enough that queueing one per tick would leave
        /// the preview seconds behind the thumb.
        /// </summary>
        private void SchedulePreview()
        {
            if (!IsEditMode || _doc == null) return;
            if (_previewTimer == null)
            {
                _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                _previewTimer.Tick += (_, _) =>
                {
                    _previewTimer!.Stop();
                    RunPreview();
                };
            }
            _previewTimer.Stop();
            _previewTimer.Start();
        }

        /// <summary>Renders the adjusted preview now. Public so tests need not wait out the debounce.</summary>
        public Task RunPreview()
        {
            if (_doc == null || !IsEditMode) return Task.CompletedTask;

            int token = ++_previewToken;
            if (!IsAdjusted)
            {
                CopyToDisplay(_doc.Bounds);
                CanvasInvalidated?.Invoke();
                return Task.CompletedTask;
            }

            var settings = CurrentAdjustments();
            var pixels = _doc.ToUnpremulBgra();
            int width = _doc.Width, height = _doc.Height;

            return Task.Run(() =>
            {
                try
                {
                    using var image = FromBgra(pixels, width, height);
                    ApplyAdjustments(image, settings);
                    var output = ToBgra(image);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (token != _previewToken || !IsEditMode) return;
                        WriteAdjustedToDisplay(output, width, height);
                        CanvasInvalidated?.Invoke();
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (token == _previewToken && IsEditMode) ErrorMessage = $"Processing Error: {ex.Message}";
                    });
                }
            });
        }

        // ---- Saving ----------------------------------------------------------------

        public async Task SaveEditedImageAsync(string? path = null)
        {
            string? target = path ?? ImagePath;
            if (string.IsNullOrEmpty(target) || _doc == null) return;

            FinishPendingWork(deselect: false);
            CommitFloat();

            var pixels = _doc.ToUnpremulBgra();
            int width = _doc.Width, height = _doc.Height;
            var settings = CurrentAdjustments();
            string? source = ImagePath;

            try
            {
                await Task.Run(() => WriteEdited(pixels, width, height, settings, source, target));
                // Loading the result ends the edit session and puts the saved file on screen.
                await LoadImageAsync(target);
                if (ErrorMessage == null) StatusMessage = "Saved";
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Failed to save: {ex.Message}";
            }
        }

        private static void WriteEdited(byte[] pixels, int width, int height, Adjustments settings, string? source, string target)
        {
            using var image = FromBgra(pixels, width, height);
            ApplyAdjustments(image, settings);

            uint quality = 92;
            if (source != null && File.Exists(source))
            {
                try
                {
                    // The camera's metadata and colour profile survive the edit. The pixels
                    // are already upright, so the orientation tag must say so.
                    using var original = new MagickImage();
                    original.Ping(source);
                    if (original.Quality > 0) quality = original.Quality;
                    if (original.GetColorProfile() is { } icc) image.SetProfile(icc);
                    if (original.GetIptcProfile() is { } iptc) image.SetProfile(iptc);
                    if (original.GetXmpProfile() is { } xmp) image.SetProfile(xmp);
                    if (original.GetExifProfile() is { } exif)
                    {
                        exif.SetValue(ExifTag.Orientation, (ushort)1);
                        image.SetProfile(exif);
                    }
                }
                catch
                {
                    // Metadata is a courtesy; failing to carry it over is no reason not to save.
                }
            }
            image.Orientation = OrientationType.TopLeft;

            var info = MagickFormatInfo.Create(new FileInfo(target));
            image.Format = info?.Format ?? MagickFormat.Png;

            if (image.Format is MagickFormat.Jpeg or MagickFormat.Jpg)
            {
                image.Quality = quality;
                image.BackgroundColor = MagickColors.White;
                image.Alpha(AlphaOption.Remove);
            }
            else if (image.IsOpaque)
            {
                // Keeps an opaque photo saved as PNG from growing an alpha channel it never had.
                image.Alpha(AlphaOption.Off);
            }

            if (image.Format == MagickFormat.Ico && (image.Width > 256 || image.Height > 256))
                image.Resize(256, 256);

            try
            {
                image.Write(target);
            }
            catch (Exception ex) when (ex.Message.Contains("no encode delegate") || ex.Message.Contains("not supported"))
            {
                throw new Exception($"Writing '{Path.GetExtension(target)}' is not available in the current Magick configuration.");
            }
        }

        // ---- Tools -----------------------------------------------------------------

        private PaintTool _tool = PaintTool.Pencil;
        private PaintTool _toolBeforePicker = PaintTool.Pencil;

        public PaintTool Tool
        {
            get => _tool;
            set
            {
                if (_tool == value) return;
                FinishPendingWork(deselect: value is not (PaintTool.Select or PaintTool.FreeSelect));
                if (value == PaintTool.Picker) _toolBeforePicker = _tool;
                _tool = value;
                this.RaisePropertyChanged();
                this.RaisePropertyChanged(nameof(ActiveShape));
                this.RaisePropertyChanged(nameof(SelectedShapeOption));
                this.RaisePropertyChanged(nameof(ActiveBrush));
                this.RaisePropertyChanged(nameof(IsTextTool));
                this.RaisePropertyChanged(nameof(IsFillTool));
                this.RaisePropertyChanged(nameof(ToolHint));
                _cursor = null;
                OverlayInvalidated?.Invoke();
            }
        }

        public bool IsTextTool => _tool == PaintTool.Text;
        public bool IsFillTool => _tool == PaintTool.Fill;

        private BrushKind _brush = BrushKind.Brush;
        public BrushKind BrushKind
        {
            get => _brush;
            set
            {
                this.RaiseAndSetIfChanged(ref _brush, value);
                this.RaisePropertyChanged(nameof(BrushName));
                this.RaisePropertyChanged(nameof(SelectedBrushOption));
                Tool = PaintTool.Brush;
                this.RaisePropertyChanged(nameof(ActiveBrush));
            }
        }

        public string BrushName => BrushRenderer.DisplayName(_brush);

        /// <summary>The brush gallery's highlight: only lit while the brush tool is in hand.</summary>
        public BrushKind? ActiveBrush => _tool == PaintTool.Brush ? _brush : null;

        private ShapeKind _shape = ShapeKind.Rectangle;

        /// <summary>The shape gallery's selection: null unless the shape tool is in hand, as in Paint.</summary>
        public ShapeKind? ActiveShape
        {
            get => _tool == PaintTool.Shape ? _shape : null;
            set
            {
                if (value is not { } shape) return;
                if (_shape != shape) FinishPendingShape();
                _shape = shape;
                Tool = PaintTool.Shape;
                this.RaisePropertyChanged();
                this.RaisePropertyChanged(nameof(SelectedShapeOption));
                this.RaisePropertyChanged(nameof(ToolHint));
            }
        }

        public string ToolHint => _tool switch
        {
            PaintTool.Select => "Drag to select. Drag a selection to move it; Ctrl+drag to copy it.",
            PaintTool.FreeSelect => "Draw around the area to select.",
            PaintTool.Pencil => "Left-click draws in colour 1, right-click in colour 2.",
            PaintTool.Brush => $"{BrushName}: left-click for colour 1, right-click for colour 2.",
            PaintTool.Fill => "Click to fill an area. Right-click fills with colour 2.",
            PaintTool.Text => "Click to place text. Click outside the box to finish.",
            PaintTool.Eraser => "Erases to colour 2. Right-drag replaces colour 1 with colour 2.",
            PaintTool.Picker => "Click to pick colour 1; right-click picks colour 2.",
            PaintTool.Magnifier => "Click to zoom in, right-click to zoom out.",
            PaintTool.Shape when _shape == ShapeKind.Curve => "Drag a line, then click twice to bend it.",
            PaintTool.Shape when _shape == ShapeKind.Polygon => "Drag the first side, click for each corner, double-click to finish.",
            PaintTool.Shape => "Drag to draw. Hold Shift for a perfect square, circle or 45° line.",
            _ => string.Empty,
        };

        private double _strokeSize = 3;
        public double StrokeSize
        {
            get => _strokeSize;
            set
            {
                this.RaiseAndSetIfChanged(ref _strokeSize, Math.Clamp(Math.Round(value), 1, 100));
                OverlayInvalidated?.Invoke();
            }
        }

        private int _outlineIndex = 1;
        /// <summary>0 = no outline, 1 = solid colour.</summary>
        public int OutlineIndex
        {
            get => _outlineIndex;
            set => this.RaiseAndSetIfChanged(ref _outlineIndex, value);
        }

        private int _fillIndex;
        /// <summary>0 = no fill, 1 = solid colour.</summary>
        public int FillIndex
        {
            get => _fillIndex;
            set => this.RaiseAndSetIfChanged(ref _fillIndex, value);
        }

        private double _fillTolerance;
        /// <summary>The paint bucket's tolerance, 0-100%. Paint itself is fixed at 0.</summary>
        public double FillTolerance
        {
            get => _fillTolerance;
            set => this.RaiseAndSetIfChanged(ref _fillTolerance, Math.Clamp(value, 0, 100));
        }

        private bool _showGridlines;
        public bool ShowGridlines
        {
            get => _showGridlines;
            set
            {
                this.RaiseAndSetIfChanged(ref _showGridlines, value);
                OverlayInvalidated?.Invoke();
            }
        }

        public IReadOnlyList<ShapeOption> ShapeGallery { get; } =
            Enum.GetValues<ShapeKind>()
                .Select(k => new ShapeOption(k, ShapeGeometry.DisplayName(k), SafeGeometry(ShapeGeometry.IconData(k))))
                .ToList();

        public IReadOnlyList<BrushOption> BrushGallery { get; } =
            Enum.GetValues<BrushKind>()
                .Select(k => new BrushOption(k, BrushRenderer.DisplayName(k)))
                .ToList();

        /// <summary>The shape list's selection; empty unless the shape tool is in hand.</summary>
        public ShapeOption? SelectedShapeOption
        {
            get => _tool == PaintTool.Shape ? ShapeGallery.FirstOrDefault(o => o.Kind == _shape) : null;
            set
            {
                if (value != null) ActiveShape = value.Kind;
            }
        }

        public BrushOption? SelectedBrushOption
        {
            get => BrushGallery.FirstOrDefault(o => o.Kind == _brush);
            set
            {
                if (value != null) BrushKind = value.Kind;
            }
        }

        private static Geometry? SafeGeometry(string data)
        {
            try { return Geometry.Parse(data); }
            catch { return null; }
        }

        // ---- Colours ---------------------------------------------------------------

        private Color _primary = Colors.Black;
        private Color _secondary = Colors.White;

        public Color PrimaryColor
        {
            get => _primary;
            set
            {
                this.RaiseAndSetIfChanged(ref _primary, value);
                this.RaisePropertyChanged(nameof(PrimaryBrush));
            }
        }

        public Color SecondaryColor
        {
            get => _secondary;
            set
            {
                this.RaiseAndSetIfChanged(ref _secondary, value);
                this.RaisePropertyChanged(nameof(SecondaryBrush));
                this.RaisePropertyChanged(nameof(TextBackgroundBrush));
                _floatDraw?.Dispose();
                _floatDraw = null;
            }
        }

        public IBrush PrimaryBrush => new SolidColorBrush(_primary);
        public IBrush SecondaryBrush => new SolidColorBrush(_secondary);

        private bool _isColor2Active;
        /// <summary>Which of the two colour wells the palette writes to, as in Paint.</summary>
        public bool IsColor2Active
        {
            get => _isColor2Active;
            set
            {
                this.RaiseAndSetIfChanged(ref _isColor2Active, value);
                this.RaisePropertyChanged(nameof(IsColor1Active));
            }
        }

        public bool IsColor1Active => !_isColor2Active;

        public void SetActiveColor(Color color)
        {
            if (IsColor2Active) SecondaryColor = color;
            else PrimaryColor = color;
        }

        public void SwapColors() => (PrimaryColor, SecondaryColor) = (SecondaryColor, PrimaryColor);

        /// <summary>Paint's twenty, in its order: the dark row, then the light one.</summary>
        public ObservableCollection<PaletteSwatch> Palette { get; } = new(new[]
        {
            ("#000000", "Black"), ("#7F7F7F", "Gray-50%"), ("#880015", "Dark red"), ("#ED1C24", "Red"),
            ("#FF7F27", "Orange"), ("#FFF200", "Yellow"), ("#22B14C", "Green"), ("#00A2E8", "Turquoise"),
            ("#3F48CC", "Indigo"), ("#A349A4", "Purple"),
            ("#FFFFFF", "White"), ("#C3C3C3", "Gray-25%"), ("#B97A57", "Brown"), ("#FFAEC9", "Rose"),
            ("#FFC90E", "Gold"), ("#EFE4B0", "Light yellow"), ("#B5E61D", "Lime"), ("#99D9EA", "Light turquoise"),
            ("#7092BE", "Blue-gray"), ("#C8BFE7", "Lavender"),
        }.Select(c => new PaletteSwatch(Color.Parse(c.Item1), c.Item2)));

        public ObservableCollection<PaletteSwatch> CustomColors { get; } =
            new(Enumerable.Range(0, 10).Select(_ => new PaletteSwatch(Colors.Transparent, "Custom colour", isEmpty: true)));

        private int _nextCustom;

        /// <summary>Edit Colours puts its result in the next custom slot, wrapping round, as Paint does.</summary>
        public void AddCustomColor(Color color)
        {
            var slot = CustomColors[_nextCustom];
            slot.Color = color;
            slot.IsEmpty = false;
            slot.Name = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            _nextCustom = (_nextCustom + 1) % CustomColors.Count;
            SetActiveColor(color);
        }

        private static SKColor Sk(Color c) => new(c.R, c.G, c.B, c.A);
        private static Color Av(SKColor c) => Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue);

        // ---- Status bar ------------------------------------------------------------

        private SKPoint? _cursor;

        public string CursorText => _cursor is { } c && _doc != null && c.X >= 0 && c.Y >= 0 && c.X < _doc.Width && c.Y < _doc.Height
            ? $"{(int)c.X}, {(int)c.Y}px"
            : string.Empty;

        public string SelectionText
        {
            get
            {
                if (_marquee is { } m) return $"{Math.Abs((int)m.Width)} × {Math.Abs((int)m.Height)}px";
                if (SelectionBounds is { } b) return $"{b.Width} × {b.Height}px";
                return string.Empty;
            }
        }

        public string CanvasSizeText => _doc == null ? string.Empty : $"{_doc.Width} × {_doc.Height}px";

        // ---- The overlay: selection outline, brush cursor --------------------------

        /// <summary>The outline to draw marching ants around, in canvas pixels.</summary>
        public Geometry? SelectionGeometry { get; private set; }

        /// <summary>Where the brush will land, or null when the tool has no footprint.</summary>
        public Rect? BrushCursor
        {
            get
            {
                if (_cursor is not { } c || _gesture is Gesture.Marquee or Gesture.Lasso or Gesture.MoveFloat) return null;
                bool sized = _tool == PaintTool.Eraser || (_tool == PaintTool.Brush) || (_tool == PaintTool.Pencil && _strokeSize > 2);
                if (!sized) return null;
                double size = _tool == PaintTool.Brush && _brush == BrushKind.Airbrush ? _strokeSize * 3 : _strokeSize;
                return new Rect(c.X - size / 2, c.Y - size / 2, size, size);
            }
        }

        public bool BrushCursorIsSquare => _tool == PaintTool.Eraser;

        private void RaiseSelection()
        {
            SKPath? outline = null;
            if (_marquee is { } m)
            {
                outline = new SKPath();
                outline.AddRect(m.Standardized);
            }
            else if (_gesture == Gesture.Lasso && _points.Count > 1)
            {
                outline = ShapeGeometry.PolylinePath(_points, close: false);
            }
            else if (_selPath != null)
            {
                outline = new SKPath(_selPath);
                outline.Offset(_selOffset.X, _selOffset.Y);
            }

            SelectionGeometry = outline == null ? null : SafeGeometry(outline.ToSvgPathData());
            outline?.Dispose();

            this.RaisePropertyChanged(nameof(HasSelection));
            this.RaisePropertyChanged(nameof(SelectionText));
            OverlayInvalidated?.Invoke();
        }

        // ---- Pointer gestures -------------------------------------------------------

        private enum Gesture { None, Stroke, WholeStroke, Shape, Marquee, Lasso, MoveFloat }

        private Gesture _gesture;
        private bool _gestureSecondary;
        private SKPoint _start, _last;
        private readonly List<SKPoint> _points = new();
        private SKRectI _previewArea = SKRectI.Empty;
        private SKRect? _marquee;
        private readonly Random _random = new();

        // Curve: 0 idle, 1 waiting for the first bend, 2 waiting for the second.
        private int _curveStage;
        private SKPoint _curveStart, _curveEnd, _bend1, _bend2;

        private bool _polygonActive;

        /// <summary>True while a stroke or drag is in progress -- the airbrush timer keys off it.</summary>
        public bool IsGestureActive => _gesture != Gesture.None;

        public bool IsSpraying => _gesture == Gesture.Stroke && _tool == PaintTool.Brush && _brush == BrushKind.Airbrush;

        private SKColor StrokeColor => Sk(_gestureSecondary ? _secondary : _primary);
        private SKColor OtherColor => Sk(_gestureSecondary ? _primary : _secondary);

        /// <summary>
        /// A button went down over the canvas, in canvas pixel coordinates.
        /// <paramref name="secondary"/> is a right-click: colour 2 instead of colour 1.
        /// </summary>
        public void PaintPointerDown(double x, double y, bool secondary, bool shift = false, bool ctrl = false, int clickCount = 1)
        {
            if (_doc == null || !IsEditMode) return;
            var p = new SKPoint((float)x, (float)y);
            _cursor = p;

            if (_isTextEditing && _tool != PaintTool.Text) CommitText();

            switch (_tool)
            {
                case PaintTool.Pencil:
                case PaintTool.Brush:
                case PaintTool.Eraser:
                    _doc.BeginOperation();
                    _gestureSecondary = secondary;
                    _gesture = _tool == PaintTool.Brush && BrushRenderer.IsRedrawn(_brush) ? Gesture.WholeStroke : Gesture.Stroke;
                    _points.Clear();
                    _previewArea = SKRectI.Empty;
                    _last = p;
                    StrokeTo(p);
                    break;

                case PaintTool.Fill:
                {
                    var color = Sk(secondary ? _secondary : _primary);
                    int tolerance = (int)Math.Round(_fillTolerance / 100 * 255);
                    _doc.FloodFill((int)Math.Floor(x), (int)Math.Floor(y), color, tolerance);
                    break;
                }

                case PaintTool.Picker:
                {
                    int px = (int)Math.Floor(x), py = (int)Math.Floor(y);
                    if (px >= 0 && py >= 0 && px < _doc.Width && py < _doc.Height)
                    {
                        var picked = Av(_doc.GetPixel(px, py));
                        if (secondary) SecondaryColor = picked; else PrimaryColor = picked;
                    }
                    // Paint drops back to whatever was in hand before the picker.
                    Tool = _toolBeforePicker == PaintTool.Picker ? PaintTool.Pencil : _toolBeforePicker;
                    break;
                }

                case PaintTool.Text:
                    if (_isTextEditing) CommitText();
                    else BeginText(p);
                    break;

                case PaintTool.Shape:
                    ShapeDown(p, secondary, shift, clickCount);
                    break;

                case PaintTool.Select:
                case PaintTool.FreeSelect:
                    SelectDown(p, ctrl);
                    break;
            }
            this.RaisePropertyChanged(nameof(CursorText));
        }

        public void PaintPointerMove(double x, double y, bool shift = false)
        {
            if (_doc == null || !IsEditMode) return;
            var p = new SKPoint((float)x, (float)y);
            _cursor = p;

            switch (_gesture)
            {
                case Gesture.Stroke:
                case Gesture.WholeStroke:
                    StrokeTo(p);
                    break;
                case Gesture.Shape:
                    ShapeMove(p, shift);
                    break;
                case Gesture.Marquee:
                    _marquee = new SKRect(_start.X, _start.Y, p.X, p.Y);
                    RaiseSelection();
                    break;
                case Gesture.Lasso:
                    if (_points.Count == 0 || SKPoint.Distance(_points[^1], p) >= 1) _points.Add(p);
                    RaiseSelection();
                    break;
                case Gesture.MoveFloat:
                {
                    int dx = (int)Math.Round(p.X - _start.X), dy = (int)Math.Round(p.Y - _start.Y);
                    if (dx != 0 || dy != 0)
                    {
                        MoveFloatBy(dx, dy);
                        _start = new SKPoint(_start.X + dx, _start.Y + dy);
                    }
                    break;
                }
            }

            this.RaisePropertyChanged(nameof(CursorText));
            OverlayInvalidated?.Invoke();
        }

        public void PaintPointerUp(double x, double y, bool shift = false)
        {
            if (_doc == null || !IsEditMode) return;
            var p = new SKPoint((float)x, (float)y);

            switch (_gesture)
            {
                case Gesture.Stroke:
                case Gesture.WholeStroke:
                    _gesture = Gesture.None;
                    break;
                case Gesture.Shape:
                    ShapeUp(p, shift);
                    break;
                case Gesture.Marquee:
                {
                    var r = new SKRect(_start.X, _start.Y, p.X, p.Y).Standardized;
                    _marquee = null;
                    _gesture = Gesture.None;
                    var clipped = SKRectI.Round(r);
                    clipped.Intersect(_doc.Bounds);
                    if (clipped.Width >= 1 && clipped.Height >= 1)
                    {
                        var path = new SKPath();
                        path.AddRect(clipped);
                        SetSelection(path);
                    }
                    else RaiseSelection();
                    break;
                }
                case Gesture.Lasso:
                {
                    _gesture = Gesture.None;
                    if (_points.Count >= 3)
                    {
                        var path = ShapeGeometry.PolylinePath(_points, close: true);
                        var b = SKRectI.Round(path.Bounds);
                        b.Intersect(_doc.Bounds);
                        if (b.Width >= 1 && b.Height >= 1) { SetSelection(path); break; }
                        path.Dispose();
                    }
                    _points.Clear();
                    RaiseSelection();
                    break;
                }
                case Gesture.MoveFloat:
                    _gesture = Gesture.None;
                    break;
            }
        }

        public void PaintPointerLeave()
        {
            _cursor = null;
            this.RaisePropertyChanged(nameof(CursorText));
            OverlayInvalidated?.Invoke();
        }

        /// <summary>The airbrush keeps spraying while held still, as a real one does.</summary>
        public void SprayTick()
        {
            if (_doc == null || !IsSpraying) return;
            var at = _last;
            float size = (float)_strokeSize;
            var color = StrokeColor;
            _doc.Draw(c => BrushRenderer.Spray(c, at, color, size, _random), BrushRenderer.SprayBounds(at, size));
        }

        private void StrokeTo(SKPoint p)
        {
            if (_doc == null) return;
            var a = _last;
            var b = p;
            float size = (float)_strokeSize;

            switch (_tool)
            {
                case PaintTool.Pencil:
                {
                    var color = StrokeColor;
                    _doc.Draw(c => BrushRenderer.Pencil(c, a, b, color, size), BrushRenderer.Bounds(a, b, size));
                    break;
                }
                case PaintTool.Eraser when !_gestureSecondary:
                {
                    var color = Sk(_secondary);
                    _doc.Draw(c => BrushRenderer.Eraser(c, a, b, color, size), BrushRenderer.Bounds(a, b, size));
                    break;
                }
                case PaintTool.Eraser:
                {
                    // Right-drag: colour replacement, colour 1 becomes colour 2 under the square.
                    float half = Math.Max(1, size) / 2f;
                    foreach (var s in BrushRenderer.Steps(a, b, Math.Max(1, half / 2)))
                        _doc.ReplaceColor(SKRectI.Round(new SKRect(s.X - half, s.Y - half, s.X + half, s.Y + half)), Sk(_primary), Sk(_secondary));
                    break;
                }
                case PaintTool.Brush when _gesture == Gesture.WholeStroke:
                {
                    _points.Add(b);
                    var area = BrushRenderer.Bounds(_points, size);
                    var dirty = Union(_previewArea, area);
                    _doc.RestoreFromBase(dirty);
                    var color = StrokeColor;
                    var kind = _brush;
                    var points = _points.ToArray();
                    _doc.Draw(c => BrushRenderer.WholeStroke(c, kind, points, color, size), dirty);
                    _previewArea = area;
                    break;
                }
                case PaintTool.Brush:
                {
                    var color = StrokeColor;
                    var kind = _brush;
                    var dirty = kind == BrushKind.Airbrush ? BrushRenderer.SprayBounds(b, size) : BrushRenderer.Bounds(a, b, size);
                    _doc.Draw(c => BrushRenderer.Segment(c, kind, a, b, color, size, _random), dirty);
                    break;
                }
            }
            _last = p;
        }

        private static SKRectI Union(SKRectI a, SKRectI b)
        {
            if (a.IsEmpty) return b;
            if (b.IsEmpty) return a;
            return SKRectI.Union(a, b);
        }

        // ---- Shapes ----------------------------------------------------------------

        private void ShapeDown(SKPoint p, bool secondary, bool shift, int clickCount)
        {
            if (_doc == null) return;

            if (_shape == ShapeKind.Curve && _curveStage > 0)
            {
                if (_curveStage == 1) _bend1 = _bend2 = p; else _bend2 = p;
                _gesture = Gesture.Shape;
                RedrawShape();
                return;
            }

            if (_shape == ShapeKind.Polygon && _polygonActive)
            {
                bool closesOnStart = _points.Count > 2 && SKPoint.Distance(_points[0], p) <= Math.Max(4, _strokeSize);
                if (clickCount >= 2 || closesOnStart)
                {
                    FinishPendingShape();
                    return;
                }
                _points.Add(p);
                _gesture = Gesture.Shape;
                RedrawShape();
                return;
            }

            FinishPendingShape();
            _doc.BeginOperation();
            _gestureSecondary = secondary;
            _start = p;
            _points.Clear();
            _points.Add(p);
            _points.Add(p);
            _previewArea = SKRectI.Empty;
            _gesture = Gesture.Shape;
        }

        private void ShapeMove(SKPoint p, bool shift)
        {
            if (_shape == ShapeKind.Curve && _curveStage > 0)
            {
                if (_curveStage == 1) _bend1 = _bend2 = p; else _bend2 = p;
            }
            else
            {
                var anchor = _shape == ShapeKind.Polygon && _points.Count > 2 ? _points[^2] : _start;
                _points[^1] = shift ? Constrain(anchor, p) : p;
            }
            RedrawShape();
        }

        /// <summary>Shift: a square box, or a line snapped to the nearest 45 degrees.</summary>
        private SKPoint Constrain(SKPoint from, SKPoint to)
        {
            float dx = to.X - from.X, dy = to.Y - from.Y;
            if (_shape is ShapeKind.Line or ShapeKind.Curve or ShapeKind.Polygon)
            {
                double angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
                double length = Math.Sqrt(dx * dx + dy * dy);
                return new SKPoint(from.X + (float)(Math.Cos(angle) * length), from.Y + (float)(Math.Sin(angle) * length));
            }
            float side = Math.Max(Math.Abs(dx), Math.Abs(dy));
            return new SKPoint(from.X + side * Math.Sign(dx == 0 ? 1 : dx), from.Y + side * Math.Sign(dy == 0 ? 1 : dy));
        }

        private void ShapeUp(SKPoint p, bool shift)
        {
            if (_doc == null) return;
            _gesture = Gesture.None;

            if (_shape == ShapeKind.Curve)
            {
                if (_curveStage == 0)
                {
                    if (SKPoint.Distance(_points[0], _points[^1]) < 1) { _doc.CancelOperation(); return; }
                    _curveStart = _points[0];
                    _curveEnd = _points[^1];
                    _bend1 = _curveStart;
                    _bend2 = _curveEnd;
                    _curveStage = 1;
                }
                else if (_curveStage == 1) _curveStage = 2;
                else _curveStage = 0;
                return;
            }

            if (_shape == ShapeKind.Polygon)
            {
                if (!_polygonActive)
                {
                    if (SKPoint.Distance(_points[0], _points[^1]) < 1) { _doc.CancelOperation(); return; }
                    _polygonActive = true;
                }
                return;
            }

            // A click without a drag draws nothing in Paint; it should not leave an empty undo step either.
            if (SKPoint.Distance(_start, _points[^1]) < 1) _doc.CancelOperation();
        }

        /// <summary>Closes an open polygon and lets go of a half-bent curve.</summary>
        private void FinishPendingShape()
        {
            if (_polygonActive)
            {
                _polygonActive = false;
                RedrawShape(closePolygon: true);
            }
            _curveStage = 0;
            if (_gesture == Gesture.Shape) _gesture = Gesture.None;
        }

        private void RedrawShape(bool closePolygon = false)
        {
            if (_doc == null) return;

            float size = (float)_strokeSize;
            SKPath path;
            bool closed;
            switch (_shape)
            {
                case ShapeKind.Line:
                    path = ShapeGeometry.LinePath(_points[0], _points[^1]);
                    closed = false;
                    break;
                case ShapeKind.Curve:
                    path = _curveStage == 0
                        ? ShapeGeometry.LinePath(_points[0], _points[^1])
                        : ShapeGeometry.CurvePath(_curveStart, _bend1, _bend2, _curveEnd);
                    closed = false;
                    break;
                case ShapeKind.Polygon:
                    path = ShapeGeometry.PolylinePath(_points, closePolygon);
                    closed = closePolygon;
                    break;
                default:
                    path = ShapeGeometry.Build(_shape, new SKRect(_start.X, _start.Y, _points[^1].X, _points[^1].Y));
                    closed = true;
                    break;
            }

            using (path)
            {
                var bounds = SKRectI.Ceiling(path.Bounds);
                bounds.Inflate((int)Math.Ceiling(size) + 2, (int)Math.Ceiling(size) + 2);
                var dirty = Union(_previewArea, bounds);
                _doc.RestoreFromBase(dirty);

                var outline = StrokeColor;
                var fill = OtherColor;
                bool drawOutline = !closed || _outlineIndex == 1;
                bool drawFill = closed && _fillIndex == 1;
                _doc.Draw(c =>
                {
                    if (drawFill)
                    {
                        using var paint = new SKPaint { Color = fill, IsAntialias = true, Style = SKPaintStyle.Fill };
                        c.DrawPath(path, paint);
                    }
                    if (drawOutline)
                    {
                        using var paint = new SKPaint
                        {
                            Color = outline,
                            IsAntialias = true,
                            Style = SKPaintStyle.Stroke,
                            StrokeWidth = size,
                            StrokeCap = SKStrokeCap.Round,
                            StrokeJoin = SKStrokeJoin.Round,
                        };
                        c.DrawPath(path, paint);
                    }
                }, dirty);
                _previewArea = bounds;
            }
        }

        // ---- Text --------------------------------------------------------------------

        private bool _isTextEditing;
        public bool IsTextEditing => _isTextEditing;

        private double _textX, _textY;
        public double TextX => _textX;
        public double TextY => _textY;

        private string _textContent = string.Empty;
        public string TextContent
        {
            get => _textContent;
            set => this.RaiseAndSetIfChanged(ref _textContent, value ?? string.Empty);
        }

        private string _textFontName = "Segoe UI";
        public string TextFontName
        {
            get => _textFontName;
            set
            {
                this.RaiseAndSetIfChanged(ref _textFontName, string.IsNullOrWhiteSpace(value) ? "Segoe UI" : value);
                this.RaisePropertyChanged(nameof(TextFontFamily));
            }
        }

        public FontFamily TextFontFamily => new(_textFontName);

        private double _textSizePt = 18;
        /// <summary>In points, as Paint shows it. The canvas is 96 dpi, so 12pt is 16px.</summary>
        public double TextSizePt
        {
            get => _textSizePt;
            set
            {
                this.RaiseAndSetIfChanged(ref _textSizePt, Math.Clamp(value, 4, 500));
                this.RaisePropertyChanged(nameof(TextSizePx));
            }
        }

        public double TextSizePx => _textSizePt * 96.0 / 72.0;

        public IReadOnlyList<double> FontSizes { get; } =
            new double[] { 8, 9, 10, 11, 12, 14, 16, 18, 20, 22, 24, 26, 28, 36, 48, 72, 96, 144 };

        private IReadOnlyList<string>? _fontFamilies;
        public IReadOnlyList<string> FontFamilies => _fontFamilies ??= LoadFontFamilies();

        private static IReadOnlyList<string> LoadFontFamilies()
        {
            try
            {
                var list = TextRenderer.SystemFamilies();
                return list.Count > 0 ? list : new[] { "Segoe UI" };
            }
            catch
            {
                return new[] { "Segoe UI" };
            }
        }

        private bool _textBold, _textItalic, _textUnderline, _textStrikethrough, _textOpaque;

        public bool TextBold
        {
            get => _textBold;
            set
            {
                this.RaiseAndSetIfChanged(ref _textBold, value);
                this.RaisePropertyChanged(nameof(TextFontWeight));
            }
        }

        public bool TextItalic
        {
            get => _textItalic;
            set
            {
                this.RaiseAndSetIfChanged(ref _textItalic, value);
                this.RaisePropertyChanged(nameof(TextFontStyle));
            }
        }

        public bool TextUnderline
        {
            get => _textUnderline;
            set => this.RaiseAndSetIfChanged(ref _textUnderline, value);
        }

        public bool TextStrikethrough
        {
            get => _textStrikethrough;
            set => this.RaiseAndSetIfChanged(ref _textStrikethrough, value);
        }

        /// <summary>Paint's Opaque / Transparent background switch for text.</summary>
        public bool TextOpaque
        {
            get => _textOpaque;
            set
            {
                this.RaiseAndSetIfChanged(ref _textOpaque, value);
                this.RaisePropertyChanged(nameof(TextBackgroundBrush));
            }
        }

        public FontWeight TextFontWeight => _textBold ? FontWeight.Bold : FontWeight.Normal;
        public FontStyle TextFontStyle => _textItalic ? FontStyle.Italic : FontStyle.Normal;
        public IBrush TextBackgroundBrush => _textOpaque ? new SolidColorBrush(_secondary) : Brushes.Transparent;

        /// <summary>
        /// How far the typed text sits inside the text box: its border plus padding. The
        /// committed text has to land exactly where it was shown while typing.
        /// </summary>
        public const double TextBoxInset = 3;

        private void BeginText(SKPoint at)
        {
            _textX = at.X;
            _textY = at.Y;
            _textContent = string.Empty;
            _isTextEditing = true;
            this.RaisePropertyChanged(nameof(TextX));
            this.RaisePropertyChanged(nameof(TextY));
            this.RaisePropertyChanged(nameof(TextContent));
            this.RaisePropertyChanged(nameof(IsTextEditing));
            TextEditStarted?.Invoke();
        }

        /// <summary>Burns the text box's contents into the picture.</summary>
        public void CommitText()
        {
            if (!_isTextEditing) return;
            _isTextEditing = false;
            this.RaisePropertyChanged(nameof(IsTextEditing));

            if (_doc == null || string.IsNullOrEmpty(_textContent)) return;

            var style = new TextStyle(_textFontName, (float)TextSizePx, _textBold, _textItalic, _textUnderline, _textStrikethrough);
            float x = (float)(_textX + TextBoxInset), y = (float)(_textY + TextBoxInset);
            string text = _textContent;
            var color = Sk(_primary);
            SKColor? background = _textOpaque ? Sk(_secondary) : null;

            _doc.BeginOperation();
            _doc.Draw(c => TextRenderer.Draw(c, text, x, y, style, color, background));
            TextContent = string.Empty;
        }

        public void CancelText()
        {
            if (!_isTextEditing) return;
            _isTextEditing = false;
            TextContent = string.Empty;
            this.RaisePropertyChanged(nameof(IsTextEditing));
        }

        // ---- Selection -------------------------------------------------------------

        private SKPath? _selPath;
        private SKPointI _selOffset;

        // A lifted ("floating") selection: its pixels, where they started, and the
        // picture underneath with the hole they left.
        private SKBitmap? _float, _floatDraw, _floatBase;
        private SKPointI _floatOrigin;

        public bool HasSelection => _selPath != null;
        public bool IsFloating => _float != null;

        private bool _transparentSelection;
        /// <summary>When on, colour 2 in a moved or pasted selection drops out and shows what is underneath.</summary>
        public bool TransparentSelection
        {
            get => _transparentSelection;
            set
            {
                this.RaiseAndSetIfChanged(ref _transparentSelection, value);
                _floatDraw?.Dispose();
                _floatDraw = null;
                if (IsFloating) RenderFloat(FloatRect);
            }
        }

        public SKRectI? SelectionBounds
        {
            get
            {
                if (_selPath == null) return null;
                var b = SKRectI.Round(_selPath.Bounds);
                b.Offset(_selOffset.X, _selOffset.Y);
                return b;
            }
        }

        private SKRectI FloatRect => _float == null
            ? SKRectI.Empty
            : SKRectI.Create(_floatOrigin.X + _selOffset.X, _floatOrigin.Y + _selOffset.Y, _float.Width, _float.Height);

        private bool SelectionContains(SKPoint p) =>
            _selPath != null && _selPath.Contains(p.X - _selOffset.X, p.Y - _selOffset.Y);

        private void SelectDown(SKPoint p, bool ctrl)
        {
            if (_selPath != null && SelectionContains(p))
            {
                if (!IsFloating) Lift(keepOriginal: ctrl);
                else if (ctrl) { StampFloat(); }
                _gesture = Gesture.MoveFloat;
                _start = p;
                return;
            }

            ClearSelection();
            _start = p;
            _points.Clear();
            _points.Add(p);
            if (_tool == PaintTool.Select)
            {
                _gesture = Gesture.Marquee;
                _marquee = new SKRect(p.X, p.Y, p.X, p.Y);
            }
            else
            {
                _gesture = Gesture.Lasso;
            }
            RaiseSelection();
        }

        private void SetSelection(SKPath path)
        {
            CommitFloat();
            _selPath?.Dispose();
            _selPath = path;
            _selOffset = default;
            RaiseSelection();
        }

        /// <summary>Lets go of the selection, leaving any moved pixels where they now are.</summary>
        public void ClearSelection()
        {
            CommitFloat();
            _selPath?.Dispose();
            _selPath = null;
            _selOffset = default;
            _marquee = null;
            RaiseSelection();
        }

        public void SelectAll()
        {
            if (_doc == null) return;
            Tool = PaintTool.Select;
            var path = new SKPath();
            path.AddRect(new SKRect(0, 0, _doc.Width, _doc.Height));
            SetSelection(path);
        }

        public void InvertSelection()
        {
            if (_doc == null) return;
            CommitFloat();
            var full = new SKPath();
            full.AddRect(new SKRect(0, 0, _doc.Width, _doc.Height));
            if (_selPath == null)
            {
                SetSelection(full);
                return;
            }
            var current = new SKPath(_selPath);
            current.Offset(_selOffset.X, _selOffset.Y);
            var inverted = full.Op(current, SKPathOp.Difference);
            full.Dispose();
            current.Dispose();
            if (inverted == null || inverted.IsEmpty) { inverted?.Dispose(); ClearSelection(); return; }
            SetSelection(inverted);
        }

        /// <summary>Is the selection just a rectangle? Then the corners need no masking.</summary>
        private bool SelectionIsRect => _selPath != null && _selPath.IsRect;

        /// <summary>
        /// Picks the selected pixels up so they can move. The hole they leave is filled
        /// with colour 2, as in Paint -- unless <paramref name="keepOriginal"/>, which is
        /// Ctrl+drag making a copy.
        /// </summary>
        private void Lift(bool keepOriginal)
        {
            if (_doc == null || _selPath == null || IsFloating) return;

            var area = SKRectI.Round(_selPath.Bounds);
            area.Intersect(_doc.Bounds);
            if (area.IsEmpty) return;

            // The path moves to where the pixels now are, so _selOffset can start at zero.
            if (_selOffset.X != 0 || _selOffset.Y != 0)
            {
                _selPath.Offset(_selOffset.X, _selOffset.Y);
                _selOffset = default;
                area = SKRectI.Round(_selPath.Bounds);
                area.Intersect(_doc.Bounds);
            }

            _doc.BeginOperation();
            var mask = SelectionIsRect ? null : _selPath;
            _float = PaintDocument.Extract(_doc.Bitmap, area, mask);
            _floatOrigin = new SKPointI(area.Left, area.Top);
            _floatBase = _doc.Bitmap.Copy();

            if (!keepOriginal)
            {
                using var canvas = new SKCanvas(_floatBase);
                using var paint = new SKPaint { Color = Sk(_secondary), IsAntialias = false, Style = SKPaintStyle.Fill, BlendMode = SKBlendMode.Src };
                canvas.DrawPath(_selPath, paint);
            }

            RenderFloat(area);
            RaiseSelection();
        }

        /// <summary>Ctrl+drag on a selection already lifted: leave a copy behind and carry on with another.</summary>
        private void StampFloat()
        {
            if (_doc == null || _float == null) return;
            _floatBase?.Dispose();
            _floatBase = _doc.Bitmap.Copy();
        }

        private SKBitmap FloatPixels()
        {
            if (!_transparentSelection) return _float!;
            return _floatDraw ??= PaintDocument.WithoutColor(_float!, Sk(_secondary));
        }

        private void RenderFloat(SKRectI previous)
        {
            if (_doc == null || _float == null || _floatBase == null) return;
            var now = FloatRect;
            var dirty = Union(previous, now);
            _doc.RestoreFrom(_floatBase, dirty);
            var pixels = FloatPixels();
            _doc.Draw(c => c.DrawBitmap(pixels, now.Left, now.Top), dirty);
        }

        private void MoveFloatBy(int dx, int dy)
        {
            if (!IsFloating) return;
            var before = FloatRect;
            _selOffset = new SKPointI(_selOffset.X + dx, _selOffset.Y + dy);
            RenderFloat(before);
            RaiseSelection();
        }

        /// <summary>The arrow keys: lift if need be, then move one pixel.</summary>
        public void NudgeSelection(int dx, int dy)
        {
            if (_selPath == null) return;
            if (!IsFloating) Lift(keepOriginal: false);
            MoveFloatBy(dx, dy);
        }

        /// <summary>Drops the floating pixels where they are. The selection outline stays.</summary>
        private void CommitFloat()
        {
            if (_float == null) return;
            if (_selPath != null)
            {
                // Re-anchor the outline to where the pixels actually landed.
                var now = FloatRect;
                if (SelectionIsRect)
                {
                    _selPath.Reset();
                    _selPath.AddRect(new SKRect(now.Left, now.Top, now.Right, now.Bottom));
                }
                else
                {
                    _selPath.Offset(_selOffset.X, _selOffset.Y);
                }
                _selOffset = default;
            }
            DropFloat();
        }

        private void DropFloat()
        {
            _float?.Dispose();
            _floatDraw?.Dispose();
            _floatBase?.Dispose();
            _float = _floatDraw = _floatBase = null;
        }

        public void DeleteSelection()
        {
            if (_doc == null || _selPath == null) return;

            if (IsFloating)
            {
                // The hole was already filled when the pixels were lifted; putting the
                // base back is the delete.
                _doc.RestoreFrom(_floatBase, _doc.Bounds);
                DropFloat();
                _doc.Invalidate(_doc.Bounds);
            }
            else
            {
                var path = new SKPath(_selPath);
                path.Offset(_selOffset.X, _selOffset.Y);
                _doc.BeginOperation();
                var color = Sk(_secondary);
                var bounds = SKRectI.Round(path.Bounds);
                _doc.Draw(c =>
                {
                    using var paint = new SKPaint { Color = color, IsAntialias = false, Style = SKPaintStyle.Fill, BlendMode = SKBlendMode.Src };
                    c.DrawPath(path, paint);
                }, bounds);
                path.Dispose();
            }

            _selPath?.Dispose();
            _selPath = null;
            _selOffset = default;
            RaiseSelection();
        }

        public void CropToSelection()
        {
            if (_doc == null || _selPath == null) return;
            CommitFloat();
            var area = SKRectI.Round(_selPath.Bounds);
            area.Intersect(_doc.Bounds);
            if (area.IsEmpty) return;

            var mask = SelectionIsRect ? null : new SKPath(_selPath);
            var cropped = PaintDocument.Cropped(_doc.Bitmap, area, mask, Sk(_secondary));
            mask?.Dispose();

            _selPath.Dispose();
            _selPath = null;
            _selOffset = default;
            _doc.Commit(cropped);
            RaiseSelection();
        }

        /// <summary>The selection's pixels, or the whole picture when nothing is selected.</summary>
        public SKBitmap? CopySelectionBitmap()
        {
            if (_doc == null) return null;
            if (IsFloating) return _float!.Copy();
            if (_selPath == null) return _doc.Bitmap.Copy();

            var path = new SKPath(_selPath);
            path.Offset(_selOffset.X, _selOffset.Y);
            var area = SKRectI.Round(path.Bounds);
            area.Intersect(_doc.Bounds);
            var result = area.IsEmpty ? null : PaintDocument.Extract(_doc.Bitmap, area, SelectionIsRect ? null : path);
            path.Dispose();
            return result;
        }

        public bool CopySelectionToClipboard()
        {
            using var bitmap = CopySelectionBitmap();
            if (bitmap == null) return false;
            if (!OperatingSystem.IsWindows())
            {
                ErrorMessage = "Copying to the clipboard is only available on Windows.";
                return false;
            }

            try
            {
                using var image = SKImage.FromBitmap(bitmap);
                using var png = image.Encode(SKEncodedImageFormat.Png, 100);
                string path = ClipboardImage.WritePaste(png.ToArray());
                if (!ClipboardImage.Copy(path))
                {
                    ErrorMessage = "Could not copy to the clipboard.";
                    return false;
                }
                StatusMessage = "Copied";
                return true;
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Could not copy: {ex.Message}";
                return false;
            }
        }

        public void CutSelection()
        {
            if (_selPath == null) return;
            if (CopySelectionToClipboard()) DeleteSelection();
        }

        /// <summary>
        /// Pastes the clipboard's image as a floating selection at <paramref name="at"/>,
        /// growing the canvas first if the image would not fit -- what Paint offers to do.
        /// </summary>
        public bool PasteIntoCanvas(PixelPoint at)
        {
            if (_doc == null) return false;
            if (!OperatingSystem.IsWindows())
            {
                ErrorMessage = "Pasting is only available on Windows.";
                return false;
            }

            string? path = ClipboardImage.Paste(IsNavigable);
            if (path == null)
            {
                StatusMessage = "No image on the clipboard";
                return false;
            }

            try
            {
                var (pixels, w, h) = DecodeForEditing(path, ImageOrientation.Identity);
                PasteBitmap(PaintDocument.BitmapFromUnpremulBgra(pixels, w, h), at);
                return true;
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Could not paste: {ex.Message}";
                return false;
            }
        }

        /// <summary>Places <paramref name="pasted"/> as a floating selection. Takes ownership of it.</summary>
        public void PasteBitmap(SKBitmap pasted, PixelPoint at)
        {
            if (_doc == null) { pasted.Dispose(); return; }
            FinishPendingWork(deselect: true);
            Tool = PaintTool.Select;

            int x = Math.Clamp(at.X, 0, Math.Max(0, _doc.Width - 1));
            int y = Math.Clamp(at.Y, 0, Math.Max(0, _doc.Height - 1));
            if (pasted.Width > _doc.Width || pasted.Height > _doc.Height)
            {
                x = y = 0;
                _doc.Commit(PaintDocument.WithCanvasSize(_doc.Bitmap,
                    Math.Max(_doc.Width, pasted.Width), Math.Max(_doc.Height, pasted.Height), Sk(_secondary)));
            }
            else
            {
                x = Math.Min(x, _doc.Width - pasted.Width);
                y = Math.Min(y, _doc.Height - pasted.Height);
            }

            _doc.BeginOperation();
            _float = pasted;
            _floatOrigin = new SKPointI(x, y);
            _floatBase = _doc.Bitmap.Copy();
            _selPath?.Dispose();
            _selPath = new SKPath();
            _selPath.AddRect(new SKRect(x, y, x + pasted.Width, y + pasted.Height));
            _selOffset = default;
            RenderFloat(FloatRect);
            RaiseSelection();
        }

        // ---- Image operations ------------------------------------------------------
        // With a selection they act on the selection, as Paint's do; otherwise on the
        // whole picture.

        private void TransformSelection(Func<SKBitmap, SKBitmap> transform)
        {
            if (_doc == null || _selPath == null) return;
            if (!IsFloating) Lift(keepOriginal: false);
            if (_float == null) return;

            var before = FloatRect;
            var next = transform(_float);
            _float.Dispose();
            _floatDraw?.Dispose();
            _floatDraw = null;
            _float = next;

            // Keep it centred on where it was.
            _floatOrigin = new SKPointI(before.MidX - next.Width / 2, before.MidY - next.Height / 2);
            _selOffset = default;
            _selPath.Reset();
            _selPath.AddRect(new SKRect(_floatOrigin.X, _floatOrigin.Y, _floatOrigin.X + next.Width, _floatOrigin.Y + next.Height));
            RenderFloat(before);
            RaiseSelection();
        }

        private void TransformCanvas(Func<SKBitmap, SKBitmap> transform)
        {
            if (_doc == null) return;
            FinishPendingWork(deselect: false);
            if (_selPath != null)
            {
                TransformSelection(transform);
                return;
            }
            _doc.Commit(transform(_doc.Bitmap));
        }

        public void RotateCanvas(int degrees) => TransformCanvas(b => PaintDocument.Rotated(b, degrees));

        public void FlipCanvas(bool horizontal) => TransformCanvas(b => PaintDocument.Flipped(b, horizontal));

        public void InvertColors() => TransformCanvas(PaintDocument.Inverted);

        public void ResizeCanvas(int width, int height) =>
            TransformCanvas(b => PaintDocument.Resized(b, width, height));

        public void SkewCanvas(double horizontalDegrees, double verticalDegrees)
        {
            if (horizontalDegrees == 0 && verticalDegrees == 0) return;
            var background = _selPath != null ? SKColors.Transparent : Sk(_secondary);
            TransformCanvas(b => PaintDocument.Skewed(b, (float)horizontalDegrees, (float)verticalDegrees, background));
        }

        /// <summary>Image Properties: a new canvas size, no scaling, extra space in colour 2.</summary>
        public void SetCanvasSize(int width, int height)
        {
            if (_doc == null || width < 1 || height < 1) return;
            if (width == _doc.Width && height == _doc.Height) return;
            ClearSelection();
            FinishPendingWork(deselect: true);
            _doc.Commit(PaintDocument.WithCanvasSize(_doc.Bitmap, width, height, Sk(_secondary)));
        }

        /// <summary>The size Resize starts from: the selection if there is one, otherwise the canvas.</summary>
        public PixelSize ResizeSubjectSize
        {
            get
            {
                if (IsFloating) return new PixelSize(_float!.Width, _float.Height);
                if (SelectionBounds is { } b) return new PixelSize(b.Width, b.Height);
                return _doc == null ? default : new PixelSize(_doc.Width, _doc.Height);
            }
        }

        // ---- History ---------------------------------------------------------------

        public void Undo()
        {
            if (_doc == null) return;
            // A text box or half-drawn shape is the newest thing; undo throws it away.
            if (_isTextEditing) { CancelText(); return; }
            _polygonActive = false;
            _curveStage = 0;
            _gesture = Gesture.None;

            // Undoing while a selection floats puts it back where it was lifted from:
            // the undo entry is the picture as it was just before the lift.
            DropFloat();
            _selPath?.Dispose();
            _selPath = null;
            _selOffset = default;
            _doc.Undo();
            RaiseSelection();
        }

        public void Redo()
        {
            if (_doc == null) return;
            FinishPendingWork(deselect: true);
            _doc.Redo();
        }

        /// <summary>
        /// Settles anything half-done -- an open text box, a polygon still taking corners,
        /// a curve waiting to be bent -- before something else happens to the picture.
        /// </summary>
        public void FinishPendingWork(bool deselect)
        {
            CommitText();
            FinishPendingShape();
            if (deselect) ClearSelection();
        }

        /// <summary>Esc: cancel the newest piece of in-progress work. True if there was any.</summary>
        public bool CancelCurrent()
        {
            if (_isTextEditing) { CancelText(); return true; }
            if (_polygonActive || _curveStage > 0) { FinishPendingShape(); return true; }
            if (_selPath != null || _marquee != null) { ClearSelection(); return true; }
            return false;
        }
    }
}
