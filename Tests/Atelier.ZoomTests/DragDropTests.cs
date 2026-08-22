using System;
using System.IO;
using Atelier.ViewModels;
using Atelier.Views;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Threading.Tasks;
using ImageMagick;
using Xunit;

namespace Atelier.ZoomTests;

/// <summary>
/// Cover for dropping a file onto the window — including the empty just-launched
/// window, whose hint text promises "Drop an image here".
/// </summary>
public class DragDropTests : IDisposable
{
    private readonly string _dir;

    public DragDropTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "atelier-dnd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string PngFixture()
    {
        var path = Path.Combine(_dir, "drop.png");
        using var img = new MagickImage(MagickColors.CornflowerBlue, 64, 48);
        img.Write(path, MagickFormat.Png);
        return path;
    }

    /// <summary>
    /// Just enough IStorageFile for the drop handler, which only reads Path.LocalPath.
    /// Avalonia's own BclStorageFile is internal, so it cannot stand in here.
    /// </summary>
    private sealed class FakeStorageFile : IStorageFile
    {
        private readonly FileInfo _info;
        public FakeStorageFile(string path) => _info = new FileInfo(path);

        public string Name => _info.Name;
        public Uri Path => new(_info.FullName);
        public bool CanBookmark => false;

        public Task<StorageItemProperties> GetBasicPropertiesAsync() =>
            Task.FromResult(new StorageItemProperties((ulong)_info.Length));
        public Task<string?> SaveBookmarkAsync() => Task.FromResult<string?>(null);
        public Task<IStorageFolder?> GetParentAsync() => Task.FromResult<IStorageFolder?>(null);
        public Task DeleteAsync() => Task.CompletedTask;
        public Task<IStorageItem?> MoveAsync(IStorageFolder destination) =>
            Task.FromResult<IStorageItem?>(null);
        public Task<System.IO.Stream> OpenReadAsync() =>
            Task.FromResult<System.IO.Stream>(_info.OpenRead());
        public Task<System.IO.Stream> OpenWriteAsync() =>
            Task.FromResult<System.IO.Stream>(_info.OpenWrite());
        public void Dispose() { }
    }

    private static void SimulateDrop(MainWindow win, Point where, string path)
    {
        var data = new DataObject();
        data.Set(DataFormats.Files, new IStorageItem[] { new FakeStorageFile(path) });

        win.DragDrop(where, RawDragEventType.DragEnter, data, DragDropEffects.Copy);
        win.DragDrop(where, RawDragEventType.DragOver, data, DragDropEffects.Copy);
        win.DragDrop(where, RawDragEventType.Drop, data, DragDropEffects.Copy);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Drop_OnTheEmptyWindow_LoadsTheImage()
    {
        var vm = new MainWindowViewModel();
        var win = new MainWindow { DataContext = vm, Width = 900, Height = 600 };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.ImagePath);

        var path = PngFixture();
        SimulateDrop(win, new Point(450, 300), path);

        Assert.Equal(path, vm.ImagePath);
    }

    /// <summary>The drop must work anywhere in the window, not just over the hint text.</summary>
    [AvaloniaFact]
    public void Drop_NearTheWindowEdge_AlsoLoads()
    {
        var vm = new MainWindowViewModel();
        var win = new MainWindow { DataContext = vm, Width = 900, Height = 600 };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        var path = PngFixture();
        SimulateDrop(win, new Point(30, 550), path);

        Assert.Equal(path, vm.ImagePath);
    }
}
