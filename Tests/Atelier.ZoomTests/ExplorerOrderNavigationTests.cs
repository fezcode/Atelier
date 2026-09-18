using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Atelier.ViewModels;
using ImageMagick;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Atelier.ZoomTests;

/// <summary>
/// Next/Prev used to walk siblings in ordinal alphabetical order no matter what the
/// folder looked like on screen: open the top picture of a wallpapers folder sorted
/// newest-first and Next jumped to whatever came next in the alphabet.
///
/// Atelier now takes the order from the Explorer window showing that folder. The window
/// itself cannot be conjured up in a test run, so these drive the seam that reads it and
/// cover what the view model does with the answer -- including every way the answer can
/// be unusable, which has to leave today's behaviour untouched.
/// </summary>
public class ExplorerOrderNavigationTests : IDisposable
{
    private readonly string _dir;
    private readonly Func<string, List<string>?> _realProvider;

    public ExplorerOrderNavigationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "atelier-order-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _realProvider = MainWindowViewModel.ExplorerOrderProvider;
    }

    public void Dispose()
    {
        MainWindowViewModel.ExplorerOrderProvider = _realProvider;
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A real decodable file, since navigating actually loads each one.</summary>
    private string Image(string name)
    {
        var path = Path.Combine(_dir, name);
        using var img = new MagickImage(MagickColors.CornflowerBlue, 8, 8);
        img.Write(path, MagickFormat.Png);
        return path;
    }

    private string Text(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "not an image");
        return path;
    }

    private static void Explorer(params string[] order) =>
        MainWindowViewModel.ExplorerOrderProvider = _ => order.ToList();

    private static void NoExplorer() =>
        MainWindowViewModel.ExplorerOrderProvider = _ => null;

    /// <summary>The reported bug, in miniature: newest-first on screen, so Next means "next newest".</summary>
    [AvaloniaFact]
    public async Task Next_FollowsTheOrderExplorerIsShowing()
    {
        var alpha = Image("alpha.png");     // alphabetically first
        var middle = Image("middle.png");
        var zulu = Image("zulu.png");       // what the window has on top

        Explorer(zulu, middle, alpha);      // e.g. sorted by date, newest first

        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(zulu);
        await vm.NextImage();

        Assert.Equal(middle, vm.ImagePath);

        await vm.NextImage();
        Assert.Equal(alpha, vm.ImagePath);
    }

    [AvaloniaFact]
    public async Task Prev_FollowsTheSameOrderBackwards()
    {
        var alpha = Image("alpha.png");
        var zulu = Image("zulu.png");

        Explorer(zulu, alpha);

        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(alpha);
        await vm.PrevImage();

        Assert.Equal(zulu, vm.ImagePath);
    }

    /// <summary>
    /// Explorer shows everything in the folder; Atelier navigates pictures. The rows it
    /// cannot open are dropped without disturbing the order of the ones it can.
    /// </summary>
    [AvaloniaFact]
    public async Task NonImageRowsAreSkipped_WithoutReorderingTheRest()
    {
        var one = Image("one.png");
        var notes = Text("notes.txt");
        var two = Image("two.png");

        Explorer(two, notes, one);

        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(two);
        await vm.NextImage();

        Assert.Equal(one, vm.ImagePath);
    }

    /// <summary>
    /// No Explorer window on that folder -- launched from Run, from another app, or the
    /// window has since been closed. Nothing about today's behaviour may change.
    /// </summary>
    [AvaloniaFact]
    public async Task WithoutAnExplorerWindow_NavigationStaysAlphabetical()
    {
        var alpha = Image("alpha.png");
        var middle = Image("middle.png");
        Image("zulu.png");

        NoExplorer();

        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(alpha);
        await vm.NextImage();

        Assert.Equal(middle, vm.ImagePath);
    }

    /// <summary>
    /// A searching or filtered window is not showing the picture we have open, so its
    /// order cannot navigate away from it -- <c>_currentIndex</c> would be -1 and Next
    /// would do nothing at all. Fall back rather than strand the user.
    /// </summary>
    [AvaloniaFact]
    public async Task WhenExplorerIsNotShowingTheOpenImage_NavigationStaysAlphabetical()
    {
        var alpha = Image("alpha.png");
        var middle = Image("middle.png");
        var zulu = Image("zulu.png");

        Explorer(zulu, middle);            // a filtered view: no alpha.png

        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(alpha);
        await vm.NextImage();

        Assert.Equal(middle, vm.ImagePath);
    }

    /// <summary>An empty or unreadable answer is the same as no answer.</summary>
    [AvaloniaFact]
    public async Task AnEmptyAnswerFallsBackInsteadOfStranding()
    {
        var alpha = Image("alpha.png");
        var middle = Image("middle.png");

        MainWindowViewModel.ExplorerOrderProvider = _ => new List<string>();

        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(alpha);
        await vm.NextImage();

        Assert.Equal(middle, vm.ImagePath);
    }

    /// <summary>
    /// The seam is read on every load, so re-sorting the Explorer window and pressing
    /// Next picks the new order up without restarting Atelier.
    /// </summary>
    [AvaloniaFact]
    public async Task ReSortingTheWindowTakesEffectOnTheNextLoad()
    {
        var a = Image("a.png");
        var b = Image("b.png");
        var c = Image("c.png");

        Explorer(a, b, c);
        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(a);
        await vm.NextImage();
        Assert.Equal(b, vm.ImagePath);

        Explorer(c, b, a);                 // the user flips the window to descending
        await vm.LoadImageAsync(b);
        await vm.NextImage();

        Assert.Equal(a, vm.ImagePath);
    }

    /// <summary>A throwing provider must not take a picture load down with it.</summary>
    [AvaloniaFact]
    public async Task AThrowingProviderFallsBack()
    {
        var alpha = Image("alpha.png");
        var middle = Image("middle.png");

        MainWindowViewModel.ExplorerOrderProvider = _ => throw new InvalidOperationException("boom");

        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(alpha);
        await vm.NextImage();

        Assert.Equal(middle, vm.ImagePath);
    }
}
