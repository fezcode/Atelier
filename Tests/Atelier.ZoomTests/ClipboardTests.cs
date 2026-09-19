using System;
using System.IO;
using System.Linq;
using Atelier;
using Avalonia.Headless.XUnit;
using ImageMagick;
using Xunit;
using Xunit.Abstractions;

namespace Atelier.ZoomTests;

/// <summary>
/// The parts of copy and paste that can be pinned down without touching the real
/// clipboard -- the DIB conversions either side of it, and the scratch folder a pasted
/// image lands in.
///
/// Split the same way <c>WallpaperHelper</c> is: everything with a decision in it is
/// here, and the Win32 calls that actually mutate the clipboard stay as thin as they
/// can be. A test run has no business overwriting whatever the developer had copied.
/// </summary>
public class ClipboardTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir;

    public ClipboardTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), "atelier-clip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private byte[] Bmp(uint w = 16, uint h = 12)
    {
        using var img = new MagickImage(MagickColors.CornflowerBlue, w, h);
        using var ms = new MemoryStream();
        img.Write(ms, MagickFormat.Bmp);
        return ms.ToArray();
    }

    // ---- BMP <-> DIB ------------------------------------------------------

    /// <summary>
    /// CF_DIB is a BMP with its 14-byte file header lopped off. Getting this wrong by
    /// even a byte hands the receiving app a picture of noise, so both directions and
    /// the round trip are pinned.
    /// </summary>
    [Fact]
    public void ToDib_StripsTheFileHeader()
    {
        var bmp = Bmp();

        var dib = ClipboardImage.BmpToDib(bmp);

        Assert.Equal(bmp.Length - 14, dib.Length);
        Assert.Equal(bmp.Skip(14).ToArray(), dib);
    }

    [Fact]
    public void FromDib_RebuildsAReadableBmp()
    {
        var original = Bmp();
        var dib = ClipboardImage.BmpToDib(original);

        var rebuilt = ClipboardImage.DibToBmp(dib);

        Assert.Equal((byte)'B', rebuilt[0]);
        Assert.Equal((byte)'M', rebuilt[1]);
        Assert.Equal(rebuilt.Length, BitConverter.ToInt32(rebuilt, 2));

        // The real check: Magick can read what we rebuilt, at the right size.
        using var img = new MagickImage(rebuilt);
        Assert.Equal(16u, img.Width);
        Assert.Equal(12u, img.Height);
    }

    [Fact]
    public void BmpToDib_ThenBack_IsLossless()
    {
        var original = Bmp(32, 24);

        var round = ClipboardImage.DibToBmp(ClipboardImage.BmpToDib(original));

        using var img = new MagickImage(round);
        Assert.Equal(32u, img.Width);
        Assert.Equal(24u, img.Height);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    public void ToDib_RejectsSomethingTooShortToBeABmp(int length)
    {
        Assert.Throws<ArgumentException>(() => ClipboardImage.BmpToDib(new byte[length]));
    }

    // ---- The paste scratch folder ----------------------------------------

    /// <summary>
    /// A pasted image has no file of its own, so one is made for it. Everything
    /// downstream -- metadata, navigation, wallpaper, Open with Paint -- already works
    /// in terms of a path on disk, and this keeps it that way rather than threading an
    /// "untitled" case through all of it.
    /// </summary>
    [Fact]
    public void NewPastePath_LandsInTheScratchFolderAsAPng()
    {
        var path = ClipboardImage.NewPastePath(_dir, new DateTime(2026, 9, 19, 14, 30, 5));

        _out.WriteLine(path);
        Assert.Equal(_dir, Path.GetDirectoryName(path));
        Assert.Equal(".png", Path.GetExtension(path));
        Assert.Contains("2026", Path.GetFileName(path));
    }

    [Fact]
    public void NewPastePath_DoesNotCollideWithinTheSameSecond()
    {
        var at = new DateTime(2026, 9, 19, 14, 30, 5);

        var first = ClipboardImage.NewPastePath(_dir, at);
        File.WriteAllBytes(first, new byte[] { 1 });
        var second = ClipboardImage.NewPastePath(_dir, at);

        Assert.NotEqual(first, second);
    }

    /// <summary>
    /// The scratch folder is swept on startup rather than on exit -- an app that is
    /// killed never gets to run its cleanup, and these files would otherwise accumulate
    /// silently for as long as Atelier is installed.
    /// </summary>
    [Fact]
    public void Sweep_RemovesStaleFilesAndKeepsRecentOnes()
    {
        var now = new DateTime(2026, 9, 19, 12, 0, 0);

        var old = Path.Combine(_dir, "paste-old.png");
        File.WriteAllBytes(old, new byte[] { 1 });
        File.SetLastWriteTimeUtc(old, now.AddDays(-30));

        var fresh = Path.Combine(_dir, "paste-fresh.png");
        File.WriteAllBytes(fresh, new byte[] { 1 });
        File.SetLastWriteTimeUtc(fresh, now.AddHours(-1));

        ClipboardImage.SweepPasteFolder(_dir, now, TimeSpan.FromDays(7));

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }

    /// <summary>The picture currently open came from here; sweeping must not pull it away.</summary>
    [Fact]
    public void Sweep_SurvivesAFileItCannotDelete()
    {
        var locked = Path.Combine(_dir, "paste-locked.png");
        File.WriteAllBytes(locked, new byte[] { 1 });
        File.SetLastWriteTimeUtc(locked, new DateTime(2020, 1, 1));

        using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        // The exception is the point: a locked leftover must not stop startup.
        ClipboardImage.SweepPasteFolder(_dir, DateTime.UtcNow, TimeSpan.FromDays(7));

        Assert.True(File.Exists(locked));
    }

    [Fact]
    public void Sweep_OnAFolderThatDoesNotExist_DoesNothing()
    {
        ClipboardImage.SweepPasteFolder(Path.Combine(_dir, "absent"), DateTime.UtcNow, TimeSpan.FromDays(7));
    }

    /// <summary>Only Atelier's own leftovers are swept, never anything else living there.</summary>
    [Fact]
    public void Sweep_LeavesForeignFilesAlone()
    {
        var foreign = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(foreign, "not ours");
        File.SetLastWriteTimeUtc(foreign, new DateTime(2020, 1, 1));

        ClipboardImage.SweepPasteFolder(_dir, DateTime.UtcNow, TimeSpan.FromDays(7));

        Assert.True(File.Exists(foreign));
    }
}
