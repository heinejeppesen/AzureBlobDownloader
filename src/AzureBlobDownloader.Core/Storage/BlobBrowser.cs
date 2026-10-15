using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureBlobDownloader.Core.Browsing;
using AzureBlobDownloader.Core.Urls;

namespace AzureBlobDownloader.Core.Storage;

/// <summary>
/// Read-only listing on the blob data plane. Clients are only ever built from a validated
/// <see cref="BlobTarget"/>, so tokens only go to allowed hosts.
/// </summary>
public sealed class BlobBrowser(TokenCredential credential) : IFolderSource
{
    private const int ProgressEvery = 1000;

    private readonly BlobClientOptions _options = CreateOptions();

    public static BlobClientOptions CreateOptions()
    {
        var options = new BlobClientOptions();
        options.Retry.Mode = RetryMode.Exponential;
        options.Retry.MaxRetries = 5;
        options.Retry.Delay = TimeSpan.FromSeconds(1);
        options.Retry.MaxDelay = TimeSpan.FromSeconds(30);
        options.Retry.NetworkTimeout = TimeSpan.FromSeconds(100);

        // No telemetry: no SDK logging, no distributed tracing, no SDK info in the User-Agent.
        options.Diagnostics.IsLoggingEnabled = false;
        options.Diagnostics.IsDistributedTracingEnabled = false;
        options.Diagnostics.IsTelemetryEnabled = false;
        return options;
    }

    /// <summary>Containers (shared folders) in the account that the user can list.</summary>
    public async Task<IReadOnlyList<BrowseItem>> ListContainersAsync(BlobTarget target, CancellationToken cancellationToken)
    {
        var service = new BlobServiceClient(target.AccountUri, credential, _options);
        var items = new List<BrowseItem>();
        await foreach (var container in service.GetBlobContainersAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            items.Add(new BrowseItem(BrowseItemKind.Container, container.Name, container.Name, LastModified: container.Properties.LastModified));
        }

        return items;
    }

    /// <summary>One folder level: sub-folders (from the <c>/</c> delimiter) and files directly under <paramref name="prefix"/>.</summary>
    /// <param name="progress">Receives the number of entries listed so far, every 1000 entries.</param>
    public async Task<IReadOnlyList<BrowseItem>> ListFolderAsync(
        BlobTarget target,
        string? prefix,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var container = GetContainer(target);
        var items = new List<BrowseItem>();
        await foreach (var entry in container
            .GetBlobsByHierarchyAsync(BlobTraits.None, BlobStates.None, delimiter: "/", prefix: NullIfEmpty(prefix), cancellationToken: cancellationToken)
            .ConfigureAwait(false))
        {
            if (entry.IsPrefix)
            {
                items.Add(new BrowseItem(BrowseItemKind.Folder, BrowseItem.LastSegment(entry.Prefix), entry.Prefix));
            }
            else
            {
                var blob = entry.Blob;
                items.Add(new BrowseItem(
                    BrowseItemKind.File,
                    BrowseItem.LastSegment(blob.Name),
                    blob.Name,
                    blob.Properties.ContentLength,
                    blob.Properties.LastModified,
                    blob.Properties.ContentHash is { Length: > 0 },
                    AzureBlobReader.ToRemoteBlob(blob)));
            }

            if (items.Count % ProgressEvery == 0)
            {
                progress?.Report(items.Count);
            }
        }

        return items;
    }

    /// <summary>
    /// Counts everything under <paramref name="prefix"/> with one flat listing, and returns the totals of
    /// that folder and of every sub-folder below it (keyed by prefix; the container root is <c>""</c>).
    /// </summary>
    /// <param name="progress">Receives the running totals of the folder, every 1000 files.</param>
    public async Task<IReadOnlyDictionary<string, FolderTotals>> CountFolderAsync(
        BlobTarget target,
        string prefix,
        IProgress<FolderTotals>? progress,
        CancellationToken cancellationToken)
    {
        var container = GetContainer(target);
        var accumulator = new FolderTotalsAccumulator(prefix);
        await foreach (var blob in container
            .GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix: NullIfEmpty(prefix), cancellationToken: cancellationToken)
            .ConfigureAwait(false))
        {
            accumulator.Add(blob.Name, blob.Properties.ContentLength ?? 0, blob.Properties.ContentHash is { Length: > 0 });
            if (accumulator.FileCount % ProgressEvery == 0)
            {
                progress?.Report(accumulator.RootTotals);
            }
        }

        return accumulator.ToDictionary();
    }

    private static string? NullIfEmpty(string? prefix) => string.IsNullOrEmpty(prefix) ? null : prefix;

    private BlobContainerClient GetContainer(BlobTarget target)
    {
        if (!target.HasContainer)
        {
            throw new ArgumentException("The target has no container.", nameof(target));
        }

        return new BlobContainerClient(target.ContainerUri, credential, _options);
    }
}
