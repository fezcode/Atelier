using System;
using System.Collections.Generic;
using System.Formats.Cbor;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Atelier.ViewModels;
using Avalonia.Headless.XUnit;
using ImageMagick;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using Xunit;

namespace Atelier.ZoomTests;

/// <summary>
/// The metadata pane's EXIF and Content Credentials sections, Remove Metadata, and the
/// formats Save As can write from edit mode.
///
/// The C2PA stores are built here rather than checked in: a minimal manifest is a few
/// nested boxes and two CBOR maps, and building it lets each container be tested --
/// including a JPEG store split across APP11 segments, which real files only do once
/// the manifest outgrows 64 KB.
/// </summary>
public class MetadataTests : IDisposable
{
    private readonly string _dir;

    public MetadataTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "atelier-metadata-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { System.IO.Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ---- Fixtures ---------------------------------------------------------------

    /// <summary>A small gradient (so a re-encode would show) carrying camera EXIF, GPS and an orientation.</summary>
    private string Photo(string name, MagickFormat format, ushort orientation = 6)
    {
        var path = Path.Combine(_dir, name);
        using var img = new MagickImage("gradient:red-blue", 48, 32);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, "TestCam");
        exif.SetValue(ExifTag.Model, "Model 1");
        exif.SetValue(ExifTag.Orientation, orientation);
        exif.SetValue(ExifTag.GPSLatitudeRef, "N");
        exif.SetValue(ExifTag.GPSLatitude, new[] { new ImageMagick.Rational(41, 1), new ImageMagick.Rational(0, 1), new ImageMagick.Rational(0, 1) });
        img.SetProfile(exif);
        // Magick writes the tag from this property, not from the profile.
        img.Orientation = (OrientationType)orientation;
        img.Write(path, format);
        return path;
    }

    private static byte[] Box(string type, byte[] payload)
    {
        var box = new byte[8 + payload.Length];
        int len = box.Length;
        box[0] = (byte)(len >> 24); box[1] = (byte)(len >> 16); box[2] = (byte)(len >> 8); box[3] = (byte)len;
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        payload.CopyTo(box, 8);
        return box;
    }

    private static byte[] Jumb(string label, params byte[][] children)
    {
        var jumd = new byte[16].Concat(new byte[] { 0x03 }).Concat(Encoding.UTF8.GetBytes(label)).Concat(new byte[] { 0 }).ToArray();
        return Box("jumb", Box("jumd", jumd).Concat(children.SelectMany(c => c)).ToArray());
    }

    private static byte[] CborMap(Action<CborWriter> write)
    {
        var w = new CborWriter();
        write(w);
        return w.Encode();
    }

    /// <summary>A one-manifest store: made by "Test App 1.0", created by generative AI.</summary>
    private static byte[] ManifestStore()
    {
        var claim = CborMap(w =>
        {
            w.WriteStartMap(2);
            w.WriteTextString("claim_generator_info");
            w.WriteStartMap(2);
            w.WriteTextString("name"); w.WriteTextString("Test App");
            w.WriteTextString("version"); w.WriteTextString("1.0");
            w.WriteEndMap();
            w.WriteTextString("dc:title"); w.WriteTextString("Sunset");
            w.WriteEndMap();
        });
        var actions = CborMap(w =>
        {
            w.WriteStartMap(1);
            w.WriteTextString("actions");
            w.WriteStartArray(1);
            w.WriteStartMap(2);
            w.WriteTextString("action"); w.WriteTextString("c2pa.created");
            w.WriteTextString("digitalSourceType");
            w.WriteTextString("http://cv.iptc.org/newscodes/digitalsourcetype/trainedAlgorithmicMedia");
            w.WriteEndMap();
            w.WriteEndArray();
            w.WriteEndMap();
        });

        return Jumb("c2pa",
            Jumb("urn:c2pa:test-manifest",
                Jumb("c2pa.assertions", Jumb("c2pa.actions.v2", Box("cbor", actions))),
                Jumb("c2pa.claim.v2", Box("cbor", claim))));
    }

    /// <summary>
    /// Embeds the store in APP11 segments right after SOI, split in two so the reader
    /// has to drop the repeated box header from the second piece.
    /// </summary>
    private static void EmbedInJpeg(string path, byte[] store)
    {
        var data = File.ReadAllBytes(path);
        int split = store.Length / 2;
        var pieces = new[] { store[..split], store[..8].Concat(store[split..]).ToArray() };

        using var output = new MemoryStream();
        output.Write(data, 0, 2);
        for (int i = 0; i < pieces.Length; i++)
        {
            int length = 2 + 8 + pieces[i].Length;
            output.Write(new byte[] { 0xFF, 0xEB, (byte)(length >> 8), (byte)length, (byte)'J', (byte)'P', 0x00, 0x01,
                0, 0, 0, (byte)(i + 1) });
            output.Write(pieces[i]);
        }
        output.Write(data, 2, data.Length - 2);
        File.WriteAllBytes(path, output.ToArray());
    }

    private static void EmbedInPng(string path, byte[] store)
    {
        var data = File.ReadAllBytes(path);
        int iend = data.Length - 12;
        var type = Encoding.ASCII.GetBytes("caBX");
        using var output = new MemoryStream();
        output.Write(data, 0, iend);
        output.Write(new[] { (byte)(store.Length >> 24), (byte)(store.Length >> 16), (byte)(store.Length >> 8), (byte)store.Length });
        output.Write(type);
        output.Write(store);
        uint crc = Crc32(type.Concat(store).ToArray());
        output.Write(new[] { (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc });
        output.Write(data, iend, 12);
        File.WriteAllBytes(path, output.ToArray());
    }

    private static void EmbedInWebP(string path, byte[] store)
    {
        var data = File.ReadAllBytes(path).ToList();
        data.AddRange(Encoding.ASCII.GetBytes("C2PA"));
        data.AddRange(BitConverter.GetBytes((uint)store.Length));
        data.AddRange(store);
        if ((store.Length & 1) == 1) data.Add(0);
        var bytes = data.ToArray();
        BitConverter.GetBytes((uint)(bytes.Length - 8)).CopyTo(bytes, 4);
        File.WriteAllBytes(path, bytes);
    }

    private static uint Crc32(byte[] bytes)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in bytes)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFF;
    }

    private static int? OrientationTag(string path) =>
        ImageMetadataReader.ReadMetadata(path).OfType<ExifIfd0Directory>()
            .Select(d => d.TryGetInt32(ExifDirectoryBase.TagOrientation, out int t) ? t : (int?)null)
            .FirstOrDefault(t => t != null);

    private static bool HasTag(string path, int tag) =>
        ImageMetadataReader.ReadMetadata(path).OfType<ExifIfd0Directory>().Any(d => d.ContainsTag(tag));

    private static void AssertSamePixels(string expected, string actual)
    {
        using var a = new MagickImage(expected);
        using var b = new MagickImage(actual);
        Assert.Equal(0, a.Compare(b, ErrorMetric.Absolute));
    }

    // ---- Reading C2PA ---------------------------------------------------------------

    public static IEnumerable<object[]> Containers() => new[]
    {
        new object[] { "photo.jpg", MagickFormat.Jpeg },
        new object[] { "photo.png", MagickFormat.Png },
        new object[] { "photo.webp", MagickFormat.WebP },
    };

    private string WithCredentials(string name, MagickFormat format)
    {
        var path = Photo(name, format);
        var store = ManifestStore();
        switch (format)
        {
            case MagickFormat.Jpeg: EmbedInJpeg(path, store); break;
            case MagickFormat.Png: EmbedInPng(path, store); break;
            default: EmbedInWebP(path, store); break;
        }
        return path;
    }

    [Theory]
    [MemberData(nameof(Containers))]
    public void C2pa_IsReadFromEachContainer(string name, MagickFormat format)
    {
        var path = WithCredentials(name, format);

        var fields = C2pa.Read(path);

        Assert.NotNull(fields);
        var byLabel = fields!.ToDictionary(f => f.Label, f => f.Value);
        Assert.Equal("Test App 1.0", byLabel["App or device used"]);
        Assert.Equal("Sunset", byLabel["Title"]);
        Assert.Equal("Created", byLabel["Actions"]);
        Assert.Equal("Generative AI", byLabel["Made with"]);
        Assert.Equal("urn:c2pa:test-manifest", byLabel["Manifest"]);
    }

    [Fact]
    public void C2pa_IsNullForAPictureWithoutCredentials()
    {
        Assert.Null(C2pa.Read(Photo("plain.jpg", MagickFormat.Jpeg)));
    }

    /// <summary>A store next to the picture, the way a cloud workflow leaves it, is found too.</summary>
    [Fact]
    public void C2pa_IsReadFromASidecarFile()
    {
        var path = Photo("plain.jpg", MagickFormat.Jpeg);
        File.WriteAllBytes(Path.ChangeExtension(path, ".c2pa"), ManifestStore());

        var fields = C2pa.Read(path);

        Assert.Contains(fields!, f => f.Label == "Stored" && f.Value.StartsWith("Sidecar"));
    }

    // ---- Stripping ------------------------------------------------------------------

    /// <summary>
    /// The core promise: camera details, GPS and credentials go; the pixels are the
    /// same bytes; and the photo still stands the right way up.
    /// </summary>
    [Theory]
    [MemberData(nameof(Containers))]
    public void Strip_RemovesExifAndC2pa_KeepsPixelsAndOrientation(string name, MagickFormat format)
    {
        var path = WithCredentials(name, format);
        Assert.True(HasTag(path, ExifDirectoryBase.TagMake), "fixture should carry EXIF");
        Assert.True(C2pa.IsPresent(path), "fixture should carry C2PA");
        var clean = Path.Combine(_dir, "clean" + Path.GetExtension(name));

        MetadataStripper.Strip(path, clean);

        Assert.False(C2pa.IsPresent(clean));
        Assert.False(HasTag(clean, ExifDirectoryBase.TagMake));
        Assert.Empty(ImageMetadataReader.ReadMetadata(clean).OfType<GpsDirectory>());
        Assert.Equal(6, OrientationTag(clean));
        AssertSamePixels(path, clean);
    }

    /// <summary>An upright photo needs no orientation block, so none is left behind.</summary>
    [Fact]
    public void Strip_LeavesNoExifAtAllOnAnUprightPhoto()
    {
        var path = Photo("upright.jpg", MagickFormat.Jpeg, orientation: 1);

        MetadataStripper.Strip(path, path);

        Assert.Empty(ImageMetadataReader.ReadMetadata(path).OfType<ExifDirectoryBase>());
    }

    /// <summary>The colour profile is not metadata to remove: without it a wide-gamut photo turns dull.</summary>
    [Fact]
    public void Strip_KeepsTheColourProfile()
    {
        var path = Path.Combine(_dir, "icc.jpg");
        using (var img = new MagickImage("gradient:red-blue", 32, 32))
        {
            img.SetProfile(ColorProfiles.SRGB);
            img.SetProfile(new ExifProfile());
            img.Write(path, MagickFormat.Jpeg);
        }

        MetadataStripper.Strip(path, path);

        using var saved = new MagickImage(path);
        Assert.NotNull(saved.GetColorProfile());
    }

    /// <summary>Formats with no container rewrite go through Magick and still come out clean.</summary>
    [Fact]
    public void Strip_ReencodesOtherFormats()
    {
        var path = Photo("photo.avif", MagickFormat.Avif, orientation: 1);
        using (var before = new MagickImage(path)) Assert.NotNull(before.GetExifProfile());

        MetadataStripper.Strip(path, path);

        using var after = new MagickImage(path);
        Assert.Null(after.GetExifProfile());
    }

    [Fact]
    public void Strip_RefusesHeic()
    {
        Assert.False(MetadataStripper.CanStrip("photo.heic"));
        Assert.Throws<NotSupportedException>(() => MetadataStripper.Strip("photo.heic", "out.heic"));
    }

    // ---- The pane and the action ----------------------------------------------------------

    [AvaloniaFact]
    public async Task Pane_ShowsExifAndCredentialSections()
    {
        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(WithCredentials("photo.jpg", MagickFormat.Jpeg));

        var headers = vm.MetadataItems.Where(m => m.IsHeader).Select(m => m.Label).ToList();
        Assert.Equal(new[] { "File", "EXIF", "Content Credentials (C2PA)" }, headers);
        Assert.Contains(vm.MetadataItems, m => m.IsField && m.Label == "Make" && m.Value == "TestCam");
        Assert.Contains(vm.MetadataItems, m => m.IsSubheader && m.Label == "GPS");
        Assert.Contains(vm.MetadataItems, m => m.IsField && m.Label == "App or device used" && m.Value == "Test App 1.0");
        Assert.True(vm.CanRemoveMetadata);
    }

    [AvaloniaFact]
    public async Task Pane_SaysNoneWhenThereIsNothing()
    {
        // BMP: Magick's PNG writer always adds date tEXt chunks, which do count as metadata.
        var path = Path.Combine(_dir, "bare.bmp");
        using (var img = new MagickImage(MagickColors.CornflowerBlue, 8, 8)) img.Write(path, MagickFormat.Bmp);

        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(path);

        Assert.All(vm.MetadataItems.Where(m => m.IsHeader && m.Label != "File"),
            h => Assert.Equal("None in this file", h.Value));
        Assert.False(vm.CanRemoveMetadata);
    }

    [AvaloniaFact]
    public async Task RemoveMetadata_InPlace_CleansTheFileAndRefreshesThePane()
    {
        var path = WithCredentials("photo.jpg", MagickFormat.Jpeg);
        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(path);

        Assert.True(await vm.RemoveMetadataAsync());

        Assert.Null(vm.ErrorMessage);
        Assert.False(C2pa.IsPresent(path));
        Assert.Equal("None in this file",
            vm.MetadataItems.Single(m => m.IsHeader && m.Label.StartsWith("Content Credentials")).Value);
        Assert.False(vm.CanRemoveMetadata);
        // Still the right way up after the reload.
        Assert.Equal(90, vm.RotationAngle);
    }

    [AvaloniaFact]
    public async Task RemoveMetadata_ToACopy_LeavesTheOriginalAlone()
    {
        var path = WithCredentials("photo.png", MagickFormat.Png);
        var copy = Path.Combine(_dir, "photo-clean.png");
        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(path);

        Assert.True(await vm.RemoveMetadataAsync(copy));

        Assert.True(C2pa.IsPresent(path));
        Assert.False(C2pa.IsPresent(copy));
        Assert.Equal(copy, vm.ImagePath);
    }

    [AvaloniaFact]
    public async Task RemoveMetadata_IsUnavailableWhileEditing()
    {
        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(WithCredentials("photo.jpg", MagickFormat.Jpeg));
        vm.EnterEditMode();

        Assert.False(vm.CanRemoveMetadata);
        Assert.False(await vm.RemoveMetadataAsync());
    }

    // ---- Save As from edit mode -------------------------------------------------------------

    /// <summary>
    /// The formats Avalonia cannot decode itself: each is written, then reopened
    /// through Magick, which is what puts the saved picture back on screen.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(".tif")]
    [InlineData(".jxl")]
    [InlineData(".avif")]
    [InlineData(".jp2")]
    [InlineData(".psd")]
    [InlineData(".qoi")]
    [InlineData(".tga")]
    [InlineData(".dds")]
    [InlineData(".exr")]
    [InlineData(".pcx")]
    [InlineData(".ppm")]
    [InlineData(".xpm")]
    public async Task EditSaveAs_WritesAndReopensEveryFormat(string ext)
    {
        var source = Path.Combine(_dir, "a.png");
        using (var img = new MagickImage(MagickColors.White, 40, 30)) img.Write(source, MagickFormat.Png);
        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(source);
        vm.EnterEditMode();
        vm.InvertColors();
        var target = Path.Combine(_dir, "out" + ext);

        await vm.SaveEditedImageAsync(target);

        Assert.Null(vm.ErrorMessage);
        Assert.Equal(target, vm.ImagePath);
        Assert.False(vm.IsEditMode);
        Assert.Equal(40, vm.ImageWidth);
        Assert.Equal(30, vm.ImageHeight);
    }

    /// <summary>PDF is written but cannot be opened again without Ghostscript, so the edit stays open.</summary>
    [AvaloniaFact]
    public async Task EditSaveAs_Pdf_ExportsAndKeepsEditing()
    {
        var source = Path.Combine(_dir, "a.png");
        using (var img = new MagickImage(MagickColors.White, 40, 30)) img.Write(source, MagickFormat.Png);
        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(source);
        vm.EnterEditMode();
        vm.InvertColors();
        var target = Path.Combine(_dir, "out.pdf");

        await vm.SaveEditedImageAsync(target);

        Assert.Null(vm.ErrorMessage);
        Assert.True(File.Exists(target));
        Assert.True(vm.IsEditMode);
        Assert.Equal(source, vm.ImagePath);
    }
}
