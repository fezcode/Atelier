using System.Linq;
using Atelier.ViewModels;
using Atelier.Views;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Atelier
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            StartupTrace.Mark("App.Initialize");
            AvaloniaXamlLoader.Load(this);
            StartupTrace.Mark("App.axaml loaded");
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                StartupTrace.Mark("FrameworkInitCompleted");
                var vm = new MainWindowViewModel();
                StartupTrace.Mark("ViewModel created");
                var window = new MainWindow { DataContext = vm };
                StartupTrace.Mark("MainWindow created");
                window.Opened += (_, _) => { StartupTrace.Mark("Opened"); window.RequestAnimationFrame(_ => StartupTrace.Mark("First frame")); };
                desktop.MainWindow = window;

                // Atelier registers itself for file associations, so "open with" is the
                // common launch path. Kick the load off once the window is up rather than
                // awaiting it here -- awaiting kept the window off-screen for the whole
                // decode, so a large photo looked like a slow-starting app.
                if (desktop.Args?.Length > 0)
                {
                    var path = desktop.Args[0];
                    window.Opened += OnStartupOpen;

                    async void OnStartupOpen(object? sender, System.EventArgs e)
                    {
                        window.Opened -= OnStartupOpen;
                        StartupTrace.Mark("Load start");
                        await window.LoadAndFitAsync(path);
                        StartupTrace.Mark("Load done");
                        window.RequestAnimationFrame(_ => StartupTrace.Mark("Image frame"));
                    }
                }
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
