using AzureBlobDownloader.Core.Configuration;

namespace AzureBlobDownloader.Core.Diagnostics;

/// <summary>Per-user folders for app data (token cache, logs).</summary>
public static class AppPaths
{
    /// <summary>
    /// Windows: <c>%LOCALAPPDATA%\AzureBlobDownloader</c>; macOS: <c>~/Library/Application Support/AzureBlobDownloader</c>;
    /// Linux: <c>~/.local/share/AzureBlobDownloader</c>.
    /// </summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        AppConfigLoader.AppFolderName);

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
}
