using System.Diagnostics.CodeAnalysis;

namespace AzureBlobDownloader.Core.Urls;

public enum BlobUrlError
{
    None,
    Empty,
    TooLong,
    NotAUrl,
    NotHttps,
    UserInfoNotAllowed,
    PortNotAllowed,
    IpAddressNotAllowed,
    InvalidHost,
    HostNotAllowed,
    InvalidContainerName,
    InvalidPath,
    MultipleLinks,
}

/// <summary>Outcome of <see cref="BlobUrlParser.Parse"/>: either a <see cref="Target"/> or an error.</summary>
public sealed record BlobUrlParseResult
{
    private BlobUrlParseResult(BlobTarget? target, BlobUrlError error, string? errorMessage, bool sasRemoved)
    {
        Target = target;
        Error = error;
        ErrorMessage = errorMessage;
        SasRemoved = sasRemoved;
    }

    public BlobTarget? Target { get; }

    public BlobUrlError Error { get; }

    /// <summary>Plain-language message for the user; <c>null</c> on success.</summary>
    public string? ErrorMessage { get; }

    /// <summary>
    /// The link carried a SAS (<c>sig=</c> in the query). It was dropped; the app always uses the
    /// user's own sign-in. The UI may tell the user so.
    /// </summary>
    public bool SasRemoved { get; }

    [MemberNotNullWhen(true, nameof(Target))]
    [MemberNotNullWhen(false, nameof(ErrorMessage))]
    public bool IsSuccess => Target is not null;

    internal static BlobUrlParseResult Success(BlobTarget target, bool sasRemoved) =>
        new(target, BlobUrlError.None, null, sasRemoved);

    internal static BlobUrlParseResult Failure(BlobUrlError error, string message) =>
        new(null, error, message, false);
}
