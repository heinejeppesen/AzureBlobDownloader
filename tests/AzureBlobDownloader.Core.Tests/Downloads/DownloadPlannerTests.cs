using AzureBlobDownloader.Core.Downloads;
using AzureBlobDownloader.Core.Urls;

namespace AzureBlobDownloader.Core.Tests.Downloads;

public class DownloadPlannerTests
{
    private static readonly char Sep = Path.DirectorySeparatorChar;

    private static BlobTarget Link(string url)
    {
        Assert.True(BlobUrlParser.TryParse(url, ["cda*.blob.core.windows.net"], out var target));
        return target;
    }

    private static readonly BlobTarget Container = Link("https://cda1.blob.core.windows.net/report-1");

    private static RemoteBlob Blob(string name, long size = 1) => new(name, size, "\"e\"", DateTimeOffset.UnixEpoch, null);

    private static DownloadPlan Map(BlobTarget link, NameRules rules, bool caseSensitive, params string[] names) =>
        DownloadPlanner.Map(names.Select(n => (link.HasContainer ? link : link.ForContainer("report-1"), Blob(n))), link, new PlanOptions(rules, caseSensitive));

    private static string P(params string[] segments) => string.Join(Sep, segments);

    [Fact]
    public void Container_link_puts_files_under_the_container_name()
    {
        var plan = Map(Container, NameRules.Windows, false, "a.txt", "Data/2026/b.bin");

        Assert.Equal([P("report-1", "Data", "2026", "b.bin"), P("report-1", "a.txt")], plan.Items.Select(i => i.RelativePath));
        Assert.Empty(plan.Issues);
    }

    [Fact]
    public void Folder_link_puts_files_under_the_folder_name_relative_to_it()
    {
        var link = Link("https://cda1.blob.core.windows.net/report-1/Survey%202026/Drone");

        var plan = Map(link, NameRules.Windows, false, "Survey 2026/Drone/f1.tif", "Survey 2026/Drone/Sub/f2.tif");

        Assert.Equal([P("Drone", "Sub", "f2.tif"), P("Drone", "f1.tif")], plan.Items.Select(i => i.RelativePath).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Account_link_puts_each_container_in_its_own_folder()
    {
        var account = Link("https://cda1.blob.core.windows.net/");
        var blobs = new[] { (account.ForContainer("c-one"), Blob("x.txt")), (account.ForContainer("c-two"), Blob("x.txt")) };

        var plan = DownloadPlanner.Map(blobs, account, new PlanOptions(NameRules.Unix, true));

        Assert.Equal([P("c-one", "x.txt"), P("c-two", "x.txt")], plan.Items.Select(i => i.RelativePath));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("a/./b.txt")]
    [InlineData("a//b.txt")]
    [InlineData("/rooted.txt")]
    public void Unsafe_or_empty_path_segments_are_skipped(string name)
    {
        foreach (var rules in new[] { NameRules.Windows, NameRules.Unix })
        {
            var plan = Map(Container, rules, true, name, "ok.txt");

            Assert.Equal([P("report-1", "ok.txt")], plan.Items.Select(i => i.RelativePath));
            var issue = Assert.Single(plan.Issues);
            Assert.Equal(PlanIssueKind.Skipped, issue.Kind);
            Assert.Equal(name, issue.BlobName);
        }
    }

    [Fact]
    public void Invalid_windows_names_are_renamed_consistently_for_folders_and_files()
    {
        var plan = Map(Container, NameRules.Windows, false, "Q1: plan/a?.txt", "Q1: plan/b.txt", "CON.log", "trailing. /x.txt");

        Assert.Equal(
            [P("report-1", "CON_.log"), P("report-1", "Q1_ plan", "a_.txt"), P("report-1", "Q1_ plan", "b.txt"), P("report-1", "trailing__", "x.txt")],
            plan.Items.Select(i => i.RelativePath).Order(StringComparer.Ordinal));
        Assert.All(plan.Issues, i => Assert.Equal(PlanIssueKind.Renamed, i.Kind));
    }

    [Fact]
    public void Unix_keeps_names_that_windows_would_change()
    {
        var plan = Map(Container, NameRules.Unix, true, "Q1: plan/a?.txt", "CON.log");

        Assert.Equal([P("report-1", "CON.log"), P("report-1", "Q1: plan", "a?.txt")], plan.Items.Select(i => i.RelativePath).Order(StringComparer.Ordinal));
        Assert.Empty(plan.Issues);
    }

    [Fact]
    public void Names_differing_only_in_case_get_a_number_on_case_insensitive_destinations()
    {
        var plan = Map(Container, NameRules.Windows, false, "Data/Report.pdf", "Data/report.pdf", "Data/REPORT.pdf");

        Assert.Equal(
            [P("report-1", "Data", "REPORT.pdf"), P("report-1", "Data", "Report (2).pdf"), P("report-1", "Data", "report (3).pdf")],
            plan.Items.Select(i => i.RelativePath));
        Assert.Equal(2, plan.Issues.Count(i => i.Kind == PlanIssueKind.Renamed));
    }

    [Fact]
    public void Names_differing_only_in_case_are_kept_on_case_sensitive_destinations()
    {
        var plan = Map(Container, NameRules.Unix, true, "Data/Report.pdf", "Data/report.pdf");

        Assert.Equal(2, plan.Items.Select(i => i.RelativePath).Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(plan.Issues);
    }

    [Fact]
    public void A_file_with_the_name_of_a_folder_is_renamed()
    {
        var plan = Map(Container, NameRules.Unix, true, "a", "a/b.txt");

        Assert.Contains(P("report-1", "a (2)"), plan.Items.Select(i => i.RelativePath));
        Assert.Contains(P("report-1", "a", "b.txt"), plan.Items.Select(i => i.RelativePath));
    }

    [Fact]
    public void Sanitising_that_creates_a_clash_is_resolved_too()
    {
        var plan = Map(Container, NameRules.Windows, true, "a?.txt", "a*.txt");

        Assert.Equal(2, plan.Items.Select(i => i.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Folder_marker_blobs_and_duplicates_are_ignored()
    {
        var blobs = new[] { (Container, Blob("Folder/", size: 0)), (Container, Blob("Folder/a.txt")), (Container, Blob("Folder/a.txt")) };

        var plan = DownloadPlanner.Map(blobs, Container, new PlanOptions(NameRules.Unix, true));

        Assert.Equal([P("report-1", "Folder", "a.txt")], plan.Items.Select(i => i.RelativePath));
        Assert.Empty(plan.Issues);
    }

    [Fact]
    public void Names_longer_than_255_are_skipped()
    {
        var plan = Map(Container, NameRules.Windows, true, new string('x', 256) + ".txt", "ok.txt");

        Assert.Single(plan.Items);
        Assert.Equal(PlanIssueKind.Skipped, Assert.Single(plan.Issues).Kind);
    }

    [Fact]
    public void Mapping_is_deterministic()
    {
        string[] names = ["b/C.txt", "b/c.txt", "a?.txt", "a*.txt", "Z/y.txt"];

        var first = Map(Container, NameRules.Windows, false, names).Items.Select(i => (i.Blob.Name, i.RelativePath));
        var second = Map(Container, NameRules.Windows, false, [.. names.Reverse()]).Items.Select(i => (i.Blob.Name, i.RelativePath));

        Assert.Equal(first, second);
    }

    [Fact]
    public void Total_bytes_sums_the_items()
    {
        var blobs = new[] { (Container, Blob("a", 10)), (Container, Blob("b", 32)) };

        Assert.Equal(42, DownloadPlanner.Map(blobs, Container, new PlanOptions(NameRules.Unix, true)).TotalBytes);
    }
}
