using Azure;
using AzureBlobDownloader.Core.Auth;
using AzureBlobDownloader.Core.Errors;
using Microsoft.Identity.Client;

namespace AzureBlobDownloader.Core.Tests.Errors;

public class ErrorTranslatorTests
{
    [Theory]
    [InlineData("AADSTS50020: User account 'x@y.com' from identity provider 'live.com' does not exist in tenant", "not set up", "AADSTS50020")]
    [InlineData("AADSTS90072: User account from identity provider does not exist in tenant", "not set up", "AADSTS90072")]
    [InlineData("AADSTS53003: Access has been blocked by Conditional Access policies.", "access policy", "AADSTS53003")]
    [InlineData("AADSTS53000: Device is not in required device state: compliant.", "access requirements", "AADSTS53000")]
    [InlineData("AADSTS65001: The user or administrator has not consented to use the application", "not been approved", "AADSTS65001")]
    [InlineData("AADSTS50105: Your administrator has configured the application to block users", "not been given access", "AADSTS50105")]
    [InlineData("AADSTS700016: Application with identifier was not found in the directory", "set up incorrectly", "AADSTS700016")]
    public void Entra_errors_get_plain_language(string message, string expectedInTitle, string expectedCode)
    {
        var error = ErrorTranslator.Translate(new MsalServiceException("invalid_grant", message));

        Assert.Contains(expectedInTitle, error.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expectedCode, error.Code);
    }

    [Fact]
    public void Unknown_entra_error_keeps_first_line_and_code()
    {
        var error = ErrorTranslator.Translate(new MsalServiceException("invalid_request", "AADSTS12345: Something odd.\r\nTrace ID: abc"));

        Assert.Equal("Sign-in failed", error.Title);
        Assert.Equal("AADSTS12345: Something odd.", error.Message);
        Assert.Equal("AADSTS12345", error.Code);
    }

    [Fact]
    public void Cancelled_sign_in_is_not_an_error_message_wall()
    {
        var error = ErrorTranslator.Translate(new MsalClientException(MsalError.AuthenticationCanceledError, "User canceled authentication."));

        Assert.Equal("Sign-in cancelled", error.Title);
    }

    [Theory]
    [InlineData(403, "AuthorizationPermissionMismatch", "No access to these files")]
    [InlineData(403, "AuthorizationFailure", "Access refused")]
    [InlineData(404, "ContainerNotFound", "Shared folder not found")]
    [InlineData(404, "BlobNotFound", "Shared folder not found")]
    [InlineData(401, "InvalidAuthenticationInfo", "Sign-in not accepted by the storage service")]
    [InlineData(0, null, "Cannot reach the storage service")]
    public void Storage_errors_get_plain_language(int status, string? code, string expectedTitle)
    {
        var error = ErrorTranslator.Translate(new RequestFailedException(status, "Service said no.", code, null));

        Assert.Equal(expectedTitle, error.Title);
    }

    [Fact]
    public void Sign_in_required_asks_to_sign_in_again()
    {
        Assert.Equal("Please sign in again", ErrorTranslator.Translate(new SignInRequiredException()).Title);
    }

    [Fact]
    public void Network_failure_is_reported_as_connectivity()
    {
        Assert.Equal("Cannot reach the storage service", ErrorTranslator.Translate(new HttpRequestException("No such host")).Title);
    }
}
