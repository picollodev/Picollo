using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Picollo.Metrics;

public abstract partial class HdrHistogram : ReadOnlyHdrHistogram, IDisposable
{
    private protected readonly HdrBuckets _buckets;
    protected readonly nuint _firstIndexOffset;
    internal long Version;

    public ulong MinTrackableValue { get; protected set; }
    public ulong MaxTrackableValue { get; protected set; }


    protected HdrHistogram(double relativeError = 0.001, ulong minTrackableValue = 0, ulong maxTrackableValue = ulong.MaxValue)
    {
        if (minTrackableValue >= maxTrackableValue)
            throw new ArgumentException($"minTrackableValue [{minTrackableValue}] >= maxTrackableValue [{maxTrackableValue}]");

        _buckets = new HdrBuckets(relativeError);

        MinTrackableValue = minTrackableValue;
        MaxTrackableValue = maxTrackableValue;

        _firstIndexOffset = _buckets.GetIndex(minTrackableValue);
    }

    public double RelativeError => _buckets.RelativeError;

    public int BlockSize => _buckets.BlockSize;

    /// <summary>
    /// The number of counters available in the backing storage.
    /// </summary>
    internal int StorageLength
    {
        get
        {
            var lastVirtualIndex =
                (nuint)Math.Min((long)_buckets.GetIndex(MaxTrackableValue), _buckets.BlockSize * _buckets.BlockCount - 1);
            var storageSize = (int)(lastVirtualIndex + 1 - _firstIndexOffset);
            return storageSize;
        }
    }

    
    
    /// <summary>
    /// Record a single observation of the <paramref name="value"/>.
    /// </summary>
    public abstract void Record(ulong value);

    /// <summary>
    /// Record <paramref name="count"/> observations of the <paramref name="value"/>.
    /// </summary>
    public abstract void Record(ulong value, uint count);

    public abstract override void GetPercentileValues(ReadOnlySpan<double> sortedRanks, Span<ulong> values,
        EquivalentValueSelection valueSelection = default);
    

    public abstract void Reset();
    public abstract void Dispose();


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong TtoUlong<TCounter>(TCounter value) where TCounter : unmanaged, IBinaryInteger<TCounter>, IUnsignedNumber<TCounter>
    {
        ulong longValue;
        if (typeof(TCounter) == typeof(uint))
            longValue = (uint)(object)value;
        else if (typeof(TCounter) == typeof(ulong))
            longValue = (ulong)(object)value;
        else
            throw new NotSupportedException("Supported storage types are only uint and ulong");

        return longValue;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static TCounter UlongToT<TCounter>(ulong value) where TCounter : unmanaged, IBinaryInteger<TCounter>, IUnsignedNumber<TCounter>
    {
        TCounter tValue;
        if (typeof(TCounter) == typeof(uint))
            tValue = (TCounter)(object)checked((uint)value);
        else if (typeof(TCounter) == typeof(ulong))
            tValue = (TCounter)(object)value;
        else
            throw new NotSupportedException("Supported storage types are only uint and ulong");

        return tValue;
    }
}
