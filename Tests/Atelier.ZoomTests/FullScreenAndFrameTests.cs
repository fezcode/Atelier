using Atelier.ViewModels;
using Atelier.Views;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
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
