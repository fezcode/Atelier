using System;
using System.IO;
using System.Threading.Tasks;
using Atelier.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
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

        public void Copy_Click(object? sender, RoutedEventArgs e) => ViewModel?.CopyToClipboard();

        public async void Paste_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel is not { } vm) return;
            if (!await EnsureSavedAsync()) return;

            if (await vm.PasteFromClipboardAsync())
                Dispatcher.UIThread.Post(FitToView, DispatcherPriority.Loaded);
        }

        // ---- Delete and rename -----------------------------------------------------

        public async void Delete_Click(object? sender, RoutedEventArgs e)
        {
            if (ViewModel is not { } vm || !vm.HasImage) return;

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

            switch (await PromptUnsavedAsync(Path.GetFileName(vm.ImagePath ?? "this image")))
            {
                case UnsavedChoice.Save:
                    await vm.SaveInPlaceAsync();
                    return true;
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

        private async Task<UnsavedChoice> PromptUnsavedAsync(string fileName)
        {
            var choice = UnsavedChoice.Cancel;

            var save = DialogButton("Save", isDefault: true);
            var discard = DialogButton("Discard");
            var cancel = DialogButton("Cancel");

            var dialog = Dialog("Unsaved rotation", 420, 210, new StackPanel
            {
                Spacing = 18,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"{fileName} has been rotated but not saved.",
                        Foreground = Brushes.White,
                        FontSize = 15,
                        FontWeight = FontWeight.SemiBold,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = "Saving rewrites the file with the turn applied.",
                        Foreground = new SolidColorBrush(Color.Parse("#AAAAAA")),
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

            var box = new TextBox
            {
                Text = currentName,
                FontSize = 14,
                Background = new SolidColorBrush(Color.Parse("#151515")),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.Parse("#333333")),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 8),
                CornerRadius = new CornerRadius(4),
            };

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
                        Foreground = new SolidColorBrush(Color.Parse("#AAAAAA")),
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

        /// <summary>A window in the same dark, mica-backed style as About.</summary>
        private static Window Dialog(string title, int width, int height, Control content) => new()
        {
            Title = title,
            Width = width,
            Height = height,
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
                Child = new Panel
                {
                    Margin = new Thickness(28, 48, 28, 24),
                    Children = { content },
                },
            },
        };

        private static Button DialogButton(string text, bool isDefault = false) => new()
        {
            Content = text,
            MinWidth = 88,
            Padding = new Thickness(14, 8),
            FontWeight = isDefault ? FontWeight.Bold : FontWeight.Medium,
            Foreground = isDefault ? Brushes.White : new SolidColorBrush(Color.Parse("#DDDDDD")),
            Background = isDefault
                ? new SolidColorBrush(Color.Parse("#1E6FD9"))
                : new SolidColorBrush(Color.Parse("#1C1C1C")),
            BorderBrush = new SolidColorBrush(Color.Parse("#333333")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsDefault = isDefault,
        };

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
