using System.Runtime.InteropServices;
using System.Text;
using AzureBlobDownloader.Core.Storage;

namespace AzureBlobDownloader.Core.Downloads;

/// <summary>Facts about the destination folder that the pre-flight check needs.</summary>
public interface IDestinationProbe
{
    /// <summary>Free bytes available to the user at <paramref name="folder"/>, or null when unknown.</summary>
    long? GetAvailableFreeSpace(string folder);
}

/// <summary>The outcome of checking a plan against the destination, before anything is downloaded.</summary>
/// <param name="ToDownload">Files that will be downloaded (skipped files removed).</param>
/// <param name="Issues">Renamed, skipped and long-path files.</param>
/// <param name="BytesNeeded">Bytes still to download (finished files and the downloaded part of partial files excluded).</param>
/// <param name="AlreadyThere">Files already downloaded earlier; they are skipped.</param>
/// <param name="Continuing">Partly downloaded files that will continue where they stopped.</param>
/// <param name="WillReplace">Existing files with the same name that differ and will be replaced.</param>
/// <param name="BlockingProblem">Why the download cannot start (e.g. not enough space); null when it can.</param>
public sealed record PreflightReport(
    string Destination,
    IReadOnlyList<DownloadItem> ToDownload,
    IReadOnlyList<PlanIssue> Issues,
    long BytesNeeded,
    long? FreeSpace,
    int AlreadyThere,
    int Continuing,
    int WillReplace,
    string? BlockingProblem)
{
    public bool CanStart => BlockingProblem is null && ToDownload.Count > 0;

    /// <summary>Whether the user should see the report before the download starts.</summary>
    public bool NeedsAttention => BlockingProblem is not null || Issues.Count > 0 || WillReplace > 0;
}

/// <summary>Checks a download plan against the destination before starting, so problems show up front.</summary>
public static class Preflight
{
    /// <summary>Longest full path many Windows programs (including Explorer) can open.</summary>
    public const int WindowsClassicMaxPath = 259;

    /// <summary>Longest path Linux and macOS accept, in bytes.</summary>
    public const int UnixMaxPathBytes = 4095;

    /// <summary>Longest path Windows accepts at all, even with long path support.</summary>
    public const int WindowsMaxPath = 32_000;

    public static PreflightReport Check(DownloadPlan plan, string destination, NameRules rules, IDestinationProbe probe)
    {
        var issues = new List<PlanIssue>(plan.Issues);
        var toDownload = new List<DownloadItem>(plan.Items.Count);
        long bytesNeeded = 0;
        int alreadyThere = 0, continuing = 0, willReplace = 0;

        foreach (var item in plan.Items)
        {
            var fullPath = Path.Combine(destination, item.RelativePath);
            var pathLength = rules == NameRules.Windows
                ? fullPath.Length + ResumePlanner.LongestSuffixLength
                : Encoding.UTF8.GetByteCount(fullPath) + ResumePlanner.LongestSuffixLength;

            if (pathLength > (rules == NameRules.Windows ? WindowsMaxPath : UnixMaxPathBytes))
            {
                issues.Add(new PlanIssue(PlanIssueKind.Skipped, item.Blob.Name, null, "the full path would be too long to store"));
                continue;
            }

            if (rules == NameRules.Windows && pathLength > WindowsClassicMaxPath)
            {
                issues.Add(new PlanIssue(
                    PlanIssueKind.LongPath,
                    item.Blob.Name,
                    item.RelativePath,
                    $"the full path is {fullPath.Length} characters; some programs, including Explorer, cannot open files with paths over 260 characters"));
            }

            toDownload.Add(item);
            switch (ResumePlanner.Inspect(item.Blob, fullPath))
            {
                case ResumeDecision.AlreadyComplete:
                    alreadyThere++;
                    break;
                case ResumeDecision.Resume resume:
                    continuing++;
                    bytesNeeded += item.Blob.Size - resume.Offset;
                    break;
                default:
                    bytesNeeded += item.Blob.Size;
                    if (File.Exists(fullPath))
                    {
                        willReplace++;
                    }

                    break;
            }
        }

        var freeSpace = probe.GetAvailableFreeSpace(destination);
        string? blocking = null;
        if (toDownload.Count == 0)
        {
            blocking = "None of the selected files can be stored in this folder.";
        }
        else if (freeSpace is { } free && free < bytesNeeded)
        {
            blocking = $"There is not enough free space in this folder: {ByteSize.Format(bytesNeeded)} is needed, {ByteSize.Format(free)} is available.";
        }

        return new PreflightReport(destination, toDownload, issues, bytesNeeded, freeSpace, alreadyThere, continuing, willReplace, blocking);
    }
}

/// <summary>The real destination: free space and file system behaviour of a local or network folder.</summary>
public sealed class LocalDestination : IDestinationProbe
{
    public long? GetAvailableFreeSpace(string folder)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Works for drive letters, mapped drives and UNC paths alike (DriveInfo does not take UNC paths).
                return GetDiskFreeSpaceEx(folder, out var available, out _, out _) ? (long)available : null;
            }

            return new DriveInfo(folder).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Checks that the folder can be written to, and whether it tells <c>a.txt</c> and <c>A.txt</c> apart,
    /// by creating and deleting a small probe file.
    /// </summary>
    /// <exception cref="DestinationException">The folder cannot be written to.</exception>
    public static bool ProbeCaseSensitivity(string folder)
    {
        var probe = Path.Combine(folder, $".abd-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(probe, []);
            return !File.Exists(Path.Combine(folder, Path.GetFileName(probe).ToUpperInvariant()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DestinationException($"Files cannot be saved in \"{folder}\": {ex.Message}", ex);
        }
        finally
        {
            try
            {
                File.Delete(probe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort.
            }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directoryName, out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);
}
