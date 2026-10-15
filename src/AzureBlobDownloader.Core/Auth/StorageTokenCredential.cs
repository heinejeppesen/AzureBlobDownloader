using Azure.Core;

namespace AzureBlobDownloader.Core.Auth;

/// <summary>
/// Hands <see cref="AuthService"/> tokens to the Azure Storage SDK. Refuses any scope outside Azure Storage,
/// so the user's sign-in can never be used for another resource through this credential.
/// </summary>
public sealed class StorageTokenCredential(AuthService auth) : TokenCredential
{
    private const string StorageAudience = "https://storage.azure.com/";

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        foreach (var scope in requestContext.Scopes)
        {
            if (!scope.StartsWith(StorageAudience, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Refusing to issue a token for \"{scope}\": only Azure Storage is allowed.");
            }
        }

        return await auth.GetStorageTokenAsync(requestContext.Claims, cancellationToken).ConfigureAwait(false);
    }
}
