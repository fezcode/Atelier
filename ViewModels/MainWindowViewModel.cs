using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Svg.Skia;
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
                else if (ext == ".heic" || ext == ".heif")
                {
                    using var image = new MagickImage(path);
                    ImageWidth = image.Width;
                    ImageHeight = image.Height;
                    ExtractBasicMetadata(path, $"HEIC {image.Width}x{image.Height}");
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

        public async Task NextImage()
        {
            if (_fileList.Count <= 1 || _currentIndex == -1) return;
            _currentIndex = (_currentIndex + 1) % _fileList.Count;
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
