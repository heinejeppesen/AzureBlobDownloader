using AzureBlobDownloader.Core.Configuration;
using AzureBlobDownloader.Core.Urls;

namespace AzureBlobDownloader.Core.Tests.Urls;

public class BlobUrlParserTests
{
    private const string Account = "https://cda12345678.blob.core.windows.net";
    private const string ContainerUrl = Account + "/report-1234567890";

    private static readonly BlobUrlParser Parser = new(["cda*.blob.core.windows.net"]);
    private static readonly BlobUrlParser WideParser = new(["*.blob.core.windows.net"]);

    private static BlobTarget AssertSuccess(BlobUrlParseResult result)
    {
        Assert.True(result.IsSuccess, $"Expected success but got {result.Error}: {result.ErrorMessage}");
        Assert.Equal(BlobUrlError.None, result.Error);
        Assert.Null(result.ErrorMessage);
        return result.Target;
    }

    private static void AssertFailure(BlobUrlParseResult result, BlobUrlError expected)
    {
        Assert.False(result.IsSuccess, $"Expected {expected} but parsing succeeded: {result.Target}");
        Assert.Null(result.Target);
        Assert.Equal(expected, result.Error);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        Assert.False(result.SasRemoved);
    }

    // ---- Normal links -------------------------------------------------------------------------

    [Fact]
    public void Container_url_is_parsed()
    {
        var target = AssertSuccess(Parser.Parse(ContainerUrl));

        Assert.Equal(BlobTargetKind.Container, target.Kind);
        Assert.Equal("cda12345678.blob.core.windows.net", target.Host);
        Assert.Equal("cda12345678", target.AccountName);
        Assert.Equal("report-1234567890", target.ContainerName);
        Assert.Null(target.Prefix);
        Assert.Equal(new Uri(Account + "/"), target.AccountUri);
        Assert.Equal(ContainerUrl, target.ContainerUri!.AbsoluteUri);
        Assert.True(target.HasContainer);
        Assert.Equal(ContainerUrl, target.ToString());
    }

    [Fact]
    public void Container_url_with_trailing_slash_is_a_container()
    {
        var target = AssertSuccess(Parser.Parse(ContainerUrl + "/"));
        Assert.Equal(BlobTargetKind.Container, target.Kind);
        Assert.Equal("report-1234567890", target.ContainerName);
        Assert.Null(target.Prefix);
    }

    [Fact]
    public void Container_url_with_only_slashes_after_container_is_a_container()
    {
        var target = AssertSuccess(Parser.Parse(ContainerUrl + "//"));
        Assert.Equal(BlobTargetKind.Container, target.Kind);
    }

    [Theory]
    [InlineData("/Data", "Data/")]
    [InlineData("/Data/", "Data/")]
    [InlineData("/Data/2026/March", "Data/2026/March/")]
    [InlineData("/Data/2026/March/", "Data/2026/March/")]
    [InlineData("/My%20Folder/Sub%20Dir", "My Folder/Sub Dir/")]
    [InlineData("/My Folder/Sub Dir/", "My Folder/Sub Dir/")]
    [InlineData("/%C3%86bler%20og%20p%C3%A6rer", "Æbler og pærer/")]
    [InlineData("/Æbler", "Æbler/")]
    [InlineData("/a+b", "a+b/")]
    [InlineData("/a%2Bb", "a+b/")]
    [InlineData("/100%25", "100%/")]
    [InlineData("/literal%2525", "literal%25/")]
    [InlineData("/hash%23tag", "hash#tag/")]
    [InlineData("/q%3Fmark", "q?mark/")]
    [InlineData("/a%2Fb", "a/b/")]
    [InlineData("/(1)%20%5Bcopy%5D", "(1) [copy]/")]
    public void Folder_path_is_decoded_and_ends_with_slash(string path, string expectedPrefix)
    {
        var target = AssertSuccess(Parser.Parse(ContainerUrl + path));

        Assert.Equal(BlobTargetKind.ContainerWithPrefix, target.Kind);
        Assert.Equal("report-1234567890", target.ContainerName);
        Assert.Equal(expectedPrefix, target.Prefix);
        Assert.Equal(ContainerUrl, target.ContainerUri!.AbsoluteUri);
    }

    [Theory]
    [InlineData(Account)]
    [InlineData(Account + "/")]
    [InlineData(Account + ":443/")]
    [InlineData(Account + "/?comp=list")]
    public void Account_url_is_parsed(string url)
    {
        var target = AssertSuccess(Parser.Parse(url));

        Assert.Equal(BlobTargetKind.Account, target.Kind);
        Assert.Equal("cda12345678", target.AccountName);
        Assert.Null(target.ContainerName);
        Assert.Null(target.ContainerUri);
        Assert.Null(target.Prefix);
        Assert.False(target.HasContainer);
        Assert.Equal("https://cda12345678.blob.core.windows.net/", target.AccountUri.AbsoluteUri);
    }

    [Fact]
    public void Explicit_port_443_is_allowed_and_dropped()
    {
        var target = AssertSuccess(Parser.Parse("https://cda12345678.blob.core.windows.net:443/report-1234567890"));
        Assert.Equal(ContainerUrl, target.ContainerUri!.AbsoluteUri);
    }

    [Theory]
    [InlineData("HTTPS://CDA12345678.BLOB.CORE.WINDOWS.NET/report-1234567890")]
    [InlineData("https://Cda12345678.Blob.Core.Windows.Net/report-1234567890")]
    public void Uppercase_scheme_and_host_are_normalised(string url)
    {
        var target = AssertSuccess(Parser.Parse(url));
        Assert.Equal("cda12345678.blob.core.windows.net", target.Host);
        Assert.Equal(ContainerUrl, target.ContainerUri!.AbsoluteUri);
    }

    [Fact]
    public void Wildcard_matches_empty_suffix()
    {
        var target = AssertSuccess(Parser.Parse("https://cda.blob.core.windows.net/abc"));
        Assert.Equal("cda", target.AccountName);
    }

    // ---- Wrapping and whitespace ------------------------------------------------------------

    [Theory]
    [InlineData("  " + ContainerUrl + "  ")]
    [InlineData("\t" + ContainerUrl + "\r\n")]
    [InlineData("\"" + ContainerUrl + "\"")]
    [InlineData("'" + ContainerUrl + "'")]
    [InlineData("<" + ContainerUrl + ">")]
    [InlineData("(" + ContainerUrl + ")")]
    [InlineData("[" + ContainerUrl + "]")]
    [InlineData("“" + ContainerUrl + "”")]
    [InlineData("‘" + ContainerUrl + "’")]
    [InlineData(" \"< " + ContainerUrl + " >\" ")]
    public void Surrounding_whitespace_quotes_and_brackets_are_removed(string input)
    {
        var target = AssertSuccess(Parser.Parse(input));
        Assert.Equal(ContainerUrl, target.ContainerUri!.AbsoluteUri);
    }

    // ---- Copied from an e-mail ----------------------------------------------------------------

    private const string SafeLinkToContainer =
        "https://eur01.safelinks.protection.outlook.com/?url=https%3A%2F%2Fcda12345678.blob.core.windows.net%2Freport-1234567890"
        + "&data=05%7C02%7Cuser%40customer.com%7C0123456789abcdef%7C&sdata=AbCdEf%2BGh%3D&reserved=0";

    [Theory]
    [InlineData(" " + ContainerUrl)] // as generated in the portal's e-mail
    [InlineData(" " + ContainerUrl + " ")]
    [InlineData("​" + ContainerUrl)]
    [InlineData("﻿" + ContainerUrl + "​")]
    [InlineData("https://cda12345678.blob.core.windows.net/report-­1234567890")]
    [InlineData("https://cda12345678.blob​.core.windows.net/report-1234567890")]
    [InlineData(ContainerUrl + ".")]
    [InlineData(ContainerUrl + ",")]
    [InlineData("Link: " + ContainerUrl)]
    [InlineData("Please download the files here: " + ContainerUrl + " before Friday.")]
    [InlineData("Hi,\r\n\r\nYour files are ready:\r\n " + ContainerUrl + "\r\n\r\nRegards")]
    [InlineData("Your files: <" + ContainerUrl + ">.")]
    [InlineData("Your files (" + ContainerUrl + "), regards")]
    [InlineData(SafeLinkToContainer)]
    [InlineData(" " + SafeLinkToContainer + " ")]
    [InlineData("Link: " + SafeLinkToContainer)]
    [InlineData("https://nam12.safelinks.protection.outlook.com/?url=https%3A%2F%2Fcda12345678.blob.core.windows.net%2Freport-1234567890&data=x")]
    public void Links_copied_from_an_email_are_understood(string input)
    {
        var target = AssertSuccess(Parser.Parse(input));

        Assert.Equal(BlobTargetKind.Container, target.Kind);
        Assert.Equal(ContainerUrl, target.ContainerUri!.AbsoluteUri);
    }

    [Fact]
    public void Safe_link_to_a_folder_keeps_the_folder()
    {
        var target = AssertSuccess(Parser.Parse(
            "https://eur01.safelinks.protection.outlook.com/?url=https%3A%2F%2Fcda12345678.blob.core.windows.net%2Freport-1234567890%2FMy%2520Folder%2F&data=x"));

        Assert.Equal("My Folder/", target.Prefix);
    }

    [Theory]
    [InlineData("https://eur01.safelinks.protection.outlook.com/?url=https%3A%2F%2Fevil.example.com%2Freport-1234567890&data=x", BlobUrlError.HostNotAllowed)]
    [InlineData("https://eur01.safelinks.protection.outlook.com/?url=https%3A%2F%2Fcda1.blob.core.windows.net.evil.com%2Fx&data=x", BlobUrlError.HostNotAllowed)]
    [InlineData("https://eur01.safelinks.protection.outlook.com/?url=http%3A%2F%2Fcda12345678.blob.core.windows.net%2Freport-1234567890", BlobUrlError.NotHttps)]
    [InlineData("https://eur01.safelinks.protection.outlook.com/?data=x", BlobUrlError.HostNotAllowed)]
    [InlineData("http://eur01.safelinks.protection.outlook.com/?url=https%3A%2F%2Fcda12345678.blob.core.windows.net%2Freport-1234567890", BlobUrlError.NotHttps)]
    [InlineData("https://safelinks.protection.outlook.com.evil.com/?url=https%3A%2F%2Fcda12345678.blob.core.windows.net%2Freport-1234567890", BlobUrlError.HostNotAllowed)]
    [InlineData("https://evil.com/eur01.safelinks.protection.outlook.com/?url=https%3A%2F%2Fcda12345678.blob.core.windows.net%2Freport-1234567890", BlobUrlError.HostNotAllowed)]
    public void Safe_links_are_only_unwrapped_from_the_real_host_and_the_inner_link_is_validated(string input, BlobUrlError expected)
    {
        AssertFailure(Parser.Parse(input), expected);
    }

    [Fact]
    public void Nested_safe_links_are_not_unwrapped_twice()
    {
        var inner = Uri.EscapeDataString(SafeLinkToContainer);
        Assert.False(Parser.Parse($"https://eur01.safelinks.protection.outlook.com/?url={inner}&data=x").IsSuccess);
    }

    [Theory]
    [InlineData("Download from https://evil.example.com/report-1234567890 today.")]
    [InlineData("See https://cda1.blob.core.windows.net.evil.com/x and http://cda12345678.blob.core.windows.net/report-1234567890")]
    [InlineData("no link in this text at all")]
    public void Text_without_an_allowed_https_link_is_refused(string input)
    {
        Assert.False(Parser.Parse(input).IsSuccess);
    }

    [Theory]
    [InlineData(ContainerUrl + "\n" + ContainerUrl)]
    [InlineData("Files: " + ContainerUrl + "\r\n\r\nIf the link does not work, use " + ContainerUrl + ".")]
    public void The_same_link_twice_is_accepted(string input)
    {
        var target = AssertSuccess(Parser.Parse(input));

        Assert.Equal(ContainerUrl, target.ContainerUri!.AbsoluteUri);
    }

    [Theory]
    [InlineData(ContainerUrl + "\n" + Account + "/report-0000000001")]
    [InlineData("Files: " + ContainerUrl + " and " + ContainerUrl + "/Data")]
    public void Different_links_in_one_text_are_refused(string input)
    {
        AssertFailure(Parser.Parse(input), BlobUrlError.MultipleLinks);
    }

    [Theory]
    [InlineData(ContainerUrl + "/Data.", "Data/")]
    [InlineData(ContainerUrl + "/Data/.", null)]
    [InlineData(ContainerUrl + "/Data/2026,", "Data/2026/")]
    [InlineData("See " + ContainerUrl + "/Data/2026.", "Data/2026/")]
    public void Sentence_punctuation_after_a_folder_link_is_not_part_of_the_folder(string input, string? expectedPrefix)
    {
        var result = Parser.Parse(input);
        if (expectedPrefix is null)
        {
            AssertFailure(result, BlobUrlError.InvalidPath);
        }
        else
        {
            Assert.Equal(expectedPrefix, AssertSuccess(result).Prefix);
        }
    }

    [Fact]
    public void Only_an_allowed_link_is_picked_from_text_with_several_links()
    {
        var target = AssertSuccess(Parser.Parse("Mirror: https://evil.example.com/x Files: " + ContainerUrl));

        Assert.Equal(ContainerUrl, target.ContainerUri!.AbsoluteUri);
    }

    [Fact]
    public void Stray_opening_quote_before_a_link_is_ignored()
    {
        var target = AssertSuccess(Parser.Parse("\"" + ContainerUrl));

        Assert.Equal(ContainerUrl, target.ContainerUri!.AbsoluteUri);
    }

    [Fact]
    public void Mismatched_wrappers_are_not_removed()
    {
        Assert.False(Parser.Parse("<" + ContainerUrl + "\"").IsSuccess);
    }

    // ---- Query string, SAS and fragment -----------------------------------------------------

    [Fact]
    public void Sas_is_removed_and_flagged()
    {
        var result = Parser.Parse(ContainerUrl + "?sv=2024-11-04&sr=c&sp=rl&se=2026-12-31T00%3A00%3A00Z&sig=abc%2Bdef%3D");
        var target = AssertSuccess(result);

        Assert.True(result.SasRemoved);
        Assert.Equal(ContainerUrl, target.ContainerUri!.AbsoluteUri);
        Assert.DoesNotContain("sig", target.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(target.ContainerUri.Query);
        Assert.Empty(target.AccountUri.Query);
    }

    [Theory]
    [InlineData("?SIG=abc")]
    [InlineData("?sv=1&Sig=abc")]
    [InlineData("?%73ig=abc")]
    public void Sas_detection_is_case_insensitive_and_decodes_keys(string query)
    {
        var result = Parser.Parse(ContainerUrl + "/Data" + query);
        var target = AssertSuccess(result);
        Assert.True(result.SasRemoved);
        Assert.Equal("Data/", target.Prefix);
    }

    [Theory]
    [InlineData("?restype=container&comp=list")]
    [InlineData("?signature=abc")]
    [InlineData("?x=sig")]
    [InlineData("#sig=abc")]
    public void Non_sas_query_and_fragment_are_dropped_without_flag(string suffix)
    {
        var result = Parser.Parse(ContainerUrl + suffix);
        var target = AssertSuccess(result);
        Assert.False(result.SasRemoved);
        Assert.Equal(ContainerUrl, target.ContainerUri!.AbsoluteUri);
    }

    [Fact]
    public void Fragment_is_not_part_of_the_prefix()
    {
        var target = AssertSuccess(Parser.Parse(ContainerUrl + "/Data#section"));
        Assert.Equal("Data/", target.Prefix);
    }

    // ---- Scheme ------------------------------------------------------------------------------

    [Theory]
    [InlineData("http://cda12345678.blob.core.windows.net/report-1234567890")]
    [InlineData("HTTP://cda12345678.blob.core.windows.net/report-1234567890")]
    [InlineData("ftp://cda12345678.blob.core.windows.net/report-1234567890")]
    [InlineData("file://cda12345678.blob.core.windows.net/report-1234567890")]
    [InlineData("wss://cda12345678.blob.core.windows.net/report-1234567890")]
    public void Non_https_scheme_is_rejected(string url)
    {
        AssertFailure(Parser.Parse(url), BlobUrlError.NotHttps);
    }

    [Theory]
    [InlineData("cda12345678.blob.core.windows.net/report-1234567890")]
    [InlineData("//cda12345678.blob.core.windows.net/report-1234567890")]
    [InlineData("https:cda12345678.blob.core.windows.net/report-1234567890")]
    [InlineData("https:/cda12345678.blob.core.windows.net/report-1234567890")]
    [InlineData("javascript:alert(1)")]
    [InlineData("report-1234567890")]
    [InlineData("https://")]
    public void Text_that_is_not_an_absolute_https_url_is_rejected(string input)
    {
        AssertFailure(Parser.Parse(input), BlobUrlError.NotAUrl);
    }

    // ---- Port --------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://cda12345678.blob.core.windows.net:8443/report-1234567890")]
    [InlineData("https://cda12345678.blob.core.windows.net:80/report-1234567890")]
    [InlineData("https://cda12345678.blob.core.windows.net:10000/report-1234567890")]
    public void Non_default_port_is_rejected(string url)
    {
        AssertFailure(Parser.Parse(url), BlobUrlError.PortNotAllowed);
    }

    // ---- User info ---------------------------------------------------------------------------

    [Theory]
    [InlineData("https://user:pass@cda12345678.blob.core.windows.net/report-1234567890")]
    [InlineData("https://user@cda12345678.blob.core.windows.net/report-1234567890")]
    [InlineData("https://@cda12345678.blob.core.windows.net/report-1234567890")]
    [InlineData("https://cda12345678.blob.core.windows.net@evil.com/report-1234567890")]
    [InlineData("https://cda12345678.blob.core.windows.net:443@evil.com/report-1234567890")]
    public void User_info_is_rejected(string url)
    {
        AssertFailure(Parser.Parse(url), BlobUrlError.UserInfoNotAllowed);
    }

    // ---- IP addresses ------------------------------------------------------------------------

    [Theory]
    [InlineData("https://20.60.1.1/report-1234567890")]
    [InlineData("https://127.0.0.1/report-1234567890")]
    [InlineData("https://[::1]/report-1234567890")]
    [InlineData("https://[2001:db8::1]/report-1234567890")]
    [InlineData("https://2130706433/report-1234567890")]
    [InlineData("https://0x7f.0.0.1/report-1234567890")]
    public void Ip_literal_is_rejected(string url)
    {
        AssertFailure(Parser.Parse(url), BlobUrlError.IpAddressNotAllowed);
    }

    // ---- Host allow list ---------------------------------------------------------------------

    [Theory]
    [InlineData("https://evil.com/cda12345678.blob.core.windows.net")]
    [InlineData("https://evil.com/cda.blob.core.windows.net/report-1234567890")]
    [InlineData("https://cdax.blob.core.windows.net.evil.com/report-1234567890")]
    [InlineData("https://cda12345678.blob.core.windows.net.evil.com/report-1234567890")]
    [InlineData("https://evil.com?cda12345678.blob.core.windows.net")]
    [InlineData("https://evil.com#@cda12345678.blob.core.windows.net")]
    [InlineData("https://xcda12345678.blob.core.windows.net/report-1234567890")]
    [InlineData("https://abc.blob.core.windows.net/report-1234567890")]
    [InlineData("https://cda12345678.file.core.windows.net/report-1234567890")]
    [InlineData("https://cda12345678.dfs.core.windows.net/report-1234567890")]
    [InlineData("https://cda12345678.blob.core.chinacloudapi.cn/report-1234567890")]
    [InlineData("https://blob.core.windows.net/report-1234567890")]
    [InlineData("https://cda1.x.blob.core.windows.net/report-1234567890")]
    [InlineData("https://x.cda1.blob.core.windows.net/report-1234567890")]
    [InlineData("https://localhost/report-1234567890")]
    public void Host_not_on_allow_list_is_rejected(string url)
    {
        var result = Parser.Parse(url);
        AssertFailure(result, BlobUrlError.HostNotAllowed);
        Assert.Equal("This link does not point to a storage location this app is allowed to use.", result.ErrorMessage);
    }

    [Theory]
    [InlineData("https://a.b.blob.core.windows.net/report-1234567890")]
    [InlineData("https://cda1.evil.blob.core.windows.net/report-1234567890")]
    public void Single_label_wildcard_does_not_match_two_labels(string url)
    {
        AssertFailure(WideParser.Parse(url), BlobUrlError.HostNotAllowed);
    }

    [Fact]
    public void Wide_pattern_still_accepts_single_label()
    {
        var target = AssertSuccess(WideParser.Parse("https://anyaccount.blob.core.windows.net/report-1234567890"));
        Assert.Equal("anyaccount", target.AccountName);
    }

    [Theory]
    [InlineData("https://cda12345678.blob.core.windows.net./report-1234567890")]
    [InlineData("https://cda12345678.blob.core.windows.net../report-1234567890")]
    [InlineData("https://cda12345678..blob.core.windows.net/report-1234567890")]
    [InlineData("https://cda_1.blob.core.windows.net/report-1234567890")]
    [InlineData("https://-cda1.blob.core.windows.net/report-1234567890")]
    public void Malformed_or_trailing_dot_host_is_rejected(string url)
    {
        var result = Parser.Parse(url);
        Assert.False(result.IsSuccess);
        Assert.Contains(result.Error, new[] { BlobUrlError.InvalidHost, BlobUrlError.NotAUrl });
    }

    [Theory]
    // Cyrillic 'а' (U+0430) instead of Latin 'a'
    [InlineData("https://cdа.blob.core.windows.net/report-1234567890")]
    [InlineData("https://cdа12345678.blob.core.windows.net/report-1234567890")]
    // Cyrillic 'с' (U+0441) instead of Latin 'c'
    [InlineData("https://сda12345678.blob.core.windows.net/report-1234567890")]
    // Greek omicron in "core"
    [InlineData("https://cda12345678.blob.cοre.windows.net/report-1234567890")]
    // Already-punycoded look-alike
    [InlineData("https://xn--cd-8kc.blob.core.windows.net/report-1234567890")]
    public void Unicode_look_alike_host_is_rejected(string url)
    {
        AssertFailure(Parser.Parse(url), BlobUrlError.HostNotAllowed);
    }

    [Fact]
    public void Unicode_look_alike_in_wildcard_label_is_compared_as_punycode()
    {
        // With the wide pattern the punycode label is a syntactically valid label under
        // blob.core.windows.net, so the token could only go to that Microsoft-owned domain.
        var target = AssertSuccess(WideParser.Parse("https://cdа.blob.core.windows.net/report-1234567890"));
        Assert.StartsWith("xn--", target.Host, StringComparison.Ordinal);
        Assert.All(target.Host, c => Assert.True(c < 0x80));
    }

    [Fact]
    public void Fullwidth_host_is_normalised_by_idna_to_the_ascii_host()
    {
        // IDNA maps full-width letters and the ideographic full stop to ASCII, so this is the
        // same host a browser or HttpClient would contact. The target only ever carries the ASCII form.
        var result = Parser.Parse("https://ｃｄａ１.blob.core.windows.net/report-1234567890");
        var target = AssertSuccess(result);
        Assert.Equal("cda1.blob.core.windows.net", target.Host);
        Assert.Equal("https://cda1.blob.core.windows.net/report-1234567890", target.ContainerUri!.AbsoluteUri);
    }

    [Fact]
    public void Ideographic_full_stop_is_normalised_by_idna()
    {
        var target = AssertSuccess(Parser.Parse("https://cda1。blob。core。windows。net/report-1234567890"));
        Assert.Equal("cda1.blob.core.windows.net", target.Host);
    }

    [Theory]
    [InlineData("https://evil.com\\@cda12345678.blob.core.windows.net/report-1234567890")]
    [InlineData("https://cda12345678.blob.core.windows.net\\.evil.com/report-1234567890")]
    [InlineData("https:\\\\cda12345678.blob.core.windows.net\\report-1234567890")]
    [InlineData(ContainerUrl + "\\Data")]
    public void Backslash_is_rejected(string url)
    {
        AssertFailure(Parser.Parse(url), BlobUrlError.NotAUrl);
    }

    [Theory]
    [InlineData("https://cda12345678.blob.core.windows.net\t/report-1234567890")]
    [InlineData("https://cda12345678.blob.core.windows.net/report-1234567890\0")]
    [InlineData("https://cda12345678.blob.core.windows.net\r\n.evil.com/report-1234567890")]
    public void Embedded_control_characters_are_rejected(string input)
    {
        AssertFailure(Parser.Parse(input), BlobUrlError.NotAUrl);
    }

    [Fact]
    public void Percent_encoded_host_cannot_smuggle_another_host()
    {
        var result = Parser.Parse("https://evil.com%2Ecda12345678.blob.core.windows.net/report-1234567890");
        Assert.False(result.IsSuccess);
    }

    // ---- Container names ---------------------------------------------------------------------

    [Theory]
    [InlineData("$root")]
    [InlineData("$web")]
    [InlineData("$logs")]
    [InlineData("%24root")]
    [InlineData("Report-1234567890")]
    [InlineData("ab")]
    [InlineData("-abc")]
    [InlineData("abc-")]
    [InlineData("ab--c")]
    [InlineData("ab_c")]
    [InlineData("ab.c")]
    [InlineData("ab%20c")]
    [InlineData("ab%2Fc")]
    [InlineData("æbc")]
    public void Invalid_container_name_is_rejected(string container)
    {
        AssertFailure(Parser.Parse(Account + "/" + container + "/Data"), BlobUrlError.InvalidContainerName);
    }

    [Fact]
    public void Container_name_length_limits()
    {
        AssertSuccess(Parser.Parse(Account + "/abc"));
        AssertSuccess(Parser.Parse(Account + "/" + new string('a', 63)));
        AssertFailure(Parser.Parse(Account + "/" + new string('a', 64)), BlobUrlError.InvalidContainerName);
    }

    [Fact]
    public void Empty_container_segment_is_rejected()
    {
        AssertFailure(Parser.Parse(Account + "//report-1234567890"), BlobUrlError.InvalidContainerName);
    }

    // ---- Folder path -------------------------------------------------------------------------

    [Theory]
    [InlineData("/%2E%2E/other")]
    [InlineData("/Data/%2e%2e")]
    [InlineData("/Data/%2E/x")]
    [InlineData("/Data/%00/x")]
    [InlineData("/Data/%0A")]
    public void Dot_segments_and_control_characters_in_path_are_rejected(string path)
    {
        AssertFailure(Parser.Parse(ContainerUrl + path), BlobUrlError.InvalidPath);
    }

    [Theory]
    [InlineData("/Data/../Other")]
    [InlineData("/../other-container")]
    [InlineData("/./Data")]
    [InlineData("/Data/..")]
    [InlineData("/Data/.%2E/x")]
    public void Literal_dot_segments_are_rejected_not_resolved(string path)
    {
        AssertFailure(Parser.Parse(ContainerUrl + path), BlobUrlError.InvalidPath);
    }

    [Theory]
    [InlineData("/Data/...", "Data/.../")]
    [InlineData("/Data/.hidden", "Data/.hidden/")]
    [InlineData("/Data/a..b", "Data/a..b/")]
    public void Names_containing_dots_are_allowed(string path, string expectedPrefix)
    {
        Assert.Equal(expectedPrefix, AssertSuccess(Parser.Parse(ContainerUrl + path)).Prefix);
    }

    [Fact]
    public void Dots_in_query_or_fragment_are_ignored()
    {
        AssertSuccess(Parser.Parse(ContainerUrl + "/Data?x=/../y#/.."));
    }

    [Fact]
    public void Prefix_longer_than_blob_name_limit_is_rejected()
    {
        AssertFailure(Parser.Parse(ContainerUrl + "/" + new string('a', 1024)), BlobUrlError.InvalidPath);
        AssertSuccess(Parser.Parse(ContainerUrl + "/" + new string('a', 1023)));
    }

    // ---- Empty and oversized input -----------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    [InlineData("\"\"")]
    [InlineData("<>")]
    public void Empty_input_is_rejected(string? input)
    {
        AssertFailure(Parser.Parse(input), BlobUrlError.Empty);
    }

    [Fact]
    public void Oversized_input_is_rejected()
    {
        AssertFailure(Parser.Parse(ContainerUrl + "/" + new string('a', 5000)), BlobUrlError.TooLong);
    }

    // ---- TryParse helpers and construction ---------------------------------------------------

    [Fact]
    public void Static_TryParse_accepts_allowed_clipboard_text()
    {
        Assert.True(BlobUrlParser.TryParse(" " + ContainerUrl + "\r\n", ["cda*.blob.core.windows.net"], out var target));
        Assert.Equal("report-1234567890", target.ContainerName);
    }

    [Theory]
    [InlineData("Hello, here is the link")]
    [InlineData("https://example.com/report-1234567890")]
    [InlineData(null)]
    public void Static_TryParse_rejects_other_clipboard_text(string? text)
    {
        Assert.False(BlobUrlParser.TryParse(text, ["cda*.blob.core.windows.net"], out var target));
        Assert.Null(target);
    }

    [Fact]
    public void Instance_TryParse_matches_Parse()
    {
        Assert.True(Parser.TryParse(ContainerUrl, out var target));
        Assert.Equal(Parser.Parse(ContainerUrl).Target, target);
        Assert.False(Parser.TryParse("http://cda1.blob.core.windows.net/abc", out _));
    }

    [Fact]
    public void Parser_can_be_built_from_config()
    {
        var config = new AppConfig { TenantId = Guid.NewGuid(), ClientId = Guid.NewGuid() };
        var parser = new BlobUrlParser(config);
        Assert.Equal(new[] { "*.blob.core.windows.net" }, parser.AllowedHosts);
        Assert.True(parser.IsHostAllowed("cda1.blob.core.windows.net"));
        Assert.False(parser.IsHostAllowed("cda1.blob.core.windows.net.evil.com"));
        Assert.False(parser.IsHostAllowed("cda1.file.core.windows.net"));
    }

    [Fact]
    public void Constructor_rejects_empty_or_invalid_patterns()
    {
        Assert.Throws<ArgumentException>(() => new BlobUrlParser([]));
        Assert.Throws<ArgumentException>(() => new BlobUrlParser(["*"]));
        Assert.Throws<ArgumentException>(() => new BlobUrlParser(["cda*.blob.core.windows.net", "https://x.com"]));
    }

    [Fact]
    public void Multiple_patterns_are_each_honoured()
    {
        var parser = new BlobUrlParser(["cda*.blob.core.windows.net", "fixed.blob.core.windows.net"]);
        Assert.True(parser.Parse("https://fixed.blob.core.windows.net/abc").IsSuccess);
        Assert.True(parser.Parse("https://cda9.blob.core.windows.net/abc").IsSuccess);
        Assert.False(parser.Parse("https://other.blob.core.windows.net/abc").IsSuccess);
    }
}
