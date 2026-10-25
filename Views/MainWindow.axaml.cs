using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using Atelier.Hoswl;
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

        /// <summary>
        /// In frame mode the hidden title bar's job moves to this strip along the top
        /// of the window: dragging there moves the window, everywhere else pans.
        /// </summary>
        private const double FrameDragStripHeight = 40.0;

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
                        else if (args.PropertyName == nameof(MainWindowViewModel.ShowMetadata))
                        {
                            PersistShowMetadata(vm.ShowMetadata);
                        }
                        else if (args.PropertyName == nameof(MainWindowViewModel.IsAnimated)
                              || args.PropertyName == nameof(MainWindowViewModel.IsPlaying))
                        {
                            SyncAnimation();
                        }
                    };

                    // The pane's own default is "open"; the saved choice wins over it.
                    // InitHisashi has already run and loaded the file by this point --
                    // DataContext is assigned after the constructor returns.
                    //
                    // This has to come *after* the subscription above. Applying it first
                    // left a launch with the pane closed raising IsRightPaneVisible into
                    // nothing, so UpdateRightPaneGrid never ran and the third column kept
                    // the 300px the XAML declares: an empty strip of window background
                    // down the right edge that read as the pane it was meant to hide.
                    vm.ShowMetadata = _userSettings.ShowMetadata;
                }
            };

            InitHisashi();
            InitAppearance();
            InitPaint();
            SweepPasteScratch();
        }

        // ---- Hisashi OS Window Layer (hoswl) ---------------------------------------

        private HisashiMenubar? _hisashi;
        private UserSettings _userSettings = new();

        /// <summary>Exposed for tests: the live integration object, if the window created one.</summary>
        public HisashiMenubar? Hisashi => _hisashi;

        /// <summary>Exposed for tests: the settings this window loaded and writes back to.</summary>
        public UserSettings Settings => _userSettings;

        /// <summary>
        /// Remembers the metadata pane's open/closed state across launches. Shares the
        /// one UserSettings instance with the Hisashi integration, so a write here keeps
        /// its two switches rather than rewriting the file from defaults.
        /// </summary>
        private void PersistShowMetadata(bool show)
        {
            if (_userSettings.ShowMetadata == show) return;
            _userSettings.ShowMetadata = show;
            _userSettings.Save();
        }

        /// <summary>
        /// Wires View → Hisashi: the two checkable items persist to settings.json, and
        /// the integration object is (re)created whenever the view model arrives so it
        /// can watch the properties the menu reflects (enabled rows, checkmarks).
        /// </summary>
        private void InitHisashi()
        {
            _userSettings = UserSettings.Load();
            var menu = this.FindControl<Menu>("MainMenu");
            var connect = this.FindControl<MenuItem>("HisashiConnectItem");
            var show = this.FindControl<MenuItem>("HisashiMenusItem");
            if (menu == null || connect == null || show == null) return;

            connect.IsChecked = _userSettings.HisashiIntegration;
            show.IsChecked = _userSettings.HisashiMenus;
            connect.PropertyChanged += (_, e) =>
            {
                if (e.Property == MenuItem.IsCheckedProperty) _hisashi?.SetIntegration(connect.IsChecked);
            };
            show.PropertyChanged += (_, e) =>
            {
                if (e.Property == MenuItem.IsCheckedProperty) _hisashi?.SetShowMenus(show.IsChecked);
            };

            void Create()
            {
                _hisashi?.Dispose();
                _hisashi = new HisashiMenubar(menu, DataContext as INotifyPropertyChanged, _userSettings, AppVersion);
                _hisashi.StateChanged += ApplyHisashiChrome;
                _hisashi.Apply();
            }

            if (DataContext != null) Create();
            DataContextChanged += (_, _) => Create();
            Closed += (_, _) => { _hisashi?.Dispose(); _hisashi = null; };
        }

        /// <summary>
        /// While Hisashi is showing our menus the in-app strip gets out of the way: the
        /// menu row hides and the top bar shrinks to just the drag strip.
        /// </summary>
        private void ApplyHisashiChrome()
        {
            var external = _hisashi?.MenusExternal == true;
            var menu = this.FindControl<Menu>("MainMenu");
            var topBar = this.FindControl<Border>("TopBar");
            if (menu != null) menu.IsVisible = !external;
            if (topBar != null) topBar.Height = external ? 40 : 80;
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
                // The guard lives here rather than in LoadAndFitAsync: that stays a plain
                // loader, which is what the tests and the command line use it as.
                if (!await EnsureSavedAsync()) return;
                await LoadAndFitAsync(file.Path.LocalPath);
            }
        }

        public async void OpenFileName_Click(object? sender, RoutedEventArgs e)
        {
            var topLevel = GetTopLevel(this);
            if (topLevel == null) return;
            if (!await EnsureSavedAsync()) return;

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
            // While editing, Save As saves the edit -- not the untouched file underneath it.
            if (ViewModel is { IsEditMode: true })
            {
                SaveAsEdit_Click(sender, e);
                return;
            }

            var topLevel = GetTopLevel(this);
            if (topLevel == null) return;

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Image As",
                SuggestedFileName = SuggestedSaveName(),
                DefaultExtension = "png",
                FileTypeChoices = SaveFileTypes
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
        /// File &gt; Set as Wallpaper. The fit comes from the clicked item's Tag, so all
        /// six modes share one handler instead of six near-identical copies.
        /// </summary>
        public async void SetWallpaper_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm) return;
            if (sender is not MenuItem item || item.Tag is not string tag) return;
            if (!Enum.TryParse<WallpaperHelper.WallpaperFit>(tag, out var fit)) return;
            if (string.IsNullOrEmpty(vm.ImagePath)) return;

            var path = vm.ImagePath;

            // Rasterising an SVG at the wrong size looks soft once Windows scales it,
            // so aim at the display this window is on.
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            int width = screen?.Bounds.Width ?? 0;
            int height = screen?.Bounds.Height ?? 0;

            try
            {
                // Converting a HEIC or a large SVG takes long enough to stutter the
                // window, so it runs off the UI thread.
                var prepared = await System.Threading.Tasks.Task.Run(
                    () => WallpaperHelper.PrepareImage(path, width, height));

                WallpaperHelper.Apply(prepared, fit);

                vm.ErrorMessage = null;
                ShowStatus($"Wallpaper set ({fit})");
            }
            catch (Exception ex)
            {
                vm.ErrorMessage = $"Could not set wallpaper: {ex.Message}";
            }
        }

        private DispatcherTimer? _statusTimer;

        /// <summary>Shows a transient line in the bottom bar and clears it a few seconds later.</summary>
        private void ShowStatus(string message)
        {
            if (DataContext is not MainWindowViewModel vm) return;
            vm.StatusMessage = message;

            _statusTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            // Restart rather than stack: an earlier message's tick must not clear a newer one.
            _statusTimer.Stop();
            _statusTimer.Tick -= ClearStatus;
            _statusTimer.Tick += ClearStatus;
            _statusTimer.Start();
        }

        private void ClearStatus(object? sender, EventArgs e)
        {
            _statusTimer?.Stop();
            if (DataContext is MainWindowViewModel vm) vm.StatusMessage = null;
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
            list.Children.Add(new Separator { Margin = new Thickness(0, 2, 0, 8) });
            foreach (var cb in checkBoxes)
                list.Children.Add(cb);

            var applyBtn = new Button
            {
                Classes = { "Modern", "Accent" },
                Content = "Save Associations",
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Padding = new Thickness(0, 11, 0, 11),
                Margin = new Thickness(0, 18, 0, 0),
                FontSize = 14
            };

            var dialog = Dialog("File Associations", 420, 580, new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "File associations", FontSize = 22, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4) },
                    Themed(new TextBlock { Text = "Choose which file types open with Atelier.", FontSize = 13, Margin = new Thickness(0, 0, 0, 18) },
                        TextBlock.ForegroundProperty, "Atelier.TextMutedBrush"),
                    new Border { Classes = { "Card" }, Padding = new Thickness(14, 8), Child = new ScrollViewer { Content = list, Height = 330 } },
                    applyBtn
                }
            });

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
            var okBtn = DialogButton("Got it", isDefault: true);
            okBtn.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
            okBtn.Padding = new Thickness(40, 9);
            var dialog = Dialog(title, 400, 220, new StackPanel
            {
                Spacing = 22,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 14, TextAlignment = TextAlignment.Center },
                    okBtn
                }
            });
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
            if (DataContext is MainWindowViewModel vm && vm.DisplayWidth > 0 && vm.DisplayHeight > 0)
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
                    // The turned size, not the stored one: a portrait photo held sideways
                    // by its EXIF tag has to be fitted as the portrait it is displayed as.
                    double ratioW = viewW / vm.DisplayWidth;
                    double ratioH = viewH / vm.DisplayHeight;
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
        /// Shift+wheel pans horizontally, and a bare wheel is deliberately inert — except in
        /// frame mode, where it zooms.
        /// </summary>
        private void OnViewerWheel(object? sender, PointerWheelEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm) return;

            var scroll = this.FindControl<ScrollViewer>("MainScroll");
            if (scroll == null) return;

            // Frame mode has no visible zoom controls, so a bare wheel zooms there.
            bool zoomGesture = e.KeyModifiers.HasFlag(KeyModifiers.Control)
                || (vm.IsFrameMode && e.KeyModifiers == KeyModifiers.None);

            if (zoomGesture)
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
            var centre = Avalonia.Layout.HorizontalAlignment.Center;
            var aboutWindow = Dialog("About Atelier", 440, 500, new StackPanel
            {
                Spacing = 14,
                Margin = new Thickness(12, 8, 12, 0),
                HorizontalAlignment = centre,
                Children =
                {
                    new Image { Source = new Bitmap(AssetLoader.Open(new Uri("avares://Atelier/Assets/atelier-icon.png"))), Width = 132, Height = 132, HorizontalAlignment = centre },
                    new TextBlock { Text = "Atelier", FontSize = 40, FontWeight = FontWeight.SemiBold, HorizontalAlignment = centre, LetterSpacing = -0.5 },
                    new Border
                    {
                        Classes = { "Card" }, CornerRadius = new CornerRadius(11), Padding = new Thickness(12, 3), HorizontalAlignment = centre,
                        Child = Themed(new TextBlock { Text = $"v{AppVersion}", FontSize = 12.5, FontWeight = FontWeight.SemiBold }, TextBlock.ForegroundProperty, "Atelier.AccentBrush"),
                    },
                    Themed(new TextBlock { Text = "A modern, high-performance image viewer built with Avalonia UI & Magick.NET.", FontSize = 14, HorizontalAlignment = centre, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) },
                        TextBlock.ForegroundProperty, "Atelier.TextSecondaryBrush"),
                    Themed(new Rectangle { Height = 1, Margin = new Thickness(30, 8) }, Shape.FillProperty, "Atelier.DividerBrush"),
                    Themed(new TextBlock { Text = "developed by fezcode", FontSize = 13, FontWeight = FontWeight.SemiBold, HorizontalAlignment = centre },
                        TextBlock.ForegroundProperty, "Atelier.AccentBrush"),
                }
            });
            aboutWindow.KeyDown += (_, k) => { if (k.Key == Key.Escape) aboutWindow.Close(); };
            aboutWindow.ShowDialog(this);
        }

        public void EditImage_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.EnterEditMode();
                // Ensure controls are visible when editing
                if (!vm.ShowControls) vm.ShowControls = true;
                // The ribbon takes height from the viewer, so the picture is refitted under it.
                Dispatcher.UIThread.Post(FitToView, DispatcherPriority.Loaded);
            }
        }

        /// <summary>
        /// Edit &gt; Open with Paint. Hands the file on disk to mspaint; the menu item is
        /// only enabled for formats Paint can decode (see CanOpenInPaint), so anything
        /// that reaches here is expected to open. Paint gets its own copy of the file --
        /// nothing is written back to the viewer, so the user has to reload to see edits.
        /// </summary>
        public void OpenInPaint_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel vm) return;
            if (!vm.CanOpenInPaint) return;

            // Same normalisation as Open File Location: a forward slash in the path
            // survives our own file handling but confuses the shell.
            var path = System.IO.Path.GetFullPath(vm.ImagePath!);
            if (!System.IO.File.Exists(path))
            {
                vm.ErrorMessage = $"File no longer exists: {path}";
                return;
            }

            try
            {
                // UseShellExecute keeps the app execution alias working: on Windows 11
                // mspaint.exe in System32 is a stub that launches the Store build.
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "mspaint.exe",
                    Arguments = $"\"{path}\"",
                    UseShellExecute = true
                });
                ShowStatus("Opened in Paint");
            }
            catch (Exception ex)
            {
                vm.ErrorMessage = $"Could not open Paint: {ex.Message}";
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
                SuggestedFileName = SuggestedSaveName(),
                DefaultExtension = "png",
                FileTypeChoices = SaveFileTypes
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
                if (!await EnsureSavedAsync()) return;
                await vm.NextImage();
                Dispatcher.UIThread.Post(FitToView, Avalonia.Threading.DispatcherPriority.Loaded);
            }
        }

        public async void Prev_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                if (!await EnsureSavedAsync()) return;
                await vm.PrevImage();
                Dispatcher.UIThread.Post(FitToView, Avalonia.Threading.DispatcherPriority.Loaded);
            }
        }

        private WindowState _stateBeforeFullScreen = WindowState.Normal;

        private void ToggleFullScreen()
        {
            if (WindowState == WindowState.FullScreen)
            {
                WindowState = _stateBeforeFullScreen;
                if (DataContext is MainWindowViewModel vm)
                {
                    vm.IsFullScreen = false;
                    // Landing back in frame mode: give the exit hint another few
                    // seconds on screen so the user is not stranded.
                    if (vm.IsFrameMode)
                    {
                        vm.ExitFrameHintVisible = true;
                        RestartExitFrameTimer();
                    }
                }
            }
            else
            {
                _stateBeforeFullScreen = WindowState;
                WindowState = WindowState.FullScreen;
                // The window state alone leaves the menu, panes and bottom bar on
                // screen; the view model flag is what strips fullscreen to the image.
                if (DataContext is MainWindowViewModel vm) vm.IsFullScreen = true;
            }

            // Refit after fullscreen toggle
            Dispatcher.UIThread.Post(FitToView, Avalonia.Threading.DispatcherPriority.Loaded);
        }

        public void FrameMode_Click(object? sender, RoutedEventArgs e) => SetFrameMode(true);

        public void ExitFrame_Click(object? sender, RoutedEventArgs e) => SetFrameMode(false);

        private void SetFrameMode(bool on)
        {
            if (DataContext is not MainWindowViewModel vm || vm.IsFrameMode == on) return;

            // Frame mode is a windowed look; entering it straight from fullscreen would
            // leave a chromeless fullscreen window with no caption buttons to close it.
            if (on && WindowState == WindowState.FullScreen)
            {
                WindowState = _stateBeforeFullScreen;
                vm.IsFullScreen = false;
            }

            vm.IsFrameMode = on;
            // A picture frame hangs in front of everything else.
            Topmost = on;

            if (on) RestartExitFrameTimer();
            else _exitFrameTimer?.Stop();

            Dispatcher.UIThread.Post(FitToView, Avalonia.Threading.DispatcherPriority.Loaded);
        }

        private DispatcherTimer? _exitFrameTimer;

        /// <summary>Arms the auto-hide on the EXIT FRAME overlay; hovering the top strip re-arms it.</summary>
        private void RestartExitFrameTimer()
        {
            _exitFrameTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _exitFrameTimer.Tick -= HideExitFrameHint;
            _exitFrameTimer.Tick += HideExitFrameHint;
            _exitFrameTimer.Stop();
            _exitFrameTimer.Start();
        }

        private void HideExitFrameHint(object? sender, EventArgs e)
        {
            _exitFrameTimer?.Stop();
            if (DataContext is MainWindowViewModel vm) vm.ExitFrameHintVisible = false;
        }

        protected override async void OnKeyDown(KeyEventArgs e)
        {
            // Typing -- the text tool's box above all -- owns the keyboard. Otherwise
            // "f" in a caption would toggle fullscreen and "r" would rotate the picture.
            if (IsTypingInTextBox)
            {
                if (HandleTextBoxKey(e)) e.Handled = true;
                base.OnKeyDown(e);
                return;
            }

            if (DataContext is MainWindowViewModel { IsEditMode: true } editing && HandleEditKey(e, editing))
            {
                e.Handled = true;
                base.OnKeyDown(e);
                return;
            }

            if (DataContext is MainWindowViewModel vm)
            {
                bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
                bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

                if (e.Key == Key.Right)
                {
                    if (!await EnsureSavedAsync()) { e.Handled = true; return; }
                    await vm.NextImage();
                    Dispatcher.UIThread.Post(FitToView, Avalonia.Threading.DispatcherPriority.Loaded);
                    e.Handled = true;
                }
                else if (e.Key == Key.Left)
                {
                    if (!await EnsureSavedAsync()) { e.Handled = true; return; }
                    await vm.PrevImage();
                    Dispatcher.UIThread.Post(FitToView, Avalonia.Threading.DispatcherPriority.Loaded);
                    e.Handled = true;
                }
                else if (e.Key == Key.R && !ctrl)
                {
                    // Shift turns it the other way, the way Shift reverses a cycle elsewhere.
                    if (shift) RotateLeft_Click(null, new RoutedEventArgs());
                    else RotateRight_Click(null, new RoutedEventArgs());
                    e.Handled = true;
                }
                else if (e.Key == Key.C && ctrl)
                {
                    Copy_Click(null, new RoutedEventArgs());
                    e.Handled = true;
                }
                else if (e.Key == Key.V && ctrl)
                {
                    Paste_Click(null, new RoutedEventArgs());
                    e.Handled = true;
                }
                else if (e.Key == Key.Delete)
                {
                    Delete_Click(null, new RoutedEventArgs());
                    e.Handled = true;
                }
                else if (e.Key == Key.F2)
                {
                    Rename_Click(null, new RoutedEventArgs());
                    e.Handled = true;
                }
                else if (e.Key == Key.Space && vm.IsAnimated)
                {
                    vm.TogglePlayback();
                    e.Handled = true;
                }
                else if (ctrl && (e.Key == Key.D0 || e.Key == Key.NumPad0))
                {
                    FitToView();
                    e.Handled = true;
                }
                else if (ctrl && (e.Key == Key.D1 || e.Key == Key.NumPad1))
                {
                    vm.ZoomLevel = 1.0;
                    e.Handled = true;
                }
                else if (ctrl && (e.Key == Key.OemPlus || e.Key == Key.Add))
                {
                    ZoomAroundViewportCentre(ButtonZoomStep);
                    e.Handled = true;
                }
                else if (ctrl && (e.Key == Key.OemMinus || e.Key == Key.Subtract))
                {
                    ZoomAroundViewportCentre(1.0 / ButtonZoomStep);
                    e.Handled = true;
                }
                else if (e.Key == Key.F)
                {
                    ToggleFullScreen();
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    // Unwind one layer at a time: fullscreen first, then frame mode.
                    if (WindowState == WindowState.FullScreen)
                    {
                        ToggleFullScreen();
                        e.Handled = true;
                    }
                    else if (vm.IsFrameMode)
                    {
                        SetFrameMode(false);
                        e.Handled = true;
                    }
                }
                else if (e.Key == Key.OemComma && ctrl)
                {
                    Appearance_Click(null, new RoutedEventArgs());
                    e.Handled = true;
                }
                else if (e.Key == Key.O && ctrl)
                {
                    OpenFileName_Click(null, new RoutedEventArgs());
                }
                else if (e.Key == Key.S && ctrl)
                {
                    // Ctrl+S saves the picture now that a rotation can leave it unsaved;
                    // Save As moves to Ctrl+Shift+S, which is where it sits everywhere else.
                    if (shift) SaveAs_Click(null, new RoutedEventArgs());
                    else Save_Click(null, new RoutedEventArgs());
                }
            }
            base.OnKeyDown(e);
        }

        private void OnScrollPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (HandlePaintPressed(e)) return;

            var props = e.GetCurrentPoint(this).Properties;
            // The middle button always pans; in edit mode it is the way to pan without
            // putting the brush down, alongside Space+drag.
            if (props.IsLeftButtonPressed || props.IsMiddleButtonPressed)
            {
                if (DataContext is MainWindowViewModel vm && vm.IsFrameMode
                    && WindowState != WindowState.FullScreen
                    && e.GetPosition(this).Y <= FrameDragStripHeight)
                {
                    BeginMoveDrag(e);
                    return;
                }

                _isPanning = true;
                _lastMousePos = e.GetPosition(this);
                Cursor = new Cursor(StandardCursorType.Hand);
                e.Handled = true;
            }
        }

        private void OnScrollPointerMoved(object? sender, PointerEventArgs e)
        {
            if (!_isPanning && HandlePaintMoved(e)) return;

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
                return;
            }

            // Hovering the top strip brings the auto-hidden EXIT FRAME button back
            // and keeps it alive while the pointer stays there.
            if (DataContext is MainWindowViewModel vm && vm.IsFrameMode && !vm.IsFullScreen
                && e.GetPosition(this).Y <= FrameDragStripHeight)
            {
                vm.ExitFrameHintVisible = true;
                RestartExitFrameTimer();
            }
        }

        private void OnScrollPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (HandlePaintReleased(e)) return;

            if (_isPanning)
            {
                _isPanning = false;
                Cursor = Cursor.Default;
                e.Handled = true;
            }
        }

    }
}
