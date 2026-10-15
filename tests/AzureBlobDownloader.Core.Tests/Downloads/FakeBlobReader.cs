using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using AzureBlobDownloader.Core.Downloads;
using AzureBlobDownloader.Core.Urls;

namespace AzureBlobDownloader.Core.Tests.Downloads;

/// <summary>In-memory blob service for one container, with hooks to inject failures and delays.</summary>
internal sealed class FakeBlobReader : IBlobReader
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Blob> _blobs = new(StringComparer.Ordinal);
    private readonly List<(string Name, long Offset, int Length)> _reads = [];
    private int _version;
    private int _inFlight;

    public static readonly DateTimeOffset Modified = new(2026, 3, 14, 15, 9, 26, TimeSpan.Zero);

    /// <summary>Called before every read; may throw to simulate a failure.</summary>
    public Action<string, long>? BeforeRead { get; set; }

    /// <summary>When set, reads wait for it (to simulate a slow service).</summary>
    public TaskCompletionSource? ReadGate { get; set; }

    /// <summary>Completed when the first read starts.</summary>
    public TaskCompletionSource FirstReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int ResetCalls { get; private set; }

    public int MaxConcurrentReads { get; private set; }

    public IReadOnlyList<(string Name, long Offset, int Length)> Reads
    {
        get
        {
            lock (_gate)
            {
                return [.. _reads];
            }
        }
    }

    public FakeBlobReader Add(string name, byte[] data, bool withMd5 = true, byte[]? md5Override = null)
    {
        lock (_gate)
        {
            _blobs[name] = new Blob(data, $"\"0x{++_version:X}\"", Modified.AddMinutes(_version), md5Override ?? (withMd5 ? MD5.HashData(data) : null));
        }

        return this;
    }

    public RemoteBlob Describe(string name)
    {
        lock (_gate)
        {
            var b = _blobs[name];
            return new RemoteBlob(name, b.Data.Length, b.ETag, b.Modified, b.Md5);
        }
    }

    public byte[] Data(string name)
    {
        lock (_gate)
        {
            return _blobs[name].Data;
        }
    }

    public async IAsyncEnumerable<RemoteBlob> EnumerateAsync(BlobTarget container, string prefix, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        string[] names;
        lock (_gate)
        {
            names = [.. _blobs.Keys.Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal)];
        }

        foreach (var name in names)
        {
            yield return Describe(name);
        }
    }

    public Task<RemoteBlob> GetPropertiesAsync(BlobTarget container, string blobName, CancellationToken cancellationToken) =>
        Task.FromResult(Describe(blobName));

    public async Task ReadRangeAsync(BlobTarget container, string blobName, string etag, long offset, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _reads.Add((blobName, offset, buffer.Length));
            MaxConcurrentReads = Math.Max(MaxConcurrentReads, ++_inFlight);
        }

        try
        {
            FirstReadStarted.TrySetResult();
            if (ReadGate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            await Task.Yield();
            BeforeRead?.Invoke(blobName, offset);

            Blob blob;
            lock (_gate)
            {
                blob = _blobs[blobName];
            }

            if (blob.ETag != etag)
            {
                throw new BlobChangedException(blobName);
            }

            blob.Data.AsMemory((int)offset, buffer.Length).CopyTo(buffer);
        }
        finally
        {
            lock (_gate)
            {
                _inFlight--;
            }
        }
    }

    public void Reset() => ResetCalls++;

    private sealed record Blob(byte[] Data, string ETag, DateTimeOffset Modified, byte[]? Md5);
}
