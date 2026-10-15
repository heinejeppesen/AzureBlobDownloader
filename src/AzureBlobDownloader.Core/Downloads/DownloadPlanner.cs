using AzureBlobDownloader.Core.Browsing;
using AzureBlobDownloader.Core.Storage;
using AzureBlobDownloader.Core.Urls;

namespace AzureBlobDownloader.Core.Downloads;

/// <param name="Rules">File name rules of the destination.</param>
/// <param name="CaseSensitive">Whether the destination tells <c>a.txt</c> and <c>A.txt</c> apart.</param>
public sealed record PlanOptions(NameRules Rules, bool CaseSensitive);

/// <summary>
/// Turns a selection into the list of files to download and where each one goes.
/// <para>
/// Layout: everything goes into a folder named after the link's root (the shared folder, or the folder the
/// link points to); below it the blob names' folder structure is kept. Downloading more of the same share
/// later lands in the same tree, so finished files are skipped and partial ones continue.
/// </para>
/// </summary>
public static class DownloadPlanner
{
    /// <summary>Lists every selected file (whole folders are listed in full) and maps it to a local path.</summary>
    /// <param name="progress">Receives the number of files found so far.</param>
    public static async Task<DownloadPlan> PlanAsync(
        Selection selection,
        BlobTarget link,
        IBlobReader reader,
        PlanOptions options,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var blobs = new List<(BlobTarget Container, RemoteBlob Blob)>();
        foreach (var folder in selection.Folders)
        {
            await foreach (var blob in reader.EnumerateAsync(folder.Node.Container, folder.Node.Path, cancellationToken))
            {
                blobs.Add((folder.Node.Container, blob));
                if (blobs.Count % 1000 == 0)
                {
                    progress?.Report(blobs.Count);
                }
            }
        }

        foreach (var file in selection.Files)
        {
            var blob = file.Node.Blob
                ?? await reader.GetPropertiesAsync(file.Node.Container, file.Node.Path, cancellationToken);
            blobs.Add((file.Node.Container, blob));
        }

        progress?.Report(blobs.Count);
        return Map(blobs, link, options);
    }

    /// <summary>Maps blobs to local relative paths: sanitises names, skips unsafe ones and resolves name clashes.</summary>
    public static DownloadPlan Map(IEnumerable<(BlobTarget Container, RemoteBlob Blob)> blobs, BlobTarget link, PlanOptions options)
    {
        var issues = new List<PlanIssue>();
        var mapped = new List<(BlobTarget Container, RemoteBlob Blob, string[] Segments)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (container, blob) in blobs
                     .OrderBy(b => b.Container.ContainerName, StringComparer.Ordinal)
                     .ThenBy(b => b.Blob.Name, StringComparer.Ordinal))
        {
            if (!seen.Add($"{container.ContainerName}/{blob.Name}"))
            {
                continue; // Listed twice (e.g. a file inside a ticked folder).
            }

            // Zero-byte "folder marker" blobs (created by some tools) are folders, not files.
            if (blob.Name.EndsWith('/') && blob.Size == 0)
            {
                continue;
            }

            if (MapSegments(link, container, blob.Name, options.Rules, out var segments, out var problem) is { } changes)
            {
                mapped.Add((container, blob, segments));
                foreach (var change in changes)
                {
                    issues.Add(new PlanIssue(PlanIssueKind.Renamed, blob.Name, Join(segments), change));
                }
            }
            else
            {
                issues.Add(new PlanIssue(PlanIssueKind.Skipped, blob.Name, null, problem!));
            }
        }

        var items = ResolveClashes(mapped, options.CaseSensitive, issues);
        return new DownloadPlan(items, issues);
    }

    /// <summary>
    /// The local path segments for a blob, starting with the root folder name. Returns the list of renames,
    /// or null with <paramref name="problem"/> set when the blob cannot be stored safely.
    /// </summary>
    internal static List<string>? MapSegments(
        BlobTarget link,
        BlobTarget container,
        string blobName,
        NameRules rules,
        out string[] segments,
        out string? problem)
    {
        segments = [];
        problem = null;

        string rootName;
        string relative;
        switch (link.Kind)
        {
            case BlobTargetKind.ContainerWithPrefix when blobName.StartsWith(link.Prefix!, StringComparison.Ordinal):
                rootName = BrowseItem.LastSegment(link.Prefix!);
                relative = blobName[link.Prefix!.Length..];
                break;
            case BlobTargetKind.ContainerWithPrefix:
                problem = "is outside the shared folder";
                return null;
            default:
                rootName = container.ContainerName!;
                relative = blobName;
                break;
        }

        var raw = new List<string> { rootName };
        raw.AddRange(relative.Split('/'));

        var changes = new List<string>();
        var result = new string[raw.Count];
        for (var i = 0; i < raw.Count; i++)
        {
            var segment = raw[i];
            if (segment.Length == 0)
            {
                problem = "has an empty folder name in its path (\"//\")";
                return null;
            }

            // Never let a name climb out of the destination folder.
            if (segment is "." or "..")
            {
                problem = "has \".\" or \"..\" in its path, which is not safe to store";
                return null;
            }

            var (name, change) = LocalNames.Sanitize(segment, rules);
            if (LocalNames.MeasureName(name, rules) > LocalNames.MaxNameLength)
            {
                problem = $"has a name longer than {LocalNames.MaxNameLength} characters";
                return null;
            }

            if (change is not null)
            {
                changes.Add($"\"{segment}\" {change}; stored as \"{name}\"");
            }

            result[i] = name;
        }

        segments = result;
        return changes;
    }

    private static List<DownloadItem> ResolveClashes(
        List<(BlobTarget Container, RemoteBlob Blob, string[] Segments)> mapped,
        bool caseSensitive,
        List<PlanIssue> issues)
    {
        var comparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var directories = new HashSet<string>(comparer);
        foreach (var (_, _, segments) in mapped)
        {
            for (var depth = 1; depth < segments.Length; depth++)
            {
                directories.Add(Join(segments.AsSpan(0, depth)));
            }
        }

        var files = new HashSet<string>(comparer);
        var items = new List<DownloadItem>(mapped.Count);
        foreach (var (container, blob, segments) in mapped)
        {
            var path = Join(segments);
            if (files.Contains(path) || directories.Contains(path))
            {
                var original = path;
                var name = segments[^1];
                var extension = Path.GetExtension(name);
                var stem = name[..^extension.Length];
                for (var n = 2; files.Contains(path) || directories.Contains(path); n++)
                {
                    segments[^1] = $"{stem} ({n}){extension}";
                    path = Join(segments);
                }

                issues.Add(new PlanIssue(
                    PlanIssueKind.Renamed,
                    blob.Name,
                    path,
                    caseSensitive
                        ? $"would get the same local name as another file or folder (\"{original}\"); stored as \"{segments[^1]}\""
                        : $"would get the same local name as another file or folder (\"{original}\", upper/lower case is not told apart here); stored as \"{segments[^1]}\""));
            }

            files.Add(path);
            items.Add(new DownloadItem(container, blob, path));
        }

        return items;
    }

    private static string Join(ReadOnlySpan<string> segments) => string.Join(Path.DirectorySeparatorChar, segments!);

    private static string Join(string[] segments) => string.Join(Path.DirectorySeparatorChar, segments);
}
