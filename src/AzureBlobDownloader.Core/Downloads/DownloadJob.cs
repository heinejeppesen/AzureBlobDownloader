using System.Buffers;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Channels;
using Azure;
using AzureBlobDownloader.Core.Auth;
using AzureBlobDownloader.Core.Diagnostics;
using AzureBlobDownloader.Core.Errors;
using Microsoft.Identity.Client;

namespace AzureBlobDownloader.Core.Downloads;

public sealed class DownloadOptions
{
    /// <summary>Files downloaded at the same time.</summary>
    public int MaxConcurrentFiles { get; init; } = 4;

    /// <summary>Parts of one file downloaded at the same time.</summary>
    public int MaxConcurrentChunksPerFile { get; init; } = 4;

    /// <summary>Size of one part (one ranged request).</summary>
    public int ChunkSize { get; init; } = 8 * 1024 * 1024;

    public TimeSpan FirstRetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How often a partial file's progress is flushed and recorded for resuming.</summary>
    public TimeSpan SaveProgressInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How often a file is restarted when the blob keeps changing during the download.</summary>
    public int MaxChangedRestarts { get; init; } = 3;
}

public enum DownloadFileState
{
    Queued,
    Downloading,
    Completed,
    AlreadyThere,
    Failed,
}

/// <summary>One file of a <see cref="DownloadJob"/>. Updated from worker threads; read by the UI.</summary>
public sealed class DownloadFile
{
    private long _bytesDone;
    private volatile DownloadItem _item;
    private volatile DownloadFileState _state;
    private volatile string? _error;

    internal DownloadFile(DownloadItem item, string localPath)
    {
        _item = item;
        LocalPath = localPath;
    }

    public DownloadItem Item
    {
        get => _item;
        internal set => _item = value;
    }

    public RemoteBlob Blob => _item.Blob;

    /// <summary>Full local path of the finished file.</summary>
    public string LocalPath { get; }

    public DownloadFileState State
    {
        get => _state;
        internal set => _state = value;
    }

    /// <summary>Bytes of this file that are on disk.</summary>
    public long BytesDone => Interlocked.Read(ref _bytesDone);

    /// <summary>Why the file failed, in plain language.</summary>
    public string? Error
    {
        get => _error;
        internal set => _error = value;
    }

    /// <summary>True when the finished file was checked against the blob's MD5 (otherwise by size).</summary>
    public bool VerifiedWithMd5 { get; internal set; }

    /// <summary>Look the blob up again before downloading (it may have been fixed since it failed).</summary>
    internal bool RefreshBeforeDownload { get; set; }

    internal void SetBytesDone(long value) => Interlocked.Exchange(ref _bytesDone, value);

    internal void AddBytes(long value) => Interlocked.Add(ref _bytesDone, value);
}

public enum DownloadJobState
{
    NotStarted,
    Running,
    WaitingForSignIn,
    Paused,
    Completed,
    Cancelled,
}

/// <summary>
/// Downloads a list of blobs to a destination folder.
/// <list type="bullet">
///   <item>Several files at a time; large files in parts read in parallel but written in order, so memory
///   stays bounded (files × parts × part size) and the MD5 is computed while writing.</item>
///   <item>Each file is written to <c>name.partial</c> and renamed when complete. <c>name.partial.json</c>
///   records the blob version and how much is safely on disk, so a later run continues where it stopped.</item>
///   <item>Every part is requested for the blob version the download started with; if the blob changes,
///   the file starts over.</item>
///   <item>Network failures are retried with growing delays, indefinitely; an expired sign-in pauses all
///   files for one re-authentication; a destination problem (disk full, share gone) pauses the job.</item>
/// </list>
/// </summary>
public sealed class DownloadJob
{
    private readonly IBlobReader _reader;
    private readonly DownloadOptions _options;
    private readonly Func<CancellationToken, Task<bool>> _reauthenticate;
    private readonly ArrayPool<byte> _buffers;
    private readonly SemaphoreSlim _signInGate = new(1, 1);
    private readonly Lock _gate = new();
    private CancellationTokenSource? _runCancellation;
    private Task? _run;
    private volatile DownloadJobState _state;
    private volatile string? _pauseReason;
    private long _transferredBytes;
    private int _waitingForNetwork;
    private int _signInGeneration;

    /// <param name="reauthenticate">
    /// Called when the sign-in expired or was rejected during the download; returns true once the user is
    /// signed in again. Called once at a time, while the other files wait.
    /// </param>
    public DownloadJob(
        IReadOnlyList<DownloadItem> items,
        string destination,
        IBlobReader reader,
        DownloadOptions options,
        Func<CancellationToken, Task<bool>> reauthenticate)
    {
        Destination = destination;
        _reader = reader;
        _options = options;
        _reauthenticate = reauthenticate;
        Files = [.. items.Select(i => new DownloadFile(i, Path.Combine(destination, i.RelativePath)))];
        TotalBytes = Files.Sum(f => f.Blob.Size);
        _buffers = ArrayPool<byte>.Create(options.ChunkSize, Math.Max(1, options.MaxConcurrentFiles * options.MaxConcurrentChunksPerFile));
    }

    /// <summary>Raised (on a background thread) when <see cref="State"/> or <see cref="IsWaitingForNetwork"/> changes.</summary>
    public event Action? StateChanged;

    public string Destination { get; }

    public IReadOnlyList<DownloadFile> Files { get; }

    public long TotalBytes { get; }

    public DownloadJobState State => _state;

    /// <summary>Why the job paused by itself (e.g. disk full); null when the user paused it.</summary>
    public string? PauseReason => _pauseReason;

    /// <summary>Bytes received from the network in this session (for the speed display).</summary>
    public long TransferredBytes => Interlocked.Read(ref _transferredBytes);

    /// <summary>True while at least one file waits to retry after a network failure.</summary>
    public bool IsWaitingForNetwork => Volatile.Read(ref _waitingForNetwork) > 0;

    public bool IsActive => _state is DownloadJobState.Running or DownloadJobState.WaitingForSignIn;

    /// <summary>Starts, or resumes, downloading every file that is not finished.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_run is not null || _state == DownloadJobState.Cancelled)
            {
                return;
            }

            _pauseReason = null;
            _state = DownloadJobState.Running;
            _runCancellation = new CancellationTokenSource();
            // On the thread pool: the job never needs (or blocks) the caller's UI thread.
            var token = _runCancellation.Token;
            _run = Task.Run(() => RunAsync(token), CancellationToken.None);
        }

        RaiseStateChanged();
    }

    /// <summary>Stops downloading; partial files are kept and continue on <see cref="Start"/>.</summary>
    public Task PauseAsync() => StopAsync(reason: null);

    /// <summary>Stops downloading and deletes the partial files of unfinished files.</summary>
    public async Task CancelAsync()
    {
        await StopAsync(reason: null).ConfigureAwait(false);
        _state = DownloadJobState.Cancelled;
        foreach (var file in Files.Where(f => f.State is not (DownloadFileState.Completed or DownloadFileState.AlreadyThere)))
        {
            try
            {
                PartialStateFile.Delete(file.LocalPath + ResumePlanner.PartialSuffix);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn($"Could not delete a partial file: {ex.Message}");
            }
        }

        RaiseStateChanged();
    }

    /// <summary>Puts failed files back in the queue and starts downloading.</summary>
    public void RetryFailed()
    {
        foreach (var file in Files.Where(f => f.State == DownloadFileState.Failed))
        {
            file.Error = null;
            file.RefreshBeforeDownload = true;
            file.State = DownloadFileState.Queued;
        }

        Start();
    }

    /// <summary>Completes when the current run has stopped (finished, paused or cancelled).</summary>
    public Task WhenStoppedAsync()
    {
        lock (_gate)
        {
            return _run ?? Task.CompletedTask;
        }
    }

    private async Task StopAsync(string? reason)
    {
        Task? run;
        lock (_gate)
        {
            run = _run;
            if (run is null)
            {
                return;
            }

            _pauseReason ??= reason;
            _runCancellation!.Cancel();
        }

        await run.ConfigureAwait(false);
    }

    /// <summary>Called by a worker that cannot continue: stops all workers and shows <paramref name="reason"/>.</summary>
    private void PauseFromWorker(string reason)
    {
        lock (_gate)
        {
            if (_runCancellation is { IsCancellationRequested: false } cancellation)
            {
                _pauseReason = reason;
                cancellation.Cancel();
            }
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var queue = Channel.CreateUnbounded<DownloadFile>();
        foreach (var file in Files.Where(f => f.State is DownloadFileState.Queued or DownloadFileState.Downloading))
        {
            file.State = DownloadFileState.Queued;
            queue.Writer.TryWrite(file);
        }

        queue.Writer.Complete();

        var workers = Enumerable.Range(0, Math.Max(1, _options.MaxConcurrentFiles))
            .Select(_ => Task.Run(() => WorkerAsync(queue.Reader, cancellationToken), CancellationToken.None))
            .ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);

        lock (_gate)
        {
            _run = null;
            _runCancellation?.Dispose();
            _runCancellation = null;
            _state = Files.Any(f => f.State == DownloadFileState.Queued) ? DownloadJobState.Paused : DownloadJobState.Completed;
        }

        AppLog.Info($"Download {_state}: {Files.Count(f => f.State == DownloadFileState.Completed)} downloaded, "
                    + $"{Files.Count(f => f.State == DownloadFileState.AlreadyThere)} already there, "
                    + $"{Files.Count(f => f.State == DownloadFileState.Failed)} failed. {_pauseReason}");
        RaiseStateChanged();
    }

    private async Task WorkerAsync(ChannelReader<DownloadFile> queue, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && queue.TryRead(out var file))
        {
            try
            {
                await DownloadWithRetriesAsync(file, cancellationToken);
            }
            catch (Exception ex)
            {
                // Should not happen; never let one file stop the others.
                file.State = DownloadFileState.Failed;
                file.Error = ex.Message;
                AppLog.Error($"Unexpected failure for a file.", ex);
            }
        }
    }

    private async Task DownloadWithRetriesAsync(DownloadFile file, CancellationToken cancellationToken)
    {
        file.State = DownloadFileState.Downloading;
        file.Error = null;
        var delay = _options.FirstRetryDelay;

        while (true)
        {
            var signInGeneration = Volatile.Read(ref _signInGeneration);
            try
            {
                await DownloadFileAsync(file, cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                file.State = DownloadFileState.Queued;
                return;
            }
            catch (DestinationException ex) when (ex.IsDiskFull || !IsDestinationReachable())
            {
                // Affects every file: pause, so nothing is lost and the user can fix it and resume.
                file.State = DownloadFileState.Queued;
                AppLog.Error("Destination problem.", ex.InnerException);
                PauseFromWorker(ex.IsDiskFull
                    ? "The destination is full. Free up space, then press Resume."
                    : $"The destination folder cannot be reached: {ex.InnerException?.Message ?? ex.Message} Check the drive or network connection, then press Resume.");
                return;
            }
            catch (DestinationException ex)
            {
                // Affects this file only, e.g. the old version is open in another program.
                file.SetBytesDone(0);
                file.State = DownloadFileState.Failed;
                file.Error = $"The file could not be saved: {ex.InnerException?.Message ?? ex.Message}";
                AppLog.Error("A file could not be saved.", ex.InnerException);
                return;
            }
            catch (Exception ex) when (IsSignInProblem(ex))
            {
                bool signedIn;
                try
                {
                    signedIn = await SignInAgainAsync(signInGeneration, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    file.State = DownloadFileState.Queued;
                    return;
                }

                if (!signedIn)
                {
                    file.State = DownloadFileState.Queued;
                    return;
                }
            }
            catch (Exception ex) when (IsTransient(ex, cancellationToken))
            {
                AppLog.Warn($"Network problem, retrying in {delay.TotalSeconds:0} s: {ex.GetType().Name}: {ex.Message}");
                Interlocked.Increment(ref _waitingForNetwork);
                RaiseStateChanged();
                try
                {
                    await Task.Delay(delay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    file.State = DownloadFileState.Queued;
                    return;
                }
                finally
                {
                    Interlocked.Decrement(ref _waitingForNetwork);
                    RaiseStateChanged();
                }

                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.MaxRetryDelay.Ticks));
            }
            catch (Exception ex)
            {
                file.SetBytesDone(0); // the overall progress shows only what is really there
                file.State = DownloadFileState.Failed;
                file.Error = Describe(ex);
                AppLog.Error("A file failed.", ex);
                return;
            }
        }
    }

    private async Task<bool> SignInAgainAsync(int generationAtFailure, CancellationToken cancellationToken)
    {
        await _signInGate.WaitAsync(cancellationToken);
        try
        {
            // The job may have been paused (e.g. the user declined to sign in) while this file waited.
            cancellationToken.ThrowIfCancellationRequested();

            // Another file already got the user signed in again while this one waited.
            if (Volatile.Read(ref _signInGeneration) != generationAtFailure)
            {
                return true;
            }

            _state = DownloadJobState.WaitingForSignIn;
            RaiseStateChanged();
            var signedIn = await _reauthenticate(cancellationToken);
            if (signedIn)
            {
                _reader.Reset();
                Interlocked.Increment(ref _signInGeneration);
            }
            else
            {
                // Pause before releasing the gate, so files waiting for it do not ask again.
                PauseFromWorker("You need to sign in again to continue. Press Resume to sign in.");
            }

            if (_state == DownloadJobState.WaitingForSignIn)
            {
                _state = DownloadJobState.Running;
                RaiseStateChanged();
            }

            return signedIn;
        }
        finally
        {
            _signInGate.Release();
        }
    }

    private async Task DownloadFileAsync(DownloadFile file, CancellationToken cancellationToken)
    {
        var finalPath = file.LocalPath;
        var partialPath = finalPath + ResumePlanner.PartialSuffix;
        var statePath = finalPath + ResumePlanner.StateSuffix;

        if (file.RefreshBeforeDownload)
        {
            file.Item = file.Item with { Blob = await _reader.GetPropertiesAsync(file.Item.Container, file.Blob.Name, cancellationToken) };
            file.RefreshBeforeDownload = false;
        }

        var decision = Io(() => ResumePlanner.Inspect(file.Blob, finalPath));
        if (decision is ResumeDecision.AlreadyComplete)
        {
            file.SetBytesDone(file.Blob.Size);
            file.State = DownloadFileState.AlreadyThere;
            return;
        }

        Io(() => Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!));
        var offset = decision is ResumeDecision.Resume resume ? resume.Offset : 0;

        for (var restart = 0; ; restart++)
        {
            try
            {
                await TransferAsync(file, partialPath, statePath, offset, cancellationToken);
                break;
            }
            catch (BlobChangedException) when (restart < _options.MaxChangedRestarts)
            {
                AppLog.Info("A blob changed during the download; starting that file over.");
                var current = await _reader.GetPropertiesAsync(file.Item.Container, file.Blob.Name, cancellationToken);
                file.Item = file.Item with { Blob = current };
                Io(() => PartialStateFile.Delete(partialPath));
                offset = 0;
            }
        }

        var lastModified = file.Blob.LastModified.UtcDateTime;
        Io(() =>
        {
            File.SetLastWriteTimeUtc(partialPath, lastModified);
            File.Move(partialPath, finalPath, overwrite: true);
            File.Delete(statePath);
        });
        file.State = DownloadFileState.Completed;
    }

    /// <summary>Downloads the blob into the partial file from <paramref name="offset"/> and verifies it.</summary>
    private async Task TransferAsync(DownloadFile file, string partialPath, string statePath, long offset, CancellationToken cancellationToken)
    {
        var blob = file.Blob;
        using var md5 = blob.HasContentMd5 ? IncrementalHash.CreateHash(HashAlgorithmName.MD5) : null;

        var stream = Io(() => new FileStream(partialPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1 << 16, FileOptions.Asynchronous));
        await using (stream)
        {
            if (offset > 0 && md5 is not null)
            {
                await IoAsync(() => HashExistingAsync(stream, offset, md5, cancellationToken));
            }

            Io(() =>
            {
                stream.SetLength(offset);
                stream.Position = offset;
                PartialStateFile.Write(statePath, new PartialState(blob.ETag, blob.Size, offset));
            });
            file.SetBytesDone(offset);

            var written = offset;
            var next = offset;
            var lastSave = Stopwatch.GetTimestamp();
            var pending = new Queue<Task<Chunk>>();
            using var chunkCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                while (true)
                {
                    // Keep up to N parts in flight; write them strictly in order as the oldest completes.
                    while (pending.Count < Math.Max(1, _options.MaxConcurrentChunksPerFile) && next < blob.Size)
                    {
                        var length = (int)Math.Min(_options.ChunkSize, blob.Size - next);
                        pending.Enqueue(ReadChunkAsync(file, blob, next, length, chunkCancellation.Token));
                        next += length;
                    }

                    if (!pending.TryDequeue(out var oldest))
                    {
                        break;
                    }

                    var chunk = await oldest;
                    try
                    {
                        await IoAsync(() => stream.WriteAsync(chunk.Memory, cancellationToken).AsTask());
                        md5?.AppendData(chunk.Memory.Span);
                    }
                    finally
                    {
                        _buffers.Return(chunk.Buffer);
                    }

                    written += chunk.Length;
                    file.AddBytes(chunk.Length);

                    if (Stopwatch.GetElapsedTime(lastSave) >= _options.SaveProgressInterval)
                    {
                        SaveProgress(stream, statePath, blob, written);
                        lastSave = Stopwatch.GetTimestamp();
                    }
                }
            }
            catch
            {
                await chunkCancellation.CancelAsync();
                while (pending.TryDequeue(out var task))
                {
                    try
                    {
                        _buffers.Return((await task).Buffer);
                    }
                    catch
                    {
                        // Cancelled or failed together with the first error.
                    }
                }

                try
                {
                    SaveProgress(stream, statePath, blob, written);
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"Could not record download progress: {ex.Message}");
                }

                throw;
            }

            Io(() => stream.Flush(flushToDisk: true));
            if (stream.Length != blob.Size)
            {
                throw new IntegrityException($"The downloaded file has {stream.Length:N0} bytes instead of {blob.Size:N0}.");
            }
        }

        if (md5 is not null)
        {
            if (!md5.GetHashAndReset().AsSpan().SequenceEqual(blob.ContentMd5))
            {
                Io(() => PartialStateFile.Delete(partialPath));
                throw new IntegrityException("The downloaded file does not match its MD5 checksum. Use Retry to download it again.");
            }

            file.VerifiedWithMd5 = true;
        }
    }

    private async Task<Chunk> ReadChunkAsync(DownloadFile file, RemoteBlob blob, long offset, int length, CancellationToken cancellationToken)
    {
        var buffer = _buffers.Rent(length);
        try
        {
            await _reader.ReadRangeAsync(file.Item.Container, blob.Name, blob.ETag, offset, buffer.AsMemory(0, length), cancellationToken);
            Interlocked.Add(ref _transferredBytes, length);
            return new Chunk(buffer, length);
        }
        catch
        {
            _buffers.Return(buffer);
            throw;
        }
    }

    /// <summary>Flushes the written bytes to disk, then records how far the file is, for resuming.</summary>
    private static void SaveProgress(FileStream stream, string statePath, RemoteBlob blob, long written) => Io(() =>
    {
        stream.Flush(flushToDisk: true);
        PartialStateFile.Write(statePath, new PartialState(blob.ETag, blob.Size, written));
    });

    private static async Task HashExistingAsync(FileStream stream, long length, IncrementalHash md5, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            stream.Position = 0;
            var remaining = length;
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                if (read == 0)
                {
                    throw new EndOfStreamException("The partial file is shorter than recorded.");
                }

                md5.AppendData(buffer, 0, read);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static T Io<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DestinationException(ex.Message, ex);
        }
    }

    private static void Io(Action action) => Io(() =>
    {
        action();
        return true;
    });

    private static async Task IoAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DestinationException(ex.Message, ex);
        }
    }

    private static IEnumerable<Exception> Chain(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }

    internal static bool IsSignInProblem(Exception ex) =>
        Chain(ex).Any(e => e is SignInRequiredException or RequestFailedException { Status: 401 });

    private bool IsDestinationReachable()
    {
        try
        {
            return Directory.Exists(Destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool IsTransient(Exception ex, CancellationToken cancellationToken) =>
        Chain(ex).Any(e => e switch
        {
            RequestFailedException r => r.Status is 0 or 408 or 429 or 500 or 502 or 503 or 504,
            MsalServiceException m => m.StatusCode is 408 or 429 or >= 500,
            MsalClientException { ErrorCode: MsalError.RequestTimeout } => true,
            HttpRequestException or IOException or TimeoutException => true,
            OperationCanceledException => !cancellationToken.IsCancellationRequested, // a request timed out
            _ => false,
        });

    private static string Describe(Exception ex) => ex switch
    {
        IntegrityException => ex.Message,
        BlobChangedException => "The file kept changing on the server during the download.",
        RequestFailedException { Status: 404 } => "The file is no longer in the shared folder.",
        _ => ErrorTranslator.Translate(ex).Message,
    };

    private void RaiseStateChanged() => StateChanged?.Invoke();

    private readonly record struct Chunk(byte[] Buffer, int Length)
    {
        public Memory<byte> Memory => Buffer.AsMemory(0, Length);
    }
}
