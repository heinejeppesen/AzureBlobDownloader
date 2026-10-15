using AzureBlobDownloader.Core.Downloads;

namespace AzureBlobDownloader.Core.Tests.Downloads;

public class LocalNamesTests
{
    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("a<b>c:d\"e|f?g*h.txt", "a_b_c_d_e_f_g_h.txt")]
    [InlineData("back\\slash", "back_slash")]
    [InlineData("tab\there", "tab_here")]
    [InlineData("ends with dot.", "ends with dot_")]
    [InlineData("ends with space ", "ends with space_")]
    [InlineData("dots...", "dots___")]
    [InlineData("CON", "CON_")]
    [InlineData("con.txt", "con_.txt")]
    [InlineData("NUL.tar.gz", "NUL_.tar.gz")]
    [InlineData("COM1", "COM1_")]
    [InlineData("LPT9.doc", "LPT9_.doc")]
    [InlineData("COM¹", "COM¹_")]
    [InlineData("CONSOLE.txt", "CONSOLE.txt")]
    [InlineData("AUX .txt", "AUX _.txt")] // Windows ignores the space, so this is the device AUX
    [InlineData("Æbler & pærer (1).xlsx", "Æbler & pærer (1).xlsx")]
    public void Windows_names_are_made_valid(string input, string expected)
    {
        var (name, change) = LocalNames.Sanitize(input, NameRules.Windows);

        Assert.Equal(expected, name);
        Assert.Equal(input == expected, change is null);
    }

    [Theory]
    [InlineData("a:b?c*.txt")]
    [InlineData("CON")]
    [InlineData("trailing.")]
    [InlineData("tab\there")]
    public void Unix_keeps_names_windows_does_not_allow(string input)
    {
        var (name, change) = LocalNames.Sanitize(input, NameRules.Unix);

        Assert.Equal(input, name);
        Assert.Null(change);
    }

    [Fact]
    public void Unix_replaces_nul()
    {
        Assert.Equal("a_b", LocalNames.Sanitize("a\0b", NameRules.Unix).Name);
    }

    [Fact]
    public void Name_length_is_counted_as_the_file_system_does()
    {
        Assert.Equal(3, LocalNames.MeasureName("æøå", NameRules.Windows));
        Assert.Equal(6, LocalNames.MeasureName("æøå", NameRules.Unix));
    }
}
