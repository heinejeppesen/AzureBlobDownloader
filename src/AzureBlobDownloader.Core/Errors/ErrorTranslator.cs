using System.Net.Http;
using System.Text.RegularExpressions;
using Azure;
using AzureBlobDownloader.Core.Auth;
using AzureBlobDownloader.Core.Downloads;
using Microsoft.Identity.Client;

namespace AzureBlobDownloader.Core.Errors;

/// <summary>A problem described for a non-technical user.</summary>
/// <param name="Code">Technical code (AADSTS number, storage error code) for support; may be null.</param>
public sealed record UserError(string Title, string Message, string? Code = null);

/// <summary>Turns sign-in and storage exceptions into plain language.</summary>
public static partial class ErrorTranslator
{
    public static UserError Translate(Exception exception) => exception switch
    {
        OperationCanceledException => new("Cancelled", "The operation was cancelled."),
        SignInRequiredException => new("Please sign in again", "Your sign-in has expired or was ended. Sign in again to continue."),
        MsalClientException { ErrorCode: MsalError.AuthenticationCanceledError } =>
            new("Sign-in cancelled", "The sign-in window was closed before sign-in finished."),
        MsalServiceException service => FromEntra(service),
        MsalClientException client => FromEntra(client),
        RequestFailedException storage => FromStorage(storage),
        DestinationException destination => new(
            destination.IsDiskFull ? "The destination is full" : "Files cannot be saved there",
            destination.IsDiskFull ? "Free up space, or choose another folder." : $"{destination.Message} Choose another folder, or check that you can save files there."),
        HttpRequestException => Network(),
        _ => new("Something went wrong", exception.Message),
    };

    /// <summary>The AADSTS error number in an Entra error, e.g. <c>50020</c>; null if none.</summary>
    public static string? GetAadstsCode(Exception exception)
    {
        var text = exception is MsalServiceException { ResponseBody: { } body } ? $"{exception.Message} {body}" : exception.ToString();
        var match = AadstsPattern().Match(text);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static UserError FromEntra(MsalException exception)
    {
        var code = GetAadstsCode(exception);
        var technical = code is null ? exception.ErrorCode : $"AADSTS{code}";
        return code switch
        {
            "50020" or "90072" => new(
                "This account is not set up for these files",
                "The account you signed in with has not been invited, or the invitation has not been accepted yet. " +
                "Sign in with the e-mail address the invitation was sent to, and accept the invitation first " +
                "(open the link in the invitation e-mail). If it still fails, ask your contact to send the invitation again.",
                technical),
            "50105" => new(
                "Your account has not been given access",
                "You are signed in, but your account has not been allowed to use this app. Ask your contact to give you access.",
                technical),
            "53003" => new(
                "Sign-in blocked by an access policy",
                "The organisation that shared the files has an access policy that blocked this sign-in. " +
                "This usually means the computer is not managed by your company or does not meet its security requirements, " +
                "or the sign-in came from an unexpected location. Try from your work computer, or contact them with the time of the attempt.",
                technical),
            "53000" or "530003" => new(
                "This computer does not meet the access requirements",
                "Access is only allowed from computers managed by your company that meet its security requirements. " +
                "Try from your work computer, or ask your IT department.",
                technical),
            "65001" => new(
                "The app has not been approved",
                "The app has not been approved by the organisation that shared the files. Guests cannot approve it themselves; " +
                "an administrator there must grant consent. Please tell your contact.",
                technical),
            "700016" or "90002" => new(
                "The app is set up incorrectly",
                "The app's configuration does not match the organisation's directory (tenant or app ID). Please tell your contact.",
                technical),
            "7000218" => new(
                "The app registration needs a change",
                "The app registration does not allow this type of sign-in (public client). Please tell your contact.",
                technical),
            _ => new("Sign-in failed", FirstLine(exception.Message), technical),
        };
    }

    private static UserError FromStorage(RequestFailedException exception)
    {
        var code = exception.ErrorCode;
        var technical = code is null ? $"HTTP {exception.Status}" : $"{code} (HTTP {exception.Status})";
        return (exception.Status, code) switch
        {
            (0, _) => Network(technical),
            (403, "AuthorizationPermissionMismatch") => new(
                "No access to these files",
                "You are signed in, but your account has not been given access to this project's files. Ask your contact to check your access.",
                technical),
            (403, _) => new(
                "Access refused",
                "The storage service refused access. Ask your contact to check your access.",
                technical),
            (401, _) => new(
                "Sign-in not accepted by the storage service",
                "The storage service did not accept your sign-in. Sign out, sign in again and retry.",
                technical),
            (404, _) => new(
                "Shared folder not found",
                "This shared folder no longer exists, or the link is not quite right. Check the link in the message you received.",
                technical),
            _ => new("The storage service reported a problem", FirstLine(exception.Message), technical),
        };
    }

    private static UserError Network(string? code = null) => new(
        "Cannot reach the storage service",
        "Check your internet connection. If you use a proxy or VPN, check that it allows connections to *.blob.core.windows.net.",
        code);

    private static string FirstLine(string text)
    {
        var newline = text.IndexOfAny(['\r', '\n']);
        return newline < 0 ? text : text[..newline];
    }

    [GeneratedRegex(@"AADSTS(\d{5,7})")]
    private static partial Regex AadstsPattern();
}
