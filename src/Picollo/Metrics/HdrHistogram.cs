using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Picollo.Internal;

namespace Picollo.Metrics;

public static class HdrHistogram
{
    /// <summary>
    /// Creates a new HdrHistogram backed by uint64 storage counters, the relative precision of 0.001 (3 significant digits) and maxTrackableValue = ulong.MaxValue.
    /// This is a safe default, but if you need higher precision or less memory usage, use <see cref="Configure"/> method to change the defaults. 
    /// </summary>
    public static HdrHistogram<ulong> Create() => new();

    public static object Configure() => throw new NotImplementedException();
}

public class HdrHistogram<T> where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
{
    private readonly UnsafeSpan<T> _data;
    private readonly HdrBuckets _buckets;
    private ulong _min = ulong.MaxValue;
    private ulong _max;
    private ulong _totalCount;
    private T _overflowCounter;
    public ulong MaxTrackableValue { get; }

    internal HdrHistogram(double relativeError = 0.001, ulong maxTrackableValue = ulong.MaxValue)
    {
        _buckets = new HdrBuckets(relativeError);
        MaxTrackableValue = MaxTrackableValue;

        var storageSize = _buckets.BucketSize * _buckets.BucketCount;
        if (maxTrackableValue != ulong.MaxValue)
        {
            var lastIndex = (int)_buckets.GetIndex(maxTrackableValue);
            if (lastIndex < storageSize)
                storageSize = lastIndex + 1;
        }

        var owner = new T[storageSize];

        _data = new UnsafeSpan<T>(owner, 0, storageSize);
    }

    public double RelativeError => _buckets.RelativeError;

    public int BucketSize => _buckets.BucketSize;
    public ulong MinValue => _min;
    public ulong MaxValue => _max;

    /// <summary>
    /// The number of counters available in the backing storage.
    /// </summary>
    public int StorageSlotsCount => _data.Count;

    public int FootprintInBytes =>
        (int)_data.ByteLength
        + 16 // this obj header 
        + 24 // Array obj header + count
        + 8 + 8 + 8 // UnsafeSpan
        + 4 + 4 + 4 // Buckets
        + 8 + 8 + 8 // min/max/total
        + Unsafe.SizeOf<T>() // Overflow counter
        + 8 // MaxTrackableValue
    ;

    public void Increment(ulong value)
    {
        // Min/max are almost never taken for normal observations.
        // See https://hotforknowledge.com/2024/01/13/1brc-in-dotnet-among-fastest-on-linux-my-optimization-journey/#avgminmax-efficient-update
        if (value < _min)
            _min = value;

        if (value > _max)
            _max = value;

        // It's free and is overlapped with subsequent slot load
        _totalCount++;

        GetRef(value)++;
    }

    public void Add(ulong value, uint count)
    {
        if (value < _min)
            _min = value;

        if (value > _max)
            _max = value;

        _totalCount += count;

        T increment;
        if (typeof(T) == typeof(uint))
            increment = (T)(object)count;
        else if (typeof(T) == typeof(ulong))
            increment = (T)(object)(ulong)count;
        else if (typeof(T) == typeof(UInt128))
            increment = (T)(object)(UInt128)count;
        else
            throw new NotSupportedException("Supported storage types are only uint, ulong and UInt128");

        GetRef(value) += increment;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T GetRef(ulong value)
    {
        var index = _buckets.GetIndex(value);
        if (index >= (nuint)_data.LongCount)
            return ref _overflowCounter;
        return ref _data.GetAtUnsafe(index);
    }
}

internal readonly struct HdrBuckets
{
    public readonly int BucketSize;
    public readonly int BucketScale;
    public readonly int BucketCount;

    public double RelativeError => 0.5 / BucketSize;

    public HdrBuckets(double relativeError = 0.001)
    {
        if (relativeError <= 0)
            relativeError = 0.001;
        else if (relativeError < 0.00001)
            relativeError = 0.00001;
        else if (relativeError > 0.1)
            relativeError = 0.1;

        BucketSize = (int)BitOperations.RoundUpToPowerOf2((uint)(0.5 / relativeError));
        BucketScale = BitOperations.TrailingZeroCount(BucketSize);
        BucketCount = 1 + 64 - BucketScale;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public nuint GetIndex(ulong value)
    {
        int bucketIndex = 64 - BitOperations.LeadingZeroCount(value >> BucketScale);
        int stepScale = bucketIndex - (bucketIndex != 0 ? 1 : 0); // No branches, JIT recognizes it's just the result of !=
        ulong subIndex = (value >> stepScale) & ((1u << BucketScale) - 1);
        var index = (((nuint)(uint)bucketIndex << BucketScale) + (nuint)subIndex);
        return index;
    }
}