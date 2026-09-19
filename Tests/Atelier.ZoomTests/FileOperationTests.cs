using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Atelier.ViewModels;
using Avalonia.Headless.XUnit;
using ImageMagick;
using Xunit;
using Xunit.Abstractions;

namespace Atelier.ZoomTests;

/// <summary>
/// Deleting and renaming the picture on screen.
///
/// The recycler is swapped for a recording stub, following the same seam
/// <c>ExplorerOrderProvider</c> uses: a test run has no business putting files in the
/// real Recycle Bin, and what is worth pinning here is which picture Atelier moves to
/// afterwards, not whether Windows can recycle a file.
/// </summary>
public class FileOperationTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir;
    private readonly List<string> _recycled = new();
    private readonly Func<string, bool> _realRecycler;

    public FileOperationTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), "atelier-fileops-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        _realRecycler = MainWindowViewModel.RecycleProvider;
        MainWindowViewModel.RecycleProvider = path =>
        {
            _recycled.Add(path);
            File.Delete(path); // stand in for the move to the bin
            return true;
        };
    }

    public void Dispose()
    {
        MainWindowViewModel.RecycleProvider = _realRecycler;
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Fixture(string name)
    {
        var path = Path.Combine(_dir, name);
        using var img = new MagickImage(MagickColors.CornflowerBlue, 8, 8);
        img.Write(path, MagickFormat.Png);
        return path;
    }

    // ---- Delete -----------------------------------------------------------

    [AvaloniaFact]
    public async Task Delete_RecyclesTheFileAndMovesToTheNextPicture()
    {
        Fixture("a.png");
        var b = Fixture("b.png");
        Fixture("c.png");

        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(b);
        await vm.DeleteCurrentAsync();

        Assert.Equal(new[] { b }, _recycled);
        Assert.False(File.Exists(b));
        Assert.EndsWith("c.png", vm.ImagePath!);
    }

    /// <summary>Deleting the last picture has to fall back to the previous one, not wrap or crash.</summary>
    [AvaloniaFact]
    public async Task Delete_OnTheLastPicture_FallsBackToThePreviousOne()
    {
        Fixture("a.png");
        var c = Fixture("c.png");

        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(c);
        await vm.DeleteCurrentAsync();

        Assert.EndsWith("a.png", vm.ImagePath!);
    }

    /// <summary>The only picture in the folder: there is nowhere to go, so the window empties.</summary>
    [AvaloniaFact]
    public async Task Delete_OnTheOnlyPicture_ClearsTheView()
    {
        var only = Fixture("only.png");

        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(only);
        await vm.DeleteCurrentAsync();

        Assert.False(File.Exists(only));
        Assert.Null(vm.ImagePath);
        Assert.Null(vm.ImageSource);
        Assert.False(vm.HasImage);
        Assert.Equal(0, vm.ImageWidth);
        Assert.Empty(vm.MetadataItems);
    }

    /// <summary>A refused delete must leave the picture exactly where it was.</summary>
    [AvaloniaFact]
    public async Task Delete_WhenTheRecyclerRefuses_KeepsThePictureOpen()
    {
        MainWindowViewModel.RecycleProvider = _ => false;
        var a = Fixture("a.png");
        Fixture("b.png");

        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(a);
        var ok = await vm.DeleteCurrentAsync();

        Assert.False(ok);
        Assert.Equal(a, vm.ImagePath);
        Assert.True(File.Exists(a));
        Assert.NotNull(vm.ErrorMessage);
    }

    [AvaloniaFact]
    public async Task Delete_WithNothingOpen_DoesNothing()
    {
        var vm = new MainWindowViewModel();

        Assert.False(await vm.DeleteCurrentAsync());
        Assert.Empty(_recycled);
    }

    /// <summary>Navigation after a delete walks what is left, with no gap where the file was.</summary>
    [AvaloniaFact]
    public async Task Delete_LeavesNavigationConsistent()
    {
        Fixture("a.png");
        var b = Fixture("b.png");
        Fixture("c.png");

        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(b);
        await vm.DeleteCurrentAsync();   // now on c
        await vm.NextImage();            // wraps to a

        _out.WriteLine($"after delete+next: {vm.ImagePath}");
        Assert.EndsWith("a.png", vm.ImagePath!);
    }

    // ---- Rename -----------------------------------------------------------

    [AvaloniaFact]
    public async Task Rename_MovesTheFileAndFollowsIt()
    {
        var a = Fixture("a.png");

        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(a);
        var ok = await vm.RenameCurrentAsync("sunset.png");

        Assert.True(ok);
        Assert.False(File.Exists(a));
        Assert.True(File.Exists(Path.Combine(_dir, "sunset.png")));
        Assert.EndsWith("sunset.png", vm.ImagePath!);
        Assert.Null(vm.ErrorMessage);
    }

    /// <summary>Typing a bare name keeps the extension rather than quietly breaking the file.</summary>
    [AvaloniaFact]
    public async Task Rename_WithoutAnExtension_KeepsTheOriginalOne()
    {
        var a = Fixture("a.png");

        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(a);
        await vm.RenameCurrentAsync("sunset");

        Assert.True(File.Exists(Path.Combine(_dir, "sunset.png")));
    }

    [AvaloniaFact]
    public async Task Rename_OntoAnExistingFile_IsRefused()
    {
        var a = Fixture("a.png");
        Fixture("b.png");

        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(a);
        var ok = await vm.RenameCurrentAsync("b.png");

        Assert.False(ok);
        Assert.True(File.Exists(a));
        Assert.NotNull(vm.ErrorMessage);
    }

    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad:name.png")]
    [InlineData("bad/name.png")]
    [InlineData("bad\\name.png")]
    public async Task Rename_WithAnUnusableName_IsRefused(string name)
    {
        var a = Fixture("a.png");

        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(a);
        var ok = await vm.RenameCurrentAsync(name);

        Assert.False(ok);
        Assert.True(File.Exists(a));
    }

    /// <summary>Renaming to the name it already has is a no-op, not a collision with itself.</summary>
    [AvaloniaFact]
    public async Task Rename_ToTheSameName_Succeeds()
    {
        var a = Fixture("a.png");

        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(a);

        Assert.True(await vm.RenameCurrentAsync("a.png"));
        Assert.True(File.Exists(a));
    }
}
