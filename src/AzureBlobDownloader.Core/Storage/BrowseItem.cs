using AzureBlobDownloader.Core.Downloads;

namespace AzureBlobDownloader.Core.Storage;

public enum BrowseItemKind
{
    Container,
    Folder,
    File,
}

/// <summary>One row in a listing: a container (for account links), a folder (blob name prefix) or a file (blob).</summary>
/// <param name="Name">Display name: the last path segment.</param>
/// <param name="Path">Container name, folder prefix ending in <c>/</c>, or full blob name.</param>
/// <param name="Blob">For files: the blob's properties (version, MD5) as listed.</param>
public sealed record BrowseItem(
    BrowseItemKind Kind,
    string Name,
    string Path,
    long? Size = null,
    DateTimeOffset? LastModified = null,
    bool? HasContentMd5 = null,
    RemoteBlob? Blob = null)
{
    public bool IsFile => Kind == BrowseItemKind.File;

    /// <summary>The last segment of a blob name or folder prefix: <c>a/b/</c> → <c>b</c>.</summary>
    public static string LastSegment(string path)
    {
        var trimmed = path.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }
}
