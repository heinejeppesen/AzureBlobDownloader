using AzureBlobDownloader.Core.Urls;

namespace AzureBlobDownloader.Core.Downloads;

/// <summary>One file to download.</summary>
/// <param name="Container">The container the blob is in.</param>
/// <param name="RelativePath">Local path below the destination folder, with the platform's separator.</param>
public sealed record DownloadItem(BlobTarget Container, RemoteBlob Blob, string RelativePath);

public enum PlanIssueKind
{
    /// <summary>The local name differs from the blob name (invalid characters, reserved name, same name in other case).</summary>
    Renamed,

    /// <summary>The file cannot be stored and will not be downloaded.</summary>
    Skipped,

    /// <summary>The full path is longer than many Windows programs (including Explorer) can open.</summary>
    LongPath,
}

/// <summary>Something about one file the user should know before the download starts.</summary>
/// <param name="BlobName">The blob, as named in the shared folder.</param>
/// <param name="LocalPath">Where it will be stored (relative), or null when skipped.</param>
public sealed record PlanIssue(PlanIssueKind Kind, string BlobName, string? LocalPath, string Message);

/// <summary>The files to download and the issues found while mapping their names.</summary>
public sealed record DownloadPlan(IReadOnlyList<DownloadItem> Items, IReadOnlyList<PlanIssue> Issues)
{
    public long TotalBytes { get; } = Items.Sum(i => i.Blob.Size);
}
