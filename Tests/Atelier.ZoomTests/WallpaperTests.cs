using System;
using System.IO;
using System.Linq;
using Atelier;
using Atelier.ViewModels;
using Atelier.Views;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ImageMagick;
using Xunit;
using Xunit.Abstractions;
using Fit = Atelier.WallpaperHelper.WallpaperFit;

namespace Atelier.ZoomTests;

/// <summary>
/// Cover for Set as Wallpaper.
///
/// <c>WallpaperHelper.Apply</c> is deliberately not exercised: it would change the
/// machine's actual desktop background. Everything worth testing lives in
/// <c>PrepareImage</c> and <c>StyleFor</c>, which touch only the filesystem. Each test
/// passes its own output directory so pruning cannot delete a wallpaper the user has set.
/// </summary>
public class WallpaperTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir;      // fixtures
    private readonly string _outDir;   // conversion target

    public WallpaperTests(ITestOutputHelper output)
    {
        _out = output;
        var root = Path.Combine(Path.GetTempPath(), "atelier-wall-" + Guid.NewGuid().ToString("N"));
        _dir = Path.Combine(root, "src");
        _outDir = Path.Combine(root, "out");
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_outDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_dir)!, recursive: true); } catch { }
    }

    private string Fixture(string name, MagickFormat format, uint w = 640, uint h = 480)
    {
        var path = Path.Combine(_dir, name);
        using var img = new MagickImage(MagickColors.CornflowerBlue, w, h);
        img.Write(path, format);
        return path;
    }

    // ---------------------------------------------------------------------
    // Fit -> the two HKCU\Control Panel\Desktop values the shell reads.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(Fit.Fill, "10", "0")]
    [InlineData(Fit.Fit, "6", "0")]
    [InlineData(Fit.Stretch, "2", "0")]
    [InlineData(Fit.Center, "0", "0")]
    [InlineData(Fit.Tile, "0", "1")]
    [InlineData(Fit.Span, "22", "0")]
    public void StyleFor_MapsEveryFit(Fit fit, string style, string tile)
    {
        var (gotStyle, gotTile) = WallpaperHelper.StyleFor(fit);
        Assert.Equal(style, gotStyle);
        Assert.Equal(tile, gotTile);
    }

    /// <summary>Tile is the only mode distinguished by TileWallpaper rather than the style.</summary>
    [Fact]
    public void StyleFor_OnlyTileSetsTheTileFlag()
    {
        foreach (Fit fit in Enum.GetValues<Fit>())
        {
            var (_, tile) = WallpaperHelper.StyleFor(fit);
            Assert.Equal(fit == Fit.Tile ? "1" : "0", tile);
        }
    }

    // ---------------------------------------------------------------------
    // Formats Windows accepts are handed over untouched.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("shot.png", MagickFormat.Png)]
    [InlineData("shot.jpg", MagickFormat.Jpeg)]
    [InlineData("shot.jpeg", MagickFormat.Jpeg)]
    [InlineData("shot.bmp", MagickFormat.Bmp)]
    public void PrepareImage_PassesThroughNativeFormats(string name, MagickFormat format)
    {
        var src = Fixture(name, format);

        var result = WallpaperHelper.PrepareImage(src, 1920, 1080, _outDir);

        Assert.Equal(src, result);
        Assert.Empty(Directory.GetFiles(_outDir));   // nothing copied or converted
    }

    /// <summary>Uppercase must pass through too -- the tr-TR dotless-i trap.</summary>
    [Fact]
    public void PrepareImage_PassesThroughUppercaseExtensions()
    {
        var src = Fixture("SHOUTY.PNG", MagickFormat.Png);

        var result = WallpaperHelper.PrepareImage(src, 1920, 1080, _outDir);

        Assert.Equal(src, result);
        Assert.Empty(Directory.GetFiles(_outDir));
    }

    // ---------------------------------------------------------------------
    // Everything else is converted, because Windows will not read it.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("photo.avif", MagickFormat.Avif)]
    [InlineData("photo.webp", MagickFormat.WebP)]
    [InlineData("photo.tif", MagickFormat.Tiff)]
    public void PrepareImage_ConvertsUnsupportedFormatsToPng(string name, MagickFormat format)
    {
        var src = Fixture(name, format);

        var result = WallpaperHelper.PrepareImage(src, 1920, 1080, _outDir);

        Assert.NotEqual(src, result);
        Assert.EndsWith(".png", result, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(result));

        using var written = new MagickImage(result);
        _out.WriteLine($"{name} -> {Path.GetFileName(result)} {written.Width}x{written.Height} {written.Format}");
        Assert.Equal(MagickFormat.Png, written.Format);
        Assert.Equal(640u, written.Width);
        Assert.Equal(480u, written.Height);
    }

    /// <summary>
    /// An SVG read at its nominal size looks soft once Windows scales it to the
    /// desktop, so it must rasterise at the size asked for.
    /// </summary>
    [Fact]
    public void PrepareImage_RasterisesSvgAtScreenSize()
    {
        var src = Path.Combine(_dir, "vector.svg");
        File.WriteAllText(src,
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"64\" height=\"48\">" +
            "<rect width=\"64\" height=\"48\" fill=\"cornflowerblue\"/></svg>");

        var result = WallpaperHelper.PrepareImage(src, 1920, 1440, _outDir);

        using var written = new MagickImage(result);
        _out.WriteLine($"svg -> {written.Width}x{written.Height}");
        Assert.True(written.Width > 64, $"expected the SVG to rasterise larger than its nominal 64px, got {written.Width}");
        Assert.Equal(1920u, written.Width);
    }

    /// <summary>An icon's first frame is often 16x16; the largest one is the usable image.</summary>
    [Fact]
    public void PrepareImage_TakesTheLargestFrameOfAMultiFrameFile()
    {
        var src = Path.Combine(_dir, "multi.ico");
        using (var frames = new MagickImageCollection())
        {
            frames.Add(new MagickImage(MagickColors.Red, 16, 16));
            frames.Add(new MagickImage(MagickColors.Green, 256, 256));
            frames.Add(new MagickImage(MagickColors.Blue, 32, 32));
            frames.Write(src, MagickFormat.Ico);
        }

        var result = WallpaperHelper.PrepareImage(src, 1920, 1080, _outDir);

        using var written = new MagickImage(result);
        _out.WriteLine($"ico -> {written.Width}x{written.Height}");
        Assert.Equal(256u, written.Width);
    }

    // ---------------------------------------------------------------------
    // Cache behaviour.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Windows caches the desktop background by path, so reusing one filename can
    /// leave the desktop showing the previous image.
    /// </summary>
    [Fact]
    public void PrepareImage_UsesAFreshFileNameEachTime()
    {
        var src = Fixture("photo.avif", MagickFormat.Avif);

        var first = WallpaperHelper.PrepareImage(src, 1920, 1080, _outDir);
        var second = WallpaperHelper.PrepareImage(src, 1920, 1080, _outDir);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void PrepareImage_PrunesEarlierConversions()
    {
        var src = Fixture("photo.avif", MagickFormat.Avif);

        var first = WallpaperHelper.PrepareImage(src, 1920, 1080, _outDir);
        Assert.True(File.Exists(first));

        var second = WallpaperHelper.PrepareImage(src, 1920, 1080, _outDir);

        Assert.False(File.Exists(first), "the previous conversion should have been pruned");
        Assert.True(File.Exists(second));
        Assert.Single(Directory.GetFiles(_outDir, "wallpaper-*.png"));
    }

    // ---------------------------------------------------------------------
    // Failures.
    // ---------------------------------------------------------------------

    [Fact]
    public void PrepareImage_ThrowsWhenTheFileIsGone()
    {
        var missing = Path.Combine(_dir, "not-here.png");
        Assert.Throws<FileNotFoundException>(() => WallpaperHelper.PrepareImage(missing, 1920, 1080, _outDir));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void PrepareImage_ThrowsOnAnEmptyPath(string path)
    {
        Assert.Throws<ArgumentException>(() => WallpaperHelper.PrepareImage(path, 1920, 1080, _outDir));
    }

    // ---------------------------------------------------------------------
    // Menu gating.
    // ---------------------------------------------------------------------

    [Fact]
    public void CanSetWallpaper_RequiresAnImage()
    {
        var vm = new MainWindowViewModel();
        Assert.False(vm.CanSetWallpaper);

        vm.ImagePath = @"C:\pictures\photo.png";
        Assert.True(vm.CanSetWallpaper);
    }

    /// <summary>
    /// Set as Wallpaper reads the file on disk, so it is disabled while editing rather
    /// than silently ignoring the adjustments on screen.
    /// </summary>
    [Fact]
    public void CanSetWallpaper_IsFalseInEditMode()
    {
        var vm = new MainWindowViewModel { ImagePath = @"C:\pictures\photo.png" };
        Assert.True(vm.CanSetWallpaper);

        var raised = false;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.CanSetWallpaper)) raised = true; };

        vm.IsEditMode = true;

        Assert.False(vm.CanSetWallpaper);
        Assert.True(raised, "without a notification the menu item would stay enabled");
    }

    /// <summary>
    /// Regression: IsEditMode never raised IsRightPaneVisible, so closing the metadata
    /// pane and then entering Edit Mode left the editor invisible.
    /// </summary>
    [AvaloniaFact]
    public void EnteringEditMode_ShowsThePane_EvenWhenMetadataWasClosed()
    {
        var vm = new MainWindowViewModel();
        var win = new MainWindow { DataContext = vm, Width = 900, Height = 600 };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        vm.ShowMetadata = false;                    // as the pane's X button does
        Dispatcher.UIThread.RunJobs();
        win.UpdateLayout();
        Assert.False(vm.IsRightPaneVisible);

        vm.IsEditMode = true;
        Dispatcher.UIThread.RunJobs();
        win.UpdateLayout();

        Assert.True(vm.IsRightPaneVisible);
        Assert.True(win.FindControl<Border>("RightPane")!.IsVisible,
            "the editor pane must actually appear, not just compute as visible");
    }
}
