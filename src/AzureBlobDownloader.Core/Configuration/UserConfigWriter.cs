using System.Text.Json;
using System.Text.Json.Nodes;

namespace AzureBlobDownloader.Core.Configuration;

/// <summary>
/// Saves the values entered on the setup screen into the per-user config file. Other settings already in
/// that file are kept. The file lives in the user's own profile, so no special permissions are needed
/// (unlike Program Files, a signed macOS app bundle or a read-only AppImage).
/// </summary>
public static class UserConfigWriter
{
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>Writes <paramref name="tenantId"/> and <paramref name="clientId"/> to the per-user file.</summary>
    /// <returns>The path of the file written.</returns>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder cannot be written.</exception>
    public static string SaveIdentity(string userDirectory, Guid tenantId, Guid clientId)
    {
        if (tenantId == Guid.Empty || clientId == Guid.Empty)
        {
            throw new ArgumentException("Tenant ID and Client ID must be set.");
        }

        Directory.CreateDirectory(userDirectory);
        var path = Path.Combine(userDirectory, AppConfigLoader.FileName);

        var root = ReadExisting(path);
        Set(root, nameof(AppConfig.TenantId), tenantId.ToString("D"));
        Set(root, nameof(AppConfig.ClientId), clientId.ToString("D"));

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, root.ToJsonString(WriteOptions));
        File.Move(temporary, path, overwrite: true);
        return path;
    }

    private static JsonObject ReadExisting(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(path), documentOptions: ReadOptions) is JsonObject existing)
            {
                return existing;
            }
        }
        catch (JsonException)
        {
            // Fall through: keep the damaged file as a backup and start a new one.
        }

        File.Copy(path, path + ".bak", overwrite: true);
        return [];
    }

    /// <summary>Sets a property, replacing any existing spelling of the name (the loader ignores case).</summary>
    private static void Set(JsonObject root, string name, string value)
    {
        foreach (var existing in root.Select(p => p.Key).Where(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            root.Remove(existing);
        }

        root[name] = value;
    }
}
