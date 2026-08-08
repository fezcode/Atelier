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
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var window = new MainWindow { DataContext = new MainWindowViewModel() };
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
                        await window.LoadAndFitAsync(path);
                    }
                }
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
