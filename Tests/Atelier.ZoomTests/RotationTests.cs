using System;
using System.IO;
using System.Threading.Tasks;
using Atelier;
using Atelier.ViewModels;
using Avalonia.Headless.XUnit;
using ImageMagick;
using Xunit;
using Xunit.Abstractions;

namespace Atelier.ZoomTests;

/// <summary>
/// Rotation and flipping, from the EXIF tag on disk through to what the view draws
/// and what a save writes back.
///
/// The case that motivated all of it: a phone photo stores a landscape frame plus an
/// orientation tag saying "really a portrait". Avalonia's decoder ignores that tag, so
/// before this Atelier showed those photos on their side.
/// </summary>
public class RotationTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir;

    public RotationTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), "atelier-rotate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// A 64x48 JPEG carrying an explicit EXIF orientation tag.
    ///
    /// Both the profile and the image attribute have to be set, and that is not
    /// belt-and-braces -- neither alone produces a readable tag. With only the
    /// attribute, Magick writes no EXIF profile at all and the tag vanishes. With only
    /// the profile, Magick overwrites the value from the image attribute on write, and
    /// since that is Undefined the file ends up saying orientation 0. (Which is itself
    /// worth knowing: files in the wild really do carry a 0 there, and Atelier reads
    /// that as "no information" rather than treating it as an error.)
    /// </summary>
    private string Fixture(string name, ushort? orientation)
    {
        var path = Path.Combine(_dir, name);
        using var img = new MagickImage(MagickColors.CornflowerBlue, 64, 48);
        if (orientation is ushort tag)
        {
            var profile = new ExifProfile();
            profile.SetValue(ExifTag.Orientation, tag);
            img.SetProfile(profile);
            img.Orientation = (OrientationType)tag;
        }
        img.Write(path, MagickFormat.Jpeg);
        return path;
    }

    // ---- Reading the tag on load -----------------------------------------

    /// <summary>
    /// The portrait phone photo. The stored frame stays 64x48 -- proving Avalonia's
    /// decoder does not apply the tag itself, so Atelier must -- while the displayed
    /// size comes back swapped.
    /// </summary>
    [AvaloniaFact]
    public async Task Load_AppliesTheExifOrientation()
    {
        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(Fixture("portrait.jpg", 6));

        Assert.Null(vm.ErrorMessage);
        Assert.Equal(new ImageOrientation(90, false), vm.Orientation);

        _out.WriteLine($"stored {vm.ImageWidth}x{vm.ImageHeight}, shown {vm.DisplayWidth}x{vm.DisplayHeight}");
        Assert.Equal(64, vm.ImageWidth);
        Assert.Equal(48, vm.ImageHeight);
        Assert.Equal(48, vm.DisplayWidth);
        Assert.Equal(64, vm.DisplayHeight);
    }

    [AvaloniaFact]
    public async Task Load_WithoutATag_LeavesTheImageUpright()
    {
        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(Fixture("plain.jpg", null));

        Assert.Equal(ImageOrientation.Identity, vm.Orientation);
        Assert.Equal(64, vm.DisplayWidth);
        Assert.Equal(48, vm.DisplayHeight);
    }

    /// <summary>An orientation that came from the file is not an unsaved edit.</summary>
    [AvaloniaFact]
    public async Task Load_WithATag_IsNotDirty()
    {
        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(Fixture("portrait.jpg", 6));

        Assert.False(vm.IsDirty);
    }

    /// <summary>Moving to another picture must not carry the previous one's rotation over.</summary>
    [AvaloniaFact]
    public async Task Load_ResetsOrientationBetweenImages()
    {
        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(Fixture("a.jpg", 6));
        Assert.Equal(90, vm.Orientation.Angle);

        await vm.LoadImageAsync(Fixture("b.jpg", null));
        Assert.Equal(ImageOrientation.Identity, vm.Orientation);
    }

    // ---- Dirty tracking ---------------------------------------------------

    [AvaloniaFact]
    public async Task Rotating_MarksTheImageDirty()
    {
        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(Fixture("plain.jpg", null));

        vm.RotateRight();

        Assert.True(vm.IsDirty);
        Assert.Equal(90, vm.Orientation.Angle);
    }

    /// <summary>
    /// Dirty means "differs from the file", not "was touched". Four rotations land
    /// back where the file already is, so there is nothing left to save.
    /// </summary>
    [AvaloniaFact]
    public async Task RotatingFullCircle_IsCleanAgain()
    {
        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(Fixture("plain.jpg", null));

        vm.RotateRight();
        vm.RotateRight();
        vm.RotateRight();
        vm.RotateRight();

        Assert.False(vm.IsDirty);
    }

    /// <summary>Likewise for a photo whose file already says 90: rotating back to 90 is clean.</summary>
    [AvaloniaFact]
    public async Task ReturningToTheStoredOrientation_IsCleanAgain()
    {
        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(Fixture("portrait.jpg", 6));

        vm.RotateRight();
        Assert.True(vm.IsDirty);

        vm.RotateLeft();
        Assert.False(vm.IsDirty);
    }

    [AvaloniaFact]
    public async Task Flipping_MarksTheImageDirty()
    {
        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(Fixture("plain.jpg", null));

        vm.FlipHorizontal();

        Assert.True(vm.IsDirty);
        Assert.True(vm.Orientation.Mirrored);
    }

    // ---- Saving -----------------------------------------------------------

    /// <summary>
    /// The double-rotation trap. Saving has to bake the turn into the pixels *and*
    /// clear the orientation tag: leave the tag at 6 on a file whose pixels are now
    /// upright and the next open turns it another 90 degrees.
    /// </summary>
    [AvaloniaFact]
    public async Task Save_BakesTheRotationAndClearsTheTag()
    {
        var source = Fixture("portrait.jpg", 6);
        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(source);

        var target = Path.Combine(_dir, "saved.jpg");
        await vm.SaveImageAsync(target);

        using (var written = new MagickImage(target))
        {
            _out.WriteLine($"written {written.Width}x{written.Height} orientation={written.Orientation}");
            // The stored frame is now portrait -- the turn is in the pixels.
            Assert.Equal(48u, written.Width);
            Assert.Equal(64u, written.Height);
            Assert.True(written.Orientation is OrientationType.TopLeft or OrientationType.Undefined);
        }

        // And re-opening it must not turn it again.
        var reopened = new MainWindowViewModel();
        await reopened.LoadImageAsync(target);
        Assert.Equal(ImageOrientation.Identity, reopened.Orientation);
        Assert.Equal(48, reopened.DisplayWidth);
        Assert.Equal(64, reopened.DisplayHeight);
    }

    /// <summary>A rotation the user applied by hand has to reach the file too.</summary>
    [AvaloniaFact]
    public async Task Save_IncludesAUserRotation()
    {
        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(Fixture("plain.jpg", null));

        vm.RotateRight();
        var target = Path.Combine(_dir, "turned.jpg");
        await vm.SaveImageAsync(target);

        using var written = new MagickImage(target);
        Assert.Equal(48u, written.Width);
        Assert.Equal(64u, written.Height);
    }

    /// <summary>Saving in place is what Ctrl+S does once something has been rotated.</summary>
    [AvaloniaFact]
    public async Task SaveInPlace_RewritesTheFileAndGoesClean()
    {
        var source = Fixture("plain.jpg", null);
        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(source);

        vm.RotateRight();
        await vm.SaveInPlaceAsync();

        Assert.False(vm.IsDirty);
        using var written = new MagickImage(source);
        Assert.Equal(48u, written.Width);
        Assert.Equal(64u, written.Height);
    }

    /// <summary>An unmirrored, unrotated image should come out of a save untouched in shape.</summary>
    [AvaloniaFact]
    public async Task Save_LeavesAnUprightImageAlone()
    {
        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(Fixture("plain.jpg", null));

        var target = Path.Combine(_dir, "copy.png");
        await vm.SaveImageAsync(target);

        using var written = new MagickImage(target);
        Assert.Equal(64u, written.Width);
        Assert.Equal(48u, written.Height);
    }

    /// <summary>A mirrored save has to flop the pixels, not just record the intent.</summary>
    [AvaloniaFact]
    public async Task Save_BakesAMirror()
    {
        // Left half black, right half white, so a flop is visible in a single pixel.
        var path = Path.Combine(_dir, "halves.png");
        using (var img = new MagickImage(MagickColors.Black, 64, 48))
        {
            using var white = new MagickImage(MagickColors.White, 32, 48);
            img.Composite(white, 32, 0);
            img.Write(path, MagickFormat.Png);
        }

        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(path);
        vm.FlipHorizontal();

        var target = Path.Combine(_dir, "flopped.png");
        await vm.SaveImageAsync(target);

        using var written = new MagickImage(target);
        using var pixels = written.GetPixels();
        // What was black on the left is now white.
        var left = pixels.GetPixel(4, 24).ToColor()!;
        _out.WriteLine($"left pixel after flop: {left}");
        Assert.True(left.R > 32000, $"expected the left edge to be white after a flop, got {left}");
    }
}
