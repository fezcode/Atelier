using Avalonia;
using Avalonia.Headless;
using Avalonia.ReactiveUI;

[assembly: AvaloniaTestApplication(typeof(Atelier.ZoomTests.TestAppBuilder))]

namespace Atelier.ZoomTests;

public static class TestAppBuilder
{
    // UseHeadlessDrawing = false keeps the real Skia backend. The headless stub reports
    // every bitmap as 1x1 and happily "decodes" arbitrary bytes, which would make the
    // decode tests assert nothing. Skia costs a little startup and needs no display.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Atelier.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .UseReactiveUI();
}
