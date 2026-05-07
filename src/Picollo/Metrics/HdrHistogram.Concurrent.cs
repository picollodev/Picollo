using System.Numerics;

namespace Picollo.Metrics;

public sealed partial class ConcurrentHdrHistogram<T> : HdrHistogram
    where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
{
    // The assumption here is that the is one monitoring thread

    public override ulong OverflowCount
    {
        get
        {
            ulong acc = 0ul;
            foreach (var histogram in _children)
            {
                acc += histogram?.OverflowCount ?? 0;
            }

            if (_deadAccumulator is { } da)
                acc += da.OverflowCount;

            return acc;
        }
    }

    internal int GetChildrenCount()
    {
        var count = 0;
        foreach (var histogram in _children)
        {
            if (histogram is not null)
                count++;
        }

        return count;
    }

    public override int FootprintInBytes =>
        (GetChildrenCount() + 1 /*acc*/ + (_deadAccumulator is null ? 0 : 1)) * _accumulator.FootprintInBytes;

    public override void Record(ulong value)
    {
        // _children1.Value!.Record(value);
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
}