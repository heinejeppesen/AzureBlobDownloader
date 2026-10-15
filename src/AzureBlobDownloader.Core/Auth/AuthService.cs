using System.Reflection;
using Azure.Core;
using AzureBlobDownloader.Core.Configuration;
using AzureBlobDownloader.Core.Diagnostics;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using Microsoft.Identity.Client.Extensions.Msal;

namespace AzureBlobDownloader.Core.Auth;

/// <summary>
/// Signs the user in against the configured resource tenant and issues Azure Storage tokens.
/// <list type="bullet">
///   <item>Authority is always <c>https://login.microsoftonline.com/{TenantId}</c>, never <c>/common</c> or
///   <c>/organizations</c>, so guests get a token from the resource tenant that Storage accepts.</item>
///   <item>Only the Azure Storage scope is ever requested (no Azure Resource Manager, no Graph calls).</item>
///   <item>Interactive sign-in uses WAM on Windows (when enabled) and the system browser elsewhere.
///   Device code flow is never used.</item>
/// </list>
/// </summary>
public sealed class AuthService
{
    public const string StorageScope = "https://storage.azure.com/user_impersonation";
    public const string LoginHost = "https://login.microsoftonline.com";

    private static readonly string[] Scopes = [StorageScope];

    private const string BrowserSuccessHtml = """
        <!doctype html><html><head><meta charset="utf-8"><title>Azure Blob Downloader</title></head>
        <body style="font-family:system-ui,sans-serif;margin:3em">
        <h2>You are signed in</h2><p>You can close this tab and return to Azure Blob Downloader.</p>
        </body></html>
        """;

    private readonly IPublicClientApplication _app;
    private readonly string _tenantId;
    private readonly SemaphoreSlim _interactiveLock = new(1, 1);
    private IAccount? _account;

    private AuthService(IPublicClientApplication app, string tenantId, bool usesBroker, TokenCacheInfo cache)
    {
        _app = app;
        _tenantId = tenantId;
        UsesBroker = usesBroker;
        Cache = cache;
    }

    /// <summary>True when sign-in goes through the Windows broker (WAM) instead of the browser.</summary>
    public bool UsesBroker { get; }

    public TokenCacheInfo Cache { get; }

    public SignedInUser? CurrentUser { get; private set; }

    public string Authority => $"{LoginHost}/{_tenantId}";

    /// <param name="parentWindow">Native handle of the main window; WAM uses it to place its dialog.</param>
    public static async Task<AuthService> CreateAsync(AppConfig config, Func<IntPtr>? parentWindow)
    {
        var tenantId = config.TenantId.ToString("D");
        var usesBroker = config.UseWindowsBroker && OperatingSystem.IsWindows();

        var builder = PublicClientApplicationBuilder
            .Create(config.ClientId.ToString("D"))
            .WithAuthority($"{LoginHost}/{tenantId}")
            .WithRedirectUri("http://localhost")
            .WithClientName("AzureBlobDownloader")
            .WithClientVersion(typeof(AuthService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0")
            .WithLogging(OnMsalLog, LogLevel.Warning, enablePiiLogging: false, enableDefaultPlatformLogging: false);

        if (usesBroker)
        {
            builder = builder.WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows)
            {
                Title = "Azure Blob Downloader",
            });
            if (parentWindow is not null)
            {
                builder = builder.WithParentActivityOrWindow(parentWindow);
            }
        }

        var app = builder.Build();
        var cache = await AttachPersistentCacheAsync(app.UserTokenCache).ConfigureAwait(false);
        AppLog.Info($"Auth ready. Tenant {tenantId}, broker {usesBroker}, persistent cache {cache.IsPersistent}.");
        return new AuthService(app, tenantId, usesBroker, cache);
    }

    /// <summary>Signs in from the token cache without any UI. Returns null when an interactive sign-in is needed.</summary>
    public async Task<SignedInUser?> TryRestoreAsync(CancellationToken cancellationToken)
    {
        var account = (await _app.GetAccountsAsync().ConfigureAwait(false)).FirstOrDefault();
        if (account is null)
        {
            return null;
        }

        try
        {
            var result = await _app.AcquireTokenSilent(Scopes, account).ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return SetUser(result);
        }
        catch (MsalUiRequiredException ex)
        {
            AppLog.Info($"Cached sign-in needs interaction ({ex.ErrorCode}).");
            return null;
        }
        catch (MsalException ex)
        {
            AppLog.Warn($"Silent sign-in failed: {ex.GetType().Name} {ex.ErrorCode}.");
            return null;
        }
    }

    /// <summary>Interactive sign-in with an account picker (WAM on Windows, system browser elsewhere).</summary>
    public async Task<SignedInUser> SignInInteractiveAsync(CancellationToken cancellationToken)
    {
        await _interactiveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var builder = _app.AcquireTokenInteractive(Scopes).WithPrompt(Prompt.SelectAccount);
            if (!UsesBroker)
            {
                builder = builder
                    .WithUseEmbeddedWebView(false)
                    .WithSystemWebViewOptions(new SystemWebViewOptions { HtmlMessageSuccess = BrowserSuccessHtml });
            }

            var result = await builder.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return SetUser(result);
        }
        finally
        {
            _interactiveLock.Release();
        }
    }

    /// <summary>
    /// Returns a Storage access token for the signed-in account, refreshing it silently when needed.
    /// </summary>
    /// <param name="claims">Claims challenge from the service, if any.</param>
    /// <exception cref="SignInRequiredException">No account, or the session can no longer be refreshed silently.</exception>
    public async Task<AccessToken> GetStorageTokenAsync(string? claims, CancellationToken cancellationToken)
    {
        var account = _account ?? throw new SignInRequiredException();
        var builder = _app.AcquireTokenSilent(Scopes, account);
        if (!string.IsNullOrEmpty(claims))
        {
            builder = builder.WithClaims(claims);
        }

        try
        {
            var result = await builder.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return new AccessToken(result.AccessToken, result.ExpiresOn);
        }
        catch (MsalUiRequiredException ex)
        {
            AppLog.Info($"Token refresh needs interaction ({ex.ErrorCode}).");
            throw new SignInRequiredException(ex);
        }
    }

    /// <summary>Gets a fresh token without any UI (e.g. after Storage rejected the current one).</summary>
    /// <returns>False when the user has to sign in interactively.</returns>
    public async Task<bool> TryRefreshSilentlyAsync(CancellationToken cancellationToken)
    {
        if (_account is not { } account)
        {
            return false;
        }

        try
        {
            await _app.AcquireTokenSilent(Scopes, account).WithForceRefresh(true).ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (MsalUiRequiredException ex)
        {
            AppLog.Info($"Silent refresh needs interaction ({ex.ErrorCode}).");
            return false;
        }
    }

    /// <summary>
    /// Interactive sign-in for the account that is already signed in (e.g. the session expired during a
    /// download). Signing in as someone else is refused, so a download never continues under another identity.
    /// </summary>
    public async Task<SignedInUser> ReauthenticateAsync(CancellationToken cancellationToken)
    {
        var expected = CurrentUser ?? throw new SignInRequiredException();
        await _interactiveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var builder = _app.AcquireTokenInteractive(Scopes);
            builder = _account is { } account ? builder.WithAccount(account) : builder.WithLoginHint(expected.UserName);
            if (!UsesBroker)
            {
                builder = builder
                    .WithUseEmbeddedWebView(false)
                    .WithSystemWebViewOptions(new SystemWebViewOptions { HtmlMessageSuccess = BrowserSuccessHtml });
            }

            var result = await builder.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(result.UniqueId, expected.ObjectId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"You signed in as {result.Account.Username}, but the download was started as {expected.UserName}. Sign in with {expected.UserName} to continue.");
            }

            return SetUser(result);
        }
        finally
        {
            _interactiveLock.Release();
        }
    }

    /// <summary>Removes all accounts of this app from the token cache.</summary>
    public async Task SignOutAsync()
    {
        foreach (var account in await _app.GetAccountsAsync().ConfigureAwait(false))
        {
            await _app.RemoveAsync(account).ConfigureAwait(false);
        }

        _account = null;
        CurrentUser = null;
        AppLog.Info("Signed out.");
    }

    private SignedInUser SetUser(AuthenticationResult result)
    {
        // Defence in depth: the authority pins the tenant, so this should never trigger.
        if (!string.Equals(result.TenantId, _tenantId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The sign-in returned a token from tenant {result.TenantId} instead of {_tenantId}.");
        }

        _account = result.Account;
        CurrentUser = new SignedInUser(
            result.Account.Username,
            result.UniqueId,
            result.TenantId,
            result.Account.HomeAccountId?.TenantId);
        AppLog.Info($"Signed in. Object id {result.UniqueId}, guest {CurrentUser.IsGuest}.");
        return CurrentUser;
    }

    private static async Task<TokenCacheInfo> AttachPersistentCacheAsync(ITokenCache tokenCache)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            var properties = new StorageCreationPropertiesBuilder("msal.cache", AppPaths.DataDirectory)
                .WithMacKeyChain("AzureBlobDownloader", "MsalCache")
                .WithLinuxKeyring(
                    "AzureBlobDownloader.TokenCache",
                    MsalCacheHelper.LinuxKeyRingDefaultCollection,
                    "Azure Blob Downloader sign-in",
                    new KeyValuePair<string, string>("Product", "AzureBlobDownloader"),
                    new KeyValuePair<string, string>("Version", "1"))
                .Build();

            var helper = await MsalCacheHelper.CreateAsync(properties).ConfigureAwait(false);
            helper.VerifyPersistence();
            helper.RegisterCache(tokenCache);

            var store = OperatingSystem.IsWindows() ? "Windows (DPAPI)"
                : OperatingSystem.IsMacOS() ? "the macOS Keychain"
                : "the system keyring (libsecret)";
            return new TokenCacheInfo(true, $"Sign-in is remembered, protected by {store}.");
        }
        catch (Exception ex)
        {
            // Typical on Linux without a running Secret Service (no GNOME Keyring / KWallet), or without libsecret.
            // Fallback: keep tokens in memory for this session only. Never write them unencrypted.
            AppLog.Warn($"Secure token storage unavailable, using memory only. {ex.GetType().Name}: {ex.Message}");
            return new TokenCacheInfo(false, "Sign-in is kept for this session only: no secure storage (keyring) is available on this computer.");
        }
    }

    private static void OnMsalLog(LogLevel level, string message, bool containsPii)
    {
        // MSAL can still call back with Info/Verbose messages; keep only warnings and errors.
        if (containsPii || level is not (LogLevel.Error or LogLevel.Warning))
        {
            return;
        }

        if (level == LogLevel.Error)
        {
            AppLog.Error($"MSAL: {message}");
        }
        else
        {
            AppLog.Warn($"MSAL: {message}");
        }
    }
}
