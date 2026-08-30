using Atelier.ViewModels;
using Xunit;

namespace Atelier.ZoomTests;

/// <summary>
/// Gating for Edit &gt; Open with Paint. The item hands the file on disk to mspaint,
/// so it must only light up for formats Paint can actually decode.
/// </summary>
public class OpenInPaintTests
{
    [Fact]
    public void CanOpenInPaint_RequiresAnImage()
    {
        var vm = new MainWindowViewModel();
        Assert.False(vm.CanOpenInPaint);

        vm.ImagePath = @"C:\pictures\photo.png";
        Assert.True(vm.CanOpenInPaint);
    }

    [Theory]
    [InlineData(@"C:\pictures\photo.png")]
    [InlineData(@"C:\pictures\photo.jpg")]
    [InlineData(@"C:\pictures\photo.JPEG")]   // the extension check is case insensitive
    [InlineData(@"C:\pictures\photo.bmp")]
    [InlineData(@"C:\pictures\photo.gif")]
    [InlineData(@"C:\pictures\photo.tif")]
    [InlineData(@"C:\pictures\photo.ico")]
    [InlineData(@"C:\pictures\photo.webp")]
    public void CanOpenInPaint_IsTrueForFormatsPaintReads(string path)
    {
        Assert.True(new MainWindowViewModel { ImagePath = path }.CanOpenInPaint);
    }

    /// <summary>
    /// SVG has no Paint support at all; HEIC/HEIF only decode with the optional Store
    /// extension installed, so both would open Paint onto an error.
    /// </summary>
    [Theory]
    [InlineData(@"C:\pictures\logo.svg")]
    [InlineData(@"C:\pictures\photo.heic")]
    [InlineData(@"C:\pictures\photo.heif")]
    [InlineData(@"C:\pictures\notes")]        // no extension at all
    public void CanOpenInPaint_IsFalseForFormatsPaintCannotRead(string path)
    {
        Assert.False(new MainWindowViewModel { ImagePath = path }.CanOpenInPaint);
    }

    /// <summary>
    /// Paint reads the file on disk, so -- like Set as Wallpaper -- it is disabled while
    /// editing rather than silently missing the adjustments on screen.
    /// </summary>
    [Fact]
    public void CanOpenInPaint_IsFalseInEditMode()
    {
        var vm = new MainWindowViewModel { ImagePath = @"C:\pictures\photo.png" };
        Assert.True(vm.CanOpenInPaint);

        var raised = false;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.CanOpenInPaint)) raised = true; };

        vm.IsEditMode = true;

        Assert.False(vm.CanOpenInPaint);
        Assert.True(raised, "without a notification the menu item would stay enabled");
    }
}
