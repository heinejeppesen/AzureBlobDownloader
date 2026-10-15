namespace AzureBlobDownloader.Core.Diagnostics;

/// <summary>
/// Minimal local log file, one per day, kept for 14 days. Never pass tokens or SAS strings to it.
/// Writing never throws.
/// </summary>
public static class AppLog
{
    private const int KeepDays = 14;
    private static readonly Lock Gate = new();
    private static bool _initialized;

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message} {exception.GetType().Name}: {exception.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                if (!_initialized)
                {
                    Directory.CreateDirectory(AppPaths.LogDirectory);
                    DeleteOldLogs();
                    _initialized = true;
                }

                var path = Path.Combine(AppPaths.LogDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {level,-5} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break the app.
        }
    }

    private static void DeleteOldLogs()
    {
        var cutoff = DateTime.Now.AddDays(-KeepDays);
        foreach (var file in Directory.EnumerateFiles(AppPaths.LogDirectory, "app-*.log"))
        {
            if (File.GetLastWriteTime(file) < cutoff)
            {
                File.Delete(file);
            }
        }
    }
}
