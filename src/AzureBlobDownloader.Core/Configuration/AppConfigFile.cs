using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzureBlobDownloader.Core.Configuration;

/// <summary>
/// Raw shape of one config file. Every property is optional so that layers can be merged:
/// a later layer overrides a property only when it sets it (non-null).
/// GUIDs are read as strings so invalid values produce a readable validation error.
/// </summary>
internal sealed class AppConfigFile
{
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public List<string>? AllowedHosts { get; set; }
    public bool? UseWindowsBroker { get; set; }
    public string? DisplayName { get; set; }
    public string? SupportContact { get; set; }
    public int? MaxConcurrentFiles { get; set; }
    public int? MaxConcurrentChunksPerFile { get; set; }
    public int? ChunkSizeMiB { get; set; }

    /// <summary>Ignored; allows editors to reference a JSON schema.</summary>
    [JsonPropertyName("$schema")]
    public string? Schema { get; set; }

    /// <summary>
    /// Returns a new instance where every property set in <paramref name="overlay"/> wins. Blank strings count
    /// as not set, so a shipped file with <c>"TenantId": ""</c> never hides a value from another layer.
    /// </summary>
    public AppConfigFile MergeWith(AppConfigFile overlay) => new()
    {
        TenantId = Set(overlay.TenantId) ?? TenantId,
        ClientId = Set(overlay.ClientId) ?? ClientId,
        AllowedHosts = overlay.AllowedHosts ?? AllowedHosts,
        UseWindowsBroker = overlay.UseWindowsBroker ?? UseWindowsBroker,
        DisplayName = Set(overlay.DisplayName) ?? DisplayName,
        SupportContact = Set(overlay.SupportContact) ?? SupportContact,
        MaxConcurrentFiles = overlay.MaxConcurrentFiles ?? MaxConcurrentFiles,
        MaxConcurrentChunksPerFile = overlay.MaxConcurrentChunksPerFile ?? MaxConcurrentChunksPerFile,
        ChunkSizeMiB = overlay.ChunkSizeMiB ?? ChunkSizeMiB,
    };

    private static string? Set(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(AppConfigFile))]
internal sealed partial class AppConfigJsonContext : JsonSerializerContext;
