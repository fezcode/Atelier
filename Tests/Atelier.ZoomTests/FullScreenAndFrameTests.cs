using Atelier.ViewModels;
using Atelier.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Xunit;

namespace Atelier.ZoomTests;

/// <summary>
/// Cover for fullscreen (F / Esc, image only) and picture frame mode (image plus the
/// system caption buttons, with an overlaid exit button).
///
/// The bug fullscreen tests were written against: ToggleFullScreen() only changed
/// WindowState, so the menu bar, bottom bar and metadata pane all stayed on screen.
/// </summary>
public class FullScreenAndFrameTests
{
    private static (MainWindow win, MainWindowViewModel vm) Show()
    {
        var vm = new MainWindowViewModel();
        var win = new MainWindow { DataContext = vm, Width = 900, Height = 600 };
        win.Show();
        Pump(win);
        return (win, vm);
    }

    private static void Pump(MainWindow win)
    {
        Dispatcher.UIThread.RunJobs();
        win.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Loads an image bigger than the viewport so the ScrollViewer has room to pan
    /// and the zoom level is meaningful.
    /// </summary>
    private static (MainWindow win, MainWindowViewModel vm, ScrollViewer scroll) ShowWithImage()
    {
        var (win, vm) = Show();
        vm.ImageSource = new WriteableBitmap(
            new PixelSize(1600, 1200), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        vm.ImageWidth = 1600;
        vm.ImageHeight = 1200;
        vm.ZoomLevel = 1.0;
        Pump(win);
        return (win, vm, win.FindControl<ScrollViewer>("MainScroll")!);
    }

    private static void AssertChromeHidden(MainWindow win)
    {
        Assert.False(win.FindControl<Border>("TopBar")!.IsVisible, "menu bar must be hidden");
        Assert.False(win.FindControl<Border>("BottomBar")!.IsVisible, "bottom bar must be hidden");
        Assert.False(win.FindControl<Border>("RightPane")!.IsVisible, "metadata pane must be hidden");
    }

    private static void AssertChromeVisible(MainWindow win)
    {
        Assert.True(win.FindControl<Border>("TopBar")!.IsVisible);
        Assert.True(win.FindControl<Border>("BottomBar")!.IsVisible);
        Assert.True(win.FindControl<Border>("RightPane")!.IsVisible);
    }

    [AvaloniaFact]
    public void FKey_EntersFullScreen_AndHidesAllChrome()
    {
        var (win, vm) = Show();
        AssertChromeVisible(win);

        win.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.None);
        Pump(win);

        Assert.Equal(WindowState.FullScreen, win.WindowState);
        Assert.True(vm.IsFullScreen);
        AssertChromeHidden(win);
    }

    [AvaloniaFact]
    public void FKey_TogglesBackOut_RestoringChrome()
    {
        var (win, vm) = Show();

        win.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.None);
        Pump(win);
        win.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.None);
        Pump(win);

        Assert.NotEqual(WindowState.FullScreen, win.WindowState);
        Assert.False(vm.IsFullScreen);
        AssertChromeVisible(win);
    }

    [AvaloniaFact]
    public void Escape_ExitsFullScreen()
    {
        var (win, vm) = Show();

        win.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.None);
        Pump(win);
        Assert.True(vm.IsFullScreen);

        win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Pump(win);

        Assert.NotEqual(WindowState.FullScreen, win.WindowState);
        Assert.False(vm.IsFullScreen);
        AssertChromeVisible(win);
    }

    /// <summary>Fullscreen entered from maximized must restore to maximized, not normal.</summary>
    [AvaloniaFact]
    public void FullScreen_RestoresThePreviousWindowState()
    {
        var (win, vm) = Show();
        win.WindowState = WindowState.Maximized;
        Pump(win);

        win.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.None);
        Pump(win);
        win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Pump(win);

        Assert.Equal(WindowState.Maximized, win.WindowState);
        Assert.False(vm.IsFullScreen);
    }

    [AvaloniaFact]
    public void FrameMode_ShowsOnlyTheImage_AndTheExitButton()
    {
        var (win, vm) = Show();

        win.FrameMode_Click(null, new RoutedEventArgs());
        Pump(win);

        Assert.True(vm.IsFrameMode);
        AssertChromeHidden(win);
        Assert.True(win.FindControl<Button>("ExitFrameButton")!.IsVisible,
            "exit frame button must be shown in frame mode");
        // Frame mode stays windowed: the system caption buttons come from the
        // extended client area, which fullscreen would remove.
        Assert.NotEqual(WindowState.FullScreen, win.WindowState);
    }

    /// <summary>
    /// Drives the real overlay button, so a broken Click binding or a button that
    /// never made it into the tree fails here.
    /// </summary>
    [AvaloniaFact]
    public void ExitFrameButton_LeavesFrameMode()
    {
        var (win, vm) = Show();
        win.FrameMode_Click(null, new RoutedEventArgs());
        Pump(win);

        var exit = win.FindControl<Button>("ExitFrameButton");
        Assert.NotNull(exit);
        exit!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(win);

        Assert.False(vm.IsFrameMode);
        AssertChromeVisible(win);
        Assert.False(exit.IsVisible);
    }

    [AvaloniaFact]
    public void Escape_ExitsFrameMode()
    {
        var (win, vm) = Show();
        win.FrameMode_Click(null, new RoutedEventArgs());
        Pump(win);

        win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Pump(win);

        Assert.False(vm.IsFrameMode);
        AssertChromeVisible(win);
    }

    [AvaloniaFact]
    public void FrameMode_KeepsTheWindowAlwaysOnTop()
    {
        var (win, vm) = Show();
        Assert.False(win.Topmost);

        win.FrameMode_Click(null, new RoutedEventArgs());
        Pump(win);
        Assert.True(win.Topmost, "a picture frame must stay above other windows");

        win.ExitFrame_Click(null, new RoutedEventArgs());
        Pump(win);
        Assert.False(win.Topmost, "leaving frame mode must drop always-on-top");
    }

    /// <summary>
    /// The view hides the exit button on a 5s timer by clearing this flag; the flag
    /// must actually hide the button, and re-entering frame mode must reset it.
    /// </summary>
    [AvaloniaFact]
    public void ExitFrameHint_HidesTheButton_AndResetsOnReentry()
    {
        var (win, vm) = Show();
        win.FrameMode_Click(null, new RoutedEventArgs());
        Pump(win);

        var exit = win.FindControl<Button>("ExitFrameButton")!;
        Assert.True(exit.IsVisible);

        vm.ExitFrameHintVisible = false;   // what the auto-hide timer does
        Pump(win);
        Assert.False(exit.IsVisible, "clearing the hint must hide the button");
        Assert.True(vm.IsFrameMode, "hiding the hint must not leave frame mode");

        win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Pump(win);
        Assert.False(vm.IsFrameMode, "Esc must still exit with the button hidden");

        win.FrameMode_Click(null, new RoutedEventArgs());
        Pump(win);
        Assert.True(exit.IsVisible, "re-entering frame mode must show the hint again");
    }

    [AvaloniaFact]
    public void BareWheel_Zooms_OnlyInFrameMode()
    {
        var (win, vm, scroll) = ShowWithImage();
        var centre = new Point(win.Width / 2, win.Height / 2);

        win.MouseWheel(centre, new Vector(0, 1));
        Pump(win);
        Assert.Equal(1.0, vm.ZoomLevel, 3);   // inert outside frame mode

        win.FrameMode_Click(null, new RoutedEventArgs());
        Pump(win);
        vm.ZoomLevel = 1.0;
        Pump(win);

        win.MouseWheel(centre, new Vector(0, 1));
        Pump(win);
        Assert.True(vm.ZoomLevel > 1.0, "wheel up must zoom in while framed");

        var zoomedIn = vm.ZoomLevel;
        win.MouseWheel(centre, new Vector(0, -1));
        Pump(win);
        Assert.True(vm.ZoomLevel < zoomedIn, "wheel down must zoom back out");
    }

    /// <summary>
    /// Left-drag pans the image everywhere except the top strip, which stands in for
    /// the hidden title bar and moves the window instead (a no-op headless, so the
    /// assertion is that the image did NOT pan).
    /// </summary>
    [AvaloniaFact]
    public void FrameMode_TopStripMovesTheWindow_LowerAreaPansTheImage()
    {
        var (win, vm, scroll) = ShowWithImage();
        win.FrameMode_Click(null, new RoutedEventArgs());
        Pump(win);
        vm.ZoomLevel = 1.0;   // frame-mode FitToView shrank it; restore pannable extent
        Pump(win);

        var before = scroll.Offset;
        win.MouseDown(new Point(450, 20), MouseButton.Left);
        win.MouseMove(new Point(400, 60));
        win.MouseUp(new Point(400, 60), MouseButton.Left);
        Pump(win);
        Assert.Equal(before, scroll.Offset);

        win.MouseDown(new Point(450, 300), MouseButton.Left);
        win.MouseMove(new Point(400, 250));
        win.MouseUp(new Point(400, 250), MouseButton.Left);
        Pump(win);
        Assert.NotEqual(before, scroll.Offset);
    }

    /// <summary>
    /// F on top of frame mode goes fullscreen; Esc then unwinds one layer at a time,
    /// and the exit button hides while fullscreen covers it.
    /// </summary>
    [AvaloniaFact]
    public void FullScreen_LayersOverFrameMode_AndEscapeUnwindsInOrder()
    {
        var (win, vm) = Show();
        win.FrameMode_Click(null, new RoutedEventArgs());
        Pump(win);

        win.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.None);
        Pump(win);
        Assert.True(vm.IsFullScreen);
        Assert.True(vm.IsFrameMode);
        Assert.False(win.FindControl<Button>("ExitFrameButton")!.IsVisible,
            "the exit frame button is misleading under fullscreen, where Esc exits fullscreen");

        win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Pump(win);
        Assert.False(vm.IsFullScreen);
        Assert.True(vm.IsFrameMode, "first Esc only leaves fullscreen");
        AssertChromeHidden(win);

        win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Pump(win);
        Assert.False(vm.IsFrameMode);
        AssertChromeVisible(win);
    }
}
