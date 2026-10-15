using System.Diagnostics.CodeAnalysis;
using AzureBlobDownloader.Core.Configuration;

namespace AzureBlobDownloader.Core.Urls;

/// <summary>
/// Validates a link pasted by the user and turns it into a <see cref="BlobTarget"/>.
/// <para>
/// Security: the user's token is only ever sent to a URI built from a <see cref="BlobTarget"/>,
/// so every check that keeps the token on an allowed host lives here. The rules are:
/// https only, port 443 only, no user info, a DNS host name (no IP literals, no trailing dot)
/// whose IDN ASCII form matches one of <see cref="AllowedHosts"/>. Query and fragment are dropped.
/// </para>
/// </summary>
public sealed class BlobUrlParser
{
    public const int MaxInputLength = 4096;
    public const int MaxBlobNameLength = 1024;

    internal static class Messages
    {
        public const string Empty = "Paste the link you received for the shared folder.";
        public const string TooLong = "This text is too long to be a link to a shared folder.";
        public const string NotAUrl = "This doesn't look like a link. Paste the full link, starting with https://.";
        public const string NotHttps = "Only secure links (starting with https://) can be used.";
        public const string UserInfo = "This link contains a user name or password, which is not allowed.";
        public const string Port = "This link uses a network port other than the standard one (443), which is not allowed.";
        public const string IpAddress = "This link uses an IP address instead of a storage address, which is not allowed.";
        public const string InvalidHost = "The address in this link is not valid.";
        public const string HostNotAllowed = "This link does not point to a storage location this app is allowed to use.";
        public const string InvalidContainer = "The shared folder name in this link is not valid.";
        public const string InvalidPath = "The folder path in this link is not valid.";
        public const string MultipleLinks = "This text contains more than one link. Copy only the link to the shared folder.";
    }

    private static readonly (char Open, char Close)[] WrapperPairs =
    [
        ('"', '"'), ('\'', '\''), ('<', '>'), ('(', ')'), ('[', ']'),
        ('“', '”'), ('‘', '’'), ('«', '»'),
    ];

    private readonly HostPattern[] _allowedHosts;

    /// <param name="allowedHosts">Host patterns, see <see cref="HostPattern"/>.</param>
    /// <exception cref="ArgumentException">The list is empty or a pattern is invalid.</exception>
    public BlobUrlParser(IEnumerable<string> allowedHosts)
    {
        ArgumentNullException.ThrowIfNull(allowedHosts);
        var patterns = new List<HostPattern>();
        foreach (var pattern in allowedHosts)
        {
            if (!HostPattern.TryParse(pattern, out var parsed, out var error))
            {
                throw new ArgumentException($"Invalid allowed host pattern \"{pattern}\": {error}", nameof(allowedHosts));
            }

            patterns.Add(parsed);
        }

        if (patterns.Count == 0)
        {
            throw new ArgumentException("At least one allowed host pattern is required.", nameof(allowedHosts));
        }

        _allowedHosts = [.. patterns];
    }

    public BlobUrlParser(AppConfig config)
        : this((config ?? throw new ArgumentNullException(nameof(config))).AllowedHosts)
    {
    }

    public IReadOnlyList<string> AllowedHosts => [.. _allowedHosts.Select(p => p.Pattern)];

    /// <summary>Quick check, e.g. for clipboard text: true when <paramref name="text"/> is an allowed link.</summary>
    public static bool TryParse(string? text, IEnumerable<string> allowedHosts, [NotNullWhen(true)] out BlobTarget? target) =>
        new BlobUrlParser(allowedHosts).TryParse(text, out target);

    /// <summary>True when <paramref name="text"/> is an allowed link.</summary>
    public bool TryParse(string? text, [NotNullWhen(true)] out BlobTarget? target)
    {
        target = Parse(text).Target;
        return target is not null;
    }

    /// <summary>True when the ASCII host name matches one of <see cref="AllowedHosts"/>.</summary>
    public bool IsHostAllowed(string host) => _allowedHosts.Any(p => p.IsMatch(host));

    /// <summary>
    /// Validates <paramref name="input"/> and returns the target or a plain-language error.
    /// <para>
    /// Accepts what users copy from an e-mail: invisible formatting characters are removed, trailing
    /// sentence punctuation is ignored, a link is found inside surrounding text, and an Outlook
    /// Safe Links wrapper is unwrapped. Every candidate goes through the same validation.
    /// </para>
    /// </summary>
    public BlobUrlParseResult Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return Fail(BlobUrlError.Empty, Messages.Empty);
        }

        if (input.Length > MaxInputLength)
        {
            return Fail(BlobUrlError.TooLong, Messages.TooLong);
        }

        var text = Unwrap(RemoveInvisible(input));
        if (text.Length == 0)
        {
            return Fail(BlobUrlError.Empty, Messages.Empty);
        }

        // A link at the end of a sentence: prefer the reading without the trailing punctuation
        // (Windows folder names cannot end in '.', so "…/Data." means folder "Data").
        var trimmed = TrimTrailingPunctuation(text);
        if (trimmed != text && ParseLinkOrSafeLink(trimmed) is { IsSuccess: true } trimmedResult)
        {
            return trimmedResult;
        }

        var result = ParseLinkOrSafeLink(text);
        if (result.IsSuccess)
        {
            return result;
        }

        return FindLinkInText(text) ?? result;
    }

    /// <summary>
    /// Finds the allowed link in copied text (e.g. a whole e-mail paragraph). Returns null when there is
    /// none, and an error when the text holds different allowed links, so the app never has to guess.
    /// </summary>
    private BlobUrlParseResult? FindLinkInText(string text)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        BlobUrlParseResult? found = null;
        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            var start = word.IndexOf("http", StringComparison.OrdinalIgnoreCase);
            if (start < 0 || !word.Contains("://", StringComparison.Ordinal))
            {
                continue;
            }

            // A link broken by a tab or line break ("…windows.net\t/container") would be read as a different
            // location; skip it rather than guess.
            if (i + 1 < words.Length && "/.?#&=%:".Contains(words[i + 1][0]))
            {
                continue;
            }

            // "<https://…>." or "(https://…)," → drop the wrapper and trailing punctuation.
            var token = TrimTrailingPunctuation(word[start..]);
            var opener = start > 0 ? word[start - 1] : '\0';
            var closer = WrapperPairs.FirstOrDefault(p => p.Open == opener).Close;
            if (closer != '\0' && token.EndsWith(closer))
            {
                token = TrimTrailingPunctuation(token[..^1]);
            }

            var candidate = ParseLinkOrSafeLink(token);
            if (!candidate.IsSuccess)
            {
                continue;
            }

            if (found?.Target is not { } foundTarget)
            {
                found = candidate;
            }
            else if (foundTarget.ToString() != candidate.Target.ToString())
            {
                return Fail(BlobUrlError.MultipleLinks, Messages.MultipleLinks);
            }
        }

        return found;
    }

    /// <summary>
    /// Removes sentence punctuation after a link, but never in a way that turns a <c>.</c> / <c>..</c>
    /// path segment into a different folder (<c>…/Data/..</c> stays as it is and is rejected later).
    /// </summary>
    private static string TrimTrailingPunctuation(string text)
    {
        var trimmed = text.TrimEnd('.', ',', ';', ':', '!');
        if (trimmed.Length == text.Length)
        {
            return text;
        }

        return trimmed.EndsWith('/') && text[trimmed.Length] == '.' ? text : trimmed;
    }

    /// <summary>
    /// Removes invisible formatting characters (Unicode category Cf: zero-width spaces, soft hyphens,
    /// byte order marks, direction marks) that HTML e-mail and word processors insert into links.
    /// </summary>
    internal static string RemoveInvisible(string text) =>
        text.Any(c => char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format)
            ? string.Concat(text.Where(c => char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format))
            : text;

    private const string SafeLinksHostSuffix = ".safelinks.protection.outlook.com";

    /// <summary>
    /// Parses a link; when it is an Outlook Safe Links wrapper, parses the original link it carries instead.
    /// Only one level is unwrapped, and the Safe Links host itself is never contacted.
    /// </summary>
    private BlobUrlParseResult ParseLinkOrSafeLink(string text)
    {
        if (TryUnwrapSafeLinks(text, out var inner))
        {
            return ParseLink(inner);
        }

        return ParseLink(text);
    }

    internal static bool TryUnwrapSafeLinks(string text, [NotNullWhen(true)] out string? inner)
    {
        inner = null;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IdnHost.EndsWith(SafeLinksHostSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var pair in uri.Query.TrimStart('?').Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && string.Equals(pair[..eq], "url", StringComparison.OrdinalIgnoreCase))
            {
                inner = Uri.UnescapeDataString(pair[(eq + 1)..]).Trim();
                return inner.Length > 0;
            }
        }

        return false;
    }

    /// <summary>Validates a single link (already unwrapped and cleaned).</summary>
    private BlobUrlParseResult ParseLink(string text)
    {
        // Control characters (line breaks, tabs, NUL) and backslashes have no place in a link and
        // are a classic way to make parsers disagree about the host.
        if (text.Any(char.IsControl) || text.Contains('\\'))
        {
            return Fail(BlobUrlError.NotAUrl, Messages.NotAUrl);
        }

        var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
        {
            return Fail(BlobUrlError.NotAUrl, Messages.NotAUrl);
        }

        if (!string.Equals(text[..schemeEnd], Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return Fail(BlobUrlError.NotHttps, Messages.NotHttps);
        }

        // Check the raw authority ourselves rather than relying only on Uri's interpretation.
        var authorityStart = schemeEnd + 3;
        var authorityEnd = text.IndexOfAny(['/', '?', '#'], authorityStart);
        var rawAuthority = authorityEnd < 0 ? text[authorityStart..] : text[authorityStart..authorityEnd];
        if (rawAuthority.Contains('@'))
        {
            return Fail(BlobUrlError.UserInfoNotAllowed, Messages.UserInfo);
        }

        // Uri silently resolves "." / ".." segments, even percent-encoded ones ("/%2E%2E/other" becomes
        // container "other"). Reject them on the raw path so the target is always what the link says.
        if (HasDotSegment(text, authorityEnd))
        {
            return Fail(BlobUrlError.InvalidPath, Messages.InvalidPath);
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return Fail(BlobUrlError.NotAUrl, Messages.NotAUrl);
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return Fail(BlobUrlError.NotHttps, Messages.NotHttps);
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return Fail(BlobUrlError.UserInfoNotAllowed, Messages.UserInfo);
        }

        switch (uri.HostNameType)
        {
            case UriHostNameType.IPv4:
            case UriHostNameType.IPv6:
                return Fail(BlobUrlError.IpAddressNotAllowed, Messages.IpAddress);
            case UriHostNameType.Dns:
                break;
            default:
                return Fail(BlobUrlError.InvalidHost, Messages.InvalidHost);
        }

        if (uri.Port != 443)
        {
            return Fail(BlobUrlError.PortNotAllowed, Messages.Port);
        }

        // Compare on the IDN ASCII (punycode) form so Unicode look-alikes cannot match an ASCII pattern.
        var host = uri.IdnHost.ToLowerInvariant();
        if (!IsValidDnsName(host))
        {
            return Fail(BlobUrlError.InvalidHost, Messages.InvalidHost);
        }

        if (!IsHostAllowed(host))
        {
            return Fail(BlobUrlError.HostNotAllowed, Messages.HostNotAllowed);
        }

        var sasRemoved = HasSasSignature(uri.Query);
        var accountUri = new Uri($"https://{host}/", UriKind.Absolute);

        // AbsolutePath is still percent-encoded; decode each part exactly once.
        var path = uri.AbsolutePath;
        var rest = path.StartsWith('/') ? path[1..] : path;
        if (rest.Length == 0)
        {
            return BlobUrlParseResult.Success(
                new BlobTarget { Kind = BlobTargetKind.Account, Host = host, AccountUri = accountUri },
                sasRemoved);
        }

        var slash = rest.IndexOf('/');
        var containerRaw = slash < 0 ? rest : rest[..slash];
        var remainderRaw = slash < 0 ? string.Empty : rest[(slash + 1)..];

        var container = Uri.UnescapeDataString(containerRaw);
        if (!IsValidContainerName(container))
        {
            return Fail(BlobUrlError.InvalidContainerName, Messages.InvalidContainer);
        }

        var containerUri = new Uri($"https://{host}/{container}", UriKind.Absolute);

        var prefix = Uri.UnescapeDataString(remainderRaw).TrimEnd('/');
        if (prefix.Length == 0)
        {
            return BlobUrlParseResult.Success(
                new BlobTarget
                {
                    Kind = BlobTargetKind.Container,
                    Host = host,
                    AccountUri = accountUri,
                    ContainerName = container,
                    ContainerUri = containerUri,
                },
                sasRemoved);
        }

        prefix += "/";
        if (!IsValidPrefix(prefix))
        {
            return Fail(BlobUrlError.InvalidPath, Messages.InvalidPath);
        }

        return BlobUrlParseResult.Success(
            new BlobTarget
            {
                Kind = BlobTargetKind.ContainerWithPrefix,
                Host = host,
                AccountUri = accountUri,
                ContainerName = container,
                ContainerUri = containerUri,
                Prefix = prefix,
            },
            sasRemoved);
    }

    /// <summary>Trims whitespace and surrounding quotes / angle brackets / brackets, repeatedly.</summary>
    internal static string Unwrap(string input)
    {
        var text = input.Trim();
        var changed = true;
        while (changed && text.Length >= 2)
        {
            changed = false;
            foreach (var (open, close) in WrapperPairs)
            {
                if (text[0] == open && text[^1] == close)
                {
                    text = text[1..^1].Trim();
                    changed = true;
                    break;
                }
            }
        }

        return text;
    }

    /// <summary>Lowercase ASCII DNS name: labels of 1–63 [a-z0-9-], no leading/trailing hyphen, no trailing dot.</summary>
    internal static bool IsValidDnsName(string host)
    {
        if (host.Length is 0 or > 253)
        {
            return false;
        }

        foreach (var label in host.Split('.'))
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-')
            {
                return false;
            }

            foreach (var c in label)
            {
                if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Azure container naming rules: 3–63 characters, lowercase letters, digits and hyphens,
    /// starting and ending with a letter or digit, no consecutive hyphens.
    /// Special containers (<c>$root</c>, <c>$web</c>, <c>$logs</c>) are rejected.
    /// </summary>
    internal static bool IsValidContainerName(string name)
    {
        if (name.Length is < 3 or > 63 || name[0] == '-' || name[^1] == '-' || name.Contains("--", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidPrefix(string prefix)
    {
        if (prefix.Length > MaxBlobNameLength || prefix.Any(char.IsControl))
        {
            return false;
        }

        // Defence in depth: dot segments are already rejected on the raw path in Parse.
        foreach (var segment in prefix.Split('/'))
        {
            if (segment is "." or "..")
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasDotSegment(string text, int pathStart)
    {
        if (pathStart < 0)
        {
            return false;
        }

        var rawPath = text[pathStart..];
        var end = rawPath.IndexOfAny(['?', '#']);
        if (end >= 0)
        {
            rawPath = rawPath[..end];
        }

        foreach (var segment in rawPath.Split('/'))
        {
            if (Uri.UnescapeDataString(segment) is "." or "..")
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasSasSignature(string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return false;
        }

        foreach (var pair in query.TrimStart('?').Split('&'))
        {
            var eq = pair.IndexOf('=');
            var key = eq < 0 ? pair : pair[..eq];
            if (string.Equals(Uri.UnescapeDataString(key).Trim(), "sig", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static BlobUrlParseResult Fail(BlobUrlError error, string message) => BlobUrlParseResult.Failure(error, message);
}
