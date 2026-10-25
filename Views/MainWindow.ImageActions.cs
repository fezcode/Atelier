using System;
using System.IO;
using System.Threading.Tasks;
using Atelier.Theme;
using Atelier.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Atelier.Views
{
    /// <summary>
    /// The actions that act on the picture itself -- turning it, copying it, renaming
    /// it, deleting it -- together with the two prompts they need.
    ///
    /// A partial rather than more of MainWindow.axaml.cs, which was already past a
    /// thousand lines of window plumbing before any of this arrived.
    /// </summary>
    public partial class MainWindow
    {
        private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

        // ---- Rotation and flipping -------------------------------------------------

        public void RotateRight_Click(object? sender, RoutedEventArgs e) => Turn(vm => vm.RotateRight());

        public void RotateLeft_Click(object? sender, RoutedEventArgs e) => Turn(vm => vm.RotateLeft());

        public void FlipHorizontal_Click(object? sender, RoutedEventArgs e) => Turn(vm => vm.FlipHorizontal());

        public void FlipVertical_Click(object? sender, RoutedEventArgs e) => Turn(vm => vm.FlipVertical());

        /// <summary>
        /// Applies a turn and re-fits. The re-fit is the point: a landscape photo turned
        /// on its side needs a different zoom to fit the window, and leaving the old one
        /// crops the picture against the edges.
        /// </summary>
        private void Turn(Action<MainWindowViewModel> apply)
        {
            if (ViewModel is not { } vm || !vm.HasImage) return;

            apply(vm);
            Dispatcher.UIThread.Post(FitToView, DispatcherPriority.Loaded);
        }

        // ---- Saving ----------------------------------------------------------------

        public async void Save_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel is { } vm) await vm.SaveInPlaceAsync();
        }

        // ---- Clipboard -------------------------------------------------------------

        // In edit mode the clipboard works on the canvas, as in Paint: copy takes the
        // selection (or the whole edited picture) and paste drops in a movable selection.

        public void Copy_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel is not { } vm) return;
            if (vm.IsEditMode) vm.CopySelectionToClipboard();
            else vm.CopyToClipboard();
        }

        public async void Paste_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel is not { } vm) return;
            if (vm.IsEditMode)
            {
                PasteIntoCanvas();
                return;
            }
            if (!await EnsureSavedAsync()) return;

            if (await vm.PasteFromClipboardAsync())
                Dispatcher.UIThread.Post(FitToView, DispatcherPriority.Loaded);
        }

        // ---- Delete and rename -----------------------------------------------------

        public async void Delete_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel is not { } vm || !vm.HasImage) return;

            // While editing, Delete belongs to the selection. Binning the file out from
            // under an open edit would be a nasty surprise for a key Paint users press often.
            if (vm.IsEditMode)
            {
                vm.DeleteSelection();
                return;
            }

            // No confirmation: the Recycle Bin is the undo, and a prompt on every cull
            // is a click people learn to dismiss without reading.
            if (await vm.DeleteCurrentAsync())
                Dispatcher.UIThread.Post(FitToView, DispatcherPriority.Loaded);
        }

        public async void Rename_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel is not { } vm || vm.ImagePath is not { } path) return;

            string? name = await PromptForNameAsync(Path.GetFileName(path));
            if (name == null) return;

            await vm.RenameCurrentAsync(name);
        }

        // ---- Remove metadata --------------------------------------------------------

        private enum StripChoice { Overwrite, Copy, Cancel }

        /// <summary>
        /// File &gt; Remove Metadata, and the button under the metadata pane. Asks first:
        /// unlike a delete there is no Recycle Bin to undo an overwrite from, so the
        /// prompt also offers to write a clean copy instead.
        /// </summary>
        public async void RemoveMetadata_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel is not { CanRemoveMetadata: true } vm || vm.ImagePath is not { } path) return;
            // A pending rotation would be lost when the cleaned file is reloaded.
            if (!await EnsureSavedAsync()) return;

            string? destination;
            switch (await PromptStripAsync(Path.GetFileName(path)))
            {
                case StripChoice.Overwrite:
                    destination = null;
                    break;
                case StripChoice.Copy:
                    destination = await PickCleanCopyAsync(path);
                    if (destination == null) return;
                    break;
                default:
                    return;
            }

            if (await vm.RemoveMetadataAsync(destination))
                Dispatcher.UIThread.Post(FitToView, DispatcherPriority.Loaded);
        }

        private async Task<string?> PickCleanCopyAsync(string path)
        {
            var topLevel = GetTopLevel(this);
            if (topLevel == null) return null;

            string ext = Path.GetExtension(path);
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Copy Without Metadata",
                SuggestedFileName = Path.GetFileNameWithoutExtension(path) + "-clean" + ext,
                DefaultExtension = ext.TrimStart('.'),
                // The copy keeps the original's format: the point is to change nothing but
                // the metadata, and switching format would re-encode the pixels.
                FileTypeChoices = new[]
                {
                    new FilePickerFileType($"{ext.TrimStart('.').ToUpperInvariant()} Image") { Patterns = new[] { "*" + ext } },
                },
            });
            return file?.Path.LocalPath;
        }

        private async Task<StripChoice> PromptStripAsync(string fileName)
        {
            var choice = StripChoice.Cancel;

            var remove = DialogButton("Remove", isDefault: true);
            var copy = DialogButton("Save a Copy...");
            var cancel = DialogButton("Cancel");

            var dialog = Dialog("Remove metadata", 480, 250, new StackPanel
            {
                Spacing = 18,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Remove all metadata from {fileName}?",
                        FontSize = 15,
                        FontWeight = FontWeight.SemiBold,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = "EXIF (camera, dates, GPS location), XMP, IPTC, comments and Content " +
                               "Credentials (C2PA) are removed from the file. The colour profile and " +
                               "orientation are kept. This cannot be undone.",
                        [!TextBlock.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Atelier.TextSecondaryBrush"),
                        FontSize = 13,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancel, copy, remove },
                    },
                },
            });

            remove.Click += (_, _) => { choice = StripChoice.Overwrite; dialog.Close(); };
            copy.Click += (_, _) => { choice = StripChoice.Copy; dialog.Close(); };
            cancel.Click += (_, _) => { choice = StripChoice.Cancel; dialog.Close(); };
            dialog.KeyDown += (_, e) => { if (e.Key == Key.Escape) dialog.Close(); };

            await dialog.ShowDialog(this);
            return choice;
        }

        // ---- Save As formats --------------------------------------------------------

        /// <summary>
        /// Everything this build of Magick can write and read back, most common first.
        /// Each one was checked by writing a test image: HEIC is missing because there is
        /// no HEIC encoder in the bundled Magick, and PDF/EPS can be written but need
        /// Ghostscript to be opened again -- see <see cref="MainWindowViewModel.IsExportOnly"/>.
        /// </summary>
        internal static readonly FilePickerFileType[] SaveFileTypes =
        {
            new("PNG Image") { Patterns = new[] { "*.png" } },
            new("JPEG Image") { Patterns = new[] { "*.jpg", "*.jpeg" } },
            new("WebP Image") { Patterns = new[] { "*.webp" } },
            new("AVIF Image") { Patterns = new[] { "*.avif" } },
            new("JPEG XL Image") { Patterns = new[] { "*.jxl" } },
            new("TIFF Image") { Patterns = new[] { "*.tif", "*.tiff" } },
            new("BMP Image") { Patterns = new[] { "*.bmp" } },
            new("GIF Image") { Patterns = new[] { "*.gif" } },
            new("Icon File") { Patterns = new[] { "*.ico" } },
            new("JPEG 2000 Image") { Patterns = new[] { "*.jp2" } },
            new("QOI Image") { Patterns = new[] { "*.qoi" } },
            new("Photoshop Document") { Patterns = new[] { "*.psd" } },
            new("Targa Image") { Patterns = new[] { "*.tga" } },
            new("DirectDraw Surface") { Patterns = new[] { "*.dds" } },
            new("OpenEXR Image") { Patterns = new[] { "*.exr" } },
            new("Radiance HDR Image") { Patterns = new[] { "*.hdr" } },
            new("PDF Document") { Patterns = new[] { "*.pdf" } },
            new("Encapsulated PostScript") { Patterns = new[] { "*.eps" } },
            new("Portable Pixmap (PPM)") { Patterns = new[] { "*.ppm" } },
            new("Portable Graymap (PGM)") { Patterns = new[] { "*.pgm" } },
            new("Portable Bitmap (PBM)") { Patterns = new[] { "*.pbm" } },
            new("Portable Arbitrary Map (PAM)") { Patterns = new[] { "*.pam" } },
            new("Portable Float Map (PFM)") { Patterns = new[] { "*.pfm" } },
            new("PCX Image") { Patterns = new[] { "*.pcx" } },
            new("SGI Image") { Patterns = new[] { "*.sgi" } },
            new("Sun Raster Image") { Patterns = new[] { "*.sun" } },
            new("X PixMap (XPM)") { Patterns = new[] { "*.xpm" } },
            new("X BitMap (XBM)") { Patterns = new[] { "*.xbm" } },
            new("Wireless Bitmap (WBMP)") { Patterns = new[] { "*.wbmp" } },
            new("FITS Image") { Patterns = new[] { "*.fits" } },
            new("Farbfeld Image") { Patterns = new[] { "*.ff" } },
            new("Magick Image File (MIFF)") { Patterns = new[] { "*.miff" } },
        };

        /// <summary>The open picture's name without its extension, so the picker's type decides it.</summary>
        private string? SuggestedSaveName() =>
            ViewModel?.ImagePath is { } path ? Path.GetFileNameWithoutExtension(path) : null;

        // ---- The unsaved-rotation guard --------------------------------------------

        private enum UnsavedChoice { Save, Discard, Cancel }

        /// <summary>
        /// Run before anything that would replace the picture on screen. Returns false
        /// only when the user cancels, in which case the caller abandons what it was
        /// about to do.
        /// </summary>
        private async Task<bool> EnsureSavedAsync()
        {
            if (ViewModel is not { } vm || !vm.IsDirty) return true;

            switch (await PromptUnsavedAsync(Path.GetFileName(vm.ImagePath ?? "this image"), vm.IsEditMode))
            {
                case UnsavedChoice.Save:
                    await vm.SaveInPlaceAsync();
                    // A failed save leaves the edits in place; carrying on would lose them.
                    return vm.ErrorMessage == null;
                case UnsavedChoice.Discard:
                    return true;
                default:
                    return false;
            }
        }

        private bool _closeConfirmed;

        /// <summary>
        /// Closing with an unsaved rotation asks first.
        ///
        /// The close has to be cancelled and re-issued afterwards, because the prompt is
        /// asynchronous and OnClosing cannot wait for an answer -- by the time the user
        /// picks one the window would already be gone.
        /// </summary>
        protected override async void OnClosing(WindowClosingEventArgs e)
        {
            if (!_closeConfirmed && ViewModel is { IsDirty: true })
            {
                e.Cancel = true;
                if (await EnsureSavedAsync())
                {
                    _closeConfirmed = true;
                    Close();
                }
                return;
            }

            _animationTimer?.Stop();
            base.OnClosing(e);
        }

        /// <summary>
        /// Clears out pasted scratch images left by earlier runs. Off the startup path
        /// on purpose -- it touches the disk, and nothing waits on the result.
        /// </summary>
        private static void SweepPasteScratch() => Task.Run(() =>
            ClipboardImage.SweepPasteFolder(ClipboardImage.PasteFolder, DateTime.UtcNow, TimeSpan.FromDays(7)));

        private async Task<UnsavedChoice> PromptUnsavedAsync(string fileName, bool edited = false)
        {
            var choice = UnsavedChoice.Cancel;

            var save = DialogButton("Save", isDefault: true);
            var discard = DialogButton("Discard");
            var cancel = DialogButton("Cancel");

            var dialog = Dialog(edited ? "Unsaved changes" : "Unsaved rotation", 420, 210, new StackPanel
            {
                Spacing = 18,
                Children =
                {
                    new TextBlock
                    {
                        Text = edited
                            ? $"{fileName} has edits that have not been saved."
                            : $"{fileName} has been rotated but not saved.",
                        FontSize = 15,
                        FontWeight = FontWeight.SemiBold,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = edited
                            ? "Saving writes them into the file."
                            : "Saving rewrites the file with the turn applied.",
                        [!TextBlock.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Atelier.TextSecondaryBrush"),
                        FontSize = 13,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancel, discard, save },
                    },
                },
            });

            save.Click += (_, _) => { choice = UnsavedChoice.Save; dialog.Close(); };
            discard.Click += (_, _) => { choice = UnsavedChoice.Discard; dialog.Close(); };
            cancel.Click += (_, _) => { choice = UnsavedChoice.Cancel; dialog.Close(); };
            dialog.KeyDown += (_, e) => { if (e.Key == Key.Escape) dialog.Close(); };

            await dialog.ShowDialog(this);
            return choice;
        }

        // ---- The rename prompt -----------------------------------------------------

        /// <summary>
        /// Asks for a new file name, or null if the user backed out. The extension is
        /// left out of the initial selection so typing replaces the name and keeps the
        /// ".jpg" -- renaming a photo almost never means changing its format.
        /// </summary>
        private async Task<string?> PromptForNameAsync(string currentName)
        {
            string? result = null;

            var box = GlassTextBox(currentName);

            var rename = DialogButton("Rename", isDefault: true);
            var cancel = DialogButton("Cancel");

            var dialog = Dialog("Rename", 460, 220, new StackPanel
            {
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = "New name",
                        [!TextBlock.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Atelier.TextSecondaryBrush"),
                        FontSize = 13,
                        FontWeight = FontWeight.SemiBold,
                    },
                    box,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 10,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancel, rename },
                    },
                },
            });

            void Accept()
            {
                result = box.Text;
                dialog.Close();
            }

            rename.Click += (_, _) => Accept();
            cancel.Click += (_, _) => dialog.Close();
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
                else if (e.Key == Key.Escape) { dialog.Close(); e.Handled = true; }
            };

            dialog.Opened += (_, _) =>
            {
                box.Focus();
                // Select the stem only, so the extension survives a straight retype.
                box.SelectionStart = 0;
                box.SelectionEnd = Path.GetFileNameWithoutExtension(currentName).Length;
            };

            await dialog.ShowDialog(this);
            return result;
        }

        // ---- Shared dialog chrome --------------------------------------------------

        /// <summary>A window on the same glass as the main one, following View &gt; Appearance.</summary>
        internal static Window Dialog(string title, int width, int height, Control content)
        {
            var dialog = new Window
            {
                Title = title,
                Width = width,
                Height = height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Atelier/Assets/atelier-icon.ico"))),
                ExtendClientAreaToDecorationsHint = true,
                ExtendClientAreaTitleBarHeightHint = -1,
                Content = GlassTheme.Host(new Panel
                {
                    Margin = new Thickness(28, 44, 28, 24),
                    Children = { content },
                }),
            };
            GlassTheme.Attach(dialog);
            return dialog;
        }

        internal static Button DialogButton(string text, bool isDefault = false)
        {
            var b = new Button
            {
                Classes = { "Modern" },
                Content = text,
                MinWidth = 88,
                Padding = new Thickness(16, 8),
                FontWeight = isDefault ? FontWeight.SemiBold : FontWeight.Medium,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                IsDefault = isDefault,
            };
            if (isDefault) b.Classes.Add("Accent");
            return b;
        }

        /// <summary>A text field on the glass: tinted, rounded, accent ring when focused.</summary>
        internal static TextBox GlassTextBox(string? text, double fontSize = 14) => new()
        {
            Text = text,
            FontSize = fontSize,
            Padding = new Thickness(10, 7),
            CornerRadius = new CornerRadius(7),
        };

        /// <summary>Binds a property to one of GlassTheme's brushes so it follows View &gt; Appearance.</summary>
        internal static T Themed<T>(T control, AvaloniaProperty property, string key) where T : Control
        {
            control[!property] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(key);
            return control;
        }

        // ---- Animation playback ----------------------------------------------------

        private DispatcherTimer? _animationTimer;

        /// <summary>
        /// Starts or stops the frame timer to match the view model.
        ///
        /// The interval is re-set on every tick rather than once: GIF frames each carry
        /// their own delay, and a single fixed interval turns a deliberately paced
        /// animation into a uniform flicker.
        /// </summary>
        private void SyncAnimation()
        {
            if (ViewModel is not { } vm || !vm.IsAnimated || !vm.IsPlaying)
            {
                _animationTimer?.Stop();
                return;
            }

            _animationTimer ??= BuildAnimationTimer();
            _animationTimer.Interval = vm.CurrentFrameDelay;
            _animationTimer.Start();
        }

        private DispatcherTimer BuildAnimationTimer()
        {
            var timer = new DispatcherTimer();
            timer.Tick += (_, _) =>
            {
                if (ViewModel is not { } vm || !vm.IsAnimated || !vm.IsPlaying)
                {
                    timer.Stop();
                    return;
                }

                vm.AdvanceFrame();
                timer.Interval = vm.CurrentFrameDelay;
            };
            return timer;
        }
    }
}
