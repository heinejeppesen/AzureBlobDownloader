using System.ComponentModel;
using System.Runtime.CompilerServices;
using AzureBlobDownloader.Core.Downloads;
using AzureBlobDownloader.Core.Storage;
using AzureBlobDownloader.Core.Urls;

namespace AzureBlobDownloader.Core.Browsing;

/// <summary>Totals for everything under a folder (all levels).</summary>
public sealed record FolderTotals(long FileCount, long Bytes, long FilesWithMd5);

/// <summary>
/// One node of the browse tree: a container, a folder (blob name prefix) or a file (blob).
/// Children are loaded lazily, one level at a time. Check state is managed by <see cref="SelectionTree"/>.
/// </summary>
public sealed class BrowseNode : INotifyPropertyChanged
{
    private List<BrowseNode>? _children;
    private bool? _isChecked = false;
    private FolderTotals? _totals;

    public BrowseNode(
        BrowseItemKind kind,
        string name,
        string path,
        BlobTarget container,
        BrowseNode? parent = null,
        long? size = null,
        DateTimeOffset? lastModified = null,
        bool? hasContentMd5 = null,
        RemoteBlob? blob = null)
    {
        if (!container.HasContainer)
        {
            throw new ArgumentException("A node always belongs to a container.", nameof(container));
        }

        Kind = kind;
        Name = name;
        Path = path;
        Container = container;
        Parent = parent;
        Size = size;
        LastModified = lastModified;
        HasContentMd5 = hasContentMd5;
        Blob = blob;
        Depth = parent is null ? 0 : parent.Depth + 1;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public BrowseItemKind Kind { get; }

    public string Name { get; }

    /// <summary>Blob name for files; folder prefix ending in <c>/</c> for folders; empty for a container root.</summary>
    public string Path { get; }

    /// <summary>The container this node lives in.</summary>
    public BlobTarget Container { get; }

    public BrowseNode? Parent { get; }

    public int Depth { get; }

    public long? Size { get; }

    public DateTimeOffset? LastModified { get; }

    public bool? HasContentMd5 { get; }

    /// <summary>For files: the blob's properties (version, MD5) as listed.</summary>
    public RemoteBlob? Blob { get; }

    public bool IsFile => Kind == BrowseItemKind.File;

    /// <summary>Loaded children, or null when this level has not been listed yet.</summary>
    public IReadOnlyList<BrowseNode>? Children => _children;

    public bool ChildrenLoaded => _children is not null;

    /// <summary>true = selected (with everything below), false = not selected, null = partly selected.</summary>
    public bool? IsChecked
    {
        get => _isChecked;
        internal set => Set(ref _isChecked, value);
    }

    /// <summary>Size of everything under a folder; null until counted.</summary>
    public FolderTotals? Totals
    {
        get => _totals;
        set => Set(ref _totals, value);
    }

    /// <summary>Creates a child node from a listing entry.</summary>
    public BrowseNode CreateChild(BrowseItem item) =>
        new(item.Kind, item.Name, item.Path, Container, this, item.Size, item.LastModified, item.HasContentMd5, item.Blob);

    internal void SetChildren(List<BrowseNode> children) => _children = children;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public override string ToString() => $"{Kind} {Container.ContainerName}/{Path}";
}
