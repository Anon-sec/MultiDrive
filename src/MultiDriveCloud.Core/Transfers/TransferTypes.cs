using System.Collections.Concurrent;
using MultiDriveCloud.Core.Models;
using MultiDriveCloud.Core.Util;

namespace MultiDriveCloud.Core.Transfers;

public readonly record struct ItemRef(bool IsFolder, Guid Id);

public sealed class TransferException : Exception
{
    public TransferException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class TransferManagerOptions
{
    public Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } = (d, ct) => Task.Delay(d, ct);
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromDays(6);
}

public sealed record TransferSnapshot(
    Guid TransferId,
    Guid? BatchId,
    Guid? FileId,
    TransferDirection Direction,
    string Name,
    TransferState State,
    long TotalBytes,
    long BytesDone,
    double BytesPerSecond,
    TimeSpan? Remaining,
    int RetryCount,
    string? ErrorMessage,
    string LocalPath,
    string Phase,
    double? PhaseProgress,
    string Destination,
    DateTime CreatedAt)
{
    public double Progress => TotalBytes <= 0 ? (State == TransferState.Completed ? 1 : 0) : Math.Min(1, (double)BytesDone / TotalBytes);
    public bool IsActive => State is TransferState.Queued or TransferState.Running or TransferState.Paused;
}

public sealed record TransferTotals(int Running, int Queued, int Paused, long TotalBytes, long DoneBytes, double BytesPerSecond, TimeSpan? Remaining)
{
    public int Active => Running + Queued + Paused;
    public double Progress => TotalBytes <= 0 ? 0 : Math.Min(1, (double)DoneBytes / TotalBytes);
}

public sealed record UploadSkip(string Path, string Reason);

public sealed record EnqueueResult(Guid BatchId, int FileCount, long TotalBytes, IReadOnlyList<UploadSkip> Skipped);

internal enum StopReason { None, Pause, Cancel, Shutdown }

internal sealed class ActiveTransfer
{
    public required TransferRecord Record { get; init; }
    public CancellationTokenSource? Cts { get; set; }
    public Task? RunTask { get; set; }
    public volatile StopReason Stop;
    public volatile string Phase = "";
    public double PhaseProgress = -1;
    public volatile string Destination = "";
    public SpeedMeter Meter { get; } = new();

    private long _completed;
    private readonly ConcurrentDictionary<Guid, long> _inflight = new();

    public long Done => Interlocked.Read(ref _completed) + _inflight.Values.Sum();

    public void ResetProgress(long completed)
    {
        Interlocked.Exchange(ref _completed, completed);
        _inflight.Clear();
        Meter.Reset();
    }

    public void ChunkProgress(Guid chunkId, long plainBytes)
    {
        long prev = 0;
        _inflight.AddOrUpdate(chunkId, plainBytes, (_, old) => { prev = old; return plainBytes; });
        var delta = plainBytes - prev;
        if (delta > 0) Meter.Add(delta);
    }

    public void ChunkCompleted(Guid chunkId, long plainSize)
    {
        _inflight.TryRemove(chunkId, out var old);
        var delta = plainSize - old;
        if (delta > 0) Meter.Add(delta);
        Interlocked.Add(ref _completed, plainSize);
    }

    public void ChunkReset(Guid chunkId) => _inflight.TryRemove(chunkId, out _);
}
