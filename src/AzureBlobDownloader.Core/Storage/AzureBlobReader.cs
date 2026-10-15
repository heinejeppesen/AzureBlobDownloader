using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureBlobDownloader.Core.Downloads;
using AzureBlobDownloader.Core.Urls;

namespace AzureBlobDownloader.Core.Storage;

/// <summary>
/// <see cref="IBlobReader"/> on the blob data plane, with the user's own token (never a SAS), so the
/// storage logs show who downloaded what. Clients are only built from validated <see cref="BlobTarget"/>s.
/// </summary>
public sealed class AzureBlobReader(TokenCredential credential) : IBlobReader
{
    private readonly BlobClientOptions _options = BlobBrowser.CreateOptions();
    private readonly ConcurrentDictionary<string, BlobContainerClient> _containers = new(StringComparer.Ordinal);

    public async IAsyncEnumerable<RemoteBlob> EnumerateAsync(
        BlobTarget container,
        string prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var blob in Container(container)
            .GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix: string.IsNullOrEmpty(prefix) ? null : prefix, cancellationToken: cancellationToken)
            .ConfigureAwait(false))
        {
            yield return ToRemoteBlob(blob);
        }
    }

    public async Task<RemoteBlob> GetPropertiesAsync(BlobTarget container, string blobName, CancellationToken cancellationToken)
    {
        var properties = (await Container(container).GetBlobClient(blobName).GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Value;
        return new RemoteBlob(blobName, properties.ContentLength, properties.ETag.ToString(), properties.LastModified, properties.ContentHash);
    }

    public async Task ReadRangeAsync(
        BlobTarget container,
        string blobName,
        string etag,
        long offset,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var options = new BlobDownloadOptions
        {
            Range = new HttpRange(offset, buffer.Length),
            Conditions = new BlobRequestConditions { IfMatch = new ETag(etag) },
        };

        try
        {
            var response = await Container(container).GetBlobClient(blobName).DownloadStreamingAsync(options, cancellationToken).ConfigureAwait(false);
            await using var content = response.Value.Content;
            await content.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status == 412)
        {
            throw new BlobChangedException(blobName, ex);
        }
    }

    public void Reset() => _containers.Clear();

    internal static RemoteBlob ToRemoteBlob(BlobItem blob) => new(
        blob.Name,
        blob.Properties.ContentLength ?? 0,
        blob.Properties.ETag?.ToString() ?? string.Empty,
        blob.Properties.LastModified ?? DateTimeOffset.MinValue,
        blob.Properties.ContentHash);

    private BlobContainerClient Container(BlobTarget target)
    {
        if (!target.HasContainer)
        {
            throw new ArgumentException("The target has no container.", nameof(target));
        }

        return _containers.GetOrAdd(target.ContainerUri.AbsoluteUri, _ => new BlobContainerClient(target.ContainerUri, credential, _options));
    }
}
