using AzureBlobDownloader.Core.Storage;
using AzureBlobDownloader.Core.Urls;

namespace AzureBlobDownloader.Core.Browsing;

/// <summary>Where the browse tree gets its listings from (the blob service, or a fake in tests).</summary>
public interface IFolderSource
{
    /// <summary>Containers (shared folders) in the account that the user can list.</summary>
    Task<IReadOnlyList<BrowseItem>> ListContainersAsync(BlobTarget account, CancellationToken cancellationToken);

    /// <summary>One folder level: sub-folders and files directly under <paramref name="prefix"/>.</summary>
    Task<IReadOnlyList<BrowseItem>> ListFolderAsync(
        BlobTarget container,
        string? prefix,
        IProgress<int>? progress,
        CancellationToken cancellationToken);

    /// <summary>Totals of the folder at <paramref name="prefix"/> and of every sub-folder below it, keyed by prefix.</summary>
    Task<IReadOnlyDictionary<string, FolderTotals>> CountFolderAsync(
        BlobTarget container,
        string prefix,
        IProgress<FolderTotals>? progress,
        CancellationToken cancellationToken);
}
