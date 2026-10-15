namespace AzureBlobDownloader.Core.Downloads;

/// <summary>A blob as the service reported it: what is needed to download, resume and verify it.</summary>
/// <param name="Name">Full blob name, e.g. <c>Survey 2026/Drone/flight-01.tif</c>.</param>
/// <param name="ETag">Version tag; ranged reads are made conditional on it, so a changed blob is never mixed.</param>
/// <param name="ContentMd5">MD5 of the whole blob when the service has one; otherwise only the size is checked.</param>
public sealed record RemoteBlob(string Name, long Size, string ETag, DateTimeOffset LastModified, byte[]? ContentMd5)
{
    public bool HasContentMd5 => ContentMd5 is { Length: > 0 };
}
