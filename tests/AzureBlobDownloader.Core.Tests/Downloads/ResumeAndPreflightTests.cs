using AzureBlobDownloader.Core.Downloads;
using AzureBlobDownloader.Core.Urls;

namespace AzureBlobDownloader.Core.Tests.Downloads;

public sealed class ResumeAndPreflightTests : IDisposable
{
    private static readonly DateTimeOffset Modified = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly RemoteBlob Blob = new("a.bin", 100, "\"v1\"", Modified, null);

    private readonly string _destination = Path.Combine(Path.GetTempPath(), "abd-tests", Guid.NewGuid().ToString("N"));

    public ResumeAndPreflightTests() => Directory.CreateDirectory(_destination);

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

    // ---- Resume decisions ------------------------------------------------------------------------

    [Fact]
    public void Same_size_and_time_is_complete()
    {
        var decision = ResumePlanner.Decide(Blob, new LocalFileState(100, Modified.UtcDateTime), null, null);

        Assert.IsType<ResumeDecision.AlreadyComplete>(decision);
    }

    [Fact]
    public void Times_within_two_seconds_count_as_equal()
    {
        var decision = ResumePlanner.Decide(Blob, new LocalFileState(100, Modified.UtcDateTime.AddSeconds(-2)), null, null);

        Assert.IsType<ResumeDecision.AlreadyComplete>(decision);
    }

    [Theory]
    [InlineData(99, 0)]   // other size
    [InlineData(100, 3)]  // other time (older version)
    public void Different_size_or_time_is_downloaded_again(long length, int secondsOff)
    {
        var decision = ResumePlanner.Decide(Blob, new LocalFileState(length, Modified.UtcDateTime.AddSeconds(secondsOff)), null, null);

        Assert.IsType<ResumeDecision.StartOver>(decision);
    }

    [Fact]
    public void Partial_of_the_same_version_resumes_at_the_recorded_offset()
    {
        var decision = ResumePlanner.Decide(Blob, null, new LocalFileState(64, DateTime.UtcNow), new PartialState("\"v1\"", 100, 48));

        Assert.Equal(new ResumeDecision.Resume(48), decision);
    }

    [Theory]
    [InlineData("\"v2\"", 100, 48, 64)] // other version
    [InlineData("\"v1\"", 120, 48, 64)] // other size
    [InlineData("\"v1\"", 100, 80, 64)] // recorded more than is on disk
    [InlineData("\"v1\"", 100, 0, 64)]  // nothing to keep
    [InlineData("\"v1\"", 100, 101, 200)] // more than the blob
    public void Partial_that_does_not_match_starts_over(string etag, long size, long valid, long partialLength)
    {
        var decision = ResumePlanner.Decide(Blob, null, new LocalFileState(partialLength, DateTime.UtcNow), new PartialState(etag, size, valid));

        Assert.IsType<ResumeDecision.StartOver>(decision);
    }

    [Fact]
    public void Partial_without_state_file_starts_over()
    {
        Assert.IsType<ResumeDecision.StartOver>(ResumePlanner.Decide(Blob, null, new LocalFileState(50, DateTime.UtcNow), null));
    }

    [Fact]
    public void State_file_round_trips_and_damaged_files_read_as_missing()
    {
        var path = Path.Combine(_destination, "x" + ResumePlanner.StateSuffix);
        PartialStateFile.Write(path, new PartialState("\"v1\"", 100, 48));
        Assert.Equal(new PartialState("\"v1\"", 100, 48), PartialStateFile.TryRead(path));

        File.WriteAllText(path, "{ not json");
        Assert.Null(PartialStateFile.TryRead(path));
        Assert.Null(PartialStateFile.TryRead(Path.Combine(_destination, "missing.json")));
    }

    // ---- Pre-flight ------------------------------------------------------------------------------

    private sealed class FixedSpace(long? free) : IDestinationProbe
    {
        public long? GetAvailableFreeSpace(string folder) => free;
    }

    private static readonly BlobTarget Container = BlobUrlParser.TryParse("https://cda1.blob.core.windows.net/report-1", ["cda*.blob.core.windows.net"], out var t) ? t : null!;

    private static DownloadPlan Plan(params RemoteBlob[] blobs) =>
        DownloadPlanner.Map(blobs.Select(b => (Container, b)), Container, new PlanOptions(LocalNames.Current, false));

    [Fact]
    public void Counts_bytes_still_needed_and_files_already_there_or_partial()
    {
        var done = new RemoteBlob("done.bin", 30, "\"d\"", Modified, null);
        var partial = new RemoteBlob("partial.bin", 50, "\"p\"", Modified, null);
        var replace = new RemoteBlob("replace.bin", 40, "\"r\"", Modified, null);
        var fresh = new RemoteBlob("new.bin", 20, "\"n\"", Modified, null);
        var folder = Path.Combine(_destination, "report-1");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "done.bin"), new byte[30]);
        File.SetLastWriteTimeUtc(Path.Combine(folder, "done.bin"), Modified.UtcDateTime);
        File.WriteAllBytes(Path.Combine(folder, "partial.bin" + ResumePlanner.PartialSuffix), new byte[35]);
        PartialStateFile.Write(Path.Combine(folder, "partial.bin" + ResumePlanner.StateSuffix), new PartialState("\"p\"", 50, 35));
        File.WriteAllBytes(Path.Combine(folder, "replace.bin"), new byte[5]);

        var report = Preflight.Check(Plan(done, partial, replace, fresh), _destination, LocalNames.Current, new FixedSpace(1_000));

        Assert.Equal(1, report.AlreadyThere);
        Assert.Equal(1, report.Continuing);
        Assert.Equal(1, report.WillReplace);
        Assert.Equal(15 + 40 + 20, report.BytesNeeded);
        Assert.True(report.CanStart);
        Assert.True(report.NeedsAttention); // one file will be replaced
    }

    [Fact]
    public void Not_enough_free_space_blocks_the_start()
    {
        var report = Preflight.Check(Plan(new RemoteBlob("a.bin", 5_000, "\"a\"", Modified, null)), _destination, LocalNames.Current, new FixedSpace(4_000));

        Assert.False(report.CanStart);
        Assert.Contains("not enough free space", report.BlockingProblem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unknown_free_space_does_not_block()
    {
        var report = Preflight.Check(Plan(new RemoteBlob("a.bin", 5_000, "\"a\"", Modified, null)), _destination, LocalNames.Current, new FixedSpace(null));

        Assert.True(report.CanStart);
        Assert.False(report.NeedsAttention);
    }

    [Fact]
    public void Windows_paths_over_260_characters_are_flagged_but_downloaded()
    {
        var longName = string.Join('/', Enumerable.Repeat(new string('d', 60), 5)) + "/file.txt";
        var plan = DownloadPlanner.Map([(Container, new RemoteBlob(longName, 1, "\"a\"", Modified, null))], Container, new PlanOptions(NameRules.Windows, false));

        var report = Preflight.Check(plan, _destination, NameRules.Windows, new FixedSpace(null));

        Assert.Single(report.ToDownload);
        Assert.Equal(PlanIssueKind.LongPath, Assert.Single(report.Issues).Kind);
    }

    [Fact]
    public void Destination_probe_reports_writability_and_case_sensitivity()
    {
        var caseSensitive = LocalDestination.ProbeCaseSensitivity(_destination);

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            Assert.False(caseSensitive);
        }

        Assert.Empty(Directory.GetFiles(_destination));
        Assert.Throws<DestinationException>(() => LocalDestination.ProbeCaseSensitivity(Path.Combine(_destination, "does-not-exist")));
        Assert.True(new LocalDestination().GetAvailableFreeSpace(_destination) > 0);
    }
}
