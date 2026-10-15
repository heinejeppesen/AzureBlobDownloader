using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using AzureBlobDownloader.App.Services;
using AzureBlobDownloader.Core.Diagnostics;
using AzureBlobDownloader.Core.Downloads;
using AzureBlobDownloader.Core.Errors;
using AzureBlobDownloader.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzureBlobDownloader.App.ViewModels;

/// <summary>A file being downloaded right now.</summary>
public sealed partial class ActiveFileRow(DownloadFile file) : ObservableObject
{
    public DownloadFile File { get; } = file;

    public string Name { get; } = file.Item.RelativePath;

    [ObservableProperty]
    public partial double Percent { get; set; }

    [ObservableProperty]
    public partial string SizeText { get; set; } = string.Empty;

    public void Refresh()
    {
        var size = File.Blob.Size;
        var done = File.BytesDone;
        Percent = size == 0 ? 100 : done * 100.0 / size;
        SizeText = $"{ByteSize.Format(done)} of {ByteSize.Format(size)}";
    }
}

/// <summary>A file that could not be downloaded, with the reason.</summary>
public sealed record FailedFileRow(string Name, string Error);

/// <summary>
/// The download screen: overall and per-file progress, speed and time left, pause/resume/cancel, retry of
/// failed files, and the sign-in prompt when the session expires during a long download.
/// </summary>
public sealed partial class DownloadViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan SpeedWindow = TimeSpan.FromSeconds(10);

    private readonly DownloadJob _job;
    private readonly IAppDialogs _dialogs;
    private readonly Func<CancellationToken, Task> _signInAgain;
    private readonly DispatcherTimer _timer;
    private readonly Queue<(long Timestamp, long Bytes)> _speedSamples = new();
    private readonly Dictionary<DownloadFile, ActiveFileRow> _activeRows = [];
    private IDisposable? _keepAwake;
    private TaskCompletionSource<bool>? _signInRequest;
    private int _failedRowsShown = -1;

    /// <param name="signInAgain">Interactive re-authentication as the same user (throws when it fails).</param>
    public DownloadViewModel(DownloadJob job, int alreadyThere, IAppDialogs dialogs, Func<CancellationToken, Task> signInAgain)
    {
        _job = job;
        _dialogs = dialogs;
        _signInAgain = signInAgain;
        AlreadyThereAtStart = alreadyThere;
        Destination = job.Destination;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Refresh());
        _timer.Start();
        Refresh();
    }

    /// <summary>Raised when the user leaves the download screen.</summary>
    public event Action? Closed;

    public string Destination { get; }

    public int AlreadyThereAtStart { get; }

    public ObservableCollection<ActiveFileRow> ActiveFiles { get; } = [];

    public ObservableCollection<FailedFileRow> FailedFiles { get; } = [];

    public bool IsActive => _job.IsActive;

    public bool IsPaused => _job.State == DownloadJobState.Paused;

    public bool IsFinished => _job.State is DownloadJobState.Completed or DownloadJobState.Cancelled;

    [ObservableProperty]
    public partial double Percent { get; set; }

    [ObservableProperty]
    public partial string HeaderText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProgressText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? SpeedText { get; set; }

    [ObservableProperty]
    public partial string StateText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasWarning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailures))]
    public partial int FailedCount { get; set; }

    public bool HasFailures => FailedCount > 0;

    [ObservableProperty]
    public partial bool NeedsSignIn { get; set; }

    [ObservableProperty]
    public partial string? SignInError { get; set; }

    /// <summary>
    /// Called by the download (on a background thread) when it needs the user to sign in again. Shows the
    /// sign-in banner and completes when the user has signed in (true) or chose not to (false).
    /// </summary>
    public Task<bool> RequestSignInAsync(CancellationToken cancellationToken) =>
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            _signInRequest?.TrySetResult(false);
            var request = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => request.TrySetResult(false));
            _signInRequest = request;
            SignInError = null;
            NeedsSignIn = true;
            Refresh();
            return request.Task;
        });

    [RelayCommand]
    private async Task SignInAsync()
    {
        SignInError = null;
        try
        {
            await _signInAgain(CancellationToken.None);
            NeedsSignIn = false;
            _signInRequest?.TrySetResult(true);
        }
        catch (Exception ex)
        {
            var error = ErrorTranslator.Translate(ex);
            SignInError = ex is InvalidOperationException ? ex.Message : $"{error.Title}. {error.Message}";
            AppLog.Error("Re-authentication during a download failed.", ex);
        }
    }

    [RelayCommand]
    private void NotNow()
    {
        NeedsSignIn = false;
        _signInRequest?.TrySetResult(false);
    }

    private bool CanPause() => _job.IsActive;

    [RelayCommand(CanExecute = nameof(CanPause))]
    private async Task PauseAsync()
    {
        await _job.PauseAsync();
        Refresh();
    }

    private bool CanResume() => _job.State == DownloadJobState.Paused;

    [RelayCommand(CanExecute = nameof(CanResume))]
    private void Resume()
    {
        _job.Start();
        Refresh();
    }

    private bool CanCancel() => !IsFinished;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private async Task CancelAsync()
    {
        var confirmed = await _dialogs.ConfirmAsync(
            "Cancel the download",
            "Stop downloading? Files that are already downloaded are kept; partly downloaded files are deleted.",
            "Stop download",
            "Keep downloading");
        if (confirmed)
        {
            NotNow();
            await _job.CancelAsync();
            Refresh();
        }
    }

    private bool CanRetry() => FailedCount > 0 && !_job.IsActive && _job.State != DownloadJobState.Cancelled;

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private void RetryFailed()
    {
        _job.RetryFailed();
        Refresh();
    }

    [RelayCommand]
    private void OpenFolder() => _dialogs.OpenFolder(Destination);

    private bool CanClose() => !_job.IsActive;

    [RelayCommand(CanExecute = nameof(CanClose))]
    private void Close() => Closed?.Invoke();

    /// <summary>Pauses the download (keeping partial files), e.g. before the app closes.</summary>
    public Task PauseForExitAsync() => _job.PauseAsync();

    public void Dispose()
    {
        _timer.Stop();
        _keepAwake?.Dispose();
        _keepAwake = null;
        _signInRequest?.TrySetResult(false);
    }

    private void Refresh()
    {
        long done = 0;
        int finished = 0, failed = 0, alreadyThere = 0, md5 = 0;
        foreach (var file in _job.Files)
        {
            done += file.BytesDone;
            switch (file.State)
            {
                case DownloadFileState.Completed:
                    finished++;
                    if (file.VerifiedWithMd5)
                    {
                        md5++;
                    }

                    break;
                case DownloadFileState.AlreadyThere:
                    finished++;
                    alreadyThere++;
                    break;
                case DownloadFileState.Failed:
                    failed++;
                    break;
            }
        }

        var total = _job.TotalBytes;
        var count = _job.Files.Count;
        Percent = total > 0 ? Math.Min(100, done * 100.0 / total) : finished == count ? 100 : 0;
        ProgressText = $"{ByteSize.Format(done)} of {ByteSize.Format(total)} · {finished:N0} of {count:N0} files";
        FailedCount = failed;

        var speed = UpdateSpeed();
        SpeedText = _job.State == DownloadJobState.Running && speed > 0
            ? $"{ByteSize.Format((long)speed)}/s · {FormatTimeLeft((total - done) / speed)}"
            : null;

        UpdateActiveRows();
        UpdateFailedRows(failed);
        UpdateState(finished - alreadyThere, alreadyThere, failed, md5);
        UpdateKeepAwake();

        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(IsFinished));
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        RetryFailedCommand.NotifyCanExecuteChanged();
        CloseCommand.NotifyCanExecuteChanged();
    }

    private void UpdateState(int downloaded, int alreadyThere, int failed, int md5)
    {
        HasWarning = false;
        switch (_job.State)
        {
            case DownloadJobState.Running when _job.IsWaitingForNetwork:
                HeaderText = $"Downloading to {Destination}";
                StateText = "The connection was lost. Retrying automatically…";
                HasWarning = true;
                break;
            case DownloadJobState.Running:
                HeaderText = $"Downloading to {Destination}";
                StateText = "Downloading. You can keep working; the computer will not go to sleep until it is done.";
                break;
            case DownloadJobState.WaitingForSignIn:
                HeaderText = $"Downloading to {Destination}";
                StateText = "Your sign-in has expired. Sign in again to continue the download.";
                HasWarning = true;
                break;
            case DownloadJobState.Paused:
                HeaderText = "Download paused";
                StateText = _job.PauseReason ?? "Paused. Press Resume to continue where it stopped.";
                HasWarning = _job.PauseReason is not null;
                break;
            case DownloadJobState.Cancelled:
                HeaderText = "Download cancelled";
                StateText = $"{downloaded:N0} files were downloaded before it was stopped.";
                break;
            case DownloadJobState.Completed:
                HeaderText = failed > 0 ? "Download finished with problems" : "Download complete";
                var checks = downloaded == 0 ? string.Empty
                    : md5 == downloaded ? $" All {downloaded:N0} were checked against their MD5 checksum."
                    : md5 == 0 ? $" {downloaded:N0} were checked by size (they have no MD5 checksum)."
                    : $" {md5:N0} were checked against their MD5 checksum, {downloaded - md5:N0} by size.";
                StateText = $"{downloaded:N0} files downloaded"
                            + (alreadyThere > 0 ? $", {alreadyThere:N0} were already there" : string.Empty)
                            + (failed > 0 ? $", {failed:N0} failed (see below; use Retry failed)." : ".")
                            + checks;
                HasWarning = failed > 0;
                break;
            default:
                HeaderText = $"Downloading to {Destination}";
                StateText = "Starting…";
                break;
        }
    }

    private double UpdateSpeed()
    {
        var now = Stopwatch.GetTimestamp();
        _speedSamples.Enqueue((now, _job.TransferredBytes));
        while (_speedSamples.Count > 2 && Stopwatch.GetElapsedTime(_speedSamples.Peek().Timestamp, now) > SpeedWindow)
        {
            _speedSamples.Dequeue();
        }

        var (firstTime, firstBytes) = _speedSamples.Peek();
        var seconds = Stopwatch.GetElapsedTime(firstTime, now).TotalSeconds;
        return seconds < 1 ? 0 : (_job.TransferredBytes - firstBytes) / seconds;
    }

    private static string FormatTimeLeft(double seconds) => seconds switch
    {
        < 60 => "less than a minute left",
        < 3600 => $"about {Math.Ceiling(seconds / 60):0} min left",
        < 86400 => $"about {Math.Floor(seconds / 3600):0} h {Math.Ceiling(seconds % 3600 / 60):0} min left",
        _ => $"about {Math.Floor(seconds / 86400):0} days left",
    };

    private void UpdateActiveRows()
    {
        var active = _job.Files.Where(f => f.State == DownloadFileState.Downloading).ToHashSet();
        foreach (var gone in _activeRows.Keys.Where(f => !active.Contains(f)).ToList())
        {
            ActiveFiles.Remove(_activeRows[gone]);
            _activeRows.Remove(gone);
        }

        foreach (var file in active)
        {
            if (!_activeRows.TryGetValue(file, out var row))
            {
                row = new ActiveFileRow(file);
                _activeRows[file] = row;
                ActiveFiles.Add(row);
            }

            row.Refresh();
        }
    }

    private void UpdateFailedRows(int failed)
    {
        if (failed == _failedRowsShown)
        {
            return;
        }

        _failedRowsShown = failed;
        FailedFiles.Clear();
        foreach (var file in _job.Files.Where(f => f.State == DownloadFileState.Failed).Take(500))
        {
            FailedFiles.Add(new FailedFileRow(file.Item.RelativePath, file.Error ?? "Failed."));
        }
    }

    private void UpdateKeepAwake()
    {
        if (_job.IsActive && _keepAwake is null)
        {
            _keepAwake = KeepAwake.Acquire();
        }
        else if (!_job.IsActive && _keepAwake is not null)
        {
            _keepAwake.Dispose();
            _keepAwake = null;
        }
    }
}
