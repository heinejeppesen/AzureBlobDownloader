using Avalonia;
using System;

namespace AzureBlobDownloader.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // All traffic (sign-in and blob downloads) uses the system proxy; let it authenticate as the
        // signed-in Windows/macOS user when the proxy asks for credentials.
        System.Net.Http.HttpClient.DefaultProxy.Credentials = System.Net.CredentialCache.DefaultCredentials;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
