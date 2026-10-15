using AzureBlobDownloader.Core.Browsing;
using AzureBlobDownloader.Core.Storage;
using AzureBlobDownloader.Core.Urls;

namespace AzureBlobDownloader.Core.Tests.Browsing;

public class SelectionTreeTests
{
    private static readonly BlobTarget Container = Parse("https://cda1.blob.core.windows.net/report-1");

    private static BlobTarget Parse(string url)
    {
        Assert.True(BlobUrlParser.TryParse(url, ["cda*.blob.core.windows.net"], out var target));
        return target;
    }

    private static BrowseNode Root() => new(BrowseItemKind.Container, "report-1", string.Empty, Container);

    private static List<BrowseNode> Load(BrowseNode parent, params string[] names)
    {
        var children = names
            .Select(n => n.EndsWith('/')
                ? parent.CreateChild(new BrowseItem(BrowseItemKind.Folder, n.TrimEnd('/'), parent.Path + n))
                : parent.CreateChild(new BrowseItem(BrowseItemKind.File, n, parent.Path + n, Size: 100)))
            .ToList();
        SelectionTree.AttachChildren(parent, children);
        return children;
    }

    [Fact]
    public void Checking_a_folder_checks_all_loaded_descendants()
    {
        var root = Root();
        var (a, f1) = (Load(root, "A/", "f1.txt") is var c ? (c[0], c[1]) : default);
        var (a1, a2) = (Load(a, "a1.txt", "a2.txt") is var d ? (d[0], d[1]) : default);

        SelectionTree.SetChecked(root, true);

        Assert.All([root, a, f1, a1, a2], n => Assert.True(n.IsChecked));
    }

    [Fact]
    public void Parent_becomes_partial_then_full_then_empty()
    {
        var root = Root();
        var a = Load(root, "A/", "f1.txt")[0];
        var files = Load(a, "a1.txt", "a2.txt");

        SelectionTree.SetChecked(files[0], true);
        Assert.Null(a.IsChecked);
        Assert.Null(root.IsChecked);

        SelectionTree.SetChecked(files[1], true);
        Assert.True(a.IsChecked);
        Assert.Null(root.IsChecked); // f1.txt is still unchecked

        SelectionTree.SetChecked(files[0], false);
        SelectionTree.SetChecked(files[1], false);
        Assert.False(a.IsChecked);
        Assert.False(root.IsChecked);
    }

    [Fact]
    public void Children_loaded_later_inherit_a_checked_parent()
    {
        var root = Root();
        var a = Load(root, "A/")[0];
        SelectionTree.SetChecked(a, true);

        var later = Load(a, "x.txt", "Sub/");

        Assert.All(later, n => Assert.True(n.IsChecked));
        Assert.True(root.IsChecked);
    }

    [Fact]
    public void Children_loaded_later_under_an_unchecked_parent_are_unchecked()
    {
        var root = Root();
        var a = Load(root, "A/")[0];

        Assert.All(Load(a, "x.txt"), n => Assert.False(n.IsChecked));
    }

    [Fact]
    public void Selection_takes_fully_checked_folders_whole_and_resolves_partial_ones()
    {
        var root = Root();
        var children = Load(root, "A/", "B/", "top.txt");
        var (a, b, top) = (children[0], children[1], children[2]);
        var bFiles = Load(b, "b1.txt", "b2.txt");

        SelectionTree.SetChecked(a, true);        // A never expanded: taken whole
        SelectionTree.SetChecked(bFiles[0], true); // B partly: only b1.txt

        var selection = SelectionTree.GetSelection([root]);

        Assert.Equal([a], selection.Folders.Select(f => f.Node));
        Assert.Equal([bFiles[0]], selection.Files.Select(f => f.Node));
        Assert.False(top.IsChecked);
    }

    [Fact]
    public void Fully_checked_root_is_one_folder_without_listing_its_contents()
    {
        var root = Root();
        Load(root, "A/", "f.txt");
        SelectionTree.SetChecked(root, true);

        var selection = SelectionTree.GetSelection([root]);

        Assert.Equal([root], selection.Folders.Select(f => f.Node));
        Assert.Empty(selection.Files);
    }

    [Fact]
    public void Summary_counts_files_totals_and_uncounted_folders()
    {
        var root = Root();
        var children = Load(root, "Counted/", "Uncounted/", "f1.txt", "f2.txt");
        children[0].Totals = new FolderTotals(10, 5000, 10);

        SelectionTree.SetChecked(children[0], true);
        SelectionTree.SetChecked(children[1], true);
        SelectionTree.SetChecked(children[2], true);

        var summary = SelectionTree.Summarize([root]);

        Assert.Equal(11, summary.FileCount);
        Assert.Equal(5100, summary.Bytes);
        Assert.Equal(1, summary.UncountedFolders);
        Assert.False(summary.IsEmpty);
    }

    [Fact]
    public void Summary_of_a_checked_uncounted_folder_uses_its_loaded_children()
    {
        var root = Root();
        var a = Load(root, "A/")[0];
        Load(a, "a1.txt", "a2.txt");
        SelectionTree.SetChecked(a, true);

        var summary = SelectionTree.Summarize([root]);

        Assert.Equal(2, summary.FileCount);
        Assert.Equal(200, summary.Bytes);
        Assert.Equal(0, summary.UncountedFolders);
    }

    [Fact]
    public void Nothing_checked_is_an_empty_selection()
    {
        var root = Root();
        Load(root, "A/", "f.txt");

        Assert.True(SelectionTree.GetSelection([root]).IsEmpty);
        Assert.True(SelectionTree.Summarize([root]).IsEmpty);
    }

    [Fact]
    public void Several_roots_from_an_account_link_are_independent()
    {
        var account = Parse("https://cda1.blob.core.windows.net/");
        var r1 = new BrowseNode(BrowseItemKind.Container, "c-one", string.Empty, account.ForContainer("c-one"));
        var r2 = new BrowseNode(BrowseItemKind.Container, "c-two", string.Empty, account.ForContainer("c-two"));

        SelectionTree.SetChecked(r2, true);

        Assert.False(r1.IsChecked);
        Assert.Equal([r2], SelectionTree.GetSelection([r1, r2]).Folders.Select(f => f.Node));
    }

    [Fact]
    public void Check_changes_raise_property_changed()
    {
        var root = Root();
        var file = Load(root, "f.txt")[0];
        var raised = new List<string?>();
        root.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        SelectionTree.SetChecked(file, true);

        Assert.Contains(nameof(BrowseNode.IsChecked), raised);
    }

    [Fact]
    public void Node_depth_follows_the_tree()
    {
        var root = Root();
        var a = Load(root, "A/")[0];
        var deep = Load(a, "B/")[0];

        Assert.Equal((0, 1, 2), (root.Depth, a.Depth, deep.Depth));
        Assert.Equal("A/B/", deep.Path);
    }
}
