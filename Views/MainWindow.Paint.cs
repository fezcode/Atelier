using System;
using System.Globalization;
using System.Threading.Tasks;
using Atelier.Painting;
using Atelier.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Atelier.Views
{
    /// <summary>
    /// Edit mode's window plumbing: turning pointer events over the picture into canvas
    /// coordinates for the tools, the Paint keyboard shortcuts, the ribbon's buttons and
    /// the three dialogs Paint has (Resize and Skew, Image Properties, Edit Colours).
    /// The tools themselves live in the view model, where the tests can drive them.
    /// </summary>
    public partial class MainWindow
    {
        private Image? _canvasImage;
        private PaintOverlay? _overlay;
        private TextBox? _canvasText;
        private ScrollViewer? _mainScroll;

        /// <summary>A tool gesture owns the pointer; moves and the release go to it, not to panning.</summary>
        private bool _isPainting;

        /// <summary>Space held: the left button pans instead of painting, as in most editors.</summary>
        private bool _spaceHeld;

        private DispatcherTimer? _sprayTimer;

        private void InitPaint()
        {
            _canvasImage = this.FindControl<Image>("CanvasImage");
            _overlay = this.FindControl<PaintOverlay>("PaintOverlay");
            _canvasText = this.FindControl<TextBox>("CanvasTextBox");
            _mainScroll = this.FindControl<ScrollViewer>("MainScroll");

            if (_mainScroll != null)
                _mainScroll.PointerExited += (_, _) => ViewModel?.PaintPointerLeave();

            MainWindowViewModel? subscribed = null;
            DataContextChanged += (_, _) =>
            {
                if (subscribed != null)
                {
                    subscribed.CanvasInvalidated -= OnCanvasInvalidated;
                    subscribed.OverlayInvalidated -= OnOverlayInvalidated;
                    subscribed.CanvasResized -= OnCanvasResized;
                    subscribed.TextEditStarted -= OnTextEditStarted;
                    subscribed.PropertyChanged -= OnPaintPropertyChanged;
                }

                subscribed = ViewModel;
                if (_overlay != null) _overlay.Source = subscribed;
                if (subscribed == null) return;

                subscribed.CanvasInvalidated += OnCanvasInvalidated;
                subscribed.OverlayInvalidated += OnOverlayInvalidated;
                subscribed.CanvasResized += OnCanvasResized;
                subscribed.TextEditStarted += OnTextEditStarted;
                subscribed.PropertyChanged += OnPaintPropertyChanged;
            };
        }

        private void OnCanvasInvalidated() => _canvasImage?.InvalidateVisual();

        private void OnOverlayInvalidated() => _overlay?.InvalidateVisual();

        private void OnCanvasResized() => Dispatcher.UIThread.Post(FitToView, DispatcherPriority.Loaded);

        private void OnTextEditStarted() =>
            Dispatcher.UIThread.Post(() => _canvasText?.Focus(), DispatcherPriority.Loaded);

        private void OnPaintPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(MainWindowViewModel.ZoomLevel):
                case nameof(MainWindowViewModel.IsEditMode):
                    UpdateCanvasInterpolation();
                    UpdateCanvasCursor();
                    _overlay?.InvalidateVisual();
                    break;
                case nameof(MainWindowViewModel.Tool):
                    UpdateCanvasCursor();
                    break;
            }
        }

        /// <summary>
        /// Zoomed in to edit, pixels should look like pixels -- crisp squares you can aim
        /// at, as in Paint -- rather than the smoothed blur that suits viewing a photo.
        /// </summary>
        private void UpdateCanvasInterpolation()
        {
            if (_canvasImage == null || ViewModel is not { } vm) return;
            RenderOptions.SetBitmapInterpolationMode(_canvasImage,
                vm.IsEditMode && vm.ZoomLevel >= 2 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality);
        }

        private void UpdateCanvasCursor()
        {
            if (_mainScroll == null || ViewModel is not { } vm) return;
            if (!vm.IsEditMode)
            {
                _mainScroll.Cursor = null;
                return;
            }
            _mainScroll.Cursor = new Cursor(_spaceHeld
                ? StandardCursorType.Hand
                : vm.Tool switch
                {
                    PaintTool.Text => StandardCursorType.Ibeam,
                    PaintTool.Magnifier => StandardCursorType.Hand,
                    _ => StandardCursorType.Cross,
                });
        }

        /// <summary>The pointer in canvas pixels. False before there is a canvas to aim at.</summary>
        private bool TryCanvasPoint(PointerEventArgs e, out Point point)
        {
            point = default;
            if (_canvasImage == null || ViewModel?.Document is not { } doc) return false;
            var bounds = _canvasImage.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0) return false;

            // Relative to the Image itself, so the zoom and centring are already undone.
            var pos = e.GetPosition(_canvasImage);
            point = new Point(pos.X * doc.Width / bounds.Width, pos.Y * doc.Height / bounds.Height);
            return true;
        }

        // ---- Pointer routing (called from the ScrollViewer's handlers) ---------------

        private bool HandlePaintPressed(PointerPressedEventArgs e)
        {
            if (ViewModel is not { IsEditMode: true } vm || vm.Document == null || _mainScroll == null) return false;

            var props = e.GetCurrentPoint(this).Properties;
            // Middle button and Space+drag pan; leave those to the viewer.
            if (props.IsMiddleButtonPressed || _spaceHeld) return false;
            if (!props.IsLeftButtonPressed && !props.IsRightButtonPressed) return false;
            bool secondary = props.IsRightButtonPressed && !props.IsLeftButtonPressed;

            if (vm.Tool == PaintTool.Magnifier)
            {
                if (_mainScroll.Presenter != null)
                    ZoomAnchored(_mainScroll, secondary ? 0.5 : 2.0, e.GetPosition(_mainScroll.Presenter));
                e.Handled = true;
                return true;
            }

            if (!TryCanvasPoint(e, out var p)) return false;

            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            vm.PaintPointerDown(p.X, p.Y, secondary, shift, ctrl, e.ClickCount);

            _isPainting = vm.IsGestureActive;
            if (_isPainting) e.Pointer.Capture(_mainScroll);
            if (vm.IsSpraying) StartSpray();
            e.Handled = true;
            return true;
        }

        private bool HandlePaintMoved(PointerEventArgs e)
        {
            if (ViewModel is not { IsEditMode: true } vm || vm.Document == null) return false;
            if (!TryCanvasPoint(e, out var p)) return false;

            vm.PaintPointerMove(p.X, p.Y, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            if (_isPainting)
            {
                e.Handled = true;
                return true;
            }
            return false;
        }

        private bool HandlePaintReleased(PointerReleasedEventArgs e)
        {
            if (!_isPainting) return false;
            _isPainting = false;
            StopSpray();

            if (ViewModel is { } vm && TryCanvasPoint(e, out var p))
                vm.PaintPointerUp(p.X, p.Y, e.KeyModifiers.HasFlag(KeyModifiers.Shift));

            e.Pointer.Capture(null);
            e.Handled = true;
            return true;
        }

        private void StartSpray()
        {
            _sprayTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Input,
                (_, _) =>
                {
                    if (ViewModel is { IsSpraying: true } vm) vm.SprayTick();
                    else _sprayTimer?.Stop();
                });
            _sprayTimer.Start();
        }

        private void StopSpray() => _sprayTimer?.Stop();

        // ---- Keyboard ------------------------------------------------------------------

        /// <summary>
        /// True while typing into a text box -- the canvas text tool, mostly -- when the
        /// single-letter shortcuts (F for fullscreen, R to rotate...) must not fire.
        /// </summary>
        private bool IsTypingInTextBox => FocusManager?.GetFocusedElement() is TextBox;

        /// <summary>Keys while the canvas text box has focus: Esc cancels, Ctrl+Enter commits.</summary>
        private bool HandleTextBoxKey(KeyEventArgs e)
        {
            if (ViewModel is not { } vm || FocusManager?.GetFocusedElement() != _canvasText) return false;

            if (e.Key == Key.Escape)
            {
                vm.CancelText();
                Focus();
                return true;
            }
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                vm.CommitText();
                Focus();
                return true;
            }
            return false;
        }

        /// <summary>
        /// Paint's shortcuts. Returns false for anything it does not own, so the viewer's
        /// keys -- R to rotate, F for fullscreen, Ctrl+S, the zoom keys -- keep working.
        /// </summary>
        private bool HandleEditKey(KeyEventArgs e, MainWindowViewModel vm)
        {
            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            bool plain = e.KeyModifiers == KeyModifiers.None;

            if (ctrl)
            {
                switch (e.Key)
                {
                    case Key.Z when shift: vm.Redo(); return true;
                    case Key.Z: vm.Undo(); return true;
                    case Key.Y: vm.Redo(); return true;
                    case Key.A: vm.SelectAll(); return true;
                    case Key.X when shift: vm.CropToSelection(); return true;
                    case Key.X: vm.CutSelection(); return true;
                    case Key.C: vm.CopySelectionToClipboard(); return true;
                    case Key.V: PasteIntoCanvas(); return true;
                    case Key.W: ResizeSkew_Click(null, new RoutedEventArgs()); return true;
                    case Key.E: ImageProperties_Click(null, new RoutedEventArgs()); return true;
                    case Key.I when shift: vm.InvertColors(); return true;
                    case Key.G: vm.ShowGridlines = !vm.ShowGridlines; return true;
                }
                return false;
            }

            switch (e.Key)
            {
                case Key.Delete:
                    // Never the Recycle Bin while editing: Delete clears the selection, as in Paint.
                    vm.DeleteSelection();
                    return true;
                case Key.Escape:
                    return vm.CancelCurrent();
                case Key.Enter:
                    vm.FinishPendingWork(deselect: true);
                    return true;
                case Key.Left: case Key.Right: case Key.Up: case Key.Down:
                {
                    // Arrows move the selection a pixel at a time. They never leave the
                    // picture for the next one in the folder while it is being edited.
                    int step = shift ? 10 : 1;
                    int dx = e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0;
                    int dy = e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
                    vm.NudgeSelection(dx, dy);
                    return true;
                }
                case Key.Space:
                    if (!_spaceHeld)
                    {
                        _spaceHeld = true;
                        UpdateCanvasCursor();
                    }
                    return true;
                case Key.OemOpenBrackets:
                    vm.StrokeSize -= vm.StrokeSize > 10 ? 5 : 1;
                    return true;
                case Key.OemCloseBrackets:
                    vm.StrokeSize += vm.StrokeSize >= 10 ? 5 : 1;
                    return true;
            }

            if (!plain) return false;
            switch (e.Key)
            {
                case Key.S: vm.Tool = PaintTool.Select; return true;
                case Key.P: vm.Tool = PaintTool.Pencil; return true;
                case Key.B: vm.Tool = PaintTool.Brush; return true;
                case Key.E: vm.Tool = PaintTool.Eraser; return true;
                case Key.G: vm.Tool = PaintTool.Fill; return true;
                case Key.I: vm.Tool = PaintTool.Picker; return true;
                case Key.T: vm.Tool = PaintTool.Text; return true;
                case Key.Z: vm.Tool = PaintTool.Magnifier; return true;
                case Key.L: vm.ActiveShape = ShapeKind.Line; return true;
                case Key.U: vm.ActiveShape = ShapeKind.Rectangle; return true;
                case Key.O: vm.ActiveShape = ShapeKind.Oval; return true;
                case Key.X: vm.SwapColors(); return true;
            }
            return false;
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.Key == Key.Space && _spaceHeld)
            {
                _spaceHeld = false;
                UpdateCanvasCursor();
            }
            base.OnKeyUp(e);
        }

        // ---- Ribbon and menu handlers ---------------------------------------------------

        public void Undo_Click(object? sender, RoutedEventArgs e) => ViewModel?.Undo();

        public void Redo_Click(object? sender, RoutedEventArgs e) => ViewModel?.Redo();

        public void Cut_Click(object? sender, RoutedEventArgs e) => ViewModel?.CutSelection();

        public void SelectAll_Click(object? sender, RoutedEventArgs e) => ViewModel?.SelectAll();

        public void InvertSelection_Click(object? sender, RoutedEventArgs e) => ViewModel?.InvertSelection();

        public void DeleteSelection_Click(object? sender, RoutedEventArgs e) => ViewModel?.DeleteSelection();

        public void Crop_Click(object? sender, RoutedEventArgs e) => ViewModel?.CropToSelection();

        public void InvertColors_Click(object? sender, RoutedEventArgs e) => ViewModel?.InvertColors();

        public void RectSelect_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel is { } vm) vm.Tool = PaintTool.Select;
        }

        public void FreeSelect_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel is { } vm) vm.Tool = PaintTool.FreeSelect;
        }

        public void RotateCanvas_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel is { } vm && sender is Control { Tag: string tag } && int.TryParse(tag, out int degrees))
                vm.RotateCanvas(degrees);
        }

        public void FlipCanvas_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel is { } vm && sender is Control { Tag: string tag })
                vm.FlipCanvas(horizontal: tag == "H");
        }

        public void ResetAdjustments_Click(object? sender, RoutedEventArgs e) => ViewModel?.ResetAdjustments();

        public void SwapColors_Click(object? sender, RoutedEventArgs e) => ViewModel?.SwapColors();

        public void Color1_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel is { } vm) vm.IsColor2Active = false;
        }

        public void Color2_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel is { } vm) vm.IsColor2Active = true;
        }

        /// <summary>A palette click sets the selected well; a right-click always sets colour 2.</summary>
        public void Swatch_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (ViewModel is not { } vm || sender is not Control { DataContext: PaletteSwatch swatch } || swatch.IsEmpty) return;
            var props = e.GetCurrentPoint(this).Properties;
            if (props.IsRightButtonPressed) vm.SecondaryColor = swatch.Color;
            else vm.SetActiveColor(swatch.Color);
            e.Handled = true;
        }

        public void BrushList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0) return;
            this.FindControl<Button>("BrushPickerButton")?.Flyout?.Hide();
        }

        /// <summary>Pastes at the top-left of what is on screen, which is where Paint puts it.</summary>
        private void PasteIntoCanvas()
        {
            if (ViewModel is not { } vm) return;
            var at = new PixelPoint(0, 0);
            if (_mainScroll?.Presenter is { } presenter && _canvasImage is { } image && vm.Document is { } doc
                && presenter.TranslatePoint(new Point(0, 0), image) is { } local && image.Bounds.Width > 0)
            {
                at = new PixelPoint(
                    (int)Math.Max(0, local.X * doc.Width / image.Bounds.Width),
                    (int)Math.Max(0, local.Y * doc.Height / image.Bounds.Height));
            }
            vm.PasteIntoCanvas(at);
        }

        // ---- Dialogs ---------------------------------------------------------------

        private static TextBox NumberBox(string text) => new()
        {
            Text = text,
            Width = 90,
            FontSize = 13,
            Background = new SolidColorBrush(Color.Parse("#151515")),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse("#333333")),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 5),
            CornerRadius = new CornerRadius(4),
        };

        private static TextBlock Label(string text, bool heading = false) => new()
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.Parse(heading ? "#888888" : "#DDDDDD")),
            FontSize = heading ? 11 : 13,
            FontWeight = heading ? FontWeight.Bold : FontWeight.Medium,
            LetterSpacing = heading ? 1 : 0,
            VerticalAlignment = VerticalAlignment.Center,
        };

        private static bool TryNumber(TextBox box, out double value) =>
            double.TryParse(box.Text?.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            || double.TryParse(box.Text?.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        private static Grid Row(string label, Control field, string? unit = null)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("110,Auto,Auto") };
            grid.Children.Add(Label(label));
            Grid.SetColumn(field, 1);
            grid.Children.Add(field);
            if (unit != null)
            {
                var u = Label(unit);
                u.Margin = new Thickness(8, 0, 0, 0);
                u.Foreground = new SolidColorBrush(Color.Parse("#888888"));
                Grid.SetColumn(u, 2);
                grid.Children.Add(u);
            }
            return grid;
        }

        /// <summary>
        /// Paint's Resize and Skew: by percentage or by pixels, aspect locked by default,
        /// with a horizontal and vertical skew below. Acts on the selection if there is one.
        /// </summary>
        public async void ResizeSkew_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel is not { IsEditMode: true } vm || vm.Document == null) return;
            vm.FinishPendingWork(deselect: false);
            var subject = vm.ResizeSubjectSize;
            if (subject.Width <= 0 || subject.Height <= 0) return;

            var byPercent = new RadioButton { Content = "Percentage", IsChecked = true, GroupName = "ResizeBy", Foreground = Brushes.White };
            var byPixels = new RadioButton { Content = "Pixels", GroupName = "ResizeBy", Foreground = Brushes.White };
            var horizontal = NumberBox("100");
            var vertical = NumberBox("100");
            var keepAspect = new CheckBox { Content = "Maintain aspect ratio", IsChecked = true, Foreground = Brushes.White };
            var skewH = NumberBox("0");
            var skewV = NumberBox("0");
            var unitH = Label("%");
            var unitV = Label("%");

            bool syncing = false;
            void SyncAspect(TextBox changed)
            {
                if (syncing || keepAspect.IsChecked != true || !TryNumber(changed, out double v)) return;
                syncing = true;
                bool percent = byPercent.IsChecked == true;
                if (changed == horizontal)
                    vertical.Text = percent ? v.ToString("0.##") : Math.Max(1, Math.Round(v * subject.Height / subject.Width)).ToString("0");
                else
                    horizontal.Text = percent ? v.ToString("0.##") : Math.Max(1, Math.Round(v * subject.Width / subject.Height)).ToString("0");
                syncing = false;
            }
            horizontal.TextChanged += (_, _) => SyncAspect(horizontal);
            vertical.TextChanged += (_, _) => SyncAspect(vertical);

            void SwitchUnits()
            {
                syncing = true;
                bool percent = byPercent.IsChecked == true;
                horizontal.Text = percent ? "100" : subject.Width.ToString();
                vertical.Text = percent ? "100" : subject.Height.ToString();
                unitH.Text = unitV.Text = percent ? "%" : "px";
                syncing = false;
            }
            byPercent.IsCheckedChanged += (_, _) => SwitchUnits();

            var ok = DialogButton("OK", isDefault: true);
            var cancel = DialogButton("Cancel");

            Grid UnitRow(string label, TextBox box, TextBlock unit)
            {
                var row = Row(label, box);
                unit.Margin = new Thickness(8, 0, 0, 0);
                Grid.SetColumn(unit, 2);
                row.Children.Add(unit);
                return row;
            }

            var dialog = Dialog("Resize and Skew", 380, 470, new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    Label("RESIZE", heading: true),
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, Children = { byPercent, byPixels } },
                    UnitRow("Horizontal", horizontal, unitH),
                    UnitRow("Vertical", vertical, unitV),
                    keepAspect,
                    new Border { Height = 1, Background = new SolidColorBrush(Color.Parse("#222222")), Margin = new Thickness(0, 6) },
                    Label("SKEW (DEGREES)", heading: true),
                    Row("Horizontal", skewH, "°"),
                    Row("Vertical", skewV, "°"),
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 12, 0, 0),
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancel, ok },
                    },
                },
            });

            bool accepted = false;
            ok.Click += (_, _) => { accepted = true; dialog.Close(); };
            cancel.Click += (_, _) => dialog.Close();
            dialog.KeyDown += (_, k) => { if (k.Key == Key.Escape) dialog.Close(); };
            await dialog.ShowDialog(this);
            if (!accepted) return;

            if (!TryNumber(horizontal, out double h) || !TryNumber(vertical, out double v) || h <= 0 || v <= 0)
            {
                vm.ErrorMessage = "Resize needs two positive numbers.";
                return;
            }
            TryNumber(skewH, out double sh);
            TryNumber(skewV, out double sv);
            sh = Math.Clamp(sh, -89, 89);
            sv = Math.Clamp(sv, -89, 89);

            int width = byPercent.IsChecked == true ? (int)Math.Round(subject.Width * h / 100) : (int)Math.Round(h);
            int height = byPercent.IsChecked == true ? (int)Math.Round(subject.Height * v / 100) : (int)Math.Round(v);
            width = Math.Clamp(width, 1, 30000);
            height = Math.Clamp(height, 1, 30000);

            if (width != subject.Width || height != subject.Height) vm.ResizeCanvas(width, height);
            if (sh != 0 || sv != 0) vm.SkewCanvas(sh, sv);
        }

        /// <summary>Paint's Image Properties: the canvas size in pixels, changed without scaling.</summary>
        public async void ImageProperties_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel is not { IsEditMode: true } vm || vm.Document is not { } doc) return;

            var width = NumberBox(doc.Width.ToString());
            var height = NumberBox(doc.Height.ToString());
            var ok = DialogButton("OK", isDefault: true);
            var cancel = DialogButton("Cancel");

            var dialog = Dialog("Image Properties", 360, 290, new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    Label("CANVAS SIZE", heading: true),
                    Row("Width", width, "px"),
                    Row("Height", height, "px"),
                    new TextBlock
                    {
                        Text = "New space is filled with colour 2. The picture is not scaled.",
                        Foreground = new SolidColorBrush(Color.Parse("#888888")), FontSize = 12, TextWrapping = TextWrapping.Wrap,
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 6, 0, 0),
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancel, ok },
                    },
                },
            });

            bool accepted = false;
            ok.Click += (_, _) => { accepted = true; dialog.Close(); };
            cancel.Click += (_, _) => dialog.Close();
            dialog.KeyDown += (_, k) => { if (k.Key == Key.Escape) dialog.Close(); };
            await dialog.ShowDialog(this);
            if (!accepted) return;

            if (TryNumber(width, out double w) && TryNumber(height, out double h) && w >= 1 && h >= 1)
                vm.SetCanvasSize(Math.Min(30000, (int)Math.Round(w)), Math.Min(30000, (int)Math.Round(h)));
            else
                vm.ErrorMessage = "Width and height must be at least 1 pixel.";
        }

        /// <summary>Edit Colours: any colour, saved into the next custom slot and the selected well.</summary>
        public async void EditColors_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel is not { } vm) return;

            var picker = new ColorView
            {
                Color = vm.IsColor2Active ? vm.SecondaryColor : vm.PrimaryColor,
                IsAlphaEnabled = true,
                IsColorPaletteVisible = false,
                Width = 340,
                Height = 360,
            };
            var ok = DialogButton("Add to custom colours", isDefault: true);
            var cancel = DialogButton("Cancel");

            var dialog = Dialog("Edit Colours", 420, 520, new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    picker,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 10,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancel, ok },
                    },
                },
            });

            bool accepted = false;
            ok.Click += (_, _) => { accepted = true; dialog.Close(); };
            cancel.Click += (_, _) => dialog.Close();
            dialog.KeyDown += (_, k) => { if (k.Key == Key.Escape) dialog.Close(); };
            await dialog.ShowDialog(this);

            if (accepted) vm.AddCustomColor(picker.Color);
        }
    }
}
