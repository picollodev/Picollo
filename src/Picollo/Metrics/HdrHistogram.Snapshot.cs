using System;
using System.Numerics;

namespace Picollo.Metrics;

public class HdrHistogramSnapshot : ReadOnlyHdrHistogram 
{
    public HdrHistogram Histogram { get; }
    private long _version;
    public DateTime Timestamp { get; private set; }

    public HdrHistogramSnapshot(HdrHistogram histogram)
    {
        Histogram = histogram;
        _version = histogram.Version;
    }

    /// <summary>
    /// Copies the current counters of the <see cref="Histogram"/>.
    /// </summary>
    /// <param name="deltas"></param>
    public void Update(bool deltas)
    {
        // TODO Must check version when using deltas
    }

    public override ulong OverflowCount => throw new NotImplementedException();

    public override int FootprintInBytes => Histogram.FootprintInBytes; // TODO Add own overheads

    public override ulong GetPercentileValue(double rank, EquivalentValueSelection valueSelection = default)
    {
        throw new NotImplementedException();
    }

    public override void GetPercentileValues(ReadOnlySpan<double> sortedRanks, Span<ulong> values,
        EquivalentValueSelection valueSelection = default)
    {
        throw new NotImplementedException();
    }

    public override void GetPercentiles(ReadOnlySpan<double> sortedRanks, Span<Percentile> percentiles)
    {
        throw new NotImplementedException();
    }

    public override Percentile GetPercentile(double rank)
    {
        throw new NotImplementedException();
    }

    public override Bucket GetBucket(ulong value)
    {
        throw new NotImplementedException();
    }
}
