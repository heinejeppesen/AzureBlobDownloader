namespace AzureBlobDownloader.Core.Browsing;

/// <summary>
/// Builds <see cref="FolderTotals"/> for a folder and every sub-folder below it from one flat listing,
/// so counting a folder also gives the totals of all its sub-folders.
/// </summary>
public sealed class FolderTotalsAccumulator(string rootPrefix)
{
    private readonly Dictionary<string, (long Files, long Bytes, long WithMd5)> _totals = new(StringComparer.Ordinal)
    {
        [rootPrefix] = default,
    };

    public string RootPrefix { get; } = rootPrefix;

    public long FileCount => _totals[RootPrefix].Files;

    public long Bytes => _totals[RootPrefix].Bytes;

    /// <summary>Adds a blob that lies under <see cref="RootPrefix"/>.</summary>
    public void Add(string blobName, long size, bool hasMd5)
    {
        if (!blobName.StartsWith(RootPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException($"\"{blobName}\" is not under \"{RootPrefix}\".", nameof(blobName));
        }

        // The root and every folder between the root and the blob.
        var end = RootPrefix.Length;
        while (true)
        {
            var prefix = blobName[..end];
            _totals.TryGetValue(prefix, out var t);
            _totals[prefix] = (t.Files + 1, t.Bytes + size, t.WithMd5 + (hasMd5 ? 1 : 0));

            var slash = blobName.IndexOf('/', end);
            if (slash < 0)
            {
                break;
            }

            end = slash + 1;
        }
    }

    public FolderTotals RootTotals => ToTotals(_totals[RootPrefix]);

    /// <summary>Totals per folder prefix, including <see cref="RootPrefix"/>.</summary>
    public IReadOnlyDictionary<string, FolderTotals> ToDictionary() =>
        _totals.ToDictionary(kv => kv.Key, kv => ToTotals(kv.Value), StringComparer.Ordinal);

    private static FolderTotals ToTotals((long Files, long Bytes, long WithMd5) t) => new(t.Files, t.Bytes, t.WithMd5);
}
