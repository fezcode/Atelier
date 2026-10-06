using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Atelier.Painting;
using Atelier.ViewModels;
using Atelier.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ImageMagick;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace Atelier.ZoomTests;

/// <summary>
/// The Paint-style edit mode: the pixel engine on its own, the tools as the view model
/// drives them, what a save writes, and -- through a real headless window -- that a
/// click on the screen lands on the pixel under it.
/// </summary>
public class PaintTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir;
    private readonly Func<string, bool> _realRecycler;
    private readonly List<string> _recycled = new();

    public PaintTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), "atelier-paint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        _realRecycler = MainWindowViewModel.RecycleProvider;
        MainWindowViewModel.RecycleProvider = path => { _recycled.Add(path); return true; };
    }

    public void Dispose()
    {
        MainWindowViewModel.RecycleProvider = _realRecycler;
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Png(string name, int width = 40, int height = 30, string color = "white")
    {
        var path = Path.Combine(_dir, name);
        using var img = new MagickImage(new MagickColor(color), (uint)width, (uint)height);
        img.Write(path, MagickFormat.Png);
        return path;
    }

    private async Task<MainWindowViewModel> Editing(string path)
    {
        var vm = new MainWindowViewModel();
        await vm.LoadImageAsync(path);
        vm.EnterEditMode();
        Assert.True(vm.IsEditMode, vm.ErrorMessage);
        return vm;
    }

    private static SKColor Pixel(MainWindowViewModel vm, int x, int y) => vm.Document!.GetPixel(x, y);

    private static MagickColor? FilePixel(string path, int x, int y)
    {
        using var img = new MagickImage(path);
        using var px = img.GetPixels();
        return px.GetPixel(x, y).ToColor() as MagickColor;
    }

    private static void AssertColor(SKColor expected, SKColor actual, int tolerance = 2)
    {
        Assert.True(Math.Abs(expected.Red - actual.Red) <= tolerance
                    && Math.Abs(expected.Green - actual.Green) <= tolerance
                    && Math.Abs(expected.Blue - actual.Blue) <= tolerance
                    && Math.Abs(expected.Alpha - actual.Alpha) <= tolerance,
            $"expected {expected}, got {actual}");
    }

    // ---- The engine on its own ---------------------------------------------------

    [Fact]
    public void FloodFill_StaysInsideTheEnclosedArea_AndUndoes()
    {
        using var doc = new PaintDocument(20, 20, SKColors.White);
        doc.Draw(c =>
        {
            using var p = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = false };
            c.DrawRect(5, 5, 10, 10, p);
        }, doc.Bounds);

        Assert.True(doc.FloodFill(10, 10, SKColors.Red, 0));

        Assert.Equal(SKColors.Red, doc.GetPixel(10, 10));
        Assert.Equal(SKColors.Black, doc.GetPixel(5, 10));   // the wall
        Assert.Equal(SKColors.White, doc.GetPixel(1, 1));    // outside

        Assert.True(doc.Undo());
        Assert.Equal(SKColors.White, doc.GetPixel(10, 10));
    }

    [Fact]
    public void FloodFill_WithTheSameColour_RecordsNothing()
    {
        using var doc = new PaintDocument(8, 8, SKColors.White);
        Assert.False(doc.FloodFill(3, 3, SKColors.White, 0));
        Assert.False(doc.CanUndo);
    }

    [Fact]
    public void FloodFill_Tolerance_ReachesNearbyShades()
    {
        using var doc = new PaintDocument(10, 1, SKColors.White);
        doc.Draw(c =>
        {
            using var p = new SKPaint { Color = new SKColor(250, 250, 250), IsAntialias = false };
            c.DrawRect(5, 0, 5, 1, p);
        }, doc.Bounds);

        doc.FloodFill(0, 0, SKColors.Blue, 0);
        Assert.Equal(new SKColor(250, 250, 250), doc.GetPixel(7, 0));

        doc.Undo();
        doc.FloodFill(0, 0, SKColors.Blue, 10);
        Assert.Equal(SKColors.Blue, doc.GetPixel(7, 0));
    }

    [Fact]
    public void Rotated_SwapsTheSize_AndMovesTheCorner()
    {
        using var src = PaintDocument.NewBitmap(4, 2);
        src.Erase(SKColors.White);
        src.SetPixel(0, 0, SKColors.Red);

        using var turned = PaintDocument.Rotated(src, 90);

        Assert.Equal(2, turned.Width);
        Assert.Equal(4, turned.Height);
        // Clockwise: the top-left corner ends up top-right.
        Assert.Equal(SKColors.Red, turned.GetPixel(1, 0));
    }

    [Fact]
    public void Flipped_Horizontal_MirrorsLeftToRight()
    {
        using var src = PaintDocument.NewBitmap(3, 1);
        src.Erase(SKColors.White);
        src.SetPixel(0, 0, SKColors.Red);

        using var flipped = PaintDocument.Flipped(src, horizontal: true);
        Assert.Equal(SKColors.Red, flipped.GetPixel(2, 0));
    }

    /// <summary>Skia paints premultiplied, Magick reads straight alpha; a half-transparent pixel must survive both ways.</summary>
    [Fact]
    public void StraightAlpha_RoundTripsThroughTheDocument()
    {
        var bytes = new byte[] { 40, 80, 200, 128 }; // B G R A
        using var doc = PaintDocument.FromUnpremulBgra(bytes, 1, 1);
        var back = doc.ToUnpremulBgra();
        for (int i = 0; i < 4; i++) Assert.InRange(back[i] - bytes[i], -2, 2);
    }

    [Fact]
    public void Inverted_FlipsColourButNotAlpha()
    {
        using var doc = new PaintDocument(1, 1, new SKColor(10, 20, 30));
        using var inverted = PaintDocument.Inverted(doc.Bitmap);
        Assert.Equal(new SKColor(245, 235, 225), inverted.GetPixel(0, 0));
    }

    [Fact]
    public void Skewed_GrowsTheCanvasToHoldTheSlant()
    {
        using var src = PaintDocument.NewBitmap(20, 20);
        using var skewed = PaintDocument.Skewed(src, 45, 0, SKColors.White);
        Assert.InRange(skewed.Width, 39, 41);
        Assert.Equal(20, skewed.Height);
    }

    [Fact]
    public void Undo_IsCappedByMemory_ButKeepsAFewSteps()
    {
        long saved = PaintDocument.UndoBudgetBytes;
        try
        {
            PaintDocument.UndoBudgetBytes = 1; // everything is over budget
            using var doc = new PaintDocument(16, 16, SKColors.White);
            for (int i = 0; i < 10; i++) doc.BeginOperation();
            Assert.Equal(3, doc.UndoCount);
        }
        finally
        {
            PaintDocument.UndoBudgetBytes = saved;
        }
    }

    [Fact]
    public void Redo_IsClearedByANewChange()
    {
        using var doc = new PaintDocument(4, 4, SKColors.White);
        doc.FloodFill(0, 0, SKColors.Red, 0);
        doc.Undo();
        Assert.True(doc.CanRedo);

        doc.FloodFill(0, 0, SKColors.Blue, 0);
        Assert.False(doc.CanRedo);
    }

    [Theory]
    [MemberData(nameof(AllShapes))]
    public void EveryShape_FitsTheBoxItWasDrawnIn(ShapeKind kind)
    {
        if (!ShapeGeometry.IsBoxed(kind)) return;
        using var path = ShapeGeometry.Build(kind, new SKRect(10, 20, 110, 70));
        var b = path.Bounds;
        Assert.False(path.IsEmpty);
        Assert.True(b.Left >= 9.5 && b.Top >= 19.5 && b.Right <= 110.5 && b.Bottom <= 70.5, $"{kind}: {b}");
        Assert.True(b.Width > 50 && b.Height > 25, $"{kind} is too small: {b}");
    }

    public static IEnumerable<object[]> AllShapes() => Enum.GetValues<ShapeKind>().Select(k => new object[] { k });

    /// <summary>The gallery icons are parsed from the real geometry; one that fails to parse is a blank button.</summary>
    [AvaloniaFact]
    public void EveryShapeInTheGallery_HasAnIcon()
    {
        var vm = new MainWindowViewModel();
        Assert.Equal(Enum.GetValues<ShapeKind>().Length, vm.ShapeGallery.Count);
        Assert.All(vm.ShapeGallery, o => Assert.NotNull(o.Icon));
    }

    // ---- Entering and leaving -----------------------------------------------------

    [AvaloniaFact]
    public async Task EnteringEditMode_PutsTheDocumentOnScreen()
    {
        var vm = await Editing(Png("a.png", 40, 30));

        Assert.NotNull(vm.Document);
        Assert.Equal(40, vm.Document!.Width);
        Assert.Equal(30, vm.Document.Height);
        Assert.IsType<WriteableBitmap>(vm.ImageSource);
        Assert.False(vm.IsDirty);
        Assert.Equal("40 × 30px", vm.CanvasSizeText);
    }

    /// <summary>
    /// A phone photo tagged "rotate 90" is shown upright. The canvas has to be that same
    /// way up -- painting on a sideways canvas would put every stroke in the wrong place.
    /// </summary>
    [AvaloniaFact]
    public async Task EditMode_PaintsOnThePictureTheWayUpItIsShown()
    {
        var path = Path.Combine(_dir, "portrait.jpg");
        using (var img = new MagickImage(MagickColors.White, 60, 40))
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Orientation, (ushort)6);
            img.SetProfile(exif);
            img.Orientation = OrientationType.RightTop;
            img.Write(path, MagickFormat.Jpeg);
        }

        var vm = await Editing(path);

        Assert.Equal(40, vm.Document!.Width);
        Assert.Equal(60, vm.Document.Height);
        Assert.Equal(0, vm.RotationAngle); // the view no longer turns it; the pixels already are
    }

    [AvaloniaFact]
    public async Task LoadingAnotherPicture_EndsTheEditSession()
    {
        var a = Png("a.png");
        var b = Png("b.png");
        var vm = await Editing(a);

        await vm.LoadImageAsync(b);

        Assert.False(vm.IsEditMode);
        Assert.Null(vm.Document);
        Assert.False(vm.CanUndo);
    }

    [AvaloniaFact]
    public async Task Discard_PutsTheFileBackOnScreen()
    {
        var path = Png("a.png");
        var vm = await Editing(path);
        vm.Tool = PaintTool.Fill;
        vm.PaintPointerDown(5, 5, secondary: false);

        await vm.ExitEditMode(discard: true);

        Assert.False(vm.IsEditMode);
        Assert.False(vm.IsDirty);
        Assert.IsNotType<WriteableBitmap>(vm.ImageSource);
        Assert.Equal(new MagickColor("white"), FilePixel(path, 5, 5));
    }

    // ---- Tools --------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Pencil_DrawsColour1_AndUndoTakesItBack()
    {
        var vm = await Editing(Png("a.png"));
        vm.Tool = PaintTool.Pencil;
        vm.StrokeSize = 1;
        vm.PrimaryColor = Colors.Red;

        vm.PaintPointerDown(2.5, 5.5, secondary: false);
        vm.PaintPointerMove(20.5, 5.5);
        vm.PaintPointerUp(20.5, 5.5);

        Assert.Equal(SKColors.Red, Pixel(vm, 10, 5));
        Assert.True(vm.CanUndo);
        Assert.True(vm.IsDirty);

        vm.Undo();
        Assert.Equal(SKColors.White, Pixel(vm, 10, 5));
        Assert.False(vm.IsDirty);
        Assert.True(vm.CanRedo);
    }

    [AvaloniaFact]
    public async Task RightButton_PaintsWithColour2()
    {
        var vm = await Editing(Png("a.png"));
        vm.Tool = PaintTool.Pencil;
        vm.StrokeSize = 1;
        vm.SecondaryColor = Colors.Lime;

        vm.PaintPointerDown(4.5, 4.5, secondary: true);
        vm.PaintPointerUp(4.5, 4.5);

        Assert.Equal(new SKColor(0, 255, 0), Pixel(vm, 4, 4));
    }

    [AvaloniaFact]
    public async Task Eraser_PaintsColour2()
    {
        var vm = await Editing(Png("a.png", color: "black"));
        vm.Tool = PaintTool.Eraser;
        vm.StrokeSize = 6;
        vm.SecondaryColor = Colors.White;

        vm.PaintPointerDown(10, 10, secondary: false);
        vm.PaintPointerUp(10, 10);

        Assert.Equal(SKColors.White, Pixel(vm, 10, 10));
        Assert.Equal(SKColors.Black, Pixel(vm, 30, 20));
    }

    /// <summary>Right-dragging the eraser swaps colour 1 for colour 2 and leaves everything else.</summary>
    [AvaloniaFact]
    public async Task Eraser_RightDrag_ReplacesOnlyColour1()
    {
        var vm = await Editing(Png("a.png", color: "red"));
        vm.Document!.Draw(c =>
        {
            using var p = new SKPaint { Color = SKColors.Blue, IsAntialias = false };
            c.DrawRect(10, 10, 2, 2, p);
        }, vm.Document.Bounds);
        vm.Tool = PaintTool.Eraser;
        vm.StrokeSize = 20;
        vm.PrimaryColor = Colors.Red;
        vm.SecondaryColor = Colors.Yellow;

        vm.PaintPointerDown(10, 10, secondary: true);
        vm.PaintPointerUp(10, 10);

        Assert.Equal(new SKColor(255, 255, 0), Pixel(vm, 5, 5));
        Assert.Equal(SKColors.Blue, Pixel(vm, 10, 10));
    }

    [AvaloniaFact]
    public async Task Brush_WithEveryKind_LeavesPaint()
    {
        foreach (var kind in Enum.GetValues<BrushKind>())
        {
            var vm = await Editing(Png($"{kind}.png", 60, 40));
            vm.BrushKind = kind;
            vm.StrokeSize = 12;
            vm.PrimaryColor = Colors.Black;

            vm.PaintPointerDown(10, 20, secondary: false);
            for (int x = 12; x <= 50; x += 2) vm.PaintPointerMove(x, 20);
            vm.SprayTick();
            vm.PaintPointerUp(50, 20);

            var bytes = vm.Document!.ToUnpremulBgra();
            int painted = 0;
            for (int i = 0; i < bytes.Length; i += 4) if (bytes[i] < 250) painted++;
            _out.WriteLine($"{kind}: {painted} pixels");
            Assert.True(painted > 20, $"{kind} painted only {painted} pixels");
            Assert.Equal(1, vm.Document.UndoCount);
        }
    }

    /// <summary>The marker is translucent, and crossing its own stroke must not darken it.</summary>
    [AvaloniaFact]
    public async Task Marker_DoesNotBuildUpWhereItOverlapsItself()
    {
        var vm = await Editing(Png("a.png", 60, 40));
        vm.BrushKind = BrushKind.Marker;
        vm.StrokeSize = 10;
        vm.PrimaryColor = Colors.Black;

        vm.PaintPointerDown(10, 20, false);
        vm.PaintPointerMove(50, 20);
        var once = Pixel(vm, 30, 20);
        vm.PaintPointerMove(10, 20);
        vm.PaintPointerMove(50, 20);
        vm.PaintPointerUp(50, 20);

        AssertColor(once, Pixel(vm, 30, 20));
        Assert.NotEqual(SKColors.White, once);
        Assert.NotEqual(SKColors.Black, once);
    }

    [AvaloniaFact]
    public async Task Fill_FloodsTheArea()
    {
        var vm = await Editing(Png("a.png"));
        vm.Tool = PaintTool.Fill;
        vm.PrimaryColor = Colors.Blue;

        vm.PaintPointerDown(3, 3, secondary: false);

        Assert.Equal(SKColors.Blue, Pixel(vm, 39, 29));
    }

    [AvaloniaFact]
    public async Task Picker_SetsTheColour_AndHandsBackThePreviousTool()
    {
        var vm = await Editing(Png("a.png", color: "#336699"));
        vm.Tool = PaintTool.Fill;
        vm.Tool = PaintTool.Picker;

        vm.PaintPointerDown(3, 3, secondary: false);

        Assert.Equal(Color.Parse("#336699"), vm.PrimaryColor);
        Assert.Equal(PaintTool.Fill, vm.Tool);
    }

    [AvaloniaFact]
    public async Task Rectangle_DrawsAnOutline_AndFillsWithColour2()
    {
        var vm = await Editing(Png("a.png", 60, 40));
        vm.ActiveShape = ShapeKind.Rectangle;
        vm.StrokeSize = 2;
        vm.PrimaryColor = Colors.Black;
        vm.SecondaryColor = Colors.Red;
        vm.FillIndex = 1;

        vm.PaintPointerDown(10, 10, false);
        vm.PaintPointerMove(30, 25);
        vm.PaintPointerMove(40, 30); // the preview is redrawn, not stacked
        vm.PaintPointerUp(40, 30);

        Assert.Equal(SKColors.Black, Pixel(vm, 10, 20));
        Assert.Equal(SKColors.Red, Pixel(vm, 25, 20));
        // Where the first preview's corner was: wiped when it was redrawn larger.
        Assert.NotEqual(SKColors.Black, Pixel(vm, 30, 25));
        Assert.Equal(1, vm.Document!.UndoCount);
    }

    [AvaloniaFact]
    public async Task AShapeClickWithoutADrag_LeavesNoUndoStep()
    {
        var vm = await Editing(Png("a.png"));
        vm.ActiveShape = ShapeKind.Oval;

        vm.PaintPointerDown(10, 10, false);
        vm.PaintPointerUp(10, 10);

        Assert.False(vm.CanUndo);
        Assert.False(vm.IsDirty);
    }

    [AvaloniaFact]
    public async Task Shift_MakesASquare()
    {
        var vm = await Editing(Png("a.png", 60, 60));
        vm.ActiveShape = ShapeKind.Rectangle;
        vm.FillIndex = 1;
        vm.SecondaryColor = Colors.Red;

        vm.PaintPointerDown(10, 10, false);
        vm.PaintPointerMove(40, 20, shift: true);
        vm.PaintPointerUp(40, 20, shift: true);

        Assert.Equal(SKColors.Red, Pixel(vm, 25, 35)); // below where the unconstrained box would end
    }

    [AvaloniaFact]
    public async Task Polygon_TakesCornersUntilADoubleClick()
    {
        var vm = await Editing(Png("a.png", 60, 60));
        vm.ActiveShape = ShapeKind.Polygon;
        vm.FillIndex = 1;
        vm.SecondaryColor = Colors.Red;

        vm.PaintPointerDown(10, 10, false);
        vm.PaintPointerMove(50, 10);
        vm.PaintPointerUp(50, 10);
        vm.PaintPointerDown(50, 50, false);
        vm.PaintPointerUp(50, 50);
        vm.PaintPointerDown(50, 50, false, clickCount: 2);

        Assert.Equal(SKColors.Red, Pixel(vm, 40, 20)); // inside the closed triangle
        Assert.Equal(1, vm.Document!.UndoCount);
    }

    [AvaloniaFact]
    public async Task Curve_BendsOnTheFollowingClicks()
    {
        var vm = await Editing(Png("a.png", 60, 60));
        vm.ActiveShape = ShapeKind.Curve;
        vm.StrokeSize = 3;

        vm.PaintPointerDown(5, 30, false);
        vm.PaintPointerMove(55, 30);
        vm.PaintPointerUp(55, 30);
        Assert.Equal(SKColors.Black, Pixel(vm, 30, 30));

        vm.PaintPointerDown(30, 0, false);
        vm.PaintPointerUp(30, 0);
        vm.PaintPointerDown(30, 0, false);
        vm.PaintPointerUp(30, 0);

        // Bent upwards: the straight line's middle is gone.
        Assert.Equal(SKColors.White, Pixel(vm, 30, 30));
        Assert.Equal(1, vm.Document!.UndoCount);
    }

    [AvaloniaFact]
    public async Task Text_IsBurnedInWhenTheBoxIsCommitted()
    {
        var vm = await Editing(Png("a.png", 120, 60));
        vm.Tool = PaintTool.Text;
        vm.PrimaryColor = Colors.Black;
        vm.TextSizePt = 24;

        vm.PaintPointerDown(5, 5, false);
        Assert.True(vm.IsTextEditing);
        vm.TextContent = "Hi";
        vm.PaintPointerDown(100, 50, false); // a click outside commits

        Assert.False(vm.IsTextEditing);
        var bytes = vm.Document!.ToUnpremulBgra();
        int ink = 0;
        for (int i = 0; i < bytes.Length; i += 4) if (bytes[i] < 128) ink++;
        Assert.True(ink > 30, $"only {ink} dark pixels");
        Assert.Equal(1, vm.Document.UndoCount);
    }

    [AvaloniaFact]
    public async Task EmptyText_RecordsNothing()
    {
        var vm = await Editing(Png("a.png"));
        vm.Tool = PaintTool.Text;
        vm.PaintPointerDown(5, 5, false);
        vm.CommitText();
        Assert.False(vm.CanUndo);
    }

    // ---- Selection ---------------------------------------------------------------

    private static void Select(MainWindowViewModel vm, int x1, int y1, int x2, int y2)
    {
        vm.Tool = PaintTool.Select;
        vm.PaintPointerDown(x1, y1, false);
        vm.PaintPointerMove(x2, y2);
        vm.PaintPointerUp(x2, y2);
    }

    [AvaloniaFact]
    public async Task DraggingASelection_MovesItsPixels_AndLeavesColour2Behind()
    {
        var vm = await Editing(Png("a.png", 60, 40));
        vm.Document!.Draw(c =>
        {
            using var p = new SKPaint { Color = SKColors.Blue, IsAntialias = false };
            c.DrawRect(0, 0, 10, 10, p);
        }, vm.Document.Bounds);
        vm.SecondaryColor = Colors.Yellow;

        Select(vm, 0, 0, 10, 10);
        Assert.True(vm.HasSelection);
        Assert.Equal("10 × 10px", vm.SelectionText);

        vm.PaintPointerDown(5, 5, false);
        vm.PaintPointerMove(25, 15);
        vm.PaintPointerUp(25, 15);

        Assert.Equal(SKColors.Blue, Pixel(vm, 25, 15));
        Assert.Equal(new SKColor(255, 255, 0), Pixel(vm, 5, 5));

        vm.Undo();
        Assert.Equal(SKColors.Blue, Pixel(vm, 5, 5));
        Assert.False(vm.HasSelection);
    }

    [AvaloniaFact]
    public async Task CtrlDrag_CopiesTheSelection()
    {
        var vm = await Editing(Png("a.png", 60, 40));
        vm.Document!.Draw(c =>
        {
            using var p = new SKPaint { Color = SKColors.Blue, IsAntialias = false };
            c.DrawRect(0, 0, 10, 10, p);
        }, vm.Document.Bounds);

        Select(vm, 0, 0, 10, 10);
        vm.PaintPointerDown(5, 5, false, ctrl: true);
        vm.PaintPointerMove(35, 5);
        vm.PaintPointerUp(35, 5);

        Assert.Equal(SKColors.Blue, Pixel(vm, 5, 5));
        Assert.Equal(SKColors.Blue, Pixel(vm, 35, 5));
    }

    [AvaloniaFact]
    public async Task TransparentSelection_LetsColour2ShowThrough()
    {
        var vm = await Editing(Png("a.png", 60, 40, "white"));
        var doc = vm.Document!;
        doc.Draw(c =>
        {
            using var red = new SKPaint { Color = SKColors.Red, IsAntialias = false };
            c.DrawRect(30, 0, 30, 40, red);
            using var blue = new SKPaint { Color = SKColors.Blue, IsAntialias = false };
            c.DrawRect(2, 2, 4, 4, blue);
        }, doc.Bounds);
        vm.SecondaryColor = Colors.White;
        vm.TransparentSelection = true;

        Select(vm, 0, 0, 10, 10);
        vm.PaintPointerDown(5, 5, false);
        vm.PaintPointerMove(35, 5);
        vm.PaintPointerUp(35, 5);

        Assert.Equal(SKColors.Blue, Pixel(vm, 33, 3));
        Assert.Equal(SKColors.Red, Pixel(vm, 39, 9)); // the selection's white dropped out
    }

    [AvaloniaFact]
    public async Task CropToSelection_ShrinksTheCanvas()
    {
        var vm = await Editing(Png("a.png", 60, 40));
        int resized = 0;
        vm.CanvasResized += () => resized++;

        Select(vm, 10, 5, 30, 25);
        vm.CropToSelection();

        Assert.Equal(20, vm.Document!.Width);
        Assert.Equal(20, vm.Document.Height);
        Assert.Equal(20, vm.ImageWidth);
        Assert.Equal(1, resized);
        Assert.False(vm.HasSelection);

        vm.Undo();
        Assert.Equal(60, vm.Document.Width);
    }

    [AvaloniaFact]
    public async Task DeleteSelection_FillsItWithColour2()
    {
        var vm = await Editing(Png("a.png", 60, 40, "black"));
        vm.SecondaryColor = Colors.White;

        Select(vm, 10, 10, 20, 20);
        vm.DeleteSelection();

        Assert.Equal(SKColors.White, Pixel(vm, 15, 15));
        Assert.Equal(SKColors.Black, Pixel(vm, 25, 25));
    }

    [AvaloniaFact]
    public async Task FreeFormSelection_OnlyTakesWhatWasCircled()
    {
        var vm = await Editing(Png("a.png", 60, 60, "blue"));
        vm.SecondaryColor = Colors.White;
        vm.Tool = PaintTool.FreeSelect;

        vm.PaintPointerDown(10, 10, false);
        vm.PaintPointerMove(40, 10);
        vm.PaintPointerMove(10, 40);
        vm.PaintPointerUp(10, 40);
        Assert.True(vm.HasSelection);

        vm.DeleteSelection();
        Assert.Equal(SKColors.White, Pixel(vm, 15, 15));     // inside the triangle
        Assert.Equal(SKColors.Blue, Pixel(vm, 35, 35));      // inside its box, outside the triangle
    }

    [AvaloniaFact]
    public async Task SelectAll_ThenInvert_SelectsNothing()
    {
        var vm = await Editing(Png("a.png"));
        vm.SelectAll();
        Assert.Equal("40 × 30px", vm.SelectionText);

        vm.InvertSelection();
        Assert.False(vm.HasSelection);
    }

    [AvaloniaFact]
    public async Task Nudge_MovesTheSelectionAPixel()
    {
        var vm = await Editing(Png("a.png", 20, 20));
        vm.Document!.Document_SetPixel(5, 5, SKColors.Red);

        Select(vm, 4, 4, 7, 7);
        vm.NudgeSelection(1, 0);

        Assert.Equal(SKColors.Red, Pixel(vm, 6, 5));
    }

    [AvaloniaFact]
    public async Task PastedPixels_FloatAndCanBeMoved()
    {
        var vm = await Editing(Png("a.png", 60, 40));
        var pasted = PaintDocument.NewBitmap(5, 5);
        pasted.Erase(SKColors.Green);

        vm.PasteBitmap(pasted, new PixelPoint(0, 0));
        Assert.True(vm.IsFloating);
        Assert.Equal(PaintTool.Select, vm.Tool);

        vm.PaintPointerDown(2, 2, false);
        vm.PaintPointerMove(22, 12);
        vm.PaintPointerUp(22, 12);

        Assert.Equal(new SKColor(0, 128, 0), Pixel(vm, 22, 12));
        Assert.Equal(SKColors.White, Pixel(vm, 2, 2));
    }

    [AvaloniaFact]
    public async Task PastingSomethingBigger_GrowsTheCanvas()
    {
        var vm = await Editing(Png("a.png", 20, 20));
        var pasted = PaintDocument.NewBitmap(50, 10);

        vm.PasteBitmap(pasted, new PixelPoint(5, 5));

        Assert.Equal(50, vm.Document!.Width);
        Assert.Equal(20, vm.Document.Height);
    }

    // ---- Image operations --------------------------------------------------------

    [AvaloniaFact]
    public async Task RotateInEditMode_TurnsThePixels_NotTheView()
    {
        var vm = await Editing(Png("a.png", 40, 30));

        vm.RotateRight();

        Assert.Equal(30, vm.Document!.Width);
        Assert.Equal(40, vm.Document.Height);
        Assert.Equal(0, vm.RotationAngle);
        Assert.True(vm.IsDirty);
    }

    [AvaloniaFact]
    public async Task Resize_ScalesTheCanvas()
    {
        var vm = await Editing(Png("a.png", 40, 30));
        vm.ResizeCanvas(80, 60);
        Assert.Equal(80, vm.Document!.Width);
        Assert.Equal(60, vm.Document.Height);
    }

    [AvaloniaFact]
    public async Task Resize_WithASelection_ScalesOnlyTheSelection()
    {
        var vm = await Editing(Png("a.png", 60, 40));
        Select(vm, 0, 0, 10, 10);
        vm.ResizeCanvas(20, 20);

        Assert.Equal(60, vm.Document!.Width);
        Assert.Equal(new PixelSize(20, 20), vm.ResizeSubjectSize);
    }

    [AvaloniaFact]
    public async Task ImageProperties_ChangesTheCanvasWithoutScaling()
    {
        var vm = await Editing(Png("a.png", 40, 30, "blue"));
        vm.SecondaryColor = Colors.White;

        vm.SetCanvasSize(50, 30);

        Assert.Equal(50, vm.Document!.Width);
        Assert.Equal(SKColors.Blue, Pixel(vm, 39, 10));
        Assert.Equal(SKColors.White, Pixel(vm, 45, 10));
    }

    [AvaloniaFact]
    public async Task InvertColors_InvertsTheCanvas()
    {
        var vm = await Editing(Png("a.png", color: "black"));
        vm.InvertColors();
        Assert.Equal(SKColors.White, Pixel(vm, 5, 5));
    }

    // ---- Saving ------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Save_WritesThePaintIntoTheFile()
    {
        var path = Png("a.png", 40, 30);
        var vm = await Editing(path);
        vm.Tool = PaintTool.Fill;
        vm.PrimaryColor = Colors.Red;
        vm.PaintPointerDown(5, 5, false);

        await vm.SaveInPlaceAsync();

        Assert.Null(vm.ErrorMessage);
        Assert.False(vm.IsEditMode);
        Assert.False(vm.IsDirty);
        Assert.Equal(new MagickColor("red"), FilePixel(path, 20, 15));
    }

    /// <summary>The colour sliders are the edit mode Atelier already had; they still reach the file.</summary>
    [AvaloniaFact]
    public async Task Save_StillAppliesTheAdjustments()
    {
        var path = Png("a.png", 20, 20, "white");
        var vm = await Editing(path);
        vm.Brightness = 0;
        Assert.True(vm.IsDirty);

        await vm.SaveEditedImageAsync();

        Assert.Equal(new MagickColor("black"), FilePixel(path, 10, 10));
    }

    [AvaloniaFact]
    public async Task AdjustedPreview_IsRenderedOverThePaint()
    {
        var vm = await Editing(Png("a.png", 20, 20, "white"));
        vm.ApplyFilter("Negate");

        await vm.RunPreview();
        Dispatcher.UIThread.RunJobs();

        var display = (WriteableBitmap)vm.ImageSource!;
        using var fb = display.Lock();
        unsafe
        {
            uint px = *(uint*)fb.Address;
            Assert.Equal(0xFF000000u, px); // white, negated
        }
        // ...while the document itself keeps the unadjusted pixels.
        Assert.Equal(SKColors.White, Pixel(vm, 0, 0));
    }

    [AvaloniaFact]
    public async Task SavingAnEditedRotatedPhoto_ClearsTheOrientationTag()
    {
        var path = Path.Combine(_dir, "portrait.jpg");
        using (var img = new MagickImage(MagickColors.White, 60, 40))
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Orientation, (ushort)6);
            img.SetProfile(exif);
            img.Orientation = OrientationType.RightTop;
            img.Write(path, MagickFormat.Jpeg);
        }

        var vm = await Editing(path);
        vm.Tool = PaintTool.Pencil;
        vm.PaintPointerDown(5, 5, false);
        vm.PaintPointerUp(5, 5);
        await vm.SaveEditedImageAsync();

        using var saved = new MagickImage(path);
        Assert.Equal(40u, saved.Width);
        Assert.Equal(60u, saved.Height);
        var tag = saved.GetExifProfile()?.GetValue(ExifTag.Orientation)?.Value;
        Assert.True(tag is null or 1, $"orientation tag {tag}");
        // And it reopens upright, not turned a second time.
        Assert.Equal(0, vm.RotationAngle);
        Assert.Equal(40, vm.DisplayWidth);
    }

    [AvaloniaFact]
    public async Task SaveAs_WritesANewFile_AndOpensIt()
    {
        var path = Png("a.png");
        var vm = await Editing(path);
        vm.InvertColors();
        var copy = Path.Combine(_dir, "copy.jpg");

        await vm.SaveEditedImageAsync(copy);

        Assert.True(File.Exists(copy));
        Assert.Equal(copy, vm.ImagePath);
        Assert.Equal(new MagickColor("white"), FilePixel(path, 0, 0)); // the original untouched
    }

    [AvaloniaFact]
    public async Task SavingAnOpaquePicture_DoesNotAddAnAlphaChannel()
    {
        var path = Path.Combine(_dir, "opaque.png");
        using (var img = new MagickImage(MagickColors.White, 10, 10))
        {
            img.Alpha(AlphaOption.Off);
            img.Write(path, MagickFormat.Png);
        }
        var vm = await Editing(path);
        vm.InvertColors();
        await vm.SaveEditedImageAsync();

        using var saved = new MagickImage(path);
        Assert.False(saved.HasAlpha);
    }

    // ---- Through a real window ----------------------------------------------------

    private static async Task<(MainWindow Win, MainWindowViewModel Vm)> EditingWindow(string path)
    {
        var vm = new MainWindowViewModel();
        var win = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
        win.Show();
        await win.LoadAndFitAsync(path);
        Dispatcher.UIThread.RunJobs();
        win.EditImage_Click(null, new Avalonia.Interactivity.RoutedEventArgs());
        Dispatcher.UIThread.RunJobs();
        win.UpdateLayout();
        return (win, vm);
    }

    /// <summary>Where canvas pixel (x, y) is on the window -- the whole zoom and centring chain, inverted.</summary>
    private static Point ScreenOf(MainWindow win, double x, double y)
    {
        var image = win.FindControl<Image>("CanvasImage")!;
        var vm = (MainWindowViewModel)win.DataContext!;
        var local = new Point(x * image.Bounds.Width / vm.Document!.Width, y * image.Bounds.Height / vm.Document.Height);
        return image.TranslatePoint(local, win)!.Value;
    }

    [AvaloniaFact]
    public async Task AClickOnTheScreen_PaintsThePixelUnderIt()
    {
        var (win, vm) = await EditingWindow(Png("a.png", 200, 100));
        vm.Tool = PaintTool.Pencil;
        vm.StrokeSize = 1;
        vm.PrimaryColor = Colors.Red;

        var at = ScreenOf(win, 150.5, 30.5);
        win.MouseDown(at, MouseButton.Left);
        win.MouseUp(at, MouseButton.Left);

        Assert.Equal(SKColors.Red, Pixel(vm, 150, 30));
        Assert.Equal(SKColors.White, Pixel(vm, 50, 30));
        win.Close();
    }

    [AvaloniaFact]
    public async Task DraggingOnTheScreen_PaintsAStroke_RatherThanPanning()
    {
        var (win, vm) = await EditingWindow(Png("a.png", 200, 100));
        vm.Tool = PaintTool.Pencil;
        vm.StrokeSize = 1;

        var from = ScreenOf(win, 20.5, 50.5);
        var to = ScreenOf(win, 180.5, 50.5);
        win.MouseDown(from, MouseButton.Left);
        win.MouseMove(to);
        win.MouseUp(to, MouseButton.Left);

        Assert.Equal(SKColors.Black, Pixel(vm, 100, 50));
        win.Close();
    }

    /// <summary>Delete in edit mode clears the selection. It must never send the file being edited to the bin.</summary>
    [AvaloniaFact]
    public async Task DeleteKey_InEditMode_NeverRecyclesTheFile()
    {
        var (win, vm) = await EditingWindow(Png("a.png", 60, 40));

        win.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(_recycled);
        Assert.True(vm.IsEditMode);
        win.Close();
    }

    [AvaloniaFact]
    public async Task ArrowKeys_InEditMode_DoNotLeaveThePicture()
    {
        var a = Png("a.png");
        Png("b.png");
        var (win, vm) = await EditingWindow(a);

        win.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(a, vm.ImagePath);
        Assert.True(vm.IsEditMode);
        win.Close();
    }

    [AvaloniaFact]
    public async Task CtrlZ_UndoesFromTheKeyboard()
    {
        var (win, vm) = await EditingWindow(Png("a.png"));
        vm.Tool = PaintTool.Fill;
        vm.PrimaryColor = Colors.Red;
        vm.PaintPointerDown(3, 3, false);

        win.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(SKColors.White, Pixel(vm, 3, 3));
        win.Close();
    }

    [AvaloniaFact]
    public async Task ToolLetters_SwitchTools()
    {
        var (win, vm) = await EditingWindow(Png("a.png"));

        win.KeyPress(Key.G, RawInputModifiers.None, PhysicalKey.G, "g");
        Assert.Equal(PaintTool.Fill, vm.Tool);
        win.KeyPress(Key.T, RawInputModifiers.None, PhysicalKey.T, "t");
        Assert.Equal(PaintTool.Text, vm.Tool);
        win.Close();
    }

    [AvaloniaFact]
    public async Task TheRibbon_IsOnlyThereWhileEditing()
    {
        var vm = new MainWindowViewModel();
        var win = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
        win.Show();
        await win.LoadAndFitAsync(Png("a.png"));
        var ribbon = win.FindControl<Border>("PaintRibbon")!;
        Assert.False(ribbon.IsVisible);

        win.EditImage_Click(null, new Avalonia.Interactivity.RoutedEventArgs());
        Assert.True(ribbon.IsVisible);

        await vm.ExitEditMode(discard: true);
        Assert.False(ribbon.IsVisible);
        win.Close();
    }
}

internal static class PaintTestExtensions
{
    public static void Document_SetPixel(this PaintDocument doc, int x, int y, SKColor color) =>
        doc.Draw(c =>
        {
            using var p = new SKPaint { Color = color, IsAntialias = false };
            c.DrawRect(x, y, 1, 1, p);
        }, doc.Bounds);
}
