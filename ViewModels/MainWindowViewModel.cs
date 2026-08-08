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
            set => this.RaiseAndSetIfChanged(ref _imagePath, value);
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

        public bool IsRightPaneVisible => ShowControls && (IsEditMode || ShowMetadata);

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
        public double ImageWidth
        {
            get => _imageWidth;
            set => this.RaiseAndSetIfChanged(ref _imageWidth, value);
        }

        private double _imageHeight;
        public double ImageHeight
        {
            get => _imageHeight;
            set => this.RaiseAndSetIfChanged(ref _imageHeight, value);
        }

        private static readonly string[] NavigableExtensions =
            { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".svg", ".heic", ".heif" };

        /// <summary>Everything produced off the UI thread, ready to hand to the view.</summary>
        private sealed class LoadedImage
        {
            public Bitmap? Bitmap;
            public SvgSource? Svg;
            public double Width;
            public double Height;
            public List<MetadataItem> Metadata = new();
            public List<string> Siblings = new();
        }

        public async Task LoadImageAsync(string path)
        {
            try
            {
                ErrorMessage = null;
                ImagePath = path;
                ZoomLevel = 1.0;
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
            }

            result.Siblings = ScanSiblings(Path.GetDirectoryName(path));
            return result;
        }

        private static List<string> ScanSiblings(string? dir)
        {
            if (dir == null) return new List<string>();
            try
            {
                return System.IO.Directory.GetFiles(dir)
                    .Where(f => NavigableExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

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
