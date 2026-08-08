using System;
using System.IO;
using System.Threading.Tasks;
using Atelier.ViewModels;
using Avalonia.Headless.XUnit;
using ImageMagick;
using Xunit;
using Xunit.Abstractions;

namespace Atelier.ZoomTests;

/// <summary>
/// Cover for <c>MainWindowViewModel.Decode</c> -- the load path that routes each
/// extension to either Avalonia's decoder, Magick.NET (HEIC/HEIF/AVIF) or SvgSource.
///
/// These exist mainly to guard Magick.NET upgrades: the HEIF family depends on bundled
/// libheif/libaom delegates, so a bad bump breaks those formats at runtime with a
/// perfectly clean compile. They also pin the invariant-casing fix -- under a Turkish
/// locale a culture-sensitive ToLower() maps 'I' to dotless 'i', so ".HEIC" would miss
/// the Magick branch and fall through to Avalonia, which cannot read it.
///
/// Every test is an [Avalonia*] one: constructing an Avalonia Bitmap needs the platform
/// render interface, which only the headless app sets up.
/// </summary>
public class DecodeTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir;

    public DecodeTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), "atelier-decode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Writes a solid 64x48 test image in the requested format.</summary>
    private string Fixture(string fileName, MagickFormat format)
    {
        var path = Path.Combine(_dir, fileName);
        using var img = new MagickImage(MagickColors.CornflowerBlue, 64, 48);
        img.Write(path, format);
        return path;
    }

    /// <summary>
    /// A HEIF-container fixture. Magick.NET-Q16-AnyCPU bundles a HEIF *decoder* but no
    /// HEIC *encoder* -- "no encode delegate for this image format `HEIC'" -- which is the
    /// same restriction MainWindowViewModel warns about when saving. AVIF encoding is
    /// available, and AVIF is a HEIF container too, so it stands in for the payload while
    /// the file name carries whichever extension the routing test needs. Decoders sniff
    /// magic bytes, so the extension only decides which branch of Decode() runs.
    /// </summary>
    private string HeifFixture(string fileName) => Fixture(fileName, MagickFormat.Avif);

    [AvaloniaTheory]
    [InlineData("solid.png", MagickFormat.Png)]
    [InlineData("solid.jpg", MagickFormat.Jpeg)]
    [InlineData("solid.bmp", MagickFormat.Bmp)]
    [InlineData("solid.webp", MagickFormat.WebP)]
    public async Task Loads_RasterFormats_ViaAvaloniaDecoder(string name, MagickFormat format)
    {
        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(Fixture(name, format));

        Assert.Null(vm.ErrorMessage);
        Assert.NotNull(vm.ImageSource);
        Assert.Equal(64, vm.ImageWidth);
        Assert.Equal(48, vm.ImageHeight);
    }

    /// <summary>
    /// The HEIF family must route to Magick, not to Avalonia. Avalonia's decoder cannot
    /// read a HEIF container at all, so a payload that decodes here proves the routing.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("photo.avif")]
    [InlineData("photo.heic")]
    [InlineData("photo.heif")]
    public async Task Loads_HeifContainer_ViaMagick(string name)
    {
        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(HeifFixture(name));

        _out.WriteLine($"{name}: err={vm.ErrorMessage} size={vm.ImageWidth}x{vm.ImageHeight}");
        Assert.Null(vm.ErrorMessage);
        Assert.NotNull(vm.ImageSource);
        Assert.Equal(64, vm.ImageWidth);
        Assert.Equal(48, vm.ImageHeight);
    }

    /// <summary>
    /// The Turkish-locale regression. ".HEIC" and ".AVIF" only reach the Magick branch
    /// when the extension is lowered with invariant casing; under tr-TR a culture-sensitive
    /// ToLower() yields dotless 'ı' and the file falls through to Avalonia, which fails.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("SHOUTY.HEIC")]
    [InlineData("SHOUTY.AVIF")]
    public async Task Loads_UppercaseHeifExtensions(string name)
    {
        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(HeifFixture(name));

        _out.WriteLine($"{name}: err={vm.ErrorMessage}");
        Assert.Null(vm.ErrorMessage);
        Assert.NotNull(vm.ImageSource);
        Assert.Equal(64, vm.ImageWidth);
    }

    [AvaloniaFact]
    public async Task Loads_Svg_AsVectorSource()
    {
        var path = Path.Combine(_dir, "glyph.svg");
        await File.WriteAllTextAsync(path,
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"64\" height=\"48\">" +
            "<rect width=\"64\" height=\"48\" fill=\"cornflowerblue\"/></svg>");

        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(path);

        Assert.Null(vm.ErrorMessage);
        Assert.NotNull(vm.ImageSource);
        Assert.Equal(64, vm.ImageWidth);
        Assert.Equal(48, vm.ImageHeight);
    }

    [AvaloniaFact]
    public async Task ReportsAnError_ForAnUnreadableFile()
    {
        var path = Path.Combine(_dir, "truncated.png");
        await File.WriteAllBytesAsync(path, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0x00 });

        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(path);

        Assert.NotNull(vm.ErrorMessage);
        Assert.Null(vm.ImageSource);
        Assert.Equal(0, vm.ImageWidth);
    }

    /// <summary>Sibling scan drives Next/Previous -- it must find images beside the file.</summary>
    [AvaloniaFact]
    public async Task ScansSiblings_ForNavigation()
    {
        Fixture("a.png", MagickFormat.Png);
        var b = Fixture("b.png", MagickFormat.Png);
        Fixture("c.jpg", MagickFormat.Jpeg);
        await File.WriteAllTextAsync(Path.Combine(_dir, "notes.txt"), "ignored");

        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(b);

        Assert.Null(vm.ErrorMessage);
        await vm.NextImage();
        Assert.NotEqual(b, vm.ImagePath);
        Assert.EndsWith(".jpg", vm.ImagePath!, StringComparison.OrdinalIgnoreCase);
    }
}
