using System;
using System.IO;
using Atelier;
using Xunit;
using Xunit.Abstractions;

namespace Atelier.ZoomTests;

/// <summary>
/// Exercises the real shell reader rather than the seam the navigation tests stub.
///
/// A test run has no Explorer window to point at, so the positive case cannot be
/// asserted here -- it was verified by hand against a live window. What these do cover
/// is the half that matters most for not regressing anyone: the whole COM walk runs on
/// a real machine, and every way it can come up empty produces null rather than an
/// exception on the picture-loading path.
/// </summary>
public class ExplorerOrderReaderTests
{
    private readonly ITestOutputHelper _out;
    public ExplorerOrderReaderTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// The common case for anyone who did not arrive from a folder window. Walking
    /// IShellWindows finds no match, and that has to be a quiet null.
    /// </summary>
    [Fact]
    public void AFolderNoWindowIsShowing_ReadsAsNull()
    {
        if (!OperatingSystem.IsWindows()) return;

        var dir = Path.Combine(Path.GetTempPath(), "atelier-noexplorer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Null(ExplorerOrder.TryGetViewOrder(dir));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void NonsensePaths_ReadAsNull()
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.Null(ExplorerOrder.TryGetViewOrder(""));
        Assert.Null(ExplorerOrder.TryGetViewOrder("Q:\\no\\such\\place"));
        Assert.Null(ExplorerOrder.TryGetViewOrder("not a path at all"));
    }

    /// <summary>
    /// Whatever the machine happens to have open. Asserts nothing about the contents --
    /// there may be no folder windows at all -- only that asking is safe and cheap, since
    /// this runs on every picture load.
    /// </summary>
    [Fact]
    public void ReadingIsSafeAndFast_WhateverIsOpen()
    {
        if (!OperatingSystem.IsWindows()) return;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = ExplorerOrder.TryGetViewOrder(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        sw.Stop();

        _out.WriteLine($"read in {sw.ElapsedMilliseconds} ms -> {(result == null ? "null" : result.Count + " items")}");
        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"the shell read sits on the image-loading path; took {sw.ElapsedMilliseconds} ms");
    }
}
