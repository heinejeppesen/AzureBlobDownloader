using System.Security.Cryptography;
using Azure;
using AzureBlobDownloader.Core.Auth;
using AzureBlobDownloader.Core.Downloads;
using AzureBlobDownloader.Core.Urls;

namespace AzureBlobDownloader.Core.Tests.Downloads;

public sealed class DownloadJobTests : IDisposable
{
    private static readonly BlobTarget Container = Link("https://cda1.blob.core.windows.net/report-1");

    private static readonly DownloadOptions FastOptions = new()
    {
        MaxConcurrentFiles = 2,
        MaxConcurrentChunksPerFile = 3,
        ChunkSize = 10,
        FirstRetryDelay = TimeSpan.FromMilliseconds(1),
        MaxRetryDelay = TimeSpan.FromMilliseconds(5),
        SaveProgressInterval = TimeSpan.Zero,
    };

    private readonly string _destination = Path.Combine(Path.GetTempPath(), "abd-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeBlobReader _reader = new();
    private int _reauthenticateCalls;

    public DownloadJobTests() => Directory.CreateDirectory(_destination);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_destination, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static BlobTarget Link(string url)
    {
        Assert.True(BlobUrlParser.TryParse(url, ["cda*.blob.core.windows.net"], out var target));
        return target;
    }

    private static byte[] Bytes(int length, int seed = 1) => [.. Enumerable.Range(0, length).Select(i => (byte)((i * 7) + seed))];

    private DownloadJob CreateJob(Func<CancellationToken, Task<bool>>? reauthenticate = null, DownloadOptions? options = null, params string[] names)
    {
        var plan = DownloadPlanner.Map(names.Select(n => (Container, _reader.Describe(n))), Container, new PlanOptions(LocalNames.Current, CaseSensitive: false));
        return new DownloadJob(plan.Items, _destination, _reader, options ?? FastOptions, reauthenticate ?? (_ =>
        {
            Interlocked.Increment(ref _reauthenticateCalls);
            return Task.FromResult(true);
        }));
    }

    private string LocalPath(string blobName) => Path.Combine([_destination, "report-1", .. blobName.Split('/')]);

    private static async Task RunToEndAsync(DownloadJob job)
    {
        job.Start();
        await job.WhenStoppedAsync().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Downloads_in_parts_verifies_md5_and_keeps_the_modified_time()
    {
        _reader.Add("big.bin", Bytes(95)).Add("Folder/small.txt", Bytes(7, seed: 3)).Add("Folder/empty.dat", []);
        var job = CreateJob(names: ["big.bin", "Folder/small.txt", "Folder/empty.dat"]);

        await RunToEndAsync(job);

        Assert.Equal(DownloadJobState.Completed, job.State);
        Assert.All(job.Files, f => Assert.Equal(DownloadFileState.Completed, f.State));
        Assert.All(job.Files, f => Assert.True(f.VerifiedWithMd5));
        Assert.Equal(_reader.Data("big.bin"), File.ReadAllBytes(LocalPath("big.bin")));
        Assert.Equal(_reader.Data("Folder/small.txt"), File.ReadAllBytes(LocalPath("Folder/small.txt")));
        Assert.Empty(File.ReadAllBytes(LocalPath("Folder/empty.dat")));
        Assert.Equal(_reader.Describe("big.bin").LastModified.UtcDateTime, File.GetLastWriteTimeUtc(LocalPath("big.bin")));
        Assert.Equal(10, _reader.Reads.Count(r => r.Name == "big.bin"));
        Assert.Empty(Directory.GetFiles(_destination, "*.partial*", SearchOption.AllDirectories));
        Assert.Equal(job.TotalBytes, job.Files.Sum(f => f.BytesDone));
    }

    [Fact]
    public async Task Without_md5_the_size_is_checked()
    {
        _reader.Add("a.bin", Bytes(33), withMd5: false);
        var job = CreateJob(names: ["a.bin"]);

        await RunToEndAsync(job);

        Assert.Equal(DownloadFileState.Completed, job.Files[0].State);
        Assert.False(job.Files[0].VerifiedWithMd5);
    }

    [Fact]
    public async Task Md5_mismatch_fails_the_file_and_removes_the_partial()
    {
        _reader.Add("a.bin", Bytes(33), md5Override: MD5.HashData([1, 2, 3])).Add("ok.bin", Bytes(5));
        var job = CreateJob(names: ["a.bin", "ok.bin"]);

        await RunToEndAsync(job);

        var failed = job.Files.Single(f => f.Blob.Name == "a.bin");
        Assert.Equal(DownloadFileState.Failed, failed.State);
        Assert.Contains("MD5", failed.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(LocalPath("a.bin")));
        Assert.False(File.Exists(LocalPath("a.bin") + ResumePlanner.PartialSuffix));
        Assert.Equal(DownloadFileState.Completed, job.Files.Single(f => f.Blob.Name == "ok.bin").State);
        Assert.Equal(DownloadJobState.Completed, job.State);
    }

    [Fact]
    public async Task Continues_a_partial_file_from_the_recorded_offset()
    {
        var data = Bytes(95);
        _reader.Add("big.bin", data);
        var blob = _reader.Describe("big.bin");
        var path = LocalPath("big.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path + ResumePlanner.PartialSuffix, [.. data.Take(40), .. new byte[13]]); // 13 bytes of junk after the valid part
        PartialStateFile.Write(path + ResumePlanner.StateSuffix, new PartialState(blob.ETag, blob.Size, 40));

        var job = CreateJob(names: ["big.bin"]);
        await RunToEndAsync(job);

        Assert.Equal(DownloadFileState.Completed, job.Files[0].State);
        Assert.True(job.Files[0].VerifiedWithMd5);
        Assert.Equal(data, File.ReadAllBytes(path));
        Assert.All(_reader.Reads, r => Assert.True(r.Offset >= 40));
        Assert.Equal(55, _reader.Reads.Sum(r => r.Length));
    }

    [Fact]
    public async Task A_partial_file_of_another_version_starts_over()
    {
        var data = Bytes(30);
        _reader.Add("a.bin", data);
        var path = LocalPath("a.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path + ResumePlanner.PartialSuffix, new byte[20]);
        PartialStateFile.Write(path + ResumePlanner.StateSuffix, new PartialState("\"old\"", 30, 20));

        await RunToEndAsync(CreateJob(names: ["a.bin"]));

        Assert.Equal(data, File.ReadAllBytes(path));
        Assert.Contains(_reader.Reads, r => r.Offset == 0);
    }

    [Fact]
    public async Task Finished_files_are_skipped_without_reading()
    {
        var data = Bytes(30);
        _reader.Add("a.bin", data);
        var path = LocalPath("a.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        File.SetLastWriteTimeUtc(path, _reader.Describe("a.bin").LastModified.UtcDateTime);

        var job = CreateJob(names: ["a.bin"]);
        await RunToEndAsync(job);

        Assert.Equal(DownloadFileState.AlreadyThere, job.Files[0].State);
        Assert.Empty(_reader.Reads);
        Assert.Equal(30, job.Files[0].BytesDone);
    }

    [Fact]
    public async Task A_blob_changed_during_the_download_is_downloaded_again_in_its_new_version()
    {
        _reader.Add("a.bin", Bytes(50));
        var replaced = 0;
        var newData = Bytes(64, seed: 9);
        _reader.BeforeRead = (_, offset) =>
        {
            if (offset >= 20 && Interlocked.Exchange(ref replaced, 1) == 0)
            {
                _reader.Add("a.bin", newData);
            }
        };

        var job = CreateJob(names: ["a.bin"]);
        await RunToEndAsync(job);

        Assert.Equal(DownloadFileState.Completed, job.Files[0].State);
        Assert.Equal(newData, File.ReadAllBytes(LocalPath("a.bin")));
        Assert.Equal(_reader.Describe("a.bin").ETag, job.Files[0].Blob.ETag);
    }

    [Fact]
    public async Task Network_failures_are_retried_until_the_file_completes()
    {
        _reader.Add("a.bin", Bytes(45));
        var failures = 0;
        _reader.BeforeRead = (_, _) =>
        {
            if (Interlocked.Increment(ref failures) <= 4)
            {
                throw new RequestFailedException(503, "Server busy");
            }
        };

        var job = CreateJob(names: ["a.bin"]);
        await RunToEndAsync(job);

        Assert.Equal(DownloadFileState.Completed, job.Files[0].State);
        Assert.Equal(_reader.Data("a.bin"), File.ReadAllBytes(LocalPath("a.bin")));
        Assert.False(job.IsWaitingForNetwork);
    }

    [Fact]
    public async Task An_expired_sign_in_asks_once_and_all_files_continue()
    {
        _reader.Add("a.bin", Bytes(45)).Add("b.bin", Bytes(45, seed: 2)).Add("c.bin", Bytes(45, seed: 3));
        var signedIn = 0;
        _reader.BeforeRead = (_, _) =>
        {
            if (Volatile.Read(ref signedIn) == 0)
            {
                throw new SignInRequiredException();
            }
        };

        var job = CreateJob(
            reauthenticate: async _ =>
            {
                Interlocked.Increment(ref _reauthenticateCalls);
                await Task.Delay(20);
                Volatile.Write(ref signedIn, 1);
                return true;
            },
            names: ["a.bin", "b.bin", "c.bin"]);
        await RunToEndAsync(job);

        Assert.Equal(1, _reauthenticateCalls);
        Assert.Equal(1, _reader.ResetCalls);
        Assert.All(job.Files, f => Assert.Equal(DownloadFileState.Completed, f.State));
    }

    [Fact]
    public async Task Storage_401_is_treated_as_an_expired_sign_in()
    {
        _reader.Add("a.bin", Bytes(25));
        var rejected = 0;
        _reader.BeforeRead = (_, _) =>
        {
            if (Interlocked.Exchange(ref rejected, 1) == 0)
            {
                throw new RequestFailedException(401, "Server failed to authenticate the request.");
            }
        };

        var job = CreateJob(names: ["a.bin"]);
        await RunToEndAsync(job);

        Assert.Equal(1, _reauthenticateCalls);
        Assert.Equal(DownloadFileState.Completed, job.Files[0].State);
    }

    [Fact]
    public async Task Declining_to_sign_in_pauses_the_job_and_resume_continues()
    {
        _reader.Add("a.bin", Bytes(25)).Add("b.bin", Bytes(25, seed: 2)).Add("c.bin", Bytes(25, seed: 3));
        var signedIn = false;
        var answer = false;
        _reader.BeforeRead = (_, _) =>
        {
            if (!Volatile.Read(ref signedIn))
            {
                throw new SignInRequiredException();
            }
        };

        var job = CreateJob(
            reauthenticate: _ =>
            {
                Interlocked.Increment(ref _reauthenticateCalls);
                Volatile.Write(ref signedIn, answer);
                return Task.FromResult(answer);
            },
            names: ["a.bin", "b.bin", "c.bin"]);

        await RunToEndAsync(job);
        Assert.Equal(DownloadJobState.Paused, job.State);
        Assert.Contains("sign in", job.PauseReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, _reauthenticateCalls);
        Assert.All(job.Files, f => Assert.Equal(DownloadFileState.Queued, f.State));

        answer = true;
        await RunToEndAsync(job);
        Assert.Equal(DownloadJobState.Completed, job.State);
        Assert.Null(job.PauseReason);
        Assert.All(job.Files, f => Assert.Equal(DownloadFileState.Completed, f.State));
    }

    [Fact]
    public async Task Pause_keeps_partial_files_and_resume_finishes()
    {
        _reader.Add("a.bin", Bytes(60));
        _reader.ReadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = CreateJob(names: ["a.bin"]);

        job.Start();
        await _reader.FirstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await job.PauseAsync();

        Assert.Equal(DownloadJobState.Paused, job.State);
        Assert.Null(job.PauseReason);
        Assert.Equal(DownloadFileState.Queued, job.Files[0].State);
        Assert.True(File.Exists(LocalPath("a.bin") + ResumePlanner.PartialSuffix));

        _reader.ReadGate.SetResult();
        await RunToEndAsync(job);
        Assert.Equal(DownloadJobState.Completed, job.State);
        Assert.Equal(_reader.Data("a.bin"), File.ReadAllBytes(LocalPath("a.bin")));
    }

    [Fact]
    public async Task Cancel_removes_partial_files()
    {
        _reader.Add("a.bin", Bytes(60));
        _reader.ReadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = CreateJob(names: ["a.bin"]);

        job.Start();
        await _reader.FirstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await job.CancelAsync();

        Assert.Equal(DownloadJobState.Cancelled, job.State);
        Assert.Empty(Directory.GetFiles(_destination, "*", SearchOption.AllDirectories));

        job.Start();
        Assert.Equal(DownloadJobState.Cancelled, job.State);
    }

    [Fact]
    public async Task A_file_that_cannot_be_saved_fails_alone()
    {
        _reader.Add("blocked.bin", Bytes(20)).Add("fine.bin", Bytes(20, seed: 4));
        Directory.CreateDirectory(LocalPath("blocked.bin")); // a folder where the file should go

        var job = CreateJob(names: ["blocked.bin", "fine.bin"]);
        await RunToEndAsync(job);

        Assert.Equal(DownloadJobState.Completed, job.State);
        Assert.Equal(DownloadFileState.Failed, job.Files.Single(f => f.Blob.Name == "blocked.bin").State);
        Assert.StartsWith("The file could not be saved", job.Files.Single(f => f.Blob.Name == "blocked.bin").Error, StringComparison.Ordinal);
        Assert.Equal(DownloadFileState.Completed, job.Files.Single(f => f.Blob.Name == "fine.bin").State);
    }

    [Fact]
    public async Task Retry_failed_looks_the_blob_up_again()
    {
        var data = Bytes(20);
        _reader.Add("a.bin", data, md5Override: MD5.HashData([9]));
        var job = CreateJob(names: ["a.bin"]);
        await RunToEndAsync(job);
        Assert.Equal(DownloadFileState.Failed, job.Files[0].State);

        _reader.Add("a.bin", data); // fixed on the server: correct MD5, new version
        job.RetryFailed();
        await job.WhenStoppedAsync().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(DownloadFileState.Completed, job.Files[0].State);
        Assert.True(job.Files[0].VerifiedWithMd5);
    }

    [Fact]
    public async Task Parts_in_flight_stay_within_the_configured_limits()
    {
        for (var i = 0; i < 6; i++)
        {
            _reader.Add($"f{i}.bin", Bytes(200, seed: i));
        }

        var job = CreateJob(names: [.. Enumerable.Range(0, 6).Select(i => $"f{i}.bin")]);
        await RunToEndAsync(job);

        Assert.All(job.Files, f => Assert.Equal(DownloadFileState.Completed, f.State));
        Assert.InRange(_reader.MaxConcurrentReads, 1, FastOptions.MaxConcurrentFiles * FastOptions.MaxConcurrentChunksPerFile);
    }
}
