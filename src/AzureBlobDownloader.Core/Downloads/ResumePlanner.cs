using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzureBlobDownloader.Core.Downloads;

/// <summary>What is stored next to a <c>.partial</c> file so the download can continue later.</summary>
/// <param name="ValidLength">The first bytes of the partial file that are downloaded and flushed to disk.</param>
public sealed record PartialState(string ETag, long Size, long ValidLength);

/// <summary>Length and modification time of a local file.</summary>
public sealed record LocalFileState(long Length, DateTime LastWriteTimeUtc)
{
    public static LocalFileState? TryGet(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? new LocalFileState(info.Length, info.LastWriteTimeUtc) : null;
    }
}

/// <summary>How to handle one file when a download (re)starts.</summary>
public abstract record ResumeDecision
{
    /// <summary>The final file is already there with the blob's size and modification time.</summary>
    public sealed record AlreadyComplete : ResumeDecision;

    /// <summary>Download from the beginning.</summary>
    public sealed record StartOver : ResumeDecision;

    /// <summary>Continue the partial file from <see cref="Offset"/>.</summary>
    public sealed record Resume(long Offset) : ResumeDecision;
}

/// <summary>Decides whether a file is finished, can be continued, or must start over.</summary>
public static class ResumePlanner
{
    public const string PartialSuffix = ".partial";
    public const string StateSuffix = ".partial.json";

    /// <summary>Longest suffix added to a file name while downloading (state file written via a temporary file).</summary>
    public const int LongestSuffixLength = 17; // ".partial.json.tmp"

    /// <summary>File systems such as FAT and some network shares store times with 2-second precision.</summary>
    private static readonly TimeSpan TimeTolerance = TimeSpan.FromSeconds(2);

    public static ResumeDecision Decide(RemoteBlob blob, LocalFileState? final, LocalFileState? partial, PartialState? state)
    {
        if (final is not null
            && final.Length == blob.Size
            && (final.LastWriteTimeUtc - blob.LastModified.UtcDateTime).Duration() <= TimeTolerance)
        {
            return new ResumeDecision.AlreadyComplete();
        }

        if (partial is not null
            && state is not null
            && state.ETag == blob.ETag
            && state.Size == blob.Size
            && state.ValidLength > 0
            && state.ValidLength <= blob.Size
            && state.ValidLength <= partial.Length)
        {
            return new ResumeDecision.Resume(state.ValidLength);
        }

        return new ResumeDecision.StartOver();
    }

    /// <summary>Looks at the destination and decides for <paramref name="blob"/> stored at <paramref name="finalPath"/>.</summary>
    public static ResumeDecision Inspect(RemoteBlob blob, string finalPath) => Decide(
        blob,
        LocalFileState.TryGet(finalPath),
        LocalFileState.TryGet(finalPath + PartialSuffix),
        PartialStateFile.TryRead(finalPath + StateSuffix));
}

/// <summary>Reads and writes <see cref="PartialState"/> files.</summary>
public static class PartialStateFile
{
    public static PartialState? TryRead(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path), DownloadJsonContext.Default.PartialState)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null; // A damaged state file only means starting that file over.
        }
    }

    /// <summary>Writes via a temporary file and a rename, so a crash never leaves a half-written state file.</summary>
    public static void Write(string path, PartialState state)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, DownloadJsonContext.Default.PartialState));
        File.Move(temporary, path, overwrite: true);
    }

    public static void Delete(string partialPath)
    {
        File.Delete(partialPath);
        File.Delete(partialPath[..^ResumePlanner.PartialSuffix.Length] + ResumePlanner.StateSuffix);
    }
}

[JsonSerializable(typeof(PartialState))]
internal sealed partial class DownloadJsonContext : JsonSerializerContext;
