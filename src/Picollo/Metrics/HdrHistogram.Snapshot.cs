using System;
using System.Threading;

namespace Picollo.Metrics;

public sealed class HdrHistogramSnapshot : ReadOnlyHdrHistogram, IDisposable
{
    public HdrHistogram Histogram { get; }
    private HdrHistogram? _snapshot;
    private long _version;
    public DateTime Timestamp { get; private set; }

    internal HdrHistogramSnapshot(HdrHistogram histogram, HdrHistogram snapshot)
    {
        Histogram = histogram;
        _snapshot = snapshot;
        _version = snapshot.Version;
        Timestamp = DateTime.UtcNow;
    }

    /// <summary>
    /// Copies the current counters of the <see cref="Histogram"/>.
    /// </summary>
    public void Update(bool deltas)
    {
        var current = GetOwnedSnapshot();
        var updated = Histogram.GetSnapshotInternal(deltas ? current : null);
        var previous = Interlocked.Exchange(ref _snapshot, updated);

        if (!ReferenceEquals(previous, updated))
            previous?.Dispose();

        _version = updated.Version;
        Timestamp = DateTime.UtcNow;
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

    public override HdrHistogramSummary GetSummary(HdrHistogramSummary? reuseInstance = null) =>
        GetOwnedSnapshot().GetSummary(reuseInstance);

    public override HdrHistogramSnapshot GetSnapshot()
    {
        var snapshot = GetOwnedSnapshot();
        return new HdrHistogramSnapshot(snapshot, snapshot.GetSnapshotInternal());
    }

    public void Dispose()
    {
        var snapshot = Interlocked.Exchange(ref _snapshot, null);
        snapshot?.Dispose();
    }

    private HdrHistogram GetOwnedSnapshot()
    {
        var snapshot = Volatile.Read(ref _snapshot);
        ObjectDisposedException.ThrowIf(snapshot is null, this);
        return snapshot;
    }
}
