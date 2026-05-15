using System;
using System.Threading;

namespace Picollo.Metrics;

public sealed class HdrHistogramSnapshot : ReadOnlyHdrHistogram, IDisposable
{
    private readonly HdrHistogram _liveSource;
    private HdrHistogram _lastBaseLine;
    private HdrHistogram? _deltaSnapshot;

    private HdrHistogram _current;

    public DateTime TimestampUtc { get; private set; }

    internal HdrHistogramSnapshot(HdrHistogram liveSource, HdrHistogram lastBaseLine)
    {
        _liveSource = liveSource;
        _lastBaseLine = lastBaseLine;
        _current = lastBaseLine;
        TimestampUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Updates this snapshot with fresh data from the source.
    /// </summary>
    public void Update(bool deltas)
    {
        ObjectDisposedException.ThrowIf(_lastBaseLine is null, this);
        
        var newBaseLine = _liveSource.GetSnapshotInternal();

        if (deltas && newBaseLine.ResetCount == _lastBaseLine.ResetCount)
        {
            if (_deltaSnapshot is { } deltaSnapshot)
            {
                deltaSnapshot.Reset();
                deltaSnapshot.Add(newBaseLine);
            }
            else
            {
                _deltaSnapshot = deltaSnapshot = newBaseLine.GetSnapshotInternal();
            }

            deltaSnapshot.Subtract(_lastBaseLine);
            _current = deltaSnapshot;
        }
        else
        {
            _current = newBaseLine;
        }

        Interlocked.Exchange(ref _lastBaseLine, newBaseLine).Dispose();

        TimestampUtc = DateTime.UtcNow;
    }

    public override ulong OverflowCount => GetOwnedSnapshot().OverflowCount;

    public override ulong TotalCount => GetOwnedSnapshot().TotalCount;

    public override int FootprintInBytes => GetOwnedSnapshot().FootprintInBytes;

    public override ulong GetPercentileValue(double rank, EquivalentValueSelection valueSelection = default) =>
        GetOwnedSnapshot().GetPercentileValue(rank, valueSelection);

    public override void GetPercentileValues(ReadOnlySpan<double> sortedRanks, Span<ulong> values,
        EquivalentValueSelection valueSelection = default) =>
        GetOwnedSnapshot().GetPercentileValues(sortedRanks, values, valueSelection);

    public override void GetPercentiles(ReadOnlySpan<double> sortedRanks, Span<Percentile> percentiles) =>
        GetOwnedSnapshot().GetPercentiles(sortedRanks, percentiles);

    public override Percentile GetPercentile(double rank) => GetOwnedSnapshot().GetPercentile(rank);

    public override Bucket GetBucket(ulong value) => GetOwnedSnapshot().GetBucket(value);

    public override HdrHistogramSummary GetSummary(HdrHistogramSummary? reuseInstance = null)
    {
        HdrHistogramSummary summary = GetOwnedSnapshot().GetSummary(reuseInstance);
        summary.TimestampUtc = TimestampUtc;
        return summary;
    }

    public override HdrHistogramSnapshot GetSnapshot() => new(_liveSource, GetOwnedSnapshot().GetSnapshotInternal());

    public void Dispose()
    {
        // ReSharper disable once ConstantConditionalAccessQualifier
        Interlocked.Exchange(ref _lastBaseLine, null!)?.Dispose();
        Interlocked.Exchange(ref _deltaSnapshot, null)?.Dispose();
        _current = null!; // Current does not own its object
    }

    private HdrHistogram GetOwnedSnapshot()
    {
        var snapshot = Volatile.Read(ref _current);
        ObjectDisposedException.ThrowIf(snapshot is null, this);
        return snapshot;
    }
}