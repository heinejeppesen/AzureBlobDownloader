using AzureBlobDownloader.Core.Browsing;
using AzureBlobDownloader.Core.Storage;
using AzureBlobDownloader.Core.Urls;

namespace AzureBlobDownloader.App.Tests;

/// <summary>In-memory blob service: containers with blob names and sizes, plus call counters and an optional gate.</summary>
internal sealed class FakeFolderSource : IFolderSource
{
    private readonly Dictionary<string, Dictionary<string, long>> _containers = new(StringComparer.Ordinal);

    public int ListFolderCalls { get; private set; }

    public int CountCalls { get; private set; }

    /// <summary>When set, listings wait for it (to simulate a slow service).</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>When set, the next folder listing throws it.</summary>
    public Exception? FailNextListing { get; set; }

    public FakeFolderSource Add(string container, params (string Name, long Size)[] blobs)
    {
        if (!_containers.TryGetValue(container, out var map))
        {
            map = new Dictionary<string, long>(StringComparer.Ordinal);
            _containers[container] = map;
        }

        foreach (var (name, size) in blobs)
        {
            map[name] = size;
        }

        return this;
    }

    public Task<IReadOnlyList<BrowseItem>> ListContainersAsync(BlobTarget account, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BrowseItem>>(
            [.. _containers.Keys.Select(c => new BrowseItem(BrowseItemKind.Container, c, c))]);

    public async Task<IReadOnlyList<BrowseItem>> ListFolderAsync(
        BlobTarget container,
        string? prefix,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        ListFolderCalls++;
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (FailNextListing is { } failure)
        {
            FailNextListing = null;
            throw failure;
        }

        prefix ??= string.Empty;
        var items = new List<BrowseItem>();
        var folders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, size) in _containers[container.ContainerName!].Where(b => b.Key.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var rest = name[prefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash >= 0)
            {
                var folder = prefix + rest[..(slash + 1)];
                if (folders.Add(folder))
                {
                    items.Add(new BrowseItem(BrowseItemKind.Folder, BrowseItem.LastSegment(folder), folder));
                }
            }
            else
            {
                items.Add(new BrowseItem(BrowseItemKind.File, rest, name, size, DateTimeOffset.UnixEpoch, HasContentMd5: false));
            }
        }

        return items;
    }

    public Task<IReadOnlyDictionary<string, FolderTotals>> CountFolderAsync(
        BlobTarget container,
        string prefix,
        IProgress<FolderTotals>? progress,
        CancellationToken cancellationToken)
    {
        CountCalls++;
        var accumulator = new FolderTotalsAccumulator(prefix);
        foreach (var (name, size) in _containers[container.ContainerName!].Where(b => b.Key.StartsWith(prefix, StringComparison.Ordinal)))
        {
            accumulator.Add(name, size, hasMd5: false);
        }

        return Task.FromResult(accumulator.ToDictionary());
    }
}
