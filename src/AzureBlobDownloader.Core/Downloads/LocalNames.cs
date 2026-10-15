using System.Text;

namespace AzureBlobDownloader.Core.Downloads;

/// <summary>Which file name rules apply on the destination.</summary>
public enum NameRules
{
    /// <summary>Windows: no <c>&lt;&gt;:"/\|?*</c> or control characters, no reserved device names, no trailing dot or space.</summary>
    Windows,

    /// <summary>macOS and Linux: anything except <c>/</c> and NUL.</summary>
    Unix,
}

/// <summary>Turns blob name segments into names that are valid (and safe) on the local file system.</summary>
public static class LocalNames
{
    /// <summary>Longest file or folder name on NTFS, APFS and ext4 (UTF-16 units on Windows, bytes elsewhere).</summary>
    public const int MaxNameLength = 255;

    private const char Replacement = '_';

    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    public static NameRules Current => OperatingSystem.IsWindows() ? NameRules.Windows : NameRules.Unix;

    /// <summary>
    /// Makes one path segment valid under <paramref name="rules"/>. The result is deterministic, so the same
    /// blob always maps to the same local name (needed to resume and to skip finished files).
    /// </summary>
    /// <returns>The local name, and a plain-language reason when it had to be changed.</returns>
    public static (string Name, string? Change) Sanitize(string segment, NameRules rules)
    {
        if (rules == NameRules.Unix)
        {
            return segment.Contains('\0')
                ? (segment.Replace('\0', Replacement), "contains a character that is not allowed")
                : (segment, null);
        }

        var builder = new StringBuilder(segment.Length);
        var invalidCharacter = false;
        foreach (var c in segment)
        {
            if (c < 32 || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*')
            {
                builder.Append(Replacement);
                invalidCharacter = true;
            }
            else
            {
                builder.Append(c);
            }
        }

        string? change = invalidCharacter ? "contains characters Windows does not allow in names" : null;

        // Windows silently drops a trailing dot or space, which would point at a different file.
        var end = builder.Length;
        while (end > 0 && builder[end - 1] is '.' or ' ')
        {
            builder[--end] = Replacement;
        }

        if (end < builder.Length)
        {
            change ??= "ends with a dot or space, which Windows does not allow";
        }

        var name = builder.ToString();
        var dot = name.IndexOf('.');
        var baseName = (dot < 0 ? name : name[..dot]).TrimEnd(' ');
        if (WindowsReservedNames.Contains(baseName))
        {
            name = dot < 0 ? name + Replacement : name[..dot] + Replacement + name[dot..];
            change ??= "is a name Windows reserves for devices";
        }

        return (name, change);
    }

    /// <summary>Length of a name as the file system counts it (UTF-16 units on Windows, UTF-8 bytes elsewhere).</summary>
    public static int MeasureName(string name, NameRules rules) =>
        rules == NameRules.Windows ? name.Length : Encoding.UTF8.GetByteCount(name);
}
