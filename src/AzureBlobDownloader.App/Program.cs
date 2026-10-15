using Avalonia;
using System;
using System.Runtime.InteropServices;

namespace AzureBlobDownloader.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Harden the native DLL search path on Windows, before anything loads a native library
        // (Skia, HarfBuzz, the MSAL runtime). This resolves native DLLs only from System32 and the
        // application directory, never the current working directory or PATH, so the app cannot be
        // made to load a planted DLL when it is launched with an attacker-controlled working directory.
        if (OperatingSystem.IsWindows())
        {
            SetDefaultDllDirectories(LoadLibrarySearchDefaultDirs);
        }

        // All traffic (sign-in and blob downloads) uses the system proxy; let it authenticate as the
        // signed-in Windows/macOS user when the proxy asks for credentials.
        System.Net.Http.HttpClient.DefaultProxy.Credentials = System.Net.CredentialCache.DefaultCredentials;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private const uint LoadLibrarySearchDefaultDirs = 0x00001000;

    // Best effort: a false return (unsupported, already set) only leaves the default search in place.
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

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
