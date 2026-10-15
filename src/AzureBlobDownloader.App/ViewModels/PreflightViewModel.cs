using AzureBlobDownloader.Core.Downloads;
using AzureBlobDownloader.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzureBlobDownloader.App.ViewModels;

/// <summary>One line in the pre-flight issue list.</summary>
public sealed record PreflightIssueRow(string KindText, string BlobName, string Message);

/// <summary>
/// Shown before a download starts when something needs the user's attention: renamed or skipped files,
/// long paths, files that will be replaced, or a reason the download cannot start.
/// </summary>
public sealed partial class PreflightViewModel : ObservableObject
{
    private const int MaxIssuesShown = 500;

    private readonly TaskCompletionSource<bool> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PreflightViewModel(PreflightReport report)
    {
        Report = report;
        SummaryText = $"{report.ToDownload.Count:N0} files, {ByteSize.Format(report.BytesNeeded)} to download into {report.Destination}";

        var notes = new List<string>();
        if (report.FreeSpace is { } free)
        {
            notes.Add($"Free space there: {ByteSize.Format(free)}.");
        }

        if (report.AlreadyThere > 0)
        {
            notes.Add($"{report.AlreadyThere:N0} files are already downloaded and will be skipped.");
        }

        if (report.Continuing > 0)
        {
            notes.Add($"{report.Continuing:N0} partly downloaded files will continue where they stopped.");
        }

        if (report.WillReplace > 0)
        {
            notes.Add($"{report.WillReplace:N0} files with the same name already exist but differ; they will be replaced.");
        }

        Notes = notes;
        Issues = [.. report.Issues.Take(MaxIssuesShown).Select(i => new PreflightIssueRow(
            i.Kind switch
            {
                PlanIssueKind.Skipped => "Skipped",
                PlanIssueKind.Renamed => "Renamed",
                _ => "Long path",
            },
            i.BlobName,
            i.Message))];
        MoreIssuesText = report.Issues.Count > MaxIssuesShown ? $"…and {report.Issues.Count - MaxIssuesShown:N0} more." : null;
    }

    public PreflightReport Report { get; }

    public string SummaryText { get; }

    public IReadOnlyList<string> Notes { get; }

    public IReadOnlyList<PreflightIssueRow> Issues { get; }

    public bool HasIssues => Issues.Count > 0;

    public string? MoreIssuesText { get; }

    public string? BlockingProblem => Report.BlockingProblem;

    public bool CanStart => Report.CanStart;

    /// <summary>Completes with true when the user starts the download, false when they go back.</summary>
    public Task<bool> Decision => _decision.Task;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start() => _decision.TrySetResult(true);

    [RelayCommand]
    private void Back() => _decision.TrySetResult(false);
}
