using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Atelier.Hoswl;
using Atelier.ViewModels;
using Atelier.Views;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Atelier.ZoomTests;

/// <summary>
/// The Hisashi OS Window Layer integration: the menu tree is built from the real
/// XAML menu, clicks coming back drive the real items, and the pipe client speaks
/// the protocol against an in-test server.
/// </summary>
public class HoswlTests
{
    private static (MainWindow win, MainWindowViewModel vm, Menu menu) Show()
    {
        var vm = new MainWindowViewModel();
        var win = new MainWindow { DataContext = vm, Width = 900, Height = 600 };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        win.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return (win, vm, win.FindControl<Menu>("MainMenu")!);
    }

    private static JsonElement Menu(JsonDocument doc, string label) =>
        doc.RootElement.EnumerateArray().First(m => m.GetProperty("label").GetString() == label);

    private static JsonElement Item(JsonElement menu, string label) =>
        menu.GetProperty("items").EnumerateArray().First(i =>
            i.TryGetProperty("label", out var l) && l.GetString() == label);

    [AvaloniaFact]
    public void Builder_WalksTheRealMenu()
    {
        var (win, vm, menu) = Show();
        var map = new Dictionary<string, MenuItem>();
        var json = HoswlMenuBuilder.Build(menu, map);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(new[] { "File", "View", "Edit", "Help" },
            doc.RootElement.EnumerateArray().Select(m => m.GetProperty("label").GetString()).ToArray());

        var file = Menu(doc, "File");
        var open = Item(file, "Open...");
        Assert.Equal("Ctrl+O", open.GetProperty("key").GetString());
        Assert.Contains(file.GetProperty("items").EnumerateArray(), i => i.TryGetProperty("sep", out _));

        var wallpaper = Item(file, "Set as Wallpaper");
        Assert.Equal(6, wallpaper.GetProperty("items").GetArrayLength());
        Assert.False(wallpaper.GetProperty("enabled").GetBoolean());   // no image loaded

        var view = Menu(doc, "View");
        Assert.Equal("F", Item(view, "Full Screen").GetProperty("key").GetString());
        Assert.True(Item(view, "Image Metadata").GetProperty("check").GetBoolean());
        Assert.True(vm.ShowMetadata);

        // Every id maps back to a live MenuItem, and labels lost their access-key underscores.
        Assert.All(map.Values, mi => Assert.DoesNotContain("_", HoswlMenuBuilder.Label(mi)));
        Assert.True(map.Count > 10);
        win.Close();
    }

    [AvaloniaFact]
    public void Dispatch_TogglesACheckableItem_ThroughTheBinding()
    {
        var (win, vm, menu) = Show();
        var map = new Dictionary<string, MenuItem>();
        HoswlMenuBuilder.Build(menu, map);
        var id = map.First(kv => HoswlMenuBuilder.Label(kv.Value) == "Image Metadata").Key;

        Assert.True(HoswlMenuBuilder.Dispatch(map, id));
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.ShowMetadata);
        win.Close();
    }

    [AvaloniaFact]
    public void Dispatch_IgnoresDisabledAndUnknownIds()
    {
        var (win, _, menu) = Show();
        var map = new Dictionary<string, MenuItem>();
        HoswlMenuBuilder.Build(menu, map);
        var wallpaper = map.First(kv => HoswlMenuBuilder.Label(kv.Value) == "Set as Wallpaper").Key;
        Assert.False(HoswlMenuBuilder.Dispatch(map, wallpaper));   // submenu header, and disabled
        Assert.False(HoswlMenuBuilder.Dispatch(map, "nope"));
        win.Close();
    }

    [AvaloniaFact]
    public void Republish_FollowsViewModelChanges()
    {
        var (win, vm, _) = Show();
        var h = win.Hisashi;
        Assert.NotNull(h);
        h!.Publish(force: true);
        var before = h.LastJson!;
        Assert.Contains("\"check\":true", before);

        vm.ShowMetadata = false;
        Dispatcher.UIThread.RunJobs();
        h.Publish();
        Assert.Contains("\"check\":false", h.LastJson);
        win.Close();
    }

    [AvaloniaFact]
    public void MenusExternal_HidesTheStrip_AndRestoresIt()
    {
        var (win, _, menu) = Show();
        var h = win.Hisashi!;
        // Keep the test off the user's real settings.json and real Hisashi pipe.
        h.Settings.FilePath = Path.Combine(Path.GetTempPath(), "atelier-hoswl-test-" + Guid.NewGuid().ToString("N") + ".json");
        HisashiMenubar.PipeName = "hoswl-atelier-test-nobody-listens";
        var topBar = win.FindControl<Border>("TopBar")!;
        Assert.True(menu.IsVisible);

        // Settings default to integration off, so a connection alone must not hide anything.
        h.SetConnectedForTest(true);
        Assert.True(menu.IsVisible);

        h.SetIntegration(true);
        h.SetConnectedForTest(true);
        Assert.False(menu.IsVisible);
        Assert.Equal(40, topBar.Height);

        h.SetShowMenus(false);
        Assert.True(menu.IsVisible);
        Assert.Equal(80, topBar.Height);

        h.SetIntegration(false);
        try { File.Delete(h.Settings.FilePath!); } catch { }
        win.Close();
    }

    [Fact]
    public async Task Client_SpeaksTheProtocol_AgainstAnInTestServer()
    {
        var pipeName = "hoswl-atelier-test-" + Guid.NewGuid().ToString("N");
        // Explicit buffers: with the defaults (0) a write blocks until the peer reads it.
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536);
        var accepted = server.WaitForConnectionAsync();

        var clicked = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HoswlClient("com.fezcode.test", "Test", "1.0", pipeName) { RetryInterval = TimeSpan.FromMilliseconds(100) };
        client.OnClick += id => clicked.TrySetResult(id);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(true); };
        client.SetMenusJson("[{\"id\":\"file\",\"label\":\"File\",\"items\":[{\"id\":\"file.new\",\"label\":\"New\"}]}]");
        client.Start();
        await accepted.WaitAsync(TimeSpan.FromSeconds(5));
        var reader = new StreamReader(server, new UTF8Encoding(false));
        var hello = JsonDocument.Parse((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!).RootElement;
        Assert.Equal("hello", hello.GetProperty("t").GetString());
        Assert.Equal("com.fezcode.test", hello.GetProperty("app").GetString());
        Assert.Equal(Environment.ProcessId, hello.GetProperty("pid").GetInt32());
        var menu = JsonDocument.Parse((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!).RootElement;
        Assert.Equal("menu", menu.GetProperty("t").GetString());
        Assert.Equal("file", menu.GetProperty("menus")[0].GetProperty("id").GetString());
        Assert.True(await connected.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        var bytes = new UTF8Encoding(false).GetBytes("{\"t\":\"welcome\",\"v\":1,\"host\":\"Hisashi\",\"ver\":\"t\"}\n{\"t\":\"click\",\"id\":\"file.new\"}\n");
        await server.WriteAsync(bytes);
        Assert.Equal("file.new", await clicked.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        client.SetEnabled(false);
        var enable = JsonDocument.Parse((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!).RootElement;
        Assert.Equal("enable", enable.GetProperty("t").GetString());
        Assert.False(enable.GetProperty("on").GetBoolean());
        client.Stop();
        var bye = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("{\"t\":\"bye\"}", bye);
        Assert.False(client.IsConnected);
    }
}
