using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using AzureBlobDownloader.App.ViewModels;
using AzureBlobDownloader.App.Views;
using AzureBlobDownloader.Core.Configuration;

namespace AzureBlobDownloader.App;

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
            AppConfigLocations? locations = null;
            AppConfigLoadResult? load = null;
            string? configError = null;
            try
            {
                locations = AppConfigLocations.ForCurrentProcess(desktop.Args);
                load = AppConfigLoader.TryLoad(locations);
            }
            catch (AppConfigException ex)
            {
                configError = ex.Message;
            }

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(locations, load, configError),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
