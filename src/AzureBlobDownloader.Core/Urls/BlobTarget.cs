using System.Diagnostics.CodeAnalysis;

namespace AzureBlobDownloader.Core.Urls;

public enum BlobTargetKind
{
    /// <summary>Account URL without a path: list the containers (shared folders) the user can read.</summary>
    Account,

    /// <summary>A container (shared folder).</summary>
    Container,

    /// <summary>A folder inside a container (blob name prefix).</summary>
    ContainerWithPrefix,
}

/// <summary>
/// A validated download target. All URIs are rebuilt from validated parts
/// (https, port 443, lowercase ASCII host on the allow list, no query, no fragment);
/// nothing of the pasted string is passed through unchecked.
/// </summary>
public sealed record BlobTarget
{
    public required BlobTargetKind Kind { get; init; }

    /// <summary>Lowercase ASCII (punycode) host name, e.g. <c>contosodata.blob.core.windows.net</c>.</summary>
    public required string Host { get; init; }

    /// <summary>The account endpoint, e.g. <c>https://contosodata.blob.core.windows.net/</c>.</summary>
    public required Uri AccountUri { get; init; }

    /// <summary>The leftmost label of <see cref="Host"/>, e.g. <c>contosodata</c>.</summary>
    public string AccountName => Host[..Host.IndexOf('.')];

    /// <summary>Container name; <c>null</c> for <see cref="BlobTargetKind.Account"/>.</summary>
    public string? ContainerName { get; init; }

    /// <summary>Container endpoint, e.g. <c>https://contosodata.blob.core.windows.net/shared-files</c>.</summary>
    public Uri? ContainerUri { get; init; }

    /// <summary>
    /// Decoded blob name prefix ending in <c>/</c> (e.g. <c>Reports 2026/March/</c>);
    /// <c>null</c> unless <see cref="Kind"/> is <see cref="BlobTargetKind.ContainerWithPrefix"/>.
    /// </summary>
    public string? Prefix { get; init; }

    [MemberNotNullWhen(true, nameof(ContainerName), nameof(ContainerUri))]
    public bool HasContainer => Kind != BlobTargetKind.Account;

    /// <summary>A container in the same account, e.g. one picked from an account listing.</summary>
    /// <exception cref="ArgumentException"><paramref name="containerName"/> is not a valid container name.</exception>
    public BlobTarget ForContainer(string containerName)
    {
        if (!BlobUrlParser.IsValidContainerName(containerName))
        {
            throw new ArgumentException($"\"{containerName}\" is not a valid container name.", nameof(containerName));
        }

        return new BlobTarget
        {
            Kind = BlobTargetKind.Container,
            Host = Host,
            AccountUri = AccountUri,
            ContainerName = containerName,
            ContainerUri = new Uri($"https://{Host}/{containerName}", UriKind.Absolute),
        };
    }

    public override string ToString() => Kind switch
    {
        BlobTargetKind.Account => AccountUri.ToString(),
        BlobTargetKind.Container => ContainerUri!.ToString(),
        _ => $"{ContainerUri}/{Prefix}",
    };
}
