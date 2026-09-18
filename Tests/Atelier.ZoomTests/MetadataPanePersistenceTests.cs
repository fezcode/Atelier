using System.IO;
using Atelier.Hoswl;
using Atelier.ViewModels;
using Atelier.Views;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Atelier.ZoomTests;

/// <summary>
/// View &gt; Image Metadata used to reset to "open" on every launch. It now lives in
/// settings.json, so a closed pane stays closed the next time Atelier starts.
/// </summary>
public class MetadataPanePersistenceTests
{
    private static (MainWindow win, MainWindowViewModel vm) Show()
    {
        var vm = new MainWindowViewModel();
        var win = new MainWindow { DataContext = vm, Width = 900, Height = 600 };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        win.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return (win, vm);
    }

    [AvaloniaFact]
    public void ClosingThePane_IsWrittenToSettings()
    {
        var (win, vm) = Show();
        Assert.True(vm.ShowMetadata);

        vm.ShowMetadata = false;                 // as the pane's X button and the menu do
        Dispatcher.UIThread.RunJobs();

        Assert.False(win.Settings.ShowMetadata);
        var file = win.Settings.FilePath!;
        Assert.True(File.Exists(file), "the choice has to reach disk, not just the in-memory object");
        Assert.Contains("\"showMetadata\": false", File.ReadAllText(file));
        win.Close();
    }

    /// <summary>
    /// The Hisashi switches live in the same file. A metadata write must merge with them,
    /// not rewrite the file from defaults -- both sides share one UserSettings instance.
    /// </summary>
    [AvaloniaFact]
    public void WritingTheMetadataChoice_KeepsTheHisashiSwitches()
    {
        var (win, vm) = Show();
        win.Settings.HisashiIntegration = true;

        vm.ShowMetadata = false;
        Dispatcher.UIThread.RunJobs();

        var json = File.ReadAllText(win.Settings.FilePath!);
        Assert.Contains("\"hisashiIntegration\": true", json);
        Assert.Contains("\"showMetadata\": false", json);
        win.Close();
    }

    [AvaloniaFact]
    public void ANewWindow_StartsFromTheSavedChoice()
    {
        var (first, vm) = Show();
        vm.ShowMetadata = false;
        Dispatcher.UIThread.RunJobs();
        first.Close();

        // A fresh window is what the next launch builds: it loads the same file.
        var (second, vm2) = Show();
        Assert.False(vm2.ShowMetadata);
        Assert.False(vm2.IsRightPaneVisible);

        vm2.ShowMetadata = true;
        Dispatcher.UIThread.RunJobs();
        second.Close();

        var (third, vm3) = Show();
        Assert.True(vm3.ShowMetadata);
        third.Close();
    }

    /// <summary>
    /// Hiding the pane is not enough: its grid column has to give its width back. A
    /// launch with the pane already closed used to leave the column at the 300px the
    /// XAML declares, so the viewer opened with an empty dark strip down the right
    /// edge that looked exactly like the pane it had just been told to hide.
    /// </summary>
    [AvaloniaFact]
    public void StartingWithThePaneClosed_LeavesTheViewerTheWholeWidth()
    {
        var (first, vm) = Show();
        vm.ShowMetadata = false;
        Dispatcher.UIThread.RunJobs();
        first.Close();

        var (second, vm2) = Show();
        Assert.False(vm2.IsRightPaneVisible);

        var grid = second.FindControl<Grid>("MainGrid")!;
        Assert.Equal(0, grid.ColumnDefinitions[1].ActualWidth);   // splitter
        Assert.Equal(0, grid.ColumnDefinitions[2].ActualWidth);   // the pane itself

        var scroll = second.FindControl<ScrollViewer>("MainScroll")!;
        Assert.Equal(grid.Bounds.Width, scroll.Bounds.Width, 1);

        second.Close();
    }

    /// <summary>A missing or corrupt settings file must not lose the pane.</summary>
    [AvaloniaFact]
    public void WithoutASettingsFile_ThePaneStartsOpen()
    {
        File.Delete(UserSettings.DefaultPath);
        var (win, vm) = Show();
        Assert.True(vm.ShowMetadata);
        win.Close();
    }
}
