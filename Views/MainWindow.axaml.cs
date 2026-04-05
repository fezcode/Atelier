using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Atelier.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
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

                // Auto-fit on initial load/resize if needed
                scroll.SizeChanged += (s, e) => {
                    // Could optionally auto-refit here
                };
            }
        }

        private async void Drop(object? sender, DragEventArgs e)
        {
            var files = e.Data.GetFiles();
            if (files != null && files.FirstOrDefault() is { } file)
            {
                if (DataContext is MainWindowViewModel vm)
                {
                    await vm.LoadImageAsync(file.Path.LocalPath);
                    // Give layout a moment to update then fit
                    Dispatcher.UIThread.Post(FitToView, Avalonia.Threading.DispatcherPriority.Loaded);
                }
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
                if (DataContext is MainWindowViewModel vm)
                {
                    await vm.LoadImageAsync(files[0].Path.LocalPath);
                    Dispatcher.UIThread.Post(FitToView, Avalonia.Threading.DispatcherPriority.Loaded);
                }
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
                    FontSize = 13,
                    Margin = new Thickness(0, 3)
                };
                checkBoxes.Add(cb);
            }

            var selectAllCb = new CheckBox
            {
                Content = "Select All",
                IsChecked = checkBoxes.All(cb => cb.IsChecked == true),
                Foreground = new SolidColorBrush(Color.Parse("#AAAAAA")),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 8)
            };
            selectAllCb.IsCheckedChanged += (_, _) =>
            {
                if (selectAllCb.IsChecked is bool val)
                    foreach (var cb in checkBoxes) cb.IsChecked = val;
            };

            var list = new StackPanel { Spacing = 2 };
            list.Children.Add(selectAllCb);
            list.Children.Add(new Separator { Background = new SolidColorBrush(Color.Parse("#333333")), Margin = new Thickness(0, 2, 0, 6) });
            foreach (var cb in checkBoxes)
                list.Children.Add(cb);

            var applyBtn = new Button
            {
                Content = "Save",
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Padding = new Thickness(0, 8),
                Background = new SolidColorBrush(Color.Parse("#2A2A2A")),
                Margin = new Thickness(0, 15, 0, 0)
            };

            var dialog = new Window
            {
                Title = "File Associations",
                Width = 340,
                Height = 480,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                Background = new SolidColorBrush(Color.Parse("#1A1A1A")),
                Content = new StackPanel
                {
                    Margin = new Thickness(25, 20),
                    Children =
                    {
                        new TextBlock { Text = "Choose which file types to open with Atelier:", Foreground = new SolidColorBrush(Color.Parse("#888888")), FontSize = 12, Margin = new Thickness(0, 0, 0, 12) },
                        list,
                        applyBtn
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
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                Background = new SolidColorBrush(Color.Parse("#1A1A1A")),
                Content = new StackPanel
                {
                    Margin = new Thickness(25),
                    Spacing = 20,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = message, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, FontSize = 13 },
                        new Button { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, Padding = new Thickness(30, 8), Background = new SolidColorBrush(Color.Parse("#2A2A2A")) }
                    }
                }
            };
            // Wire OK button to close
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

        public void ZoomIn_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                var scroll = this.FindControl<ScrollViewer>("MainScroll");
                if (scroll?.Presenter != null)
                {
                    double oldZoom = vm.ZoomLevel;
                    vm.ZoomLevel *= 1.2;
                    scroll.UpdateLayout();
                    
                    // Zoom centered on viewport center
                    var viewportCenter = new Point(scroll.Viewport.Width / 2, scroll.Viewport.Height / 2);
                    var contentCenter = (scroll.Offset + viewportCenter) * (vm.ZoomLevel / oldZoom);
                    scroll.Offset = contentCenter - viewportCenter;
                }
                else
                {
                    vm.ZoomLevel *= 1.2;
                }
            }
        }

        public void ZoomOut_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                var scroll = this.FindControl<ScrollViewer>("MainScroll");
                if (scroll?.Presenter != null)
                {
                    double oldZoom = vm.ZoomLevel;
                    vm.ZoomLevel /= 1.2;
                    if (vm.ZoomLevel < 0.01) vm.ZoomLevel = 0.01;
                    scroll.UpdateLayout();

                    var viewportCenter = new Point(scroll.Viewport.Width / 2, scroll.Viewport.Height / 2);
                    var contentCenter = (scroll.Offset + viewportCenter) * (vm.ZoomLevel / oldZoom);
                    scroll.Offset = contentCenter - viewportCenter;
                }
                else
                {
                    vm.ZoomLevel /= 1.2;
                    if (vm.ZoomLevel < 0.01) vm.ZoomLevel = 0.01;
                }
            }
        }

        public void About_Click(object? sender, RoutedEventArgs e)
        {
            var aboutWindow = new Window
            {
                Title = "About Atelier",
                Width = 350,
                Height = 300,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                Background = new SolidColorBrush(Color.Parse("#1A1A1A")),
                Content = new StackPanel
                {
                    Margin = new Thickness(30),
                    Spacing = 10,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = "Atelier", FontSize = 32, FontWeight = FontWeight.Bold, Foreground = Brushes.White, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center },
                        new TextBlock { Text = "v0.2.45", FontSize = 12, Foreground = Brushes.LightGray, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center },
                        new TextBlock { Text = "by fezcode", FontSize = 14, FontWeight = FontWeight.Medium, Foreground = Brushes.DodgerBlue, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, Margin = new Thickness(0,0,0,15) },
                        new TextBlock { Text = "A modern, high-performance image viewer.", Foreground = Brushes.Gray, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = "Built with Avalonia UI & Magick.NET", Foreground = Brushes.Gray, FontSize = 10, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, Margin = new Thickness(0,20,0,0) }
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
                // Ensure side pane is visible when editing
                if (!vm.ShowControls) ToggleControls_Click(null, new RoutedEventArgs());
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

                var grid = this.FindControl<Grid>("MainGrid");
                if (grid != null && grid.ColumnDefinitions.Count >= 3)
                {
                    var splitCol = grid.ColumnDefinitions[1];
                    var rightCol = grid.ColumnDefinitions[2];

                    if (vm.ShowControls)
                    {
                        rightCol.Width = _lastRightColumnWidth;
                        rightCol.MinWidth = 150;
                        rightCol.MaxWidth = 600;
                        splitCol.Width = GridLength.Auto;
                    }
                    else
                    {
                        _lastRightColumnWidth = rightCol.Width;
                        rightCol.Width = new GridLength(0);
                        rightCol.MinWidth = 0;
                        rightCol.MaxWidth = 0;
                        splitCol.Width = new GridLength(0);
                    }
                }

                // Refit after layout changes (right pane appearing/disappearing)
                // Use a slightly lower priority or post to ensure layout is done
                Dispatcher.UIThread.Post(() => {
                    FitToView();
                }, DispatcherPriority.Render);
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

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                // Ctrl + Scroll = Zoom (Highest priority)
                if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    double delta = e.Delta.Y > 0 ? 1.1 : 0.9;
                    
                    var scroll = this.FindControl<ScrollViewer>("MainScroll");
                    if (scroll?.Presenter?.Child != null)
                    {
                        var mousePos = e.GetPosition(scroll.Presenter.Child);
                        double oldZoom = vm.ZoomLevel;
                        vm.ZoomLevel *= delta;
                        if (vm.ZoomLevel < 0.01) vm.ZoomLevel = 0.01;

                        scroll.UpdateLayout();

                        var newMousePos = mousePos * (vm.ZoomLevel / oldZoom);
                        var scrollMousePos = e.GetPosition(scroll.Presenter);
                        scroll.Offset = new Vector(newMousePos.X - scrollMousePos.X, newMousePos.Y - scrollMousePos.Y);
                    }
                    else
                    {
                        vm.ZoomLevel *= delta;
                    }
                    
                    e.Handled = true;
                    return;
                }
                // Shift + Scroll = Horizontal Move
                else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    var scroll = this.FindControl<ScrollViewer>("MainScroll");
                    if (scroll != null)
                    {
                        scroll.Offset = scroll.Offset.WithX(scroll.Offset.X - (e.Delta.Y * 50));
                        e.Handled = true;
                    }
                    return;
                }
                
                // Disable regular middle mouse scroll as requested
                // Handled = true is already here but let's make sure it doesn't do anything else
                e.Handled = true;
            }
        }
    }
}
