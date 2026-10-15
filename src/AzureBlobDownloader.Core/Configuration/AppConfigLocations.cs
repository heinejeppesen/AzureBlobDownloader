namespace AzureBlobDownloader.Core.Configuration;

/// <summary>
/// Where the configuration layers are read from, lowest priority first:
/// the file shipped next to the app, a machine-wide file placed by IT, the per-user file (written by the
/// setup screen) and a file given with <c>--config &lt;path&gt;</c>.
/// </summary>
/// <param name="BaseDirectory">Folder of the shipped file (next to the app).</param>
/// <param name="MachineDirectory">Machine-wide folder, or null to skip.</param>
/// <param name="UserDirectory">Per-user folder, or null to skip (also where the setup screen saves).</param>
/// <param name="ExplicitPath">Path from <c>--config</c>, or null.</param>
public sealed record AppConfigLocations(string BaseDirectory, string? MachineDirectory, string? UserDirectory, string? ExplicitPath)
{
    /// <summary>The standard locations for the running app.</summary>
    /// <exception cref="AppConfigException"><c>--config</c> has no value.</exception>
    public static AppConfigLocations ForCurrentProcess(IReadOnlyList<string>? args) => new(
        AppContext.BaseDirectory,
        AppConfigLoader.GetMachineConfigDirectory(),
        AppConfigLoader.GetUserConfigDirectory(),
        AppConfigLoader.GetConfigPathFromArgs(args ?? []));

    /// <summary>The per-user config file, or null when there is no per-user folder.</summary>
    public string? UserConfigPath => UserDirectory is null ? null : Path.Combine(UserDirectory, AppConfigLoader.FileName);
}

/// <summary>
/// The outcome of loading: either a complete <see cref="Config"/>, or the information that the app still
/// needs its Tenant ID and Client ID (with whatever partial values were found, to pre-fill the setup screen).
/// </summary>
public sealed record AppConfigLoadResult
{
    private AppConfigLoadResult(AppConfig? config, string? tenantId, string? clientId, IReadOnlyList<string> missing, IReadOnlyList<string> sources)
    {
        Config = config;
        TenantId = tenantId;
        ClientId = clientId;
        Missing = missing;
        Sources = sources;
    }

    /// <summary>The validated configuration; null when <see cref="NeedsSetup"/>.</summary>
    public AppConfig? Config { get; }

    public bool NeedsSetup => Config is null;

    /// <summary>The Tenant ID as found in the files (may be blank or a placeholder).</summary>
    public string? TenantId { get; }

    /// <summary>The Client ID as found in the files (may be blank or a placeholder).</summary>
    public string? ClientId { get; }

    /// <summary>Why setup is needed, e.g. "TenantId is missing. …".</summary>
    public IReadOnlyList<string> Missing { get; }

    /// <summary>The files that were read, lowest priority first.</summary>
    public IReadOnlyList<string> Sources { get; }

    internal static AppConfigLoadResult Complete(AppConfig config, IReadOnlyList<string> sources) =>
        new(config, config.TenantId.ToString("D"), config.ClientId.ToString("D"), [], sources);

    internal static AppConfigLoadResult Setup(string? tenantId, string? clientId, IReadOnlyList<string> missing, IReadOnlyList<string> sources) =>
        new(null, tenantId, clientId, missing, sources);
}
