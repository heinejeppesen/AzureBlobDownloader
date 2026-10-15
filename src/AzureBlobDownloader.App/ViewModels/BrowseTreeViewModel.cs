using Avalonia.Collections;
using AzureBlobDownloader.Core.Browsing;
using AzureBlobDownloader.Core.Storage;
using AzureBlobDownloader.Core.Urls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AzureBlobDownloader.App.ViewModels;

/// <summary>
/// The browse tree of one opened link, shown as a flat virtualized list: folders load one level at a time
/// when opened, folder sizes are counted on demand (and when a folder is ticked), and the selection
/// summary updates as the user ticks files and folders.
/// </summary>
public sealed partial class BrowseTreeViewModel : ObservableObject, ITreeRowHost
{
    private const int MaxParallelCounts = 2;

    private readonly Func<IFolderSource> _source;
    private readonly Action<Exception> _onError;
    private readonly Lock _pendingGate = new();
    private readonly HashSet<Task> _pending = [];
    private BrowseSession? _session;

    /// <param name="source">Provides the listing service (created after sign-in).</param>
    /// <param name="onError">Shows errors from background work (loading, counting).</param>
    public BrowseTreeViewModel(Func<IFolderSource> source, Action<Exception> onError)
    {
        _source = source;
        _onError = onError;
        Rows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRows));
    }

    /// <summary>The visible rows of the tree, in display order.</summary>
    public AvaloniaList<TreeRow> Rows { get; } = [];

    public bool HasRows => Rows.Count > 0;

    /// <summary>The link currently shown, or null.</summary>
    public BlobTarget? Link => _session?.Link;

    public IReadOnlyList<BrowseNode> Roots => _session?.Roots ?? [];

    [ObservableProperty]
    public partial TreeRow? SelectedRow { get; set; }

    [ObservableProperty]
    public partial string? LocationText { get; set; }

    [ObservableProperty]
    public partial string SelectionText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasSelection { get; set; }

    [ObservableProperty]
    public partial SelectionSummary Summary { get; set; } = SelectionSummary.Empty;

    /// <summary>Opens <paramref name="target"/>: lists the shared folders of an account, or opens a single shared folder.</summary>
    /// <returns>The number of top-level entries.</returns>
    public async Task<int> OpenAsync(BlobTarget target, CancellationToken cancellationToken)
    {
        Close();
        var session = new BrowseSession(target);
        _session = session;
        LocationText = target.ToString();

        List<BrowseNode> roots;
        if (target.Kind == BlobTargetKind.Account)
        {
            var containers = await _source().ListContainersAsync(target, cancellationToken);
            roots = containers
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c => new BrowseNode(BrowseItemKind.Container, c.Name, string.Empty, target.ForContainer(c.Path), lastModified: c.LastModified))
                .ToList();
        }
        else
        {
            roots = target.Kind == BlobTargetKind.Container
                ? [new BrowseNode(BrowseItemKind.Container, target.ContainerName!, string.Empty, target)]
                : [new BrowseNode(BrowseItemKind.Folder, BrowseItem.LastSegment(target.Prefix!), target.Prefix!, target)];
        }

        if (session != _session)
        {
            return 0;
        }

        session.Roots.AddRange(roots);
        Rows.AddRange(roots.Select(session.RowFor(this)));

        // A link to one shared folder opens it straight away.
        if (roots.Count == 1)
        {
            var row = Rows[0];
            if (await EnsureChildrenLoadedAsync(row, session))
            {
                row.IsExpanded = true;
            }
        }

        UpdateSelectionSummary();
        return roots.Count;
    }

    /// <summary>Closes the current link and cancels its background work.</summary>
    public void Close()
    {
        _session?.Dispose();
        _session = null;
        Rows.Clear();
        SelectedRow = null;
        LocationText = null;
        UpdateSelectionSummary();
    }

    /// <summary>What is ticked, as whole folders and single files.</summary>
    public Selection GetSelection() => SelectionTree.GetSelection(Roots);

    /// <summary>Completes when no listing or counting started by the tree is running (for tests).</summary>
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_pendingGate)
            {
                pending = [.. _pending];
            }

            if (pending.Length == 0)
            {
                return;
            }

            await Task.WhenAll(pending);
        }
    }

    /// <summary>
    /// Keyboard navigation: open the selected folder, or close it; closing an already closed row (or a file)
    /// moves to its parent folder, like a file manager.
    /// </summary>
    public void ExpandSelected(bool expand)
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        if (expand)
        {
            if (row.CanExpand)
            {
                row.IsExpanded = true;
            }
        }
        else if (row.IsExpanded)
        {
            row.IsExpanded = false;
        }
        else if (row.Node.Parent is { } parent && _session?.FindRow(parent) is { } parentRow)
        {
            SelectedRow = parentRow;
        }
    }

    /// <summary>Toggles the check box of the selected row.</summary>
    public void ToggleSelectedCheck()
    {
        if (SelectedRow is { } row)
        {
            row.IsChecked = row.IsChecked != true;
        }
    }

    void ITreeRowHost.OnExpandedChanged(TreeRow row, bool expanded) => Track(ExpandOrCollapseAsync(row, expanded));

    void ITreeRowHost.SetChecked(TreeRow row, bool value)
    {
        SelectionTree.SetChecked(row.Node, value);

        // Ticking a folder is the moment its size matters: count it in the background.
        if (value && !row.Node.IsFile && row.Node.Totals is null)
        {
            Track(CountAsync(row));
        }

        UpdateSelectionSummary();
    }

    void ITreeRowHost.RequestCount(TreeRow row) => Track(CountAsync(row));

    private void Track(Task task)
    {
        lock (_pendingGate)
        {
            _pending.Add(task);
        }

        task.ContinueWith(
            t =>
            {
                lock (_pendingGate)
                {
                    _pending.Remove(t);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task ExpandOrCollapseAsync(TreeRow row, bool expanded)
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        if (!expanded)
        {
            Collapse(row);
            return;
        }

        if (!await EnsureChildrenLoadedAsync(row, session))
        {
            row.IsExpanded = false;
            return;
        }

        // The user may have closed the row, or opened another link, while it was loading.
        if (row.IsExpanded && session == _session)
        {
            InsertVisibleDescendants(row, session);
        }
    }

    /// <summary>Lists the row's children once; callers that arrive while it is loading share the same listing.</summary>
    private async Task<bool> EnsureChildrenLoadedAsync(TreeRow row, BrowseSession session)
    {
        if (row.Node.ChildrenLoaded)
        {
            return true;
        }

        var task = row.LoadTask ??= LoadChildrenAsync(row, session);
        var loaded = await task;

        // Forget a failed or cancelled listing so the next attempt lists again.
        if (!loaded && row.LoadTask == task)
        {
            row.LoadTask = null;
        }

        return loaded;
    }

    private async Task<bool> LoadChildrenAsync(TreeRow row, BrowseSession session)
    {
        var node = row.Node;
        row.IsLoading = true;
        try
        {
            var progress = new Progress<int>(n => row.ProgressText = $"Loading… {n:N0}");
            var items = await _source().ListFolderAsync(node.Container, node.Path, progress, session.Cancellation.Token);

            var children = items
                .OrderBy(i => i.IsFile)
                .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(node.CreateChild)
                .ToList();
            foreach (var child in children.Where(c => !c.IsFile))
            {
                child.Totals = session.CachedTotals(child);
            }

            SelectionTree.AttachChildren(node, children);
            UpdateSelectionSummary();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _onError(ex);
            return false;
        }
        finally
        {
            row.IsLoading = false;
            row.ProgressText = null;
        }
    }

    private void InsertVisibleDescendants(TreeRow row, BrowseSession session)
    {
        var index = Rows.IndexOf(row);
        if (index < 0 || (index + 1 < Rows.Count && Rows[index + 1].Node.Depth > row.Node.Depth))
        {
            return; // Gone, or already showing its children.
        }

        var rows = new List<TreeRow>();
        var rowFor = session.RowFor(this);
        AddVisible(row);
        Rows.InsertRange(index + 1, rows);

        void AddVisible(TreeRow parent)
        {
            foreach (var child in parent.Node.Children ?? [])
            {
                var childRow = rowFor(child);
                rows.Add(childRow);
                if (childRow.IsExpanded && child.ChildrenLoaded)
                {
                    AddVisible(childRow);
                }
            }
        }
    }

    private void Collapse(TreeRow row)
    {
        var index = Rows.IndexOf(row);
        if (index < 0)
        {
            return;
        }

        var end = index + 1;
        while (end < Rows.Count && Rows[end].Node.Depth > row.Node.Depth)
        {
            end++;
        }

        if (end > index + 1)
        {
            Rows.RemoveRange(index + 1, end - index - 1);
        }
    }

    /// <summary>Counts files and bytes under a folder (and, from the same listing, all its sub-folders).</summary>
    private async Task CountAsync(TreeRow row)
    {
        var session = _session;
        var node = row.Node;
        if (session is null || node.IsFile || node.Totals is not null || !session.Counting.Add(node))
        {
            return;
        }

        row.IsCounting = true;
        UpdateSelectionSummary();
        try
        {
            await session.CountGate.WaitAsync(session.Cancellation.Token);
            try
            {
                var progress = new Progress<FolderTotals>(t => row.ProgressText = $"{t.FileCount:N0} files…");
                var totals = await _source().CountFolderAsync(node.Container, node.Path, progress, session.Cancellation.Token);
                session.StoreTotals(node.Container, totals);

                node.Totals = session.CachedTotals(node);
                foreach (var folder in SelectionTree.LoadedDescendants(node).Where(n => !n.IsFile))
                {
                    folder.Totals ??= session.CachedTotals(folder);
                }
            }
            finally
            {
                session.CountGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Link closed.
        }
        catch (Exception ex)
        {
            _onError(ex);
        }
        finally
        {
            session.Counting.Remove(node);
            row.IsCounting = false;
            row.ProgressText = null;
            UpdateSelectionSummary();
        }
    }

    private void UpdateSelectionSummary()
    {
        var session = _session;
        Summary = session is null ? SelectionSummary.Empty : SelectionTree.Summarize(session.Roots);
        HasSelection = !Summary.IsEmpty;

        if (session is null)
        {
            SelectionText = string.Empty;
        }
        else if (Summary.IsEmpty)
        {
            SelectionText = "Nothing selected yet.";
        }
        else
        {
            var text = $"Selected: {Summary.FileCount:N0} files, {ByteSize.Format(Summary.Bytes)}";
            if (Summary.UncountedFolders > 0)
            {
                text += session.Counting.Count > 0
                    ? " so far, counting the rest…"
                    : $", plus {Summary.UncountedFolders:N0} folders not counted yet";
            }

            SelectionText = text;
        }
    }

    /// <summary>State of one opened link: the tree, its rows, cached folder totals and running counts.</summary>
    private sealed class BrowseSession(BlobTarget link) : IDisposable
    {
        private readonly Dictionary<BrowseNode, TreeRow> _rows = [];
        private readonly Dictionary<string, FolderTotals> _totals = new(StringComparer.Ordinal);

        public BlobTarget Link { get; } = link;

        public List<BrowseNode> Roots { get; } = [];

        public CancellationTokenSource Cancellation { get; } = new();

        public SemaphoreSlim CountGate { get; } = new(MaxParallelCounts);

        public HashSet<BrowseNode> Counting { get; } = [];

        /// <summary>Rows are kept per node, so a closed folder remembers which sub-folders were open.</summary>
        public Func<BrowseNode, TreeRow> RowFor(ITreeRowHost host) => node =>
        {
            if (!_rows.TryGetValue(node, out var row))
            {
                row = new TreeRow(node, host);
                _rows[node] = row;
            }

            return row;
        };

        public TreeRow? FindRow(BrowseNode node) => _rows.GetValueOrDefault(node);

        public void StoreTotals(BlobTarget container, IReadOnlyDictionary<string, FolderTotals> totals)
        {
            foreach (var (prefix, value) in totals)
            {
                _totals[Key(container, prefix)] = value;
            }
        }

        public FolderTotals? CachedTotals(BrowseNode folder) => _totals.GetValueOrDefault(Key(folder.Container, folder.Path));

        // Cancel only: background listings may still read the token while they wind down.
        public void Dispose() => Cancellation.Cancel();

        private static string Key(BlobTarget container, string prefix) => $"{container.ContainerName}/{prefix}";
    }
}
