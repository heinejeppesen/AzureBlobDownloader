using AzureBlobDownloader.Core.Urls;

namespace AzureBlobDownloader.Core.Tests.Urls;

public class HostPatternTests
{
    [Theory]
    [InlineData("cda*.blob.core.windows.net", "cda*.blob.core.windows.net")]
    [InlineData("*.blob.core.windows.net", "*.blob.core.windows.net")]
    [InlineData("  CDA*.Blob.Core.Windows.Net ", "cda*.blob.core.windows.net")]
    [InlineData("cda12345678.blob.core.windows.net", "cda12345678.blob.core.windows.net")]
    [InlineData("cda*x*.blob.core.windows.net", "cda*x*.blob.core.windows.net")]
    [InlineData("xn--bcher-kva.example.com", "xn--bcher-kva.example.com")]
    public void Valid_patterns_are_normalised(string input, string expected)
    {
        Assert.True(HostPattern.TryParse(input, out var pattern, out var error), error);
        Assert.Equal(expected, pattern.Pattern);
        Assert.Equal(expected, pattern.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*")]
    [InlineData("*.net")]
    [InlineData("*.*.windows.net")]
    [InlineData("cda.*.core.windows.net")]
    [InlineData("cda*.blob.core.windows.*")]
    [InlineData("cda*.blob.core.windows.net.")]
    [InlineData(".cda*.blob.core.windows.net")]
    [InlineData("cda*..core.windows.net")]
    [InlineData("https://cda*.blob.core.windows.net")]
    [InlineData("cda*.blob.core.windows.net/x")]
    [InlineData("cda*.blob.core.windows.net:443")]
    [InlineData("cdæ*.blob.core.windows.net")]
    [InlineData("cda?.blob.core.windows.net")]
    [InlineData("-cda.blob.core.windows.net")]
    [InlineData("cda-.blob.core.windows.net")]
    [InlineData("localhost")]
    public void Invalid_patterns_are_rejected(string? input)
    {
        Assert.False(HostPattern.TryParse(input, out var pattern, out var error));
        Assert.Null(pattern);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Parse_throws_on_invalid_pattern()
    {
        Assert.Throws<FormatException>(() => HostPattern.Parse("*"));
    }

    [Theory]
    [InlineData("cda*.blob.core.windows.net", "cda.blob.core.windows.net", true)]
    [InlineData("cda*.blob.core.windows.net", "cda1.blob.core.windows.net", true)]
    [InlineData("cda*.blob.core.windows.net", "cda12345678.blob.core.windows.net", true)]
    [InlineData("cda*.blob.core.windows.net", "CDA12345678.BLOB.CORE.WINDOWS.NET", true)]
    [InlineData("cda*.blob.core.windows.net", "xcda1.blob.core.windows.net", false)]
    [InlineData("cda*.blob.core.windows.net", "cd.blob.core.windows.net", false)]
    [InlineData("cda*.blob.core.windows.net", "cda1.x.blob.core.windows.net", false)]
    [InlineData("cda*.blob.core.windows.net", "cda1.blob.core.windows.net.evil.com", false)]
    [InlineData("cda*.blob.core.windows.net", "cdax.blob.core.windows.net.evil.com", false)]
    [InlineData("cda*.blob.core.windows.net", "cda1.blob.core.windows.net.", false)]
    [InlineData("cda*.blob.core.windows.net", "cda1.blob.core.windows", false)]
    [InlineData("cda*.blob.core.windows.net", "cdа.blob.core.windows.net", false)]
    [InlineData("*.blob.core.windows.net", "a.blob.core.windows.net", true)]
    [InlineData("*.blob.core.windows.net", "a.b.blob.core.windows.net", false)]
    [InlineData("*.blob.core.windows.net", ".blob.core.windows.net", false)]
    [InlineData("*.blob.core.windows.net", "blob.core.windows.net", false)]
    [InlineData("cda*x.blob.core.windows.net", "cdax.blob.core.windows.net", true)]
    [InlineData("cda*x.blob.core.windows.net", "cda123x.blob.core.windows.net", true)]
    [InlineData("cda*x.blob.core.windows.net", "cda123xy.blob.core.windows.net", false)]
    [InlineData("cda*x*.blob.core.windows.net", "cdaxx.blob.core.windows.net", true)]
    [InlineData("cda*x*.blob.core.windows.net", "cda1x2.blob.core.windows.net", true)]
    [InlineData("fixed.blob.core.windows.net", "fixed.blob.core.windows.net", true)]
    [InlineData("fixed.blob.core.windows.net", "fixed2.blob.core.windows.net", false)]
    public void IsMatch(string pattern, string host, bool expected)
    {
        Assert.Equal(expected, HostPattern.Parse(pattern).IsMatch(host));
    }
}
