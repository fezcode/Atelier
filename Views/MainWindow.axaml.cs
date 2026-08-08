using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Atelier.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Svg.Skia;
using Avalonia.Threading;

namespace Atelier.Views
{
    public partial class MainWindow : Window
    {
        private bool _isPanning;
        private Point _lastMousePos;
        private GridLength _lastRightColumnWidth = new GridLength(300);

        private const double ZoomStep = 1.1;
        private const double ButtonZoomStep = 1.2;
        private const double MinZoom = 0.01;
        private const double MaxZoom = 64.0;
        private const double ShiftPanStep = 50.0;

        /// <summary>Read off the assembly so it tracks &lt;Version&gt; in Atelier.csproj.</summary>
        private static readonly string AppVersion =
            typeof(MainWindow).Assembly
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion.Split('+')[0]
            ?? typeof(MainWindow).Assembly.GetName().Version?.ToString(3)
            ?? "dev";

        public MainWindow()
        {
            InitializeComponent();
            AddHandler(DragDrop.DropEvent, Drop);

            var scroll = this.FindControl<ScrollViewer>("MainScroll");
            if (scroll != null)
            {
                scroll.PointerPressed += OnScrollPointerPressed;
                scroll.PointerMoved += OnScrollPointerMoved;
                scroll.PointerReleased += OnScrollPointerReleased;

                // The wheel must be claimed during the TUNNEL phase. PointerWheelChanged
                // bubbles, so ScrollContentPresenter's class handler would otherwise run
                // first: it scrolls whenever Extent > Viewport and marks the event handled,
                // which stops it ever reaching our zoom code. Tunnelling on the ScrollViewer
                // beats the presenter while leaving the metadata/edit panes free to scroll.
                scroll.AddHandler(InputElement.PointerWheelChangedEvent, OnViewerWheel,
                    RoutingStrategies.Tunnel, handledEventsToo: true);
            }

            var dragArea = this.FindControl<Panel>("DragArea");
            if (dragArea != null)
            {
                dragArea.PointerPressed += (s, e) =>
                {
                    if (e.ClickCount == 2 && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                    {
                        WindowState = WindowState == WindowState.Maximized 
                            ? WindowState.Normal 
                            : WindowState.Maximized;
                        e.Handled = true;
                    }
                    else if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                    {
                        BeginMoveDrag(e);
                    }
                };
            }

            DataContextChanged += (s, e) =>
            {
                if (DataContext is MainWindowViewModel vm)
                {
                    vm.PropertyChanged += (sender, args) =>
                    {
                        if (args.PropertyName == nameof(MainWindowViewModel.IsRightPaneVisible))
                        {
                            UpdateRightPaneGrid();
                        }
                        else if (args.PropertyName == nameof(MainWindowViewModel.IsEditMode))
                        {
                            UpdateRightPaneHeader();
                        }
                    };
                }
            };
        }

        /// <summary>Loads an image and fits it to the viewer once layout has caught up.</summary>
        public async System.Threading.Tasks.Task LoadAndFitAsync(string path)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                await vm.LoadImageAsync(path);
                Dispatcher.UIThread.Post(FitToView, DispatcherPriority.Loaded);
            }
        }

        private async void Drop(object? sender, DragEventArgs e)
        {
            var files = e.Data.GetFiles();
            if (files != null && files.FirstOrDefault() is { } file)
            {
                await LoadAndFitAsync(file.Path.LocalPath);
            }
        }

        public async void OpenFileName_Click(object? sender, RoutedEventArgs e)
        {
            var topLevel = GetTopLevel(this);
            if (topLevel == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open Image",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Images")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp", "*.svg", "*.heic", "*.heif", "*.ico" }
                    }
                }
            });

            if (files.Count >= 1)
            {
                await LoadAndFitAsync(files[0].Path.LocalPath);
            }
        }

        public async void SaveAs_Click(object? sender, RoutedEventArgs e)
        {
            var topLevel = GetTopLevel(this);
            if (topLevel == null) return;

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Image As",
                DefaultExtension = "png",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("PNG Image") { Patterns = new[] { "*.png" } },
                    new FilePickerFileType("JPEG Image") { Patterns = new[] { "*.jpg", "*.jpeg" } },
                    new FilePickerFileType("WebP Image") { Patterns = new[] { "*.webp" } },
                    new FilePickerFileType("BMP Image") { Patterns = new[] { "*.bmp" } },
                    new FilePickerFileType("Icon File") { Patterns = new[] { "*.ico" } },
                    new FilePickerFileType("HEIC Image") { Patterns = new[] { "*.heic", "*.heif" } },
                    new FilePickerFileType("AVIF Image") { Patterns = new[] { "*.avif" } }
                }
            });

            if (file != null)
            {
                if (DataContext is MainWindowViewModel vm)
                {
                    await vm.SaveImageAsync(file.Path.LocalPath);
                }
            }
        }

        /// <summary>
        /// Reveals the current image in File Explorer with the file selected.
        /// </summary>
        public void OpenFileLocation_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm) return;
            if (string.IsNullOrEmpty(vm.ImagePath)) return;

            // GetFullPath normalises separators: explorer treats a forward slash as
            // part of the name and silently falls back to Documents.
            var path = System.IO.Path.GetFullPath(vm.ImagePath);

            try
            {
                if (System.IO.File.Exists(path))
                {
                    // The comma binds /select to the argument and there is no space
                    // after it -- "explorer /select, <path>" opens Documents instead.
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{path}\"",
                        UseShellExecute = true
                    });
                    return;
                }

                // The file moved or was deleted while open -- still useful to land in
                // the folder it came from, if that survives.
                var dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = dir,
                        UseShellExecute = true
                    });
                }
                else
                {
                    vm.ErrorMessage = $"Folder no longer exists: {dir}";
                }
            }
            catch (Exception ex)
            {
                vm.ErrorMessage = $"Could not open file location: {ex.Message}";
            }
        }

        /// <summary>
        /// The metadata pane's close button. Mirrors View &gt; Image Metadata, which
        /// stays checkable so the pane can be brought back.
        /// </summary>
        public void CloseMetadata_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.ShowMetadata = false;
            }
        }

        private void UpdateRightPaneHeader()
        {
            if (DataContext is MainWindowViewModel vm)
            {
                var header = this.FindControl<TextBlock>("RightPaneHeader");
                if (header != null)
                {
                    header.Text = vm.IsEditMode ? "EDIT IMAGE" : "IMAGE METADATA";
                }
            }
        }

        private void UpdateRightPaneGrid()
        {
             if (DataContext is MainWindowViewModel vm)
             {
                var grid = this.FindControl<Grid>("MainGrid");
                if (grid != null && grid.ColumnDefinitions.Count >= 3)
                {
                    var splitCol = grid.ColumnDefinitions[1];
                    var rightCol = grid.ColumnDefinitions[2];

                    if (vm.IsRightPaneVisible)
                    {
                        rightCol.Width = _lastRightColumnWidth;
                        rightCol.MinWidth = 150;
                        rightCol.MaxWidth = 600;
                        splitCol.Width = GridLength.Auto;
                    }
                    else
                    {
                        if (rightCol.Width.IsAbsolute)
                            _lastRightColumnWidth = rightCol.Width;
                            
                        rightCol.Width = new GridLength(0);
                        rightCol.MinWidth = 0;
                        rightCol.MaxWidth = 0;
                        splitCol.Width = new GridLength(0);
                    }
                }

                Dispatcher.UIThread.Post(() => {
                    FitToView();
                }, DispatcherPriority.Render);
             }
        }

        public async void FileAssociations_Click(object? sender, RoutedEventArgs e)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                await ShowMessageDialog("Not Supported", "File associations are only supported on Windows.");
                return;
            }

            var registered = FileAssociationHelper.GetRegisteredExtensions();
            var checkBoxes = new List<CheckBox>();

            foreach (var (ext, desc) in FileAssociationHelper.SupportedTypes)
            {
                var cb = new CheckBox
                {
                    Content = $"{ext}  —  {desc}",
                    IsChecked = registered.Contains(ext),
                    Foreground = Brushes.White,
                    FontSize = 14,
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(0, 4)
                };
                checkBoxes.Add(cb);
            }

            var selectAllCb = new CheckBox
            {
                Content = "Select All",
                IsChecked = checkBoxes.All(cb => cb.IsChecked == true),
                Foreground = Brushes.White,
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 0, 0, 10)
            };
            selectAllCb.IsCheckedChanged += (_, _) =>
            {
                if (selectAllCb.IsChecked is bool val)
                    foreach (var cb in checkBoxes) cb.IsChecked = val;
            };

            var list = new StackPanel { Spacing = 2 };
            list.Children.Add(selectAllCb);
            list.Children.Add(new Separator { Background = new SolidColorBrush(Color.Parse("#333333")), Margin = new Thickness(0, 2, 0, 8) });
            foreach (var cb in checkBoxes)
                list.Children.Add(cb);

            var applyBtn = new Button
            {
                Classes = { "Modern" },
                Content = "Save Associations",
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Padding = new Thickness(0, 12, 0, 12),
                Background = new SolidColorBrush(Color.Parse("#2A2A2A")),
                Margin = new Thickness(0, 25, 0, 0),
                FontWeight = FontWeight.ExtraBold,
                FontSize = 14
            };

            var dialog = new Window
            {
                Title = "File Associations",
                Width = 400,
                Height = 560,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                Background = new SolidColorBrush(Color.Parse("#0A0A0A")),
                TransparencyLevelHint = new[] { WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur },
                ExtendClientAreaToDecorationsHint = true,
                ExtendClientAreaTitleBarHeightHint = -1,
                Content = new Border
                {
                    BorderBrush = new SolidColorBrush(Color.Parse("#222222")),
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(0),
                    Child = new StackPanel
                    {
                        Margin = new Thickness(35, 50, 35, 35),
                        Children =
                        {
                            new TextBlock { Text = "FILE ASSOCIATIONS", Foreground = new SolidColorBrush(Color.Parse("#888888")), FontSize = 11, FontWeight = FontWeight.ExtraBold, LetterSpacing = 1.5, Margin = new Thickness(0, 0, 0, 25) },
                            new TextBlock { Text = "Choose which file types to open with Atelier:", Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 20) },
                            new ScrollViewer { Content = list, Height = 320 },
                            applyBtn
                        }
                    }
                }
            };

            applyBtn.Click += (_, _) =>
            {
                try
                {
                    var selected = new List<string>();
                    for (int i = 0; i < checkBoxes.Count; i++)
                    {
                        if (checkBoxes[i].IsChecked == true)
                            selected.Add(FileAssociationHelper.SupportedTypes[i].Extension);
                    }
                    FileAssociationHelper.RegisterFileAssociations(selected);
                    dialog.Close();
                }
                catch (Exception ex)
                {
                    dialog.Title = $"Error: {ex.Message}";
                }
            };

            await dialog.ShowDialog(this);
        }

        private async System.Threading.Tasks.Task ShowMessageDialog(string title, string message)
        {
            var dialog = new Window
            {
                Title = title,
                Width = 400,
                Height = 220,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                Background = new SolidColorBrush(Color.Parse("#0A0A0A")),
                Content = new StackPanel
                {
                    Margin = new Thickness(30),
                    Spacing = 25,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = message, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, FontSize = 14, TextAlignment = TextAlignment.Center },
                        new Button { Classes = { "Modern" }, Content = "Got it", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, Padding = new Thickness(40, 10), Background = new SolidColorBrush(Color.Parse("#2A2A2A")) }
                    }
                }
            };
            if (dialog.Content is StackPanel sp && sp.Children[1] is Button okBtn)
                okBtn.Click += (_, _) => dialog.Close();
            await dialog.ShowDialog(this);
        }

        public void Exit_Click(object? sender, RoutedEventArgs e)
        {
            Close();
        }

        public void FullScreen_Click(object? sender, RoutedEventArgs e)
        {
            ToggleFullScreen();
        }

        private void FitToView()
        {
            if (DataContext is MainWindowViewModel vm && vm.ImageWidth > 0 && vm.ImageHeight > 0)
            {
                var scroll = this.FindControl<ScrollViewer>("MainScroll");
                if (scroll == null) return;

                // Ensure we have layout measurements
                scroll.UpdateLayout();

                double viewW = scroll.Viewport.Width > 0 ? scroll.Viewport.Width : scroll.Bounds.Width;
                double viewH = scroll.Viewport.Height > 0 ? scroll.Viewport.Height : scroll.Bounds.Height;

                // Subtract small padding to avoid scrollbars
                viewW -= 20;
                viewH -= 20;

                if (viewW > 0 && viewH > 0)
                {
                    double ratioW = viewW / vm.ImageWidth;
                    double ratioH = viewH / vm.ImageHeight;
                    vm.ZoomLevel = Math.Min(ratioW, ratioH);

                    // Reset offset to center the newly fitted image
                    scroll.Offset = new Vector(0, 0);
                }
            }
        }

        public void FitToScreen_Click(object? sender, RoutedEventArgs e)
        {
            FitToView();
        }

        public void ResetZoom_Click(object? sender, RoutedEventArgs e)
        {
            FitToView();
        }

        public void ZoomIn_Click(object? sender, RoutedEventArgs e) => ZoomAroundViewportCentre(ButtonZoomStep);

        public void ZoomOut_Click(object? sender, RoutedEventArgs e) => ZoomAroundViewportCentre(1 / ButtonZoomStep);

        private void ZoomAroundViewportCentre(double factor)
        {
            var scroll = this.FindControl<ScrollViewer>("MainScroll");
            if (scroll?.Presenter == null) return;

            var centre = new Point(scroll.Viewport.Width / 2, scroll.Viewport.Height / 2);
            ZoomAnchored(scroll, factor, centre);
        }

        /// <summary>
        /// Scales by <paramref name="factor"/> while keeping the image pixel currently sitting
        /// under <paramref name="anchorInViewport"/> (a point in ScrollContentPresenter
        /// coordinates) pinned to that same spot.
        /// </summary>
        /// <remarks>
        /// The anchor is captured in the unscaled coordinate space of the content inside the
        /// LayoutTransformControl, then re-measured after layout and the difference applied to
        /// Offset. Deriving the correction from the actual post-layout transform — rather than
        /// multiplying the old offset by the zoom ratio — is what makes this correct when the
        /// content is smaller than the viewport: ImagePanel is centre-aligned, so the content
        /// origin is not the extent origin and a pure ratio calculation drifts.
        /// </remarks>
        private void ZoomAnchored(ScrollViewer scroll, double factor, Point anchorInViewport)
        {
            if (DataContext is not MainWindowViewModel vm) return;

            double target = Math.Clamp(vm.ZoomLevel * factor, MinZoom, MaxZoom);
            if (Math.Abs(target - vm.ZoomLevel) < double.Epsilon) return;

            var presenter = scroll.Presenter;
            var content = this.FindControl<Panel>("ZoomContent");

            if (presenter == null || content == null)
            {
                vm.ZoomLevel = target;
                return;
            }

            // The image pixel under the anchor, in unscaled content coordinates. Invariant.
            var pixel = presenter.TranslatePoint(anchorInViewport, content);

            vm.ZoomLevel = target;
            scroll.UpdateLayout();

            if (pixel is { } p && content.TranslatePoint(p, presenter) is { } landed)
            {
                // Offset is clamped by the ScrollViewer, which is exactly what we want once
                // the content becomes smaller than the viewport and re-centres itself.
                scroll.Offset += landed - anchorInViewport;
            }
        }

        /// <summary>
        /// Tunnel-phase wheel handler for the image viewer. Ctrl+wheel zooms about the cursor,
        /// Shift+wheel pans horizontally, and a bare wheel is deliberately inert.
        /// </summary>
        private void OnViewerWheel(object? sender, PointerWheelEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm) return;

            var scroll = this.FindControl<ScrollViewer>("MainScroll");
            if (scroll == null) return;

            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                if (scroll.Presenter != null)
                    ZoomAnchored(scroll, e.Delta.Y > 0 ? ZoomStep : 1 / ZoomStep,
                        e.GetPosition(scroll.Presenter));
                else
                    vm.ZoomLevel = Math.Clamp(vm.ZoomLevel * (e.Delta.Y > 0 ? ZoomStep : 1 / ZoomStep),
                        MinZoom, MaxZoom);
            }
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                scroll.Offset = scroll.Offset.WithX(scroll.Offset.X - e.Delta.Y * ShiftPanStep);
            }

            // Claimed either way: a bare wheel does nothing over the image.
            e.Handled = true;
        }

        public void About_Click(object? sender, RoutedEventArgs e)
        {
            var aboutWindow = new Window
            {
                Title = "About Atelier",
                Width = 450,
                Height = 480,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                Background = new SolidColorBrush(Color.Parse("#0A0A0A")),
                TransparencyLevelHint = new[] { WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur },
                ExtendClientAreaToDecorationsHint = true,
                ExtendClientAreaTitleBarHeightHint = -1,
                Content = new Border
                {
                    BorderBrush = new SolidColorBrush(Color.Parse("#222222")),
                    BorderThickness = new Thickness(1),
                    Child = new StackPanel
                    {
                        Margin = new Thickness(40, 60, 40, 40),
                        Spacing = 20,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                        Children =
                        {
                            new Image { Source = new Bitmap(AssetLoader.Open(new Uri("avares://Atelier/Assets/atelier-icon.png"))), Width = 96, Height = 96, Margin = new Thickness(0,0,0,10) },
                            new TextBlock { Text = "ATELIER", FontSize = 48, FontWeight = FontWeight.ExtraBold, Foreground = Brushes.White, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, LetterSpacing = -1 },
                            new TextBlock { Text = $"v{AppVersion}", FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#AAAAAA")), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, Margin = new Thickness(0,0,0,15) },
                            new TextBlock { Text = "A modern, high-performance image viewer built with Avalonia UI & Magick.NET.", FontWeight = FontWeight.Medium, Foreground = new SolidColorBrush(Color.Parse("#DDDDDD")), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, FontSize = 16 },
                            new Rectangle { Height = 1, Fill = new SolidColorBrush(Color.Parse("#222222")), Margin = new Thickness(20,10) },
                            new TextBlock { Text = "developed by fezcode", FontSize = 14, FontWeight = FontWeight.Bold, Foreground = Brushes.DodgerBlue, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center }
                        }
                    }
                }
            };
            aboutWindow.ShowDialog(this);
        }

        public void EditImage_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.EnterEditMode();
                // Ensure controls are visible when editing
                if (!vm.ShowControls) vm.ShowControls = true;
            }
        }

        public async void DiscardEdit_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                await vm.ExitEditMode(true);
                FitToView();
            }
        }

        public void Filter_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string filterName && DataContext is MainWindowViewModel vm)
            {
                vm.ApplyFilter(filterName);
            }
        }

        public async void SaveEdit_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                await vm.SaveEditedImageAsync();
            }
        }

        public async void SaveAsEdit_Click(object? sender, RoutedEventArgs e)
        {
            var topLevel = GetTopLevel(this);
            if (topLevel == null) return;

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Edited Image As",
                DefaultExtension = "png",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("PNG Image") { Patterns = new[] { "*.png" } },
                    new FilePickerFileType("JPEG Image") { Patterns = new[] { "*.jpg", "*.jpeg" } },
                    new FilePickerFileType("WebP Image") { Patterns = new[] { "*.webp" } },
                    new FilePickerFileType("AVIF Image") { Patterns = new[] { "*.avif" } }
                }
            });

            if (file != null && DataContext is MainWindowViewModel vm)
            {
                await vm.SaveEditedImageAsync(file.Path.LocalPath);
            }
        }

        public void ToggleControls_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.ShowControls = !vm.ShowControls;
                // UpdateRightPaneGrid() will be called via PropertyChanged subscription in constructor
            }
        }

        public async void Next_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                await vm.NextImage();
                Dispatcher.UIThread.Post(FitToView, Avalonia.Threading.DispatcherPriority.Loaded);
            }
        }

        public async void Prev_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                await vm.PrevImage();
                Dispatcher.UIThread.Post(FitToView, Avalonia.Threading.DispatcherPriority.Loaded);
            }
        }

        private void ToggleFullScreen()
        {
            WindowState = WindowState == WindowState.FullScreen 
                ? WindowState.Normal 
                : WindowState.FullScreen;
            
            // Refit after fullscreen toggle
            Dispatcher.UIThread.Post(FitToView, Avalonia.Threading.DispatcherPriority.Loaded);
        }

        protected override async void OnKeyDown(KeyEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                if (e.Key == Key.Right)
                {
                    await vm.NextImage();
                    Dispatcher.UIThread.Post(FitToView, Avalonia.Threading.DispatcherPriority.Loaded);
                    e.Handled = true;
                }
                else if (e.Key == Key.Left)
                {
                    await vm.PrevImage();
                    Dispatcher.UIThread.Post(FitToView, Avalonia.Threading.DispatcherPriority.Loaded);
                    e.Handled = true;
                }
                else if (e.Key == Key.F)
                {
                    ToggleFullScreen();
                }
                else if (e.Key == Key.O && e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    OpenFileName_Click(null, new RoutedEventArgs());
                }
                else if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    SaveAs_Click(null, new RoutedEventArgs());
                }
            }
            base.OnKeyDown(e);
        }

        private void OnScrollPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _isPanning = true;
                _lastMousePos = e.GetPosition(this);
                Cursor = new Cursor(StandardCursorType.Hand);
                e.Handled = true;
            }
        }

        private void OnScrollPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_isPanning)
            {
                var currentPos = e.GetPosition(this);
                var delta = _lastMousePos - currentPos;
                _lastMousePos = currentPos;

                var scroll = this.FindControl<ScrollViewer>("MainScroll");
                if (scroll != null)
                {
                    scroll.Offset = new Vector(scroll.Offset.X + delta.X, scroll.Offset.Y + delta.Y);
                }
                e.Handled = true;
            }
        }

        private void OnScrollPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_isPanning)
            {
                _isPanning = false;
                Cursor = Cursor.Default;
                e.Handled = true;
            }
        }

    }
}
