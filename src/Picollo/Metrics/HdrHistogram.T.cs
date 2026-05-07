using System;
using System.Numerics;
using System.Numerics.Tensors;
using System.Runtime.CompilerServices;
using System.Threading;
using Picollo.Internal;

namespace Picollo.Metrics;

/// <summary>
/// 
/// </summary>
/// <remarks>
/// This type can be used across threads, but with caveats.
/// Reads during updates should return usable but imprecise results.
/// Hot concurrent writes can lose some updates, especially for hot buckets with typical values, and can badly affect the writer thread performance due to false sharing. 
/// </remarks>
/// <typeparam name="T">The backing storage type for counters. Only <see cref="uint"/> and <see cref="ulong"/> are supported.</typeparam>
public sealed class HdrHistogram<T> : HdrHistogram
    where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
{
    internal UnsafeSpan<T> Data;
    internal T OverflowSlot;
    internal int OwnerThreadId;

    internal HdrHistogram(double relativeError = 0.001, ulong minTrackableValue = 0, ulong maxTrackableValue = ulong.MaxValue)
        : base(relativeError, minTrackableValue, maxTrackableValue)
    {
        // Only uint and ulong backing counter storage is supported initially
        if (Unsafe.SizeOf<T>() >= 8 && nint.Size < 8)
            throw new PlatformNotSupportedException(
                $"32-bit runtimes are not supported for storage type `{typeof(T).Name}`. Use `Uint32` storage type.");

        Data = new UnsafeSpan<T>(new T[StorageLength]);
    }

    /// <summary>
    /// The total number of observations that fell outside the [<see cref="HdrHistogram.MinTrackableValue"/>, <see cref="HdrHistogram.MaxTrackableValue"/>] range.
    /// </summary>
    public override ulong OverflowCount => TtoUlong(OverflowSlot);

    public override int FootprintInBytes =>
        (int)Data.ByteLength
        + 16 // this obj header 
        + 24 // Array obj header + dim + count
        + 8 + 8 + 8 // Data
        + 4 + 4 + 4 // Buckets
        + Unsafe.SizeOf<T>() // Overflow counter
        + 8 // MaxTrackableValue
    ;

    public override void Reset()
    {
        lock (this)
        {
            Interlocked.Increment(ref Version);
            try
            {
                Clear();
            }
            finally
            {
                Interlocked.Increment(ref Version);
            }
        }
    }

    internal void Clear()
    {
        OverflowSlot = default;
        Data.AsSpan().Clear();
    }

    public override void Dispose()
    {
    }

    /// <summary>
    /// Record a single observation of the <paramref name="value"/>.
    /// </summary>
    public override void Record(ulong value) => GetRef(value)++;

    /// <summary>
    /// Record <paramref name="count"/> observations of the <paramref name="value"/>.
    /// </summary>
    public override void Record(ulong value, uint count) => GetRef(value) += UlongToT(count);

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
    /// Returns <see cref="Bucket"/> details for the given value.
    /// </summary>
    public override Bucket GetBucket(ulong value)
    {
        var logicalIndex = _buckets.GetIndex(value);
        var storageIndex = logicalIndex - _firstIndexOffset;
        if (storageIndex >= (nuint)Data.LongCount)
            return new Bucket(0, 0, OverflowCount, -1);
        var count = TtoUlong(Data.GetAtUnsafe(storageIndex));
        var (start, step) = _buckets.GetBucketRange(logicalIndex);
        return new Bucket(start, step, count, (int)storageIndex);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T GetRef(ulong value)
    {
        var storageIndex = _buckets.GetIndex(value) - _firstIndexOffset;
        if (storageIndex >= (nuint)Data.LongCount)
            return ref OverflowSlot;
        return ref Data.GetAtUnsafe(storageIndex);
    }

    /// <summary>
    /// Returns the smallest value for which its percentile is greater or equal than the requested <paramref name="rank"/>.
    /// <para />
    /// If this instance is being updated during this call, then the value may be skewed lower.
    /// </summary>
    /// <param name="rank">A value from 0.0 to 100.0. Values outside this range are clamped.</param>
    /// <param name="valueSelection">A rule to select the equivalent value in a bucket.</param>
    /// <returns>Returns the smallest value for which its percentile is greater or equal than the requested <paramref name="rank"/></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public override ulong GetPercentileValue(double rank, EquivalentValueSelection valueSelection = default) =>
        GetPercentile(rank).GetValue(valueSelection);

    /// <summary>
    /// Return <see cref="Percentile"/> struct with detailed information about the percentile, counts and the bucket where the percentiles is found.
    /// </summary>
    /// <param name="rank"></param>
    /// <returns></returns>
    public override Percentile GetPercentile(double rank) => GetPercentile(rank, Data);

    /// <summary>
    /// Return <see cref="Percentile"/> struct with detailed information about the percentile, counts and the bucket where the percentiles is found.
    /// </summary>
    /// <param name="rank">The percentile rank.</param>
    /// <param name="data"></param>
    /// <param name="existingTotalCount"></param>
    /// <returns>Returns the smallest value for which its percentile is greater or equal than the requested <paramref name="rank"/></returns>
    internal Percentile GetPercentile(double rank, UnsafeSpan<T> data, ulong? existingTotalCount = null)
    {
        Percentile percentile;
        var spinner = new SpinWait();
        while (true)
        {
            percentile = default;
            var version = Volatile.Read(ref Version);
            if ((version & 1) != 0)
            {
                spinner.SpinOnce();
                continue;
            }

            var totalCount = existingTotalCount ?? TtoUlong(TensorPrimitives.Sum(data.AsSpan()));

            if (totalCount > 0)
            {
                if (double.IsNaN(rank))
                    rank = 0.0;

                rank = Math.Clamp(rank, 0.0, 100.0);

                ulong targetCount = (ulong)Math.Ceiling(rank / 100.0 * totalCount);
                targetCount = Math.Clamp(targetCount, 1UL, totalCount);

                ulong runningCount = 0;

                for (nint i = 0; i < data.LongCount; i++)
                {
                    T value = data.GetAtUnsafe(i);
                    ulong count = TtoUlong(value);
                    runningCount += count;

                    if (runningCount < targetCount)
                        continue;

                    var storageIndex = (nuint)i;
                    var logicalIndex = storageIndex + _firstIndexOffset;

                    var (start, step) = _buckets.GetBucketRange(logicalIndex);

                    var bucket = new Bucket(start, step, count, (int)storageIndex);

                    percentile = new Percentile(rank, bucket, targetCount, runningCount, totalCount);
                    break;
                }
            }

            if (version == Volatile.Read(ref Version))
                break;
            spinner.Reset();
        }

        return percentile;
    }

    // Bad for public API, this should be exposed on the snapshot
    internal void Add(HdrHistogram<T> other)
    {
        OverflowSlot += UlongToT(other.OverflowCount);
        TensorPrimitives.Add(other.Data.AsSpan(), Data.AsSpan(), Data.AsSpan());
    }
}