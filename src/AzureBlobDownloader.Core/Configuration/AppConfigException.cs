using System.Text;

namespace AzureBlobDownloader.Core.Configuration;

/// <summary>
/// Thrown when the configuration cannot be read or is invalid. <see cref="Exception.Message"/>
/// lists every problem and the files that were read, so it can be shown to the user as is.
/// </summary>
public sealed class AppConfigException : Exception
{
    public AppConfigException(IReadOnlyList<string> errors, IReadOnlyList<string> sources, Exception? innerException = null)
        : base(BuildMessage(errors, sources), innerException)
    {
        Errors = errors;
        Sources = sources;
    }

    /// <summary>The individual problems, one sentence each.</summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>Full paths of the config files that were read, in merge order.</summary>
    public IReadOnlyList<string> Sources { get; }

    private static string BuildMessage(IReadOnlyList<string> errors, IReadOnlyList<string> sources)
    {
        var sb = new StringBuilder("The app configuration is invalid:");
        foreach (var error in errors)
        {
            sb.AppendLine().Append(" - ").Append(error);
        }

        if (sources.Count > 0)
        {
            sb.AppendLine().Append("Configuration files read: ").Append(string.Join("; ", sources));
        }

        return sb.ToString();
    }
}
