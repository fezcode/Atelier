using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Atelier;
using Atelier.ViewModels;
using Avalonia.Headless.XUnit;
using ImageMagick;
using Xunit;
using Xunit.Abstractions;

namespace Atelier.ZoomTests;

/// <summary>
/// Decoding animated images. Avalonia's Bitmap holds exactly one frame, so a GIF used
/// to open as its first frame and sit there -- the file looked broken rather than
/// unsupported.
///
/// The budget tests matter more than they look. Frames are held decoded, and decoded
/// frames are enormous next to the file: a 900-frame GIF that occupies 4MB on disk is
/// well over a gigabyte once coalesced. Atelier checks before committing to that.
/// </summary>
public class AnimationTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir;
    private readonly long _budget;

    public AnimationTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), "atelier-anim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _budget = AnimationDecoder.MemoryBudgetBytes;
    }

    public void Dispose()
    {
        AnimationDecoder.MemoryBudgetBytes = _budget;
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>An animated GIF of <paramref name="frames"/> frames, each held for <paramref name="centiseconds"/>.</summary>
    private string Gif(string name, int frames, uint centiseconds = 5, uint size = 16)
    {
        var path = Path.Combine(_dir, name);
        using var collection = new MagickImageCollection();
        for (int i = 0; i < frames; i++)
        {
            var frame = new MagickImage(i % 2 == 0 ? MagickColors.Red : MagickColors.Blue, size, size);
            frame.AnimationDelay = centiseconds;
            collection.Add(frame);
        }
        collection.Write(path, MagickFormat.Gif);
        return path;
    }

    // ---- What counts as animated -----------------------------------------

    [Theory]
    [InlineData("a.gif", true)]
    [InlineData("a.webp", true)]
    [InlineData("a.png", false)]
    [InlineData("a.jpg", false)]
    [InlineData("a.svg", false)]
    [InlineData("A.GIF", true)]
    public void MightAnimate_ScreensByExtension(string name, bool expected)
    {
        Assert.Equal(expected, AnimationDecoder.MightAnimate(name));
    }

    // ---- Decoding ---------------------------------------------------------

    [AvaloniaFact]
    public void Decode_ReadsEveryFrame()
    {
        using var animation = AnimationDecoder.Decode(Gif("spin.gif", 4));

        Assert.NotNull(animation);
        Assert.Equal(4, animation!.FrameCount);
        Assert.Equal(4, animation.Frames.Count);
        Assert.All(animation.Frames, Assert.NotNull);
    }

    [AvaloniaFact]
    public void Decode_KeepsTheFrameDelays()
    {
        using var animation = AnimationDecoder.Decode(Gif("slow.gif", 3, centiseconds: 25));

        Assert.NotNull(animation);
        Assert.All(animation!.Delays, d => Assert.Equal(TimeSpan.FromMilliseconds(250), d));
    }

    /// <summary>
    /// The oldest quirk in the format: GIFs written with a delay of 0 or 1 centisecond
    /// mean "as fast as possible", and every browser has clamped that to 100ms for
    /// decades. Honouring the literal value spins the timer pointlessly fast.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(0u)]
    [InlineData(1u)]
    public void Decode_ClampsAbsurdlyShortDelays(uint centiseconds)
    {
        using var animation = AnimationDecoder.Decode(Gif("fast.gif", 3, centiseconds));

        Assert.NotNull(animation);
        Assert.All(animation!.Delays, d => Assert.Equal(TimeSpan.FromMilliseconds(100), d));
    }

    /// <summary>A GIF with one frame is just a picture; there is nothing to play.</summary>
    [AvaloniaFact]
    public void Decode_ReturnsNothing_ForASingleFrameGif()
    {
        Assert.Null(AnimationDecoder.Decode(Gif("still.gif", 1)));
    }

    [AvaloniaFact]
    public void Decode_ReturnsNothing_ForAStillFormat()
    {
        var path = Path.Combine(_dir, "flat.png");
        using (var img = new MagickImage(MagickColors.Red, 16, 16)) img.Write(path, MagickFormat.Png);

        Assert.Null(AnimationDecoder.Decode(path));
    }

    [AvaloniaFact]
    public void Decode_ReturnsNothing_ForAMissingFile()
    {
        Assert.Null(AnimationDecoder.Decode(Path.Combine(_dir, "nope.gif")));
    }

    // ---- The memory budget ------------------------------------------------

    /// <summary>
    /// Over budget, nothing is decoded at all -- the caller falls back to the static
    /// first frame. The point is to never allocate the frames, so this asserts on the
    /// probe rather than on a null return alone.
    /// </summary>
    [AvaloniaFact]
    public void Probe_RefusesAnAnimationOverTheBudget()
    {
        var path = Gif("huge.gif", 8, size: 64);
        AnimationDecoder.MemoryBudgetBytes = 1024; // 8 frames of 64x64 is far past this

        var probe = AnimationDecoder.Probe(path);

        _out.WriteLine($"frames={probe.FrameCount} estimated={probe.EstimatedBytes} within={probe.WithinBudget}");
        Assert.True(probe.IsAnimated);
        Assert.Equal(8, probe.FrameCount);
        Assert.False(probe.WithinBudget);
        Assert.Null(AnimationDecoder.Decode(path));
    }

    [AvaloniaFact]
    public void Probe_AcceptsAnAnimationWithinTheBudget()
    {
        var probe = AnimationDecoder.Probe(Gif("small.gif", 4));

        Assert.True(probe.IsAnimated);
        Assert.True(probe.WithinBudget);
        Assert.Equal(4, probe.FrameCount);
    }

    /// <summary>Probing must not decode pixels -- that is the whole point of asking first.</summary>
    [AvaloniaFact]
    public void Probe_ReportsFrameCountWithoutDecoding()
    {
        var probe = AnimationDecoder.Probe(Gif("many.gif", 12));

        Assert.Equal(12, probe.FrameCount);
        Assert.True(probe.EstimatedBytes > 0);
    }

    [AvaloniaFact]
    public void Probe_OnAStillImage_ReportsNotAnimated()
    {
        var path = Path.Combine(_dir, "flat.png");
        using (var img = new MagickImage(MagickColors.Red, 16, 16)) img.Write(path, MagickFormat.Png);

        var probe = AnimationDecoder.Probe(path);

        Assert.False(probe.IsAnimated);
    }

    // ---- What the view model does with it --------------------------------

    [AvaloniaFact]
    public async Task Load_StartsAnAnimationPlaying()
    {
        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(Gif("play.gif", 4));

        Assert.True(vm.IsAnimated);
        Assert.True(vm.IsPlaying);
        Assert.Equal(4, vm.FrameCount);
        Assert.NotNull(vm.ImageSource);
    }

    [AvaloniaFact]
    public async Task Load_AStillImage_IsNotAnimated()
    {
        var path = Path.Combine(_dir, "flat.png");
        using (var img = new MagickImage(MagickColors.Red, 16, 16)) img.Write(path, MagickFormat.Png);

        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(path);

        Assert.False(vm.IsAnimated);
        Assert.False(vm.IsPlaying);
        Assert.Equal(0, vm.FrameCount);
    }

    /// <summary>Moving from a GIF to a photo has to put the animation down.</summary>
    [AvaloniaFact]
    public async Task Load_ClearsThePreviousAnimation()
    {
        var still = Path.Combine(_dir, "flat.png");
        using (var img = new MagickImage(MagickColors.Red, 16, 16)) img.Write(still, MagickFormat.Png);

        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(Gif("play.gif", 4));
        await vm.LoadImageAsync(still);

        Assert.False(vm.IsAnimated);
        Assert.False(vm.IsPlaying);
        Assert.NotNull(vm.ImageSource);
    }

    [AvaloniaFact]
    public async Task TogglePlayback_PausesAndResumes()
    {
        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(Gif("play.gif", 4));

        vm.TogglePlayback();
        Assert.False(vm.IsPlaying);

        vm.TogglePlayback();
        Assert.True(vm.IsPlaying);
    }

    [AvaloniaFact]
    public async Task AdvanceFrame_WalksTheFramesAndLoops()
    {
        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(Gif("play.gif", 3));

        var first = vm.ImageSource;
        vm.AdvanceFrame();
        Assert.NotSame(first, vm.ImageSource);

        vm.AdvanceFrame();
        vm.AdvanceFrame();
        Assert.Same(first, vm.ImageSource); // back round to frame zero
    }

    /// <summary>Paused means paused: the timer may still tick during the fade out.</summary>
    [AvaloniaFact]
    public async Task AdvanceFrame_DoesNothingWhilePaused()
    {
        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(Gif("play.gif", 3));
        vm.TogglePlayback();

        var held = vm.ImageSource;
        vm.AdvanceFrame();

        Assert.Same(held, vm.ImageSource);
    }

    /// <summary>
    /// Edit mode is colour work on one still. Rather than silently flattening an
    /// animation to a single frame inside a Save, the menu item is simply closed off.
    /// </summary>
    [AvaloniaFact]
    public async Task AnAnimation_CannotBeEdited()
    {
        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(Gif("play.gif", 3));

        Assert.False(vm.CanEditImage);
    }

    [AvaloniaFact]
    public async Task AStillImage_CanBeEdited()
    {
        var path = Path.Combine(_dir, "flat.png");
        using (var img = new MagickImage(MagickColors.Red, 16, 16)) img.Write(path, MagickFormat.Png);

        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(path);

        Assert.True(vm.CanEditImage);
    }

    /// <summary>
    /// Past the budget the picture still opens -- as its first frame, with the metadata
    /// pane saying why. Refusing to show it at all would be worse than not animating it.
    /// </summary>
    [AvaloniaFact]
    public async Task AnOversizedAnimation_StillOpensAsAStill()
    {
        var path = Gif("huge.gif", 8, size: 64);
        AnimationDecoder.MemoryBudgetBytes = 1024;

        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(path);

        Assert.Null(vm.ErrorMessage);
        Assert.NotNull(vm.ImageSource);
        Assert.False(vm.IsAnimated);

        var frames = vm.MetadataItems.FirstOrDefault(m => m.Label == "Frames");
        Assert.NotNull(frames);
        _out.WriteLine($"Frames metadata: {frames!.Value}");
        Assert.Contains("too large", frames.Value);
    }

    [AvaloniaFact]
    public async Task AnAnimation_ReportsItsFrameCountInTheMetadata()
    {
        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(Gif("play.gif", 6));

        var frames = vm.MetadataItems.FirstOrDefault(m => m.Label == "Frames");
        Assert.NotNull(frames);
        Assert.Equal("6", frames!.Value);
    }

    /// <summary>GIFs used to be skipped by Next/Prev even though Atelier could show them.</summary>
    [AvaloniaFact]
    public async Task GifsAreReachable_FromTheNeighbouringPicture()
    {
        var a = Path.Combine(_dir, "a.png");
        using (var img = new MagickImage(MagickColors.Red, 16, 16)) img.Write(a, MagickFormat.Png);
        Gif("b.gif", 3);

        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(a);
        await vm.NextImage();

        Assert.EndsWith("b.gif", vm.ImagePath!);
    }
}
