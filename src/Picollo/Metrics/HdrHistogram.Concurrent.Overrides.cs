using System.Numerics;

namespace Picollo.Metrics;

public sealed partial class ConcurrentHdrHistogram<T> : HdrHistogram
    where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>, IAdditionOperators<T, T, T>, IAdditiveIdentity<T, T>
{
    public override ulong OverflowCount
    {
        get
        {
            ulong acc = 0ul;
            foreach ((_, HdrHistogram<T> histogram) in _children)
            {
                acc += histogram.OverflowCount;
            }

            return acc;
        }
    }

    public override int FootprintInBytes => (_children.Count + 1) * _accumulator.FootprintInBytes;

    public override void Record(ulong value)
    {
        GetLocalHistogram().Record(value);
    }

    public override void Record(ulong value, uint count)
    {
        GetLocalHistogram().Record(value, count);
    }

    public override ulong GetPercentileValue(double rank, EquivalentValueSelection valueSelection = default)
    {
        Accumulate();
        return _accumulator.GetPercentileValue(rank, valueSelection);
    }

    public override Percentile GetPercentile(double rank)
    {
        Accumulate();
        return _accumulator.GetPercentile(rank);
    }

    public override Bucket GetBucket(ulong value)
    {
        Accumulate();
        return _accumulator.GetBucket(value);
    }

    public override void Reset()
    {
        _accumulator.Reset();
        foreach ((_, HdrHistogram<T> histogram) in _children)
        {
            histogram.Reset();
        }
    }
}