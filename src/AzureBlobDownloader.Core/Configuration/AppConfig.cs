namespace AzureBlobDownloader.Core.Configuration;

/// <summary>
/// Validated application configuration. Create it with <see cref="AppConfigLoader"/>,
/// which merges the shipped config file, the per-user override and <c>--config &lt;path&gt;</c>.
/// </summary>
public sealed record AppConfig
{
    /// <summary>Default for <see cref="AllowedHosts"/>: any Azure blob endpoint. Narrow it in a shipped or machine-wide file.</summary>
    public static IReadOnlyList<string> DefaultAllowedHosts { get; } = ["*.blob.core.windows.net"];

    public const bool DefaultUseWindowsBroker = true;
    public const int DefaultMaxConcurrentFiles = 4;
    public const int DefaultMaxConcurrentChunksPerFile = 4;
    public const int DefaultChunkSizeMiB = 8;

    /// <summary>The resource tenant users sign in to. Never the all-zero GUID.</summary>
    public required Guid TenantId { get; init; }

    /// <summary>Application (client) ID of the app registration. Never the all-zero GUID.</summary>
    public required Guid ClientId { get; init; }

    /// <summary>
    /// Host patterns the app may send a token to, lowercase ASCII, e.g. <c>contoso*.blob.core.windows.net</c>.
    /// <c>*</c> matches within a single DNS label.
    /// </summary>
    public IReadOnlyList<string> AllowedHosts { get; init; } = DefaultAllowedHosts;

    /// <summary>Use the Windows sign-in broker (WAM) on Windows. Ignored on other platforms.</summary>
    public bool UseWindowsBroker { get; init; } = DefaultUseWindowsBroker;

    /// <summary>Optional branding shown in the window title / header.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Optional support contact text shown with errors.</summary>
    public string? SupportContact { get; init; }

    public int MaxConcurrentFiles { get; init; } = DefaultMaxConcurrentFiles;

    public int MaxConcurrentChunksPerFile { get; init; } = DefaultMaxConcurrentChunksPerFile;

    public int ChunkSizeMiB { get; init; } = DefaultChunkSizeMiB;

    /// <summary><see cref="ChunkSizeMiB"/> in bytes.</summary>
    public long ChunkSizeBytes => ChunkSizeMiB * 1024L * 1024L;
}
