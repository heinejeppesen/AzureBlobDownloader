namespace AzureBlobDownloader.Core.Auth;

/// <summary>The signed-in account as seen by the resource tenant.</summary>
/// <param name="UserName">Sign-in name, e.g. <c>someone@customer.com</c>.</param>
/// <param name="ObjectId">Object id in the resource tenant; this is what StorageBlobLogs records.</param>
/// <param name="TokenTenantId">Tenant that issued the token; always the configured resource tenant.</param>
/// <param name="HomeTenantId">The account's home tenant; differs from <paramref name="TokenTenantId"/> for guests.</param>
public sealed record SignedInUser(string UserName, string ObjectId, string TokenTenantId, string? HomeTenantId)
{
    public bool IsGuest =>
        HomeTenantId is not null && !string.Equals(HomeTenantId, TokenTenantId, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Where the token cache lives.</summary>
/// <param name="IsPersistent">False when the OS secure store is unavailable and tokens are kept in memory only.</param>
/// <param name="Description">Plain-language description for the UI.</param>
public sealed record TokenCacheInfo(bool IsPersistent, string Description);

/// <summary>A new interactive sign-in is needed (e.g. the session expired or was revoked).</summary>
public sealed class SignInRequiredException : Exception
{
    public SignInRequiredException()
        : base("Sign-in is required.")
    {
    }

    public SignInRequiredException(Exception innerException)
        : base("Sign-in is required.", innerException)
    {
    }
}
