using System.Diagnostics.CodeAnalysis;

namespace AzureBlobDownloader.Core.Urls;

/// <summary>
/// A host name pattern from <c>AllowedHosts</c>, e.g. <c>contoso*.blob.core.windows.net</c>.
/// <list type="bullet">
///   <item>Lowercase ASCII only (write internationalised names in punycode).</item>
///   <item><c>*</c> matches zero or more characters <b>within one DNS label</b>; it never matches a dot.
///   It is only allowed in the leftmost label.</item>
///   <item>The host must have exactly as many labels as the pattern, so <c>*.blob.core.windows.net</c>
///   does not match <c>a.b.blob.core.windows.net</c>.</item>
/// </list>
/// </summary>
public sealed class HostPattern
{
    private readonly string[] _labels;

    private HostPattern(string pattern, string[] labels)
    {
        Pattern = pattern;
        _labels = labels;
    }

    /// <summary>The normalised (trimmed, lowercase) pattern.</summary>
    public string Pattern { get; }

    public override string ToString() => Pattern;

    /// <exception cref="FormatException">The pattern is invalid.</exception>
    public static HostPattern Parse(string pattern) =>
        TryParse(pattern, out var result, out var error) ? result : throw new FormatException(error);

    public static bool TryParse(
        string? pattern,
        [NotNullWhen(true)] out HostPattern? result,
        [NotNullWhen(false)] out string? error)
    {
        result = null;
        var normalized = pattern?.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(normalized))
        {
            error = "the pattern is empty.";
            return false;
        }

        if (normalized.Length > 253)
        {
            error = "the pattern is longer than 253 characters.";
            return false;
        }

        foreach (var c in normalized)
        {
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.' or '*'))
            {
                error = $"the character '{c}' is not allowed; use letters, digits, '-', '.' and '*' (international names in punycode).";
                return false;
            }
        }

        var labels = normalized.Split('.');
        if (labels.Any(l => l.Length == 0))
        {
            error = "the pattern has an empty label (leading, trailing or double dot).";
            return false;
        }

        var hasWildcard = normalized.Contains('*');
        if (labels.Skip(1).Any(l => l.Contains('*')))
        {
            error = "'*' is only allowed in the leftmost label, e.g. \"contoso*.blob.core.windows.net\".";
            return false;
        }

        if (hasWildcard ? labels.Length < 3 : labels.Length < 2)
        {
            error = "the pattern is too broad; name the full domain, e.g. \"*.blob.core.windows.net\".";
            return false;
        }

        foreach (var label in labels)
        {
            if (label.Length > 63)
            {
                error = $"the label \"{label}\" is longer than 63 characters.";
                return false;
            }

            if (!label.Contains('*') && (label.StartsWith('-') || label.EndsWith('-')))
            {
                error = $"the label \"{label}\" starts or ends with '-'.";
                return false;
            }
        }

        result = new HostPattern(normalized, labels);
        error = null;
        return true;
    }

    /// <summary>
    /// True when <paramref name="host"/> (an ASCII / punycode DNS name, any case, no trailing dot) matches.
    /// Non-ASCII input never matches.
    /// </summary>
    public bool IsMatch(string host)
    {
        ArgumentNullException.ThrowIfNull(host);

        foreach (var c in host)
        {
            if (c > 0x7F)
            {
                return false;
            }
        }

        var hostLabels = host.ToLowerInvariant().Split('.');
        if (hostLabels.Length != _labels.Length)
        {
            return false;
        }

        for (var i = 0; i < _labels.Length; i++)
        {
            if (hostLabels[i].Length == 0 || !LabelMatches(_labels[i], hostLabels[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Glob match of a single label where '*' matches any run of characters (labels contain no dots).</summary>
    private static bool LabelMatches(string pattern, string label)
    {
        if (!pattern.Contains('*'))
        {
            return string.Equals(pattern, label, StringComparison.Ordinal);
        }

        int p = 0, s = 0, starP = -1, starS = 0;
        while (s < label.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p++;
                starS = s;
            }
            else if (p < pattern.Length && pattern[p] == label[s])
            {
                p++;
                s++;
            }
            else if (starP >= 0)
            {
                p = starP + 1;
                s = ++starS;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}
