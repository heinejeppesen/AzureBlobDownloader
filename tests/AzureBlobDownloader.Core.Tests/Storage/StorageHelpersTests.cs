using System.Globalization;
using AzureBlobDownloader.Core.Storage;
using AzureBlobDownloader.Core.Urls;

namespace AzureBlobDownloader.Core.Tests.Storage;

public class StorageHelpersTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(10L * 1024 * 1024, "10.0 MB")]
    [InlineData(250L * 1024 * 1024 * 1024, "250 GB")]
    [InlineData(3L * 1024 * 1024 * 1024 * 1024, "3 TB")]
    public void Formats_sizes(long bytes, string expected)
    {
        Assert.Equal(expected, ByteSize.Format(bytes, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("a/b/c.txt", "c.txt")]
    [InlineData("a/b/", "b")]
    [InlineData("top.txt", "top.txt")]
    [InlineData("folder/", "folder")]
    public void Last_segment_of_blob_paths(string path, string expected)
    {
        Assert.Equal(expected, BrowseItem.LastSegment(path));
    }

    [Fact]
    public void For_container_builds_a_container_target_on_the_same_host()
    {
        Assert.True(BlobUrlParser.TryParse("https://cda12345678.blob.core.windows.net/", ["cda*.blob.core.windows.net"], out var account));

        var container = account.ForContainer("report-1234567890");

        Assert.Equal(BlobTargetKind.Container, container.Kind);
        Assert.Equal(new Uri("https://cda12345678.blob.core.windows.net/report-1234567890"), container.ContainerUri);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("UPPER")]
    [InlineData("ab")]
    [InlineData("a/b")]
    public void For_container_rejects_invalid_names(string name)
    {
        Assert.True(BlobUrlParser.TryParse("https://cda1.blob.core.windows.net/", ["cda*.blob.core.windows.net"], out var account));

        Assert.Throws<ArgumentException>(() => account.ForContainer(name));
    }
}
