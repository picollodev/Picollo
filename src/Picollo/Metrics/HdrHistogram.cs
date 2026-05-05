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

/// <summary>
/// 
/// </summary>
/// <remarks>
/// This type can be used across threads, but with caveats.
/// Reads during updates should return usable but imprecise results.
/// Hot concurrent writes can lose some updates, especially for hot buckets with typical values, and can badly affect the writer thread performance due to false sharing. 
/// </remarks>
/// <typeparam name="T">The backing storage type for counters. Only <see cref="uint"/> and <see cref="ulong"/> are supported.</typeparam>
public class HdrHistogram<T> where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
{
    private readonly HdrBuckets _buckets;
    private readonly UnsafeSpan<T> _data;
    public ulong MaxTrackableValue { get; }
    private T _overflowCount;

    internal HdrHistogram(double relativeError = 0.001, ulong maxTrackableValue = ulong.MaxValue)
    {
        // Only uint and ulong backing counter storage is supported initially
        if (Unsafe.SizeOf<T>() >= 8 && nint.Size < 8)
            throw new PlatformNotSupportedException(
                $"32-bit runtimes are not supported for storage type `{typeof(T).Name}`. Use `Uint32` storage type.");

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

    /// <summary>
    /// The number of counters available in the backing storage.
    /// </summary>
    internal int StorageSlotsCount => _data.Count;

    public int FootprintInBytes =>
        (int)_data.ByteLength
        + 16 // this obj header 
        + 24 // Array obj header + dim + count
        + 8 + 8 + 8 // UnsafeSpan
        + 4 + 4 + 4 // Buckets
        + Unsafe.SizeOf<T>() // Overflow counter
        + 8 // MaxTrackableValue
    ;

    /// <summary>
    /// Record a single observation of the <paramref name="value"/>.
    /// </summary>
    public void Record(ulong value) => GetRef(value)++;

    /// <summary>
    /// Record <paramref name="count"/> observations of the <paramref name="value"/>.
    /// </summary>
    public void Record(ulong value, uint count) => GetRef(value) += UlongToT(count);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T GetRef(ulong value)
    {
        var index = _buckets.GetIndex(value);
        if (index >= (nuint)_data.LongCount)
            return ref _overflowCount;
        return ref _data.GetAtUnsafe(index);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong TtoUlong(T value)
    {
        ulong longValue;
        if (typeof(T) == typeof(uint))
            longValue = (uint)(object)value;
        else if (typeof(T) == typeof(ulong))
            longValue = (ulong)(object)value;
        else
            throw new NotSupportedException("Supported storage types are only uint and ulong");

        return longValue;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static T UlongToT(ulong value)
    {
        T tValue;
        if (typeof(T) == typeof(uint))
            tValue = (T)(object)checked((uint)value);
        else if (typeof(T) == typeof(ulong))
            tValue = (T)(object)value;
        else
            throw new NotSupportedException("Supported storage types are only uint and ulong");

        return tValue;
    }

    /// <summary>
    /// Returns the smallest value for which its percentile is greater or equal than the requested <paramref name="percentile"/>.
    /// <para />
    /// If this instance is being updated during this call, then the value may be skewed higher.
    /// If this instance is reset updated during this then this method can throw.
    /// </summary>
    /// <param name="percentile">A value from 0.0 to 100.0. Values outside this range are clamped.</param>
    /// <returns>Returns the smallest value for which its percentile is greater or equal than the requested <paramref name="percentile"/></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public ulong GetValueAtPercentile(double percentile) => GetValueAtPercentile(percentile, _data.GetEnumerator(), null);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="percentile"></param>
    /// <param name="enumerator"></param>
    /// <param name="existingTotalCount"></param>
    /// <returns>Returns the smallest value for which its percentile is greater or equal than the requested <paramref name="percentile"/></returns>
    /// <exception cref="InvalidOperationException"></exception>
    internal ulong GetValueAtPercentile(double percentile, UnsafeSpan<T>.Enumerator enumerator, ulong? existingTotalCount)
    {
        enumerator.Reset();
        var totalCount = existingTotalCount.GetValueOrDefault();
        if (existingTotalCount is null)
        {
            while (enumerator.MoveNext())
            {
                totalCount += TtoUlong(enumerator.Current);
            }

            enumerator.Reset();
        }

        if (totalCount == 0)
            return 0; 
        
        if (double.IsNaN(percentile))
            percentile = 0.0;

        percentile = Math.Clamp(percentile, 0.0, 100.0);
        
        ulong targetCount = (ulong)Math.Ceiling(percentile / 100.0 * totalCount);
        targetCount = Math.Clamp(targetCount, 1UL, totalCount);
        
        ulong runningCount = 0;
        
        while (enumerator.MoveNext())
        {
            ulong count = TtoUlong(enumerator.Current);
            runningCount += count;
            
            if (runningCount < targetCount)
                continue;

            var (start, step) = _buckets.GetBucket((nuint)enumerator.Index);

            if (step != 1)
            {
                ulong previous = runningCount - count;
                ulong rankInBucket = targetCount - previous - 1;
                ulong offset = (ulong)((double)rankInBucket / count * step);
                if (offset >= step)
                    offset = step - 1;
                start += offset;
            }

            return start;
        }

        throw new InvalidOperationException("Cannot find the requested percentile.");
    }
}