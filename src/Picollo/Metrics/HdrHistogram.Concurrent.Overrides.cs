using System;
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
            foreach ((_, HdrHistogram<T>? histogram) in _children)
            {
                acc += histogram?.OverflowCount ?? 0;
            }

            if (_deadAccumulator is { } da)
                acc += da.OverflowCount;
            
            return acc;
        }
    }

    internal int ChildrenCount
    {
        get
        {
            var count = 0;
            foreach ((_, HdrHistogram<T>? histogram) in _children)
            {
                if (histogram is not null)
                    count++;
            }

            return count;
        }
    }

    public override int FootprintInBytes =>
        (ChildrenCount + 1 /*acc*/ + (_deadAccumulator is null ? 0 : 1)) * _accumulator.FootprintInBytes;

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

    public override void Reset()
    {
        lock (_accumulator)
        {
            _accumulator.Reset();
            _deadAccumulator = null;
            foreach ((_, HdrHistogram<T>? histogram) in _children)
            {
                histogram?.Reset();
            }
        }
    }
}