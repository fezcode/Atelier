using Atelier.ViewModels;
using Atelier.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Xunit;
using Xunit.Abstractions;

namespace Atelier.ZoomTests;

/// <summary>
/// Regression cover for the wheel handling in <see cref="MainWindow"/>.
///
/// The bug these were written against: Ctrl+wheel-up zoomed in but Ctrl+wheel-down
/// scrolled the image down instead of zooming out. Root cause was routing, not math --
/// ScrollContentPresenter handles PointerWheelChanged while bubbling and marks it
/// handled whenever the offset actually changed, and MainWindow's class handler is
/// registered without handledEventsToo. Parked at offset 0 after FitToView(), wheel-up
/// could not move the offset (so it stayed unhandled and reached the zoom code) while
/// wheel-down could -- hence the asymmetry. The fix claims the wheel in the tunnel
/// phase on MainScroll.
/// </summary>
public class WheelZoomTests
{
    private readonly ITestOutputHelper _out;
    public WheelZoomTests(ITestOutputHelper output) => _out = output;

    private const double WinW = 800;
    private const double WinH = 600;

    /// <summary>
    /// Builds a window showing an image big enough that the ScrollViewer's extent
    /// exceeds its viewport in both axes -- i.e. the scroll viewer *can* scroll.
    /// </summary>
    private (MainWindow win, MainWindowViewModel vm, ScrollViewer scroll) Setup(
        int imgW = 1600, int imgH = 1200)
    {
        var vm = new MainWindowViewModel
        {
            // Collapse the chrome so the viewer gets nearly the whole window.
            ShowControls = false,
            ShowMetadata = false,
        };

        var win = new MainWindow { DataContext = vm, Width = WinW, Height = WinH };
        win.Show();

        vm.ImageSource = new WriteableBitmap(
            new PixelSize(imgW, imgH), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        vm.ImageWidth = imgW;
        vm.ImageHeight = imgH;
        vm.ZoomLevel = 1.0;

        Pump(win);

        var scroll = win.FindControl<ScrollViewer>("MainScroll")!;
        Assert.NotNull(scroll);
        return (win, vm, scroll);
    }

    private static void Pump(MainWindow win)
    {
        Dispatcher.UIThread.RunJobs();
        win.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private Point ViewerCentre(ScrollViewer scroll, MainWindow win)
    {
        var origin = scroll.TranslatePoint(new Point(0, 0), win) ?? new Point(0, 0);
        return origin + new Point(scroll.Viewport.Width / 2, scroll.Viewport.Height / 2);
    }

    // ---------------------------------------------------------------------
    // The reported bug: zoom IN works, zoom OUT does not -- the view scrolls
    // instead. Both are Ctrl+wheel over a scrollable (zoomed-in) image.
    // ---------------------------------------------------------------------

    [AvaloniaFact]
    public void CtrlWheelDown_ZoomsOut_WhenContentIsScrollable()
    {
        var (win, vm, scroll) = Setup();

        // Precondition: the content really is larger than the viewport, and we
        // are parked at the top -- exactly the state FitToView() leaves behind.
        _out.WriteLine($"extent={scroll.Extent} viewport={scroll.Viewport} offset={scroll.Offset}");
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height,
            $"test setup: expected scrollable content, extent={scroll.Extent} viewport={scroll.Viewport}");
        Assert.Equal(0, scroll.Offset.Y);

        double before = vm.ZoomLevel;
        win.MouseWheel(ViewerCentre(scroll, win), new Vector(0, -1), RawInputModifiers.Control);
        Pump(win);

        _out.WriteLine($"after wheel-down: zoom={vm.ZoomLevel} offset={scroll.Offset}");
        Assert.True(vm.ZoomLevel < before,
            $"Ctrl+wheel-down must zoom out. zoom stayed {vm.ZoomLevel}, " +
            $"scroll offset moved to {scroll.Offset} instead.");
    }

    [AvaloniaFact]
    public void CtrlWheelUp_ZoomsIn_WhenContentIsScrollable()
    {
        var (win, vm, scroll) = Setup();

        double before = vm.ZoomLevel;
        win.MouseWheel(ViewerCentre(scroll, win), new Vector(0, 1), RawInputModifiers.Control);
        Pump(win);

        Assert.True(vm.ZoomLevel > before,
            $"Ctrl+wheel-up must zoom in. zoom stayed {vm.ZoomLevel}.");
    }

    /// <summary>
    /// Zooming out repeatedly must keep working, including once the image has
    /// become smaller than the viewport (content centred, nothing to scroll).
    /// </summary>
    [AvaloniaFact]
    public void CtrlWheelDown_KeepsZoomingOut_AcrossTheFitBoundary()
    {
        var (win, vm, scroll) = Setup();

        double prev = vm.ZoomLevel;
        for (int i = 0; i < 12; i++)
        {
            win.MouseWheel(ViewerCentre(scroll, win), new Vector(0, -1), RawInputModifiers.Control);
            Pump(win);
            _out.WriteLine($"step {i}: zoom={vm.ZoomLevel:F4} extent={scroll.Extent} offset={scroll.Offset}");
            Assert.True(vm.ZoomLevel < prev,
                $"zoom-out stalled at step {i}: zoom stuck at {vm.ZoomLevel}");
            prev = vm.ZoomLevel;
        }
    }

    // ---------------------------------------------------------------------
    // The code-behind intends plain wheel to do nothing ("Disable regular
    // scroll as requested") -- but the ScrollViewer consumed it first.
    // ---------------------------------------------------------------------

    [AvaloniaFact]
    public void PlainWheel_DoesNotScroll()
    {
        var (win, vm, scroll) = Setup();

        var before = scroll.Offset;
        win.MouseWheel(ViewerCentre(scroll, win), new Vector(0, -1));
        Pump(win);

        Assert.Equal(before, scroll.Offset);
    }

    [AvaloniaFact]
    public void ShiftWheel_PansHorizontally()
    {
        var (win, vm, scroll) = Setup();

        var before = scroll.Offset;
        win.MouseWheel(ViewerCentre(scroll, win), new Vector(0, -1), RawInputModifiers.Shift);
        Pump(win);

        _out.WriteLine($"shift+wheel: {before} -> {scroll.Offset}");
        Assert.True(scroll.Offset.X > before.X,
            $"Shift+wheel-down must pan right. offset went {before} -> {scroll.Offset}");
        Assert.Equal(before.Y, scroll.Offset.Y);
    }

    // ---------------------------------------------------------------------
    // Zoom must be anchored under the cursor, not drift the image around.
    // ---------------------------------------------------------------------

    [AvaloniaFact]
    public void CtrlWheel_KeepsThePointUnderTheCursorStable()
    {
        var (win, vm, scroll) = Setup();

        // Scroll into the interior first. Anchoring is only *achievable* when the
        // offset the anchor demands lies inside the scrollable range -- see
        // CtrlWheel_AtTheTopLeftEdge_ClampsInsteadOfAnchoring for the boundary case.
        scroll.Offset = new Vector(300, 300);
        Pump(win);

        var centre = ViewerCentre(scroll, win);

        // The image pixel currently sitting under the cursor.
        var child = (Visual)scroll.Presenter!.Child!;
        var childPt = win.TranslatePoint(centre, child) ?? default;
        var imagePixelBefore = new Point(childPt.X / vm.ZoomLevel, childPt.Y / vm.ZoomLevel);

        win.MouseWheel(centre, new Vector(0, -1), RawInputModifiers.Control);
        Pump(win);

        var childPtAfter = win.TranslatePoint(centre, child) ?? default;
        var imagePixelAfter = new Point(childPtAfter.X / vm.ZoomLevel, childPtAfter.Y / vm.ZoomLevel);

        _out.WriteLine($"image pixel under cursor: {imagePixelBefore} -> {imagePixelAfter}");
        Assert.True(System.Math.Abs(imagePixelBefore.Y - imagePixelAfter.Y) < 2.0,
            $"image drifted vertically under the cursor: {imagePixelBefore.Y} -> {imagePixelAfter.Y}");
        Assert.True(System.Math.Abs(imagePixelBefore.X - imagePixelAfter.X) < 2.0,
            $"image drifted horizontally under the cursor: {imagePixelBefore.X} -> {imagePixelAfter.X}");
    }

    /// <summary>
    /// Documents the boundary: parked at offset (0,0), zooming out about the viewport
    /// centre would require a negative offset, so the ScrollViewer clamps to the corner.
    /// The zoom itself must still happen -- that is the part that was broken.
    /// </summary>
    [AvaloniaFact]
    public void CtrlWheel_AtTheTopLeftEdge_ClampsInsteadOfAnchoring()
    {
        var (win, vm, scroll) = Setup();
        Assert.Equal(new Vector(0, 0), scroll.Offset);

        win.MouseWheel(ViewerCentre(scroll, win), new Vector(0, -1), RawInputModifiers.Control);
        Pump(win);

        Assert.True(vm.ZoomLevel < 1.0, "zoom must still apply at the edge");
        Assert.Equal(new Vector(0, 0), scroll.Offset);
    }
}
