using System;
using System.IO;
using Atelier.ViewModels;
using Atelier.Views;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;

namespace Atelier.ZoomTests;

/// <summary>
/// Cover for the metadata pane's close button and the File &gt; Open File Location
/// menu item.
/// </summary>
public class PaneAndMenuTests
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

    /// <summary>
    /// Drives the real button rather than the handler, so a broken Click binding or a
    /// button that never made it into the tree fails here.
    /// </summary>
    [AvaloniaFact]
    public void CloseButton_HidesTheMetadataPane()
    {
        var (win, vm) = Show();
        Assert.True(vm.ShowMetadata, "metadata pane starts open");
        Assert.True(vm.IsRightPaneVisible);

        var close = win.FindControl<Button>("CloseMetadataButton");
        Assert.NotNull(close);
        Assert.True(close!.IsVisible, "close button must be shown in view mode");

        close.Command?.Execute(close.CommandParameter);
        close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        win.UpdateLayout();

        Assert.False(vm.ShowMetadata);
        Assert.False(vm.IsRightPaneVisible);
        Assert.False(win.FindControl<Border>("RightPane")!.IsVisible);
    }

    /// <summary>The close button is meaningless over the editor, so it hides there.</summary>
    [AvaloniaFact]
    public void CloseButton_IsHiddenInEditMode()
    {
        var (win, vm) = Show();
        vm.IsEditMode = true;
        Dispatcher.UIThread.RunJobs();
        win.UpdateLayout();

        Assert.False(win.FindControl<Button>("CloseMetadataButton")!.IsVisible);
    }

    /// <summary>
    /// The button is a second route to the same state as View &gt; Image Metadata,
    /// so the menu must be able to bring the pane back after the button hid it.
    /// </summary>
    [AvaloniaFact]
    public void MenuCanReopenThePane_AfterTheCloseButton()
    {
        var (win, vm) = Show();

        win.CloseMetadata_Click(null, new RoutedEventArgs());
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsRightPaneVisible);

        vm.ShowMetadata = true;   // what the checkable menu item binds to
        Dispatcher.UIThread.RunJobs();
        win.UpdateLayout();

        Assert.True(vm.IsRightPaneVisible);
        Assert.True(win.FindControl<Border>("RightPane")!.IsVisible);
    }

    /// <summary>
    /// In edit mode the pane holds the editor, so hiding metadata must not close it
    /// -- which is why the close button is bound to IsViewMode.
    /// </summary>
    [AvaloniaFact]
    public void EditMode_KeepsThePaneOpen_WhenMetadataIsHidden()
    {
        var (win, vm) = Show();
        vm.IsEditMode = true;
        vm.ShowMetadata = false;
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsRightPaneVisible, "the editor still needs the pane");
        Assert.False(vm.IsViewMode);
    }

    [AvaloniaFact]
    public void HasImage_TracksImagePath()
    {
        var vm = new MainWindowViewModel();
        Assert.False(vm.HasImage);

        var raised = false;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.HasImage)) raised = true; };

        vm.ImagePath = @"C:\pictures\photo.png";
        Assert.True(vm.HasImage);
        Assert.True(raised, "HasImage must notify or the menu item never enables");

        vm.ImagePath = null;
        Assert.False(vm.HasImage);
    }

    /// <summary>
    /// Guard only -- with no image loaded the handler must return without launching
    /// Explorer or throwing. The success path is not exercised here because it spawns
    /// a real Explorer window.
    /// </summary>
    [AvaloniaFact]
    public void OpenFileLocation_DoesNothing_WithoutAnImage()
    {
        var (win, vm) = Show();
        Assert.Null(vm.ImagePath);

        win.OpenFileLocation_Click(null, new RoutedEventArgs());

        Assert.Null(vm.ErrorMessage);
    }

    [AvaloniaFact]
    public void OpenFileLocation_ReportsAMissingFolder()
    {
        var (win, vm) = Show();
        vm.ImagePath = Path.Combine(Path.GetTempPath(), "atelier-gone-" + Guid.NewGuid().ToString("N"), "x.png");

        win.OpenFileLocation_Click(null, new RoutedEventArgs());

        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains("no longer exists", vm.ErrorMessage!);
    }
}
