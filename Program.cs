using Avalonia;
using Avalonia.ReactiveUI;
using Avalonia.Svg.Skia;
using System;

namespace Atelier;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, FreeDesktop... or any other
    // standard .NET libraries until ApplicationMain is called: entropy causes fast
    // things to go wrong.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        GC.KeepAlive(typeof(SvgImageExtension).Assembly);
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI();
    }
}
