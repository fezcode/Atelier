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

        public override async void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var viewModel = new MainWindowViewModel();
                desktop.MainWindow = new MainWindow
                {
                    DataContext = viewModel
                };

                // Handle Command Line Arguments
                if (desktop.Args?.Length > 0)
                {
                    await viewModel.LoadImageAsync(desktop.Args[0]);
                }
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
