using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Svg.Skia;
using Avalonia.Threading;
using ImageMagick;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using ReactiveUI;

namespace Atelier.ViewModels
{
    public class MetadataItem
    {
        public string Label { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    public class MainWindowViewModel : ViewModelBase
    {
        private object? _imageSource;
        public object? ImageSource
        {
            get => _imageSource;
            set => this.RaiseAndSetIfChanged(ref _imageSource, value);
        }

        private string? _imagePath;
        public string? ImagePath
        {
            get => _imagePath;
            set
            {
                this.RaiseAndSetIfChanged(ref _imagePath, value);
                this.RaisePropertyChanged(nameof(HasImage));
                this.RaisePropertyChanged(nameof(CanSetWallpaper));
                this.RaisePropertyChanged(nameof(CanOpenInPaint));
                this.RaisePropertyChanged(nameof(CanEditImage));
                this.RaisePropertyChanged(nameof(IsDirty));
            }
        }

        /// <summary>Greys out the menu items that act on the file on disk.</summary>
        public bool HasImage => !string.IsNullOrEmpty(ImagePath);

        /// <summary>
        /// Set as Wallpaper reads the file on disk, so it is unavailable while editing --
        /// otherwise it would quietly ignore the adjustments on screen.
        /// </summary>
        public bool CanSetWallpaper => HasImage && IsViewMode;

        /// <summary>
        /// What Microsoft Paint will actually open. Deliberately narrower than the
        /// formats Atelier can display: Paint has no SVG support at all, and HEIC/HEIF
        /// only decode when the optional Store extension is installed, so offering
        /// either would hand the user a Paint window with an error in it.
        /// </summary>
        private static readonly HashSet<string> PaintExtensions = new(StringComparer.OrdinalIgnoreCase)
            { ".bmp", ".dib", ".jpg", ".jpeg", ".jpe", ".jfif", ".gif", ".png", ".tif", ".tiff", ".ico", ".webp" };

        /// <summary>
        /// Paint reads the file on disk, so -- like Set as Wallpaper -- it is unavailable
        /// while editing, where it would silently miss the adjustments on screen.
        /// </summary>
        public bool CanOpenInPaint =>
            HasImage && IsViewMode && PaintExtensions.Contains(Path.GetExtension(ImagePath!));

        private string? _statusMessage;
        /// <summary>Transient confirmation shown in the bottom bar; cleared on a timer.</summary>
        public string? StatusMessage
        {
            get => _statusMessage;
            set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
        }

        private string? _errorMessage;
        public string? ErrorMessage
        {
            get => _errorMessage;
            set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
        }

        private double _zoomLevel = 1.0;
        public double ZoomLevel
        {
            get => _zoomLevel;
            set => this.RaiseAndSetIfChanged(ref _zoomLevel, value);
        }

        private bool _showControls = true;
        public bool ShowControls
        {
            get => _showControls;
            set
            {
                this.RaiseAndSetIfChanged(ref _showControls, value);
                this.RaisePropertyChanged(nameof(ChevronData));
                this.RaisePropertyChanged(nameof(ShowTopBar));
                this.RaisePropertyChanged(nameof(IsRightPaneVisible));
            }
        }

        private bool _showMetadata = true;
        public bool ShowMetadata
        {
            get => _showMetadata;
            set
            {
                this.RaiseAndSetIfChanged(ref _showMetadata, value);
                this.RaisePropertyChanged(nameof(IsRightPaneVisible));
            }
        }

        private bool _isFullScreen;
        /// <summary>Fullscreen shows nothing but the image, so every piece of chrome keys off this.</summary>
        public bool IsFullScreen
        {
            get => _isFullScreen;
            set
            {
                this.RaiseAndSetIfChanged(ref _isFullScreen, value);
                RaiseChromeChanged();
            }
        }

        private bool _isFrameMode;
        /// <summary>Picture frame mode: only the image in a plain window with the system caption buttons.</summary>
        public bool IsFrameMode
        {
            get => _isFrameMode;
            set
            {
                this.RaiseAndSetIfChanged(ref _isFrameMode, value);
                // A fresh entry always shows the exit hint; the view hides it on a timer.
                if (value) ExitFrameHintVisible = true;
                RaiseChromeChanged();
            }
        }

        private bool _exitFrameHintVisible = true;
        /// <summary>
        /// The EXIT FRAME overlay auto-hides a few seconds into frame mode; hovering
        /// the top strip brings it back. The timer lives in the view.
        /// </summary>
        public bool ExitFrameHintVisible
        {
            get => _exitFrameHintVisible;
            set
            {
                this.RaiseAndSetIfChanged(ref _exitFrameHintVisible, value);
                this.RaisePropertyChanged(nameof(ShowExitFrameButton));
            }
        }

        private void RaiseChromeChanged()
        {
            this.RaisePropertyChanged(nameof(IsChromeVisible));
            this.RaisePropertyChanged(nameof(ShowTopBar));
            this.RaisePropertyChanged(nameof(IsRightPaneVisible));
            this.RaisePropertyChanged(nameof(ShowExitFrameButton));
        }

        /// <summary>False in fullscreen and picture frame mode, which both strip the window down to the image.</summary>
        public bool IsChromeVisible => !IsFullScreen && !IsFrameMode;

        public bool ShowTopBar => ShowControls && IsChromeVisible;

        /// <summary>
        /// Hidden while fullscreen is layered on top of frame mode: Esc/F unwind
        /// fullscreen first, so the button would mislead there.
        /// </summary>
        public bool ShowExitFrameButton => IsFrameMode && !IsFullScreen && ExitFrameHintVisible;

        public bool IsRightPaneVisible => ShowControls && IsChromeVisible && (IsEditMode || ShowMetadata);

        public string ChevronData => ShowControls ? "M 0 0 L 5 5 L 10 0" : "M 0 5 L 5 0 L 10 5";

        private ObservableCollection<MetadataItem> _metadataItems = new();
        public ObservableCollection<MetadataItem> MetadataItems
        {
            get => _metadataItems;
            set => this.RaiseAndSetIfChanged(ref _metadataItems, value);
        }

        private List<string> _fileList = new();
        private int _currentIndex = -1;

        private double _imageWidth;
        /// <summary>The frame as stored on disk, before any rotation is applied.</summary>
        public double ImageWidth
        {
            get => _imageWidth;
            set
            {
                this.RaiseAndSetIfChanged(ref _imageWidth, value);
                this.RaisePropertyChanged(nameof(DisplayWidth));
                this.RaisePropertyChanged(nameof(DisplayHeight));
            }
        }

        private double _imageHeight;
        public double ImageHeight
        {
            get => _imageHeight;
            set
            {
                this.RaiseAndSetIfChanged(ref _imageHeight, value);
                this.RaisePropertyChanged(nameof(DisplayWidth));
                this.RaisePropertyChanged(nameof(DisplayHeight));
            }
        }

        private ImageOrientation _orientation = ImageOrientation.Identity;

        /// <summary>
        /// What the file itself says, so <see cref="IsDirty"/> can mean "differs from
        /// disk" rather than "was touched". Rotating a photo four times leaves nothing
        /// to save, and neither does undoing a turn -- both land back on this value.
        /// </summary>
        private ImageOrientation _storedOrientation = ImageOrientation.Identity;

        /// <summary>
        /// How the picture is turned on screen. Starts at whatever the EXIF orientation
        /// tag asked for and moves from there as the user rotates; see
        /// <see cref="ImageOrientation"/> for why those are deliberately the same value.
        /// </summary>
        public ImageOrientation Orientation
        {
            get => _orientation;
            private set
            {
                this.RaiseAndSetIfChanged(ref _orientation, value);
                this.RaisePropertyChanged(nameof(RotationAngle));
                this.RaisePropertyChanged(nameof(MirrorScaleX));
                this.RaisePropertyChanged(nameof(DisplayWidth));
                this.RaisePropertyChanged(nameof(DisplayHeight));
                this.RaisePropertyChanged(nameof(IsDirty));
            }
        }

        /// <summary>Bound to the rotate half of the image transform.</summary>
        public double RotationAngle => Orientation.Angle;

        /// <summary>Bound to the mirror half: -1 flips the image left-to-right.</summary>
        public double MirrorScaleX => Orientation.Mirrored ? -1.0 : 1.0;

        /// <summary>
        /// The size the image occupies once turned -- swapped from the stored frame when
        /// it is on its side. Fit-to-view has to use these or a portrait photo is fitted
        /// as though it were still landscape.
        /// </summary>
        public double DisplayWidth => Orientation.SwapsDimensions ? ImageHeight : ImageWidth;

        public double DisplayHeight => Orientation.SwapsDimensions ? ImageWidth : ImageHeight;

        /// <summary>
        /// True when the rotation on screen is not the one in the file. Rotation is
        /// deliberately non-destructive: nothing reaches the disk until Save.
        /// </summary>
        public bool IsDirty => HasImage && Orientation != _storedOrientation;

        public void RotateRight() => Turn(Orientation.RotateRight());

        public void RotateLeft() => Turn(Orientation.RotateLeft());

        public void FlipHorizontal() => Turn(Orientation.FlipHorizontal());

        public void FlipVertical() => Turn(Orientation.FlipVertical());

        private void Turn(ImageOrientation next)
        {
            if (!HasImage) return;
            Orientation = next;
        }

        /// <summary>
        /// What Next/Prev will walk to. GIF and AVIF joined the list when animation
        /// arrived: Atelier has always been able to display both, so arrow-keying past
        /// them in a folder was a gap rather than a decision.
        /// </summary>
        private static readonly string[] NavigableExtensions =
            { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".svg", ".heic", ".heif", ".gif", ".avif" };

        /// <summary>Everything produced off the UI thread, ready to hand to the view.</summary>
        private sealed class LoadedImage
        {
            public Bitmap? Bitmap;
            public SvgSource? Svg;
            public double Width;
            public double Height;
            public ImageOrientation Orientation = ImageOrientation.Identity;
            public AnimatedImage? Animation;
            public int AnimationFrames;
            public bool AnimationTooLarge;
            public List<MetadataItem> Metadata = new();
            public List<string> Siblings = new();
        }

        private AnimatedImage? _animation;
        private int _frameIndex;

        /// <summary>True once an animation has been decoded and there is something to play.</summary>
        public bool IsAnimated => _animation != null;

        private bool _isPlaying;
        public bool IsPlaying
        {
            get => _isPlaying;
            private set => this.RaiseAndSetIfChanged(ref _isPlaying, value);
        }

        public int FrameCount => _animation?.FrameCount ?? 0;

        /// <summary>How long the frame now on screen should be held for.</summary>
        public TimeSpan CurrentFrameDelay =>
            _animation == null ? TimeSpan.FromMilliseconds(100) : _animation.Delays[_frameIndex];

        /// <summary>
        /// Editing is colour work on a single still, so it is closed off for animations
        /// rather than silently flattening one to a single frame inside a Save.
        /// </summary>
        public bool CanEditImage => HasImage && IsViewMode && !IsAnimated;

        /// <summary>Moves to the next frame and loops. Driven by the view's timer.</summary>
        public void AdvanceFrame()
        {
            if (_animation == null || !IsPlaying) return;

            _frameIndex = (_frameIndex + 1) % _animation.FrameCount;
            ImageSource = _animation.Frames[_frameIndex];
        }

        public void TogglePlayback()
        {
            if (_animation == null) return;
            IsPlaying = !IsPlaying;
            StatusMessage = IsPlaying ? null : "Paused";
        }

        public async Task LoadImageAsync(string path)
        {
            try
            {
                ErrorMessage = null;
                ImagePath = path;
                ZoomLevel = 1.0;
                // A fresh picture starts upright until its own tag says otherwise; without
                // this the previous image's rotation would carry over to the next one.
                _storedOrientation = ImageOrientation.Identity;
                Orientation = ImageOrientation.Identity;
                MetadataItems = new ObservableCollection<MetadataItem>();

                // Decoding, EXIF extraction and the directory scan are all file/CPU bound.
                // They used to run on the UI thread, freezing the window for the whole load.
                var loaded = await Task.Run(() => Decode(path));

                // SvgImage is an AvaloniaObject and has thread affinity, so it is built here.
                // Bitmap has none, so it is already decoded on the worker thread.
                ImageSource = loaded.Svg != null
                    ? new SvgImage { Source = loaded.Svg }
                    : loaded.Bitmap;

                ImageWidth = loaded.Width;
                ImageHeight = loaded.Height;

                _storedOrientation = loaded.Orientation;
                Orientation = loaded.Orientation;

                // Only now that ImageSource points at the new picture is the previous
                // animation safe to release -- its frames were what the view was drawing,
                // and a decoded animation is far too large to leave to the collector.
                var previous = _animation;
                _animation = loaded.Animation;
                _frameIndex = 0;
                IsPlaying = _animation != null;
                previous?.Dispose();

                this.RaisePropertyChanged(nameof(IsAnimated));
                this.RaisePropertyChanged(nameof(FrameCount));
                this.RaisePropertyChanged(nameof(CanEditImage));

                if (_animation != null)
                    ImageSource = _animation.Frames[0];

                // One assignment instead of one CollectionChanged per tag -- a photo with a
                // few hundred EXIF tags used to trigger a layout pass for every one of them.
                MetadataItems = new ObservableCollection<MetadataItem>(loaded.Metadata);

                _fileList = loaded.Siblings;
                _currentIndex = _fileList.FindIndex(
                    f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Failed to load image: {ex.Message}";
                ImageSource = null;
                ImageWidth = 0;
                ImageHeight = 0;
            }
        }

        private static LoadedImage Decode(string path)
        {
            var result = new LoadedImage();

            // Invariant casing: on a Turkish locale ToLower() maps 'I' to dotless 'ı',
            // so ".HEIF"/".AVIF"/".ICO" would never match the comparisons below.
            string ext = Path.GetExtension(path).ToLowerInvariant();

            if (ext == ".svg")
            {
                result.Svg = SvgSource.Load(path, null);
                if (result.Svg?.Picture != null)
                {
                    result.Width = result.Svg.Picture.CullRect.Width;
                    result.Height = result.Svg.Picture.CullRect.Height;
                }
                AddBasicMetadata(result.Metadata, path, "SVG Vector");
            }
            else if (ext == ".heic" || ext == ".heif" || ext == ".avif")
            {
                using var image = new MagickImage(path);
                // The HEIF family carries its rotation in the container, and Magick is the
                // only thing here that can read it -- Avalonia is handed flat PNG bytes and
                // never sees the metadata. So this branch bakes the turn in rather than
                // handing an orientation back for the view to apply, which would double it.
                image.AutoOrient();
                result.Width = image.Width;
                result.Height = image.Height;
                string formatLabel = ext == ".avif" ? "AVIF" : "HEIC";
                AddBasicMetadata(result.Metadata, path, $"{formatLabel} {image.Width}x{image.Height}");

                using var ms = new MemoryStream();
                image.Write(ms, MagickFormat.Png);
                ms.Position = 0;
                result.Bitmap = new Bitmap(ms);
                AddExifMetadata(result.Metadata, path);
            }
            else
            {
                var bitmap = new Bitmap(path);
                result.Bitmap = bitmap;
                result.Width = bitmap.Size.Width;
                result.Height = bitmap.Size.Height;
                AddBasicMetadata(result.Metadata, path,
                    $"{(int)bitmap.Size.Width}x{(int)bitmap.Size.Height} {ext.ToUpperInvariant().TrimStart('.')}");
                AddExifMetadata(result.Metadata, path);
                // Avalonia's decoder hands back the sensor frame and ignores the orientation
                // tag entirely, so a portrait phone photo arrives on its side. The view turns
                // it; see ImageOrientation.
                result.Orientation = ReadOrientation(path);
            }

            // Asked before decoding: a long animation is enormous once its frames are
            // expanded, so the file is measured first and declined if it will not fit.
            var probe = AnimationDecoder.Probe(path);
            if (probe.IsAnimated)
            {
                result.AnimationFrames = probe.FrameCount;
                result.AnimationTooLarge = !probe.WithinBudget;
                if (probe.WithinBudget) result.Animation = AnimationDecoder.Decode(path);

                result.Metadata.Add(new MetadataItem
                {
                    Label = "Frames",
                    Value = result.Animation != null
                        ? probe.FrameCount.ToString()
                        : $"{probe.FrameCount} (too large to animate, showing the first)",
                });
            }

            result.Siblings = ScanSiblings(path);
            return result;
        }

        /// <summary>
        /// How the sibling list learns the order Explorer is showing a folder in. A field so
        /// the tests can answer it themselves: a test run has no Explorer window to ask, and
        /// the real reader would only ever hand back null there.
        /// </summary>
        internal static Func<string, List<string>?> ExplorerOrderProvider =
            dir => OperatingSystem.IsWindows() ? ExplorerOrder.TryGetViewOrder(dir) : null;

        /// <summary>
        /// The pictures Next/Prev walk, in the order the folder is laid out on screen.
        ///
        /// Explorer's window is asked first, so navigation matches what the user is looking
        /// at -- a wallpapers folder sorted newest-first walks newest-first. Its answer is
        /// only usable if it actually contains <paramref name="path"/>: a window that is
        /// searching or filtered may not be showing the open picture at all, and a list
        /// without it leaves nowhere to navigate to. Everything else -- no window on that
        /// folder, nothing readable, a list holding nothing we can open -- falls back to the
        /// plain alphabetical scan, which is what Atelier has always done.
        /// </summary>
        private static List<string> ScanSiblings(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (dir == null) return new List<string>();
            try
            {
                var onScreen = FromExplorer(dir);
                if (onScreen != null &&
                    onScreen.Any(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase)))
                    return onScreen;

                return System.IO.Directory.GetFiles(dir)
                    .Where(IsNavigable)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        /// <summary>Explorer's view of a folder, reduced to the pictures we can open. Never throws.</summary>
        private static List<string>? FromExplorer(string dir)
        {
            try
            {
                var shown = ExplorerOrderProvider(dir);
                if (shown == null) return null;
                var navigable = shown.Where(IsNavigable).ToList();
                return navigable.Count > 0 ? navigable : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsNavigable(string file) =>
            NavigableExtensions.Contains(Path.GetExtension(file).ToLowerInvariant());

        public async Task SaveImageAsync(string destinationPath)
        {
            if (string.IsNullOrEmpty(ImagePath)) return;

            try
            {
                string ext = Path.GetExtension(destinationPath).ToLowerInvariant();
                MagickFormat format = ext switch
                {
                    ".jpg" or ".jpeg" => MagickFormat.Jpeg,
                    ".png" => MagickFormat.Png,
                    ".bmp" => MagickFormat.Bmp,
                    ".webp" => MagickFormat.WebP,
                    ".ico" => MagickFormat.Ico,
                    ".heic" => MagickFormat.Heic,
                    ".heif" => MagickFormat.Heif,
                    ".avif" => MagickFormat.Avif,
                    _ => MagickFormat.Png
                };

                // Captured here: the worker thread must not read view model state.
                var orientation = Orientation;

                await Task.Run(() =>
                {
                    try
                    {
                        var readSettings = new MagickReadSettings();
                        if (Path.GetExtension(ImagePath).ToLowerInvariant() == ".svg")
                        {
                            readSettings.Format = MagickFormat.Svg;
                            readSettings.Density = new Density(300);
                            readSettings.BackgroundColor = MagickColors.Transparent;
                        }

                        using var image = new MagickImage(ImagePath, readSettings);

                        ApplyOrientation(image, orientation);

                        if (format == MagickFormat.Jpeg)
                        {
                            image.Quality = 95;
                            image.BackgroundColor = MagickColors.White;
                            image.Alpha(AlphaOption.Remove);
                        }
                        else if (format == MagickFormat.Ico || format == MagickFormat.Png || format == MagickFormat.Heic || format == MagickFormat.Heif || format == MagickFormat.Avif)
                        {
                            image.BackgroundColor = MagickColors.Transparent;
                        }
                        
                        if (format == MagickFormat.Ico)
                        {
                            if (image.Width > 256 || image.Height > 256)
                            {
                                image.Resize(256, 256);
                            }
                        }
                        
                        // Try extension-based write first
                        image.Write(destinationPath);
                    }
                    catch (Exception ex)
                    {
                        if (ex.Message.Contains("no encode delegate") || ex.Message.Contains("not supported"))
                        {
                             throw new Exception($"Writing to format '{format}' is not available in the current Magick configuration. Some patent-restricted formats like HEIC may require specific encoders.");
                        }
                        throw;
                    }
                });
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Failed to save image: {ex.Message}";
            }
        }

        /// <summary>
        /// Turns the pixels to match what is on screen, then clears the orientation tag.
        ///
        /// Clearing the tag is the part that is easy to miss and impossible to ignore
        /// afterwards: bake a 90 degree turn into a file that still says "rotate me 90
        /// degrees" and every viewer, this one included, turns it again on the next open.
        ///
        /// Flop before rotate, matching how <see cref="ImageOrientation"/> is defined.
        /// </summary>
        private static void ApplyOrientation(MagickImage image, ImageOrientation orientation)
        {
            if (orientation.Mirrored) image.Flop();
            if (orientation.Angle != 0) image.Rotate(orientation.Angle);

            image.Orientation = OrientationType.TopLeft;
            var exif = image.GetExifProfile();
            if (exif != null)
            {
                exif.SetValue(ExifTag.Orientation, (ushort)1);
                image.SetProfile(exif);
            }
        }

        /// <summary>
        /// Writes the rotation back to the picture the user is looking at -- what Ctrl+S
        /// does once something has been turned.
        ///
        /// Reloads afterwards rather than just clearing the dirty flag. The file now holds
        /// the rotated pixels and no tag, so the in-memory state has to come back to
        /// identity with it; leaving the old angle in place would apply the same turn a
        /// second time on the next save.
        /// </summary>
        public async Task SaveInPlaceAsync()
        {
            if (string.IsNullOrEmpty(ImagePath) || !IsDirty) return;

            string path = ImagePath;
            await SaveImageAsync(path);
            if (ErrorMessage == null)
            {
                await LoadImageAsync(path);
                StatusMessage = "Saved";
            }
        }

        private static void AddBasicMetadata(List<MetadataItem> items, string path, string typeInfo)
        {
            var info = new FileInfo(path);
            items.Add(new MetadataItem { Label = "Name", Value = info.Name });
            items.Add(new MetadataItem { Label = "Format", Value = typeInfo });
            items.Add(new MetadataItem { Label = "Size", Value = FormatFileSize(info.Length) });
            items.Add(new MetadataItem { Label = "Location", Value = info.DirectoryName ?? "" });
            items.Add(new MetadataItem { Label = "Created", Value = info.CreationTime.ToString("g") });
        }

        private static string FormatFileSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int unitIndex = 0;
            while (size >= 1024 && unitIndex < units.Length - 1)
            {
                size /= 1024;
                unitIndex++;
            }
            return $"{size:0.##} {units[unitIndex]}";
        }

        /// <summary>
        /// The EXIF orientation tag, or upright if the file does not carry one -- which
        /// most PNGs and screenshots do not. Never throws: an unreadable tag is the same
        /// as no tag, and refusing to open a picture over it would be absurd.
        /// </summary>
        private static ImageOrientation ReadOrientation(string path)
        {
            try
            {
                foreach (var directory in ImageMetadataReader.ReadMetadata(path).OfType<ExifIfd0Directory>())
                {
                    if (directory.TryGetInt32(ExifDirectoryBase.TagOrientation, out int tag))
                        return ImageOrientation.FromExif(tag);
                }
            }
            catch { }
            return ImageOrientation.Identity;
        }

        private static void AddExifMetadata(List<MetadataItem> items, string path)
        {
            try
            {
                var directories = ImageMetadataReader.ReadMetadata(path);
                foreach (var directory in directories)
                {
                    if (!directory.Name.Contains("Exif") && directory.Name != "JPEG" && directory.Name != "PNG")
                        continue;

                    foreach (var tag in directory.Tags)
                        items.Add(new MetadataItem { Label = tag.Name, Value = tag.Description ?? "" });
                }
            }
            catch { }
        }

        private bool _isEditMode;
        public bool IsEditMode
        {
            get => _isEditMode;
            set 
            {
                this.RaiseAndSetIfChanged(ref _isEditMode, value);
                this.RaisePropertyChanged(nameof(IsViewMode));
                // Without this, closing the metadata pane and then entering Edit Mode
                // left the editor invisible: IsRightPaneVisible flipped but never notified.
                this.RaisePropertyChanged(nameof(IsRightPaneVisible));
                this.RaisePropertyChanged(nameof(CanSetWallpaper));
                this.RaisePropertyChanged(nameof(CanOpenInPaint));
                this.RaisePropertyChanged(nameof(CanEditImage));
            }
        }

        public bool IsViewMode => !IsEditMode;

        private MagickImage? _workingImage;
        
        // Edit controls properties
        private double _brightness = 100;
        public double Brightness
        {
            get => _brightness;
            set { this.RaiseAndSetIfChanged(ref _brightness, value); ApplyModulate(); }
        }

        private double _saturation = 100;
        public double Saturation
        {
            get => _saturation;
            set { this.RaiseAndSetIfChanged(ref _saturation, value); ApplyModulate(); }
        }

        private double _hue = 100;
        public double Hue
        {
            get => _hue;
            set { this.RaiseAndSetIfChanged(ref _hue, value); ApplyModulate(); }
        }

        private double _contrast = 0;
        public double Contrast
        {
            get => _contrast;
            set { this.RaiseAndSetIfChanged(ref _contrast, value); ApplyContrast(); }
        }

        private double _blur = 0;
        public double BlurValue
        {
            get => _blur;
            set { this.RaiseAndSetIfChanged(ref _blur, value); ApplyBlur(); }
        }

        private byte[]? _originalImageBytes;

        public void EnterEditMode()
        {
            if (string.IsNullOrEmpty(ImagePath) || Path.GetExtension(ImagePath).ToLowerInvariant() == ".svg") return;

            try
            {
                _originalImageBytes = File.ReadAllBytes(ImagePath);
                IsEditMode = true;
                _currentFilter = null;
                ResetEditParameters();
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Cannot edit this format: {ex.Message}";
            }
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
        }

        public async Task ExitEditMode(bool discard)
        {
            if (discard)
            {
                _workingImage?.Dispose();
                _workingImage = null;
                _originalImageBytes = null;
                _currentFilter = null;
                if (!string.IsNullOrEmpty(ImagePath))
                {
                    await LoadImageAsync(ImagePath);
                }
            }
            else
            {
                _originalImageBytes = null;
            }
            IsEditMode = false;
        }

        private string? _currentFilter;
        private int _previewToken = 0;

        private void ApplyModulate() => UpdatePreview();
        private void ApplyContrast() => UpdatePreview();
        private void ApplyBlur() => UpdatePreview();

        public void ApplyFilter(string filterName)
        {
            if (_currentFilter == filterName)
                _currentFilter = null;
            else
                _currentFilter = filterName;
                
            UpdatePreview();
        }

        private void UpdatePreview()
        {
            if (_originalImageBytes == null || !IsEditMode) return;
            
            int token = ++_previewToken;
            byte[] data = _originalImageBytes;
            double b = Brightness;
            double s = Saturation;
            double h = Hue;
            double c = Contrast;
            double blur = BlurValue;
            string? filter = _currentFilter;

            Task.Run(async () => {
                try
                {
                    // Debounce: Wait for 300ms of inactivity
                    await Task.Delay(300);
                    if (token != _previewToken || !IsEditMode) return;

                    using var tempImage = new MagickImage(data);
                    
                    // 1. Basic adjustments
                    tempImage.Modulate(new Percentage(b), new Percentage(s), new Percentage(h));
                    
                    if (c != 0)
                        tempImage.BrightnessContrast(new Percentage(0), new Percentage(c));
                    
                    if (blur > 0)
                        tempImage.Blur(0, blur);

                    // 2. Filter
                    if (!string.IsNullOrEmpty(filter))
                    {
                        switch (filter)
                        {
                            case "Grayscale": tempImage.Grayscale(); break;
                            case "Sepia": tempImage.SepiaTone(); break;
                            case "Negate": tempImage.Negate(); break;
                            case "Charcoal": tempImage.Charcoal(); break;
                            case "Edge": tempImage.Edge(1); break;
                        }
                    }

                    using var ms = new MemoryStream();
                    tempImage.Write(ms, MagickFormat.Png);
                    ms.Position = 0;
                    var bitmap = new Bitmap(ms);
                    
                    Dispatcher.UIThread.Post(() => {
                        if (IsEditMode && token == _previewToken)
                        {
                            ImageSource = bitmap;
                        }
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.UIThread.Post(() => {
                        if (IsEditMode && token == _previewToken)
                            ErrorMessage = $"Processing Error: {ex.Message}";
                    });
                }
            });
        }

        public async Task SaveEditedImageAsync(string? path = null)
        {
            string targetPath = path ?? ImagePath!;
            if (string.IsNullOrEmpty(targetPath) || _originalImageBytes == null) return;

            try
            {
                byte[] data = _originalImageBytes;
                await Task.Run(() => {
                    using var finalImage = new MagickImage(data);
                    finalImage.Modulate(new Percentage(Brightness), new Percentage(Saturation), new Percentage(Hue));
                    if (Contrast != 0) finalImage.BrightnessContrast(new Percentage(0), new Percentage(Contrast));
                    if (BlurValue > 0) finalImage.Blur(0, BlurValue);

                    if (!string.IsNullOrEmpty(_currentFilter))
                    {
                        switch (_currentFilter)
                        {
                            case "Grayscale": finalImage.Grayscale(); break;
                            case "Sepia": finalImage.SepiaTone(); break;
                            case "Negate": finalImage.Negate(); break;
                            case "Charcoal": finalImage.Charcoal(); break;
                            case "Edge": finalImage.Edge(1); break;
                        }
                    }

                    finalImage.Write(targetPath);
                });
                
                await LoadImageAsync(targetPath);
                await ExitEditMode(false);
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Failed to save: {ex.Message}";
            }
        }
        /// <summary>
        /// How a picture reaches the Recycle Bin. A field so tests can answer it
        /// themselves -- the same seam <see cref="ExplorerOrderProvider"/> uses -- since a
        /// test run has no business putting files in the user's real bin.
        /// </summary>
        internal static Func<string, bool> RecycleProvider =
            path => OperatingSystem.IsWindows() && RecycleBin.Send(path);

        /// <summary>
        /// Moves the open picture to the Recycle Bin and shows whatever comes next.
        ///
        /// The successor is chosen before the list is touched: the following picture
        /// normally, the preceding one when the last in the folder was deleted, and
        /// nothing at all when that was the only one -- which empties the window rather
        /// than leaving a stale image on screen with no file behind it.
        /// </summary>
        public async Task<bool> DeleteCurrentAsync()
        {
            if (!HasImage) return false;

            string path = ImagePath!;
            string? successor = null;
            if (_currentIndex >= 0 && _fileList.Count > 1)
            {
                successor = _currentIndex + 1 < _fileList.Count
                    ? _fileList[_currentIndex + 1]
                    : _fileList[_currentIndex - 1];
            }

            if (!RecycleProvider(path))
            {
                ErrorMessage = $"Could not move {Path.GetFileName(path)} to the Recycle Bin.";
                return false;
            }

            string name = Path.GetFileName(path);
            if (successor != null)
            {
                await LoadImageAsync(successor);
            }
            else
            {
                CloseImage();
            }

            StatusMessage = $"{name} moved to the Recycle Bin";
            return true;
        }

        /// <summary>Empties the window -- no file open, nothing drawn, nothing to navigate.</summary>
        private void CloseImage()
        {
            ImagePath = null;
            ImageSource = null;
            ImageWidth = 0;
            ImageHeight = 0;
            _storedOrientation = ImageOrientation.Identity;
            Orientation = ImageOrientation.Identity;
            MetadataItems = new ObservableCollection<MetadataItem>();
            _fileList = new List<string>();
            _currentIndex = -1;

            var animation = _animation;
            _animation = null;
            _frameIndex = 0;
            IsPlaying = false;
            animation?.Dispose();
            this.RaisePropertyChanged(nameof(IsAnimated));
            this.RaisePropertyChanged(nameof(FrameCount));
            this.RaisePropertyChanged(nameof(CanEditImage));
        }

        /// <summary>
        /// Renames the open picture and keeps following it.
        ///
        /// A bare name keeps the original extension: someone retyping "sunset" over
        /// "IMG_0421.jpg" means to rename the photo, not to strip what makes it openable.
        /// </summary>
        public async Task<bool> RenameCurrentAsync(string newName)
        {
            if (!HasImage) return false;

            string path = ImagePath!;
            string? dir = Path.GetDirectoryName(path);
            if (dir == null) return false;

            newName = (newName ?? string.Empty).Trim();
            if (newName.Length == 0)
            {
                ErrorMessage = "The name cannot be empty.";
                return false;
            }

            if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                ErrorMessage = "A file name cannot contain \\ / : * ? \" < > |";
                return false;
            }

            if (Path.GetExtension(newName).Length == 0)
                newName += Path.GetExtension(path);

            string target = Path.Combine(dir, newName);

            // Renaming a file to the name it already has is a no-op, not a collision
            // with itself. On Windows that comparison has to ignore case.
            if (string.Equals(target, path, StringComparison.OrdinalIgnoreCase))
                return true;

            if (File.Exists(target))
            {
                ErrorMessage = $"{newName} already exists in this folder.";
                return false;
            }

            try
            {
                File.Move(path, target);
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Could not rename: {ex.Message}";
                return false;
            }

            await LoadImageAsync(target);
            StatusMessage = $"Renamed to {newName}";
            return true;
        }

        /// <summary>
        /// Puts the picture on the clipboard as pixels, as a file and as its path at
        /// once, so it pastes usefully into an editor, a folder and a text box alike.
        /// </summary>
        public bool CopyToClipboard()
        {
            if (!HasImage) return false;
            if (!OperatingSystem.IsWindows())
            {
                ErrorMessage = "Copying to the clipboard is only available on Windows.";
                return false;
            }

            if (!ClipboardImage.Copy(ImagePath!))
            {
                ErrorMessage = "Could not copy to the clipboard.";
                return false;
            }

            StatusMessage = "Copied";
            return true;
        }

        /// <summary>
        /// Opens whatever image the clipboard is holding. A copied file is opened where
        /// it lies; loose pixels are written to a scratch file first, so everything
        /// downstream keeps working in terms of a real path.
        /// </summary>
        public async Task<bool> PasteFromClipboardAsync()
        {
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

            await LoadImageAsync(path);
            return ErrorMessage == null;
        }

        public async Task NextImage()
        {
            if (_fileList.Count <= 1 || _currentIndex == -1) return;
            _currentIndex = (_currentIndex + 1) % _fileList.Count;
            if (_fileList[_currentIndex] != null)
                await LoadImageAsync(_fileList[_currentIndex]);
        }

        public async Task PrevImage()
        {
            if (_fileList.Count <= 1 || _currentIndex == -1) return;
            _currentIndex = (_currentIndex - 1 + _fileList.Count) % _fileList.Count;
            await LoadImageAsync(_fileList[_currentIndex]);
        }
    }
}
