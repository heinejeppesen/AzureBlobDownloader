namespace AzureBlobDownloader.Core.Browsing;

/// <summary>A folder selected with everything below it (also the parts not loaded yet).</summary>
public sealed record SelectedFolder(BrowseNode Node);

/// <summary>A single selected file.</summary>
public sealed record SelectedFile(BrowseNode Node);

/// <summary>What to download: whole folders plus individual files, with no overlap.</summary>
public sealed record Selection(IReadOnlyList<SelectedFolder> Folders, IReadOnlyList<SelectedFile> Files)
{
    public bool IsEmpty => Folders.Count == 0 && Files.Count == 0;
}

/// <summary>Size of the selection as far as it is known.</summary>
/// <param name="UncountedFolders">Selected folders whose size is not known yet.</param>
public sealed record SelectionSummary(long FileCount, long Bytes, int UncountedFolders)
{
    public static SelectionSummary Empty { get; } = new(0, 0, 0);

    public bool IsEmpty => FileCount == 0 && UncountedFolders == 0;
}

/// <summary>
/// Tri-state selection over a lazily loaded tree.
/// <list type="bullet">
///   <item>Checking or unchecking a node applies to all its loaded descendants.</item>
///   <item>A parent is checked when all children are, unchecked when none are, partly checked otherwise.</item>
///   <item>Children loaded later inherit the parent's state, so a checked folder stays fully selected.</item>
/// </list>
/// </summary>
public static class SelectionTree
{
    public static void SetChecked(BrowseNode node, bool value)
    {
        SetDescendants(node, value);
        UpdateAncestors(node.Parent);
    }

    /// <summary>Attaches a freshly listed level. Children inherit a checked/unchecked parent's state.</summary>
    public static void AttachChildren(BrowseNode parent, IEnumerable<BrowseNode> children)
    {
        var list = children.ToList();
        var inherited = parent.IsChecked ?? false;
        foreach (var child in list)
        {
            child.IsChecked = inherited;
        }

        parent.SetChildren(list);
    }

    /// <summary>
    /// The selection as whole folders and single files: a fully checked folder is taken whole (including
    /// levels never expanded); a partly checked folder is resolved through its loaded children.
    /// </summary>
    public static Selection GetSelection(IEnumerable<BrowseNode> roots)
    {
        var folders = new List<SelectedFolder>();
        var files = new List<SelectedFile>();
        var stack = new Stack<BrowseNode>(roots.Reverse());
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            switch (node.IsChecked)
            {
                case true when node.IsFile:
                    files.Add(new SelectedFile(node));
                    break;
                case true:
                    folders.Add(new SelectedFolder(node));
                    break;
                case null when node.Children is { } children:
                    for (var i = children.Count - 1; i >= 0; i--)
                    {
                        stack.Push(children[i]);
                    }

                    break;
            }
        }

        return new Selection(folders, files);
    }

    /// <summary>
    /// File count and bytes of the selection. A selected folder counts through its totals, or through its
    /// loaded children when it has not been counted; otherwise it is reported as uncounted.
    /// </summary>
    public static SelectionSummary Summarize(IEnumerable<BrowseNode> roots)
    {
        long files = 0, bytes = 0;
        var uncounted = 0;
        var stack = new Stack<BrowseNode>(roots);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.IsChecked == false)
            {
                continue;
            }

            if (node.IsFile)
            {
                files++;
                bytes += node.Size ?? 0;
            }
            else if (node.IsChecked == true && node.Totals is { } totals)
            {
                files += totals.FileCount;
                bytes += totals.Bytes;
            }
            else if (node.Children is { } children)
            {
                foreach (var child in children)
                {
                    stack.Push(child);
                }
            }
            else
            {
                uncounted++;
            }
        }

        return new SelectionSummary(files, bytes, uncounted);
    }

    /// <summary>Loaded nodes under <paramref name="node"/> (not including it), depth first.</summary>
    public static IEnumerable<BrowseNode> LoadedDescendants(BrowseNode node)
    {
        var stack = new Stack<BrowseNode>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current.Children is null)
            {
                continue;
            }

            foreach (var child in current.Children)
            {
                yield return child;
                stack.Push(child);
            }
        }
    }

    private static void SetDescendants(BrowseNode node, bool value)
    {
        node.IsChecked = value;
        foreach (var descendant in LoadedDescendants(node))
        {
            descendant.IsChecked = value;
        }
    }

    private static void UpdateAncestors(BrowseNode? parent)
    {
        while (parent?.Children is { Count: > 0 } children)
        {
            bool? state = children[0].IsChecked;
            for (var i = 1; i < children.Count && state is not null; i++)
            {
                if (children[i].IsChecked != state)
                {
                    state = null;
                }
            }

            if (parent.IsChecked == state)
            {
                return;
            }

            parent.IsChecked = state;
            parent = parent.Parent;
        }
    }
}
