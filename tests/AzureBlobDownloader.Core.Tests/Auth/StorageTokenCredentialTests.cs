using Azure.Core;
using AzureBlobDownloader.Core.Auth;

namespace AzureBlobDownloader.Core.Tests.Auth;

public class StorageTokenCredentialTests
{
    [Theory]
    [InlineData("https://management.azure.com/.default")]
    [InlineData("https://graph.microsoft.com/.default")]
    [InlineData("https://storage.azure.com.evil.example/.default")]
    [InlineData("https://vault.azure.net/.default")]
    public async Task Refuses_scopes_outside_azure_storage(string scope)
    {
        // The scope check runs before the auth service is touched, so no service is needed.
        var credential = new StorageTokenCredential(null!);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await credential.GetTokenAsync(new TokenRequestContext([scope]), TestContext.Current.CancellationToken));
    }
}
