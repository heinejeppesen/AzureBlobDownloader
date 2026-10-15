using AzureBlobDownloader.App.ViewModels;
using AzureBlobDownloader.Core.Browsing;
using AzureBlobDownloader.Core.Urls;

namespace AzureBlobDownloader.App.Tests;

public class BrowseTreeViewModelTests
{
    private static readonly string[] AllowedHosts = ["cda*.blob.core.windows.net"];

    private readonly FakeFolderSource _source = new FakeFolderSource()
        .Add(
            "report-1",
            ("top.txt", 1),
            ("A/a1.txt", 10),
            ("A/a2.txt", 20),
            ("A/Sub/s1.txt", 100),
            ("B/b1.txt", 1000));

    private readonly List<Exception> _errors = [];

    private BrowseTreeViewModel CreateTree() => new(() => _source, _errors.Add);

    private static BlobTarget Link(string url)
    {
        Assert.True(BlobUrlParser.TryParse(url, AllowedHosts, out var target));
        return target;
    }

    private static string[] Names(BrowseTreeViewModel tree) =>
        [.. tree.Rows.Select(r => new string(' ', r.Node.Depth * 2) + r.Name)];

    private static TreeRow Row(BrowseTreeViewModel tree, string name) => tree.Rows.Single(r => r.Name == name);

    private static async Task ExpandAsync(BrowseTreeViewModel tree, string name, bool expand = true)
    {
        Row(tree, name).IsExpanded = expand;
        await tree.WhenIdleAsync();
    }

    [Fact]
    public void Container_link_opens_the_container_with_folders_first() => UiThread.Run(async() =>
    {
        var tree = CreateTree();

        var count = await tree.OpenAsync(Link("https://cda1.blob.core.windows.net/report-1"), TestContext.Current.CancellationToken);
        await tree.WhenIdleAsync();

        Assert.Equal(1, count);
        Assert.Equal(["report-1", "  A", "  B", "  top.txt"], Names(tree));
        Assert.True(tree.Rows[0].IsExpanded);
        Assert.Equal("Nothing selected yet.", tree.SelectionText);
    });

    [Fact]
    public void Folder_link_opens_at_that_folder() => UiThread.Run(async() =>
    {
        var tree = CreateTree();

        await tree.OpenAsync(Link("https://cda1.blob.core.windows.net/report-1/A"), TestContext.Current.CancellationToken);
        await tree.WhenIdleAsync();

        Assert.Equal(["A", "  Sub", "  a1.txt", "  a2.txt"], Names(tree));
    });

    [Fact]
    public void Expanding_and_collapsing_inserts_and_removes_the_right_rows() => UiThread.Run(async() =>
    {
        var tree = CreateTree();
        await tree.OpenAsync(Link("https://cda1.blob.core.windows.net/report-1"), TestContext.Current.CancellationToken);

        await ExpandAsync(tree, "A");
        Assert.Equal(["report-1", "  A", "    Sub", "    a1.txt", "    a2.txt", "  B", "  top.txt"], Names(tree));

        await ExpandAsync(tree, "Sub");
        Assert.Equal(["report-1", "  A", "    Sub", "      s1.txt", "    a1.txt", "    a2.txt", "  B", "  top.txt"], Names(tree));

        await ExpandAsync(tree, "A", expand: false);
        Assert.Equal(["report-1", "  A", "  B", "  top.txt"], Names(tree));
    });

    [Fact]
    public void Reopening_a_folder_uses_the_loaded_level_and_remembers_open_sub_folders() => UiThread.Run(async() =>
    {
        var tree = CreateTree();
        await tree.OpenAsync(Link("https://cda1.blob.core.windows.net/report-1"), TestContext.Current.CancellationToken);
        await ExpandAsync(tree, "A");
        await ExpandAsync(tree, "Sub");
        await ExpandAsync(tree, "A", expand: false);
        var calls = _source.ListFolderCalls;

        await ExpandAsync(tree, "A");

        Assert.Equal(calls, _source.ListFolderCalls);
        Assert.Contains("      s1.txt", Names(tree));
    });

    [Fact]
    public void Toggling_while_loading_does_not_duplicate_rows() => UiThread.Run(async() =>
    {
        var tree = CreateTree();
        await tree.OpenAsync(Link("https://cda1.blob.core.windows.net/report-1"), TestContext.Current.CancellationToken);
        _source.Gate = new TaskCompletionSource();

        var a = Row(tree, "A");
        a.IsExpanded = true;
        a.IsExpanded = false;
        a.IsExpanded = true;
        _source.Gate.SetResult();
        await tree.WhenIdleAsync();

        Assert.Equal(["report-1", "  A", "    Sub", "    a1.txt", "    a2.txt", "  B", "  top.txt"], Names(tree));
    });

    [Fact]
    public void Ticking_a_folder_counts_it_and_updates_the_summary() => UiThread.Run(async() =>
    {
        var tree = CreateTree();
        await tree.OpenAsync(Link("https://cda1.blob.core.windows.net/report-1"), TestContext.Current.CancellationToken);

        Row(tree, "A").IsChecked = true;
        await tree.WhenIdleAsync();

        Assert.Equal(1, _source.CountCalls);
        Assert.Equal(new FolderTotals(3, 130, 0), Row(tree, "A").Node.Totals);
        Assert.Equal((3, 130L, 0), (tree.Summary.FileCount, tree.Summary.Bytes, tree.Summary.UncountedFolders));
        Assert.StartsWith("Selected: 3 files", tree.SelectionText, StringComparison.Ordinal);
        Assert.Null(tree.Rows[0].IsChecked); // the container is partly selected
    });

    [Fact]
    public void Sub_folders_get_their_totals_from_the_parent_count() => UiThread.Run(async() =>
    {
        var tree = CreateTree();
        await tree.OpenAsync(Link("https://cda1.blob.core.windows.net/report-1"), TestContext.Current.CancellationToken);
        Row(tree, "report-1").CountCommand.Execute(null);
        await tree.WhenIdleAsync();

        await ExpandAsync(tree, "A");

        Assert.Equal(1, _source.CountCalls);
        Assert.Equal(new FolderTotals(1, 100, 0), Row(tree, "Sub").Node.Totals);
        Assert.Equal(new FolderTotals(5, 1131, 0), Row(tree, "report-1").Node.Totals);
        Assert.False(Row(tree, "Sub").ShowCountButton);
    });

    [Fact]
    public void Selection_reports_whole_folders_and_single_files() => UiThread.Run(async() =>
    {
        var tree = CreateTree();
        await tree.OpenAsync(Link("https://cda1.blob.core.windows.net/report-1"), TestContext.Current.CancellationToken);
        await ExpandAsync(tree, "A");

        Row(tree, "B").IsChecked = true;
        Row(tree, "a1.txt").IsChecked = true;
        await tree.WhenIdleAsync();

        var selection = tree.GetSelection();
        Assert.Equal(["B/"], selection.Folders.Select(f => f.Node.Path));
        Assert.Equal(["A/a1.txt"], selection.Files.Select(f => f.Node.Path));
        Assert.Equal((2, 1010L), (tree.Summary.FileCount, tree.Summary.Bytes));
    });

    [Fact]
    public void Account_link_lists_containers_closed() => UiThread.Run(async() =>
    {
        _source.Add("report-2", ("x.bin", 5));
        var tree = CreateTree();

        var count = await tree.OpenAsync(Link("https://cda1.blob.core.windows.net/"), TestContext.Current.CancellationToken);

        Assert.Equal(2, count);
        Assert.Equal(["report-1", "report-2"], Names(tree));
        Assert.All(tree.Rows, r => Assert.False(r.IsExpanded));

        await ExpandAsync(tree, "report-2");
        Assert.Equal(["report-1", "report-2", "  x.bin"], Names(tree));
    });

    [Fact]
    public void Failed_listing_reports_the_error_closes_the_row_and_can_be_retried() => UiThread.Run(async() =>
    {
        var tree = CreateTree();
        await tree.OpenAsync(Link("https://cda1.blob.core.windows.net/report-1"), TestContext.Current.CancellationToken);
        _source.FailNextListing = new InvalidOperationException("boom");

        await ExpandAsync(tree, "A");

        Assert.Single(_errors);
        Assert.False(Row(tree, "A").IsExpanded);
        Assert.Equal(["report-1", "  A", "  B", "  top.txt"], Names(tree));

        await ExpandAsync(tree, "A");
        Assert.Contains("    a1.txt", Names(tree));
    });

    [Fact]
    public void Keyboard_left_closes_then_moves_to_the_parent() => UiThread.Run(async() =>
    {
        var tree = CreateTree();
        await tree.OpenAsync(Link("https://cda1.blob.core.windows.net/report-1"), TestContext.Current.CancellationToken);
        await ExpandAsync(tree, "A");
        tree.SelectedRow = Row(tree, "a1.txt");

        tree.ExpandSelected(false);
        Assert.Same(Row(tree, "A"), tree.SelectedRow);

        tree.ExpandSelected(false);
        await tree.WhenIdleAsync();
        Assert.False(Row(tree, "A").IsExpanded);

        tree.ExpandSelected(true);
        await tree.WhenIdleAsync();
        Assert.True(Row(tree, "A").IsExpanded);

        tree.ToggleSelectedCheck();
        await tree.WhenIdleAsync();
        Assert.True(Row(tree, "A").IsChecked);
    });

    [Fact]
    public void Close_clears_the_tree_and_the_selection() => UiThread.Run(async() =>
    {
        var tree = CreateTree();
        await tree.OpenAsync(Link("https://cda1.blob.core.windows.net/report-1"), TestContext.Current.CancellationToken);
        Row(tree, "top.txt").IsChecked = true;

        tree.Close();

        Assert.Empty(tree.Rows);
        Assert.False(tree.HasRows);
        Assert.False(tree.HasSelection);
        Assert.Null(tree.Link);
        Assert.True(tree.GetSelection().IsEmpty);
    });
}
