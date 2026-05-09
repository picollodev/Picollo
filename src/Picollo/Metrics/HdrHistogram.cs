using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Picollo.Metrics;

public abstract partial class HdrHistogram : IDisposable
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
    /// The total number of observations that fell outside the [<see cref="MinTrackableValue"/>, <see cref="MaxTrackableValue"/>] range.
    /// </summary>
    public abstract ulong OverflowCount { get; }

    public abstract int FootprintInBytes { get; }

    /// <summary>
    /// Record a single observation of the <paramref name="value"/>.
    /// </summary>
    public abstract void Record(ulong value);

    /// <summary>
    /// Record <paramref name="count"/> observations of the <paramref name="value"/>.
    /// </summary>
    public abstract void Record(ulong value, uint count);

    /// <summary>
    /// Returns the smallest value for which its percentile is greater or equal than the requested <paramref name="rank"/>.
    /// <para />
    /// If this instance is being updated during this call, then the value may be skewed lower.
    /// </summary>
    /// <param name="rank">A value from 0.0 to 100.0. Values outside this range are clamped.</param>
    /// <param name="valueSelection">A rule to select the equivalent value in a bucket.</param>
    /// <returns>Returns the smallest value for which its percentile is greater or equal than the requested <paramref name="rank"/></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public abstract ulong GetPercentileValue(double rank, EquivalentValueSelection valueSelection = default);

    /// <summary>
    /// Returns a <see cref="Percentile"/> struct with bucket and count details.
    /// </summary>
    /// <param name="rank"></param>
    /// <returns></returns>
    public abstract Percentile GetPercentile(double rank);

    /// <summary>
    /// Returns <see cref="Bucket"/> details for the given value.
    /// </summary>
    public abstract Bucket GetBucket(ulong value);

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