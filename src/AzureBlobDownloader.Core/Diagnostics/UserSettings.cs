using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzureBlobDownloader.Core.Diagnostics;

/// <summary>Small per-user conveniences remembered between runs (not configuration).</summary>
public sealed class UserSettings
{
    private static string FilePath => Path.Combine(AppPaths.DataDirectory, "settings.json");

    /// <summary>The folder the user last downloaded to; offered first in the folder picker.</summary>
    public string? LastDestination { get; set; }

    public static UserSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize(File.ReadAllText(FilePath), UserSettingsJsonContext.Default.UserSettings) ?? new UserSettings()
                : new UserSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new UserSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, UserSettingsJsonContext.Default.UserSettings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"Could not save settings: {ex.Message}");
        }
    }
}

[JsonSerializable(typeof(UserSettings))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class UserSettingsJsonContext : JsonSerializerContext;
