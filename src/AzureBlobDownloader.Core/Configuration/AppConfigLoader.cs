using System.Text.Json;
using AzureBlobDownloader.Core.Urls;

namespace AzureBlobDownloader.Core.Configuration;

/// <summary>
/// Loads <see cref="AppConfig"/> from up to four layers, later layers overriding earlier ones
/// property by property (lists are replaced, not appended; blank strings count as not set):
/// <list type="number">
///   <item><c>AzureBlobDownloader.config.json</c> next to the app (<see cref="AppContext.BaseDirectory"/>).</item>
///   <item>The same file name in the machine-wide folder (<see cref="GetMachineConfigDirectory"/>), placed by IT.</item>
///   <item>The same file name in the per-user folder (<see cref="GetUserConfigDirectory"/>), written by the setup screen.</item>
///   <item>The file given with <c>--config &lt;path&gt;</c>, which must exist.</item>
/// </list>
/// The app can ship without a Tenant ID and Client ID: <see cref="TryLoad"/> then reports that setup is needed.
/// </summary>
public static class AppConfigLoader
{
    public const string FileName = "AzureBlobDownloader.config.json";
    public const string AppFolderName = "AzureBlobDownloader";
    public const string ConfigArgument = "--config";

    /// <summary>
    /// Every allowed host must be an Azure blob endpoint, whichever file it comes from: the user's token is
    /// for Azure Storage, so it may only ever be sent to Azure Storage.
    /// </summary>
    public const string RequiredHostSuffix = ".blob.core.windows.net";

    public const int MaxConcurrency = 64;
    public const int MaxChunkSizeMiB = 256;

    /// <summary>
    /// Loads all layers. Returns a complete configuration, or reports that the Tenant ID and/or Client ID
    /// still have to be entered (setup). Everything else must be valid.
    /// </summary>
    /// <exception cref="AppConfigException">A file cannot be read, or a setting is invalid.</exception>
    public static AppConfigLoadResult TryLoad(AppConfigLocations locations)
    {
        var (merged, sources) = ReadLayers(locations);
        return Validate(merged, sources);
    }

    /// <summary>Loads the shipped, per-user and explicit layers; a missing Tenant ID or Client ID is an error.</summary>
    /// <param name="baseDirectory">Folder holding the shipped config file (missing file is allowed).</param>
    /// <param name="userConfigDirectory">Per-user folder; <c>null</c> or a missing file is skipped.</param>
    /// <param name="explicitConfigPath">Path from <c>--config</c>; <c>null</c> to skip. Must exist when given.</param>
    /// <exception cref="AppConfigException">A file cannot be read, or the merged configuration is invalid or incomplete.</exception>
    public static AppConfig Load(string baseDirectory, string? userConfigDirectory, string? explicitConfigPath) =>
        Load(new AppConfigLocations(baseDirectory, null, userConfigDirectory, explicitConfigPath));

    /// <inheritdoc cref="Load(string, string?, string?)"/>
    public static AppConfig Load(AppConfigLocations locations)
    {
        var (merged, sources) = ReadLayers(locations);
        if (sources.Count == 0)
        {
            throw new AppConfigException([$"No configuration file was found. Looked for: {string.Join("; ", Candidates(locations))}"], sources);
        }

        var result = Validate(merged, sources);
        return result.Config ?? throw new AppConfigException(result.Missing, sources);
    }

    private static IEnumerable<string> Candidates(AppConfigLocations locations)
    {
        yield return Path.GetFullPath(Path.Combine(locations.BaseDirectory, FileName));
        foreach (var folder in new[] { locations.MachineDirectory, locations.UserDirectory })
        {
            if (!string.IsNullOrWhiteSpace(folder))
            {
                yield return Path.GetFullPath(Path.Combine(folder, FileName));
            }
        }

        if (locations.ExplicitPath is not null)
        {
            yield return Path.GetFullPath(locations.ExplicitPath);
        }
    }

    private static (AppConfigFile Merged, List<string> Sources) ReadLayers(AppConfigLocations locations)
    {
        var sources = new List<string>();
        var errors = new List<string>();
        var merged = new AppConfigFile();

        void AddLayer(string path, bool required)
        {
            path = Path.GetFullPath(path);
            if (!File.Exists(path))
            {
                if (required)
                {
                    errors.Add($"The configuration file given with {ConfigArgument} was not found: {path}");
                }

                return;
            }

            var layer = ReadFile(path, errors);
            if (layer is not null)
            {
                sources.Add(path);
                merged = merged.MergeWith(layer);
            }
        }

        AddLayer(Path.Combine(locations.BaseDirectory, FileName), required: false);

        foreach (var folder in new[] { locations.MachineDirectory, locations.UserDirectory })
        {
            if (!string.IsNullOrWhiteSpace(folder))
            {
                AddLayer(Path.Combine(folder, FileName), required: false);
            }
        }

        if (locations.ExplicitPath is not null)
        {
            AddLayer(locations.ExplicitPath, required: true);
        }

        if (errors.Count > 0)
        {
            throw new AppConfigException(errors, sources);
        }

        return (merged, sources);
    }

    /// <summary>
    /// The machine-wide config folder, for IT to deploy settings for all users:
    /// <c>%ProgramData%\AzureBlobDownloader</c> on Windows, <c>/Library/Application Support/AzureBlobDownloader</c>
    /// on macOS and <c>/etc/AzureBlobDownloader</c> on Linux.
    /// </summary>
    public static string? GetMachineConfigDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return string.IsNullOrEmpty(programData) ? null : Path.Combine(programData, AppFolderName);
        }

        return OperatingSystem.IsMacOS()
            ? Path.Combine("/Library", "Application Support", AppFolderName)
            : Path.Combine("/etc", AppFolderName);
    }

    /// <summary>
    /// The per-user config folder: <c>%APPDATA%\AzureBlobDownloader</c> on Windows,
    /// <c>~/Library/Application Support/AzureBlobDownloader</c> on macOS and
    /// <c>$XDG_CONFIG_HOME/AzureBlobDownloader</c> (default <c>~/.config/AzureBlobDownloader</c>) on Linux.
    /// Returns <c>null</c> when the base folder cannot be determined.
    /// </summary>
    public static string? GetUserConfigDirectory()
    {
        string root;
        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            root = string.IsNullOrEmpty(home) ? string.Empty : Path.Combine(home, "Library", "Application Support");
        }
        else
        {
            // Windows: %APPDATA% (roaming). Linux: $XDG_CONFIG_HOME or ~/.config.
            root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }

        return string.IsNullOrEmpty(root) ? null : Path.Combine(root, AppFolderName);
    }

    /// <summary>
    /// Returns the value of the last <c>--config &lt;path&gt;</c> or <c>--config=&lt;path&gt;</c> argument,
    /// or <c>null</c> when absent. Other arguments are ignored.
    /// </summary>
    /// <exception cref="AppConfigException"><c>--config</c> has no value.</exception>
    public static string? GetConfigPathFromArgs(IReadOnlyList<string> args)
    {
        string? path = null;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, ConfigArgument, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]))
                {
                    throw new AppConfigException([$"{ConfigArgument} must be followed by the path of a configuration file."], []);
                }

                path = args[++i];
            }
            else if (arg.StartsWith(ConfigArgument + "=", StringComparison.OrdinalIgnoreCase))
            {
                var value = arg[(ConfigArgument.Length + 1)..];
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new AppConfigException([$"{ConfigArgument} must be followed by the path of a configuration file."], []);
                }

                path = value;
            }
        }

        return path?.Trim().Trim('"');
    }

    private static AppConfigFile? ReadFile(string path, List<string> errors)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var file = JsonSerializer.Deserialize(stream, AppConfigJsonContext.Default.AppConfigFile);
            if (file is null)
            {
                errors.Add($"Configuration file {path} is empty.");
            }

            return file;
        }
        catch (JsonException ex)
        {
            errors.Add($"Configuration file {path} is not valid: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add($"Configuration file {path} could not be read: {ex.Message}");
        }

        return null;
    }

    internal static AppConfigLoadResult Validate(AppConfigFile file, IReadOnlyList<string> sources)
    {
        var errors = new List<string>();
        var missing = new List<string>();

        var tenantId = ValidateGuid(file.TenantId, nameof(AppConfig.TenantId),
            "Set it to the Directory (tenant) ID of the organisation that shares the files.", errors, missing);
        var clientId = ValidateGuid(file.ClientId, nameof(AppConfig.ClientId),
            "Set it to the Application (client) ID of the app registration.", errors, missing);

        var allowedHosts = new List<string>();
        if (file.AllowedHosts is null)
        {
            allowedHosts.AddRange(AppConfig.DefaultAllowedHosts);
        }
        else if (file.AllowedHosts.Count == 0)
        {
            errors.Add($"{nameof(AppConfig.AllowedHosts)} is empty. It must list at least one host pattern, e.g. \"*.blob.core.windows.net\".");
        }
        else
        {
            foreach (var pattern in file.AllowedHosts)
            {
                if (HostPattern.TryParse(pattern, out var parsed, out var error))
                {
                    if (!parsed.Pattern.EndsWith(RequiredHostSuffix, StringComparison.Ordinal))
                    {
                        errors.Add($"{nameof(AppConfig.AllowedHosts)} entry \"{pattern}\" is not allowed: only Azure blob endpoints (\"…{RequiredHostSuffix}\") can be used.");
                    }
                    else if (!allowedHosts.Contains(parsed.Pattern))
                    {
                        allowedHosts.Add(parsed.Pattern);
                    }
                }
                else
                {
                    errors.Add($"{nameof(AppConfig.AllowedHosts)} entry \"{pattern}\" is invalid: {error}");
                }
            }
        }

        var maxFiles = ValidateRange(file.MaxConcurrentFiles, AppConfig.DefaultMaxConcurrentFiles,
            nameof(AppConfig.MaxConcurrentFiles), 1, MaxConcurrency, errors);
        var maxChunks = ValidateRange(file.MaxConcurrentChunksPerFile, AppConfig.DefaultMaxConcurrentChunksPerFile,
            nameof(AppConfig.MaxConcurrentChunksPerFile), 1, MaxConcurrency, errors);
        var chunkSize = ValidateRange(file.ChunkSizeMiB, AppConfig.DefaultChunkSizeMiB,
            nameof(AppConfig.ChunkSizeMiB), 1, MaxChunkSizeMiB, errors);

        if (errors.Count > 0)
        {
            throw new AppConfigException(errors, sources);
        }

        if (missing.Count > 0)
        {
            return AppConfigLoadResult.Setup(file.TenantId?.Trim(), file.ClientId?.Trim(), missing, sources);
        }

        return AppConfigLoadResult.Complete(new AppConfig
        {
            TenantId = tenantId,
            ClientId = clientId,
            AllowedHosts = allowedHosts.AsReadOnly(),
            UseWindowsBroker = file.UseWindowsBroker ?? AppConfig.DefaultUseWindowsBroker,
            DisplayName = NullIfBlank(file.DisplayName),
            SupportContact = NullIfBlank(file.SupportContact),
            MaxConcurrentFiles = maxFiles,
            MaxConcurrentChunksPerFile = maxChunks,
            ChunkSizeMiB = chunkSize,
        }, sources);
    }

    /// <summary>A blank or all-zero value means "not set up yet" (setup screen); anything else must be a GUID.</summary>
    private static Guid ValidateGuid(string? value, string name, string hint, List<string> errors, List<string> missing)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            missing.Add($"{name} is missing. {hint}");
            return Guid.Empty;
        }

        if (!Guid.TryParseExact(value.Trim(), "D", out var guid))
        {
            errors.Add($"{name} \"{value}\" is not a valid GUID (expected the form xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx). {hint}");
            return Guid.Empty;
        }

        if (guid == Guid.Empty)
        {
            missing.Add($"{name} is not configured yet (it is still the placeholder {Guid.Empty}). {hint}");
        }

        return guid;
    }

    private static int ValidateRange(int? value, int defaultValue, string name, int min, int max, List<string> errors)
    {
        if (value is null)
        {
            return defaultValue;
        }

        if (value < min || value > max)
        {
            errors.Add($"{name} is {value}; it must be between {min} and {max}.");
            return defaultValue;
        }

        return value.Value;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
