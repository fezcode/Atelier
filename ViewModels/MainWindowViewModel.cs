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
            }
        }

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

        public async Task LoadImageAsync(string path)
        {
            try
            {
                ErrorMessage = null;
                ImagePath = path;
                ZoomLevel = 1.0;
                MetadataItems.Clear();

                string ext = Path.GetExtension(path).ToLower();

                if (ext == ".svg")
                {
                    var svgSource = SvgSource.Load(path, null);
                    ImageSource = new SvgImage { Source = svgSource };
                    if (svgSource?.Picture != null)
                    {
                        ImageWidth = svgSource.Picture.CullRect.Width;
                        ImageHeight = svgSource.Picture.CullRect.Height;
                    }
                    ExtractBasicMetadata(path, "SVG Vector");
                }
                else if (ext == ".heic" || ext == ".heif" || ext == ".avif")
                {
                    using var image = new MagickImage(path);
                    ImageWidth = image.Width;
                    ImageHeight = image.Height;
                    string formatLabel = ext == ".avif" ? "AVIF" : "HEIC";
                    ExtractBasicMetadata(path, $"{formatLabel} {image.Width}x{image.Height}");
                    using var ms = new MemoryStream();
                    image.Write(ms, MagickFormat.Png);
                    ms.Position = 0;
                    ImageSource = new Bitmap(ms);
                    ExtractExifMetadata(path);
                }
                else
                {
                    var bitmap = new Bitmap(path);
                    ImageSource = bitmap;
                    ImageWidth = bitmap.Size.Width;
                    ImageHeight = bitmap.Size.Height;
                    ExtractBasicMetadata(path, $"{(int)bitmap.Size.Width}x{(int)bitmap.Size.Height} {ext.ToUpper().TrimStart('.')}");
                    ExtractExifMetadata(path);
                }

                UpdateFileList(path);
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Failed to load image: {ex.Message}";
                ImageSource = null;
                ImageWidth = 0;
                ImageHeight = 0;
            }
        }

        public async Task SaveImageAsync(string destinationPath)
        {
            if (string.IsNullOrEmpty(ImagePath)) return;

            try
            {
                string ext = Path.GetExtension(destinationPath).ToLower();
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
                        if (Path.GetExtension(ImagePath).ToLower() == ".svg")
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

        private void ExtractBasicMetadata(string path, string typeInfo)
        {
            var info = new FileInfo(path);
            MetadataItems.Add(new MetadataItem { Label = "Name", Value = info.Name });
            MetadataItems.Add(new MetadataItem { Label = "Format", Value = typeInfo });
            MetadataItems.Add(new MetadataItem { Label = "Size", Value = FormatFileSize(info.Length) });
            MetadataItems.Add(new MetadataItem { Label = "Location", Value = info.DirectoryName ?? "" });
            MetadataItems.Add(new MetadataItem { Label = "Created", Value = info.CreationTime.ToString("g") });
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

        private void ExtractExifMetadata(string path)
        {
            try
            {
                var directories = ImageMetadataReader.ReadMetadata(path);
                foreach (var directory in directories)
                {
                    foreach (var tag in directory.Tags)
                    {
                        if (directory.Name.Contains("Exif") || directory.Name == "JPEG" || directory.Name == "PNG")
                        {
                             MetadataItems.Add(new MetadataItem { Label = tag.Name, Value = tag.Description ?? "" });
                        }
                    }
                }
            }
            catch { }
        }

        private void UpdateFileList(string currentPath)
        {
            try
            {
                string? dir = Path.GetDirectoryName(currentPath);
                if (dir == null) return;

                var extensions = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".svg", ".heic", ".heif" };
                _fileList = System.IO.Directory.GetFiles(dir)
                    .Where(f => extensions.Contains(Path.GetExtension(f).ToLower()))
                    .OrderBy(f => f)
                    .ToList();

                _currentIndex = _fileList.IndexOf(currentPath);
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
            if (string.IsNullOrEmpty(ImagePath) || Path.GetExtension(ImagePath).ToLower() == ".svg") return;

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
