using AzureBlobDownloader.Core.Browsing;

namespace AzureBlobDownloader.Core.Tests.Browsing;

public class FolderTotalsAccumulatorTests
{
    [Fact]
    public void Totals_roll_up_to_every_folder_on_the_way()
    {
        var acc = new FolderTotalsAccumulator(string.Empty);
        acc.Add("top.txt", 1, hasMd5: true);
        acc.Add("A/a.txt", 10, hasMd5: false);
        acc.Add("A/B/b1.txt", 100, hasMd5: true);
        acc.Add("A/B/b2.txt", 1000, hasMd5: true);
        acc.Add("C/c.txt", 10000, hasMd5: false);

        var totals = acc.ToDictionary();

        Assert.Equal(new FolderTotals(5, 11111, 3), totals[""]);
        Assert.Equal(new FolderTotals(3, 1110, 2), totals["A/"]);
        Assert.Equal(new FolderTotals(2, 1100, 2), totals["A/B/"]);
        Assert.Equal(new FolderTotals(1, 10000, 0), totals["C/"]);
        Assert.Equal(4, totals.Count);
    }

    [Fact]
    public void Root_prefix_is_the_starting_point()
    {
        var acc = new FolderTotalsAccumulator("Data/");
        acc.Add("Data/x.bin", 5, hasMd5: false);
        acc.Add("Data/Sub/y.bin", 7, hasMd5: true);

        var totals = acc.ToDictionary();

        Assert.Equal(new FolderTotals(2, 12, 1), totals["Data/"]);
        Assert.Equal(new FolderTotals(1, 7, 1), totals["Data/Sub/"]);
        Assert.False(totals.ContainsKey(""));
        Assert.Equal(2, acc.FileCount);
        Assert.Equal(12, acc.Bytes);
    }

    [Fact]
    public void Empty_folder_has_zero_totals()
    {
        Assert.Equal(new FolderTotals(0, 0, 0), new FolderTotalsAccumulator("Empty/").RootTotals);
    }

    [Fact]
    public void Blob_outside_the_folder_is_refused()
    {
        var acc = new FolderTotalsAccumulator("Data/");

        Assert.Throws<ArgumentException>(() => acc.Add("Other/x.bin", 1, hasMd5: false));
    }

    [Fact]
    public void Names_with_consecutive_slashes_are_counted_without_crashing()
    {
        var acc = new FolderTotalsAccumulator(string.Empty);
        acc.Add("A//x.txt", 3, hasMd5: false);

        var totals = acc.ToDictionary();

        Assert.Equal(3, totals[""].Bytes);
        Assert.Equal(3, totals["A/"].Bytes);
        Assert.Equal(3, totals["A//"].Bytes);
    }
}
