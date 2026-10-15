using AzureBlobDownloader.Core.Urls;

namespace AzureBlobDownloader.Core.Downloads;

/// <summary>Read access to blobs: listing what to download, and reading byte ranges of one blob version.</summary>
public interface IBlobReader
{
    /// <summary>All blobs under <paramref name="prefix"/> (all levels), with the properties needed to download them.</summary>
    IAsyncEnumerable<RemoteBlob> EnumerateAsync(BlobTarget container, string prefix, CancellationToken cancellationToken);

    /// <summary>Current properties of one blob.</summary>
    Task<RemoteBlob> GetPropertiesAsync(BlobTarget container, string blobName, CancellationToken cancellationToken);

    /// <summary>
    /// Fills <paramref name="buffer"/> with the bytes at <paramref name="offset"/> of the blob version
    /// <paramref name="etag"/>.
    /// </summary>
    /// <exception cref="BlobChangedException">The blob is no longer that version.</exception>
    Task ReadRangeAsync(BlobTarget container, string blobName, string etag, long offset, Memory<byte> buffer, CancellationToken cancellationToken);

    /// <summary>Drops cached connections and tokens, e.g. after the user signed in again.</summary>
    void Reset();
}

/// <summary>The blob was changed or replaced while it was being downloaded.</summary>
public sealed class BlobChangedException(string blobName, Exception? innerException = null)
    : Exception($"\"{blobName}\" was changed on the server during the download.", innerException);

/// <summary>A downloaded file did not match the blob's MD5 or size.</summary>
public sealed class IntegrityException(string message) : Exception(message);

/// <summary>Reading or writing the destination failed (disk full, share disconnected, no permission).</summary>
public sealed class DestinationException(string message, Exception innerException) : Exception(message, innerException)
{
    /// <summary>The destination has no free space left.</summary>
    public bool IsDiskFull { get; } = IsDiskFullError(innerException);

    private static bool IsDiskFullError(Exception ex) =>
        ex is IOException io && ((io.HResult & 0xFFFF) is 0x70 or 0x27 || io.HResult == 28 /* ENOSPC */);
}
