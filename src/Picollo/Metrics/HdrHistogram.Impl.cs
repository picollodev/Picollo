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
/// <typeparam name="TCounter">The backing storage type for counters. Only <see cref="uint"/> and <see cref="ulong"/> are supported.</typeparam>
/// <typeparam name="TAddition"></typeparam>
internal class HdrHistogram<TCounter, TAddition> : HdrHistogram
    where TCounter : unmanaged, IBinaryInteger<TCounter>, IUnsignedNumber<TCounter>
    where TAddition : struct, IAddition
{
    internal UnsafeSpan<TCounter> Data;
    internal TCounter OverflowSlot;
    internal volatile int OwnerThreadId;
    internal volatile int ResetCount;

    internal HdrHistogram(double relativeError, ulong minTrackableValue, ulong maxTrackableValue)
        : base(relativeError, minTrackableValue, maxTrackableValue)
    {
        // Only uint and ulong backing counter storage is supported initially
        if (Unsafe.SizeOf<TCounter>() >= 8 && nint.Size < 8)
            throw new PlatformNotSupportedException(
                $"32-bit runtimes are not supported for storage type `{typeof(TCounter).Name}`. Use `Uint32` storage type.");

        Data = new UnsafeSpan<TCounter>(new TCounter[StorageLength]);
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
        + Unsafe.SizeOf<TCounter>() // Overflow counter
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
                Interlocked.Increment(ref ResetCount);
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
    public sealed override void Record(ulong value) => TAddition.Increment(ref GetRef(value));

    /// <summary>
    /// Record <paramref name="count"/> observations of the <paramref name="value"/>.
    /// </summary>
    public sealed override void Record(ulong value, uint count) => TAddition.Add(ref GetRef(value), UlongToT<TCounter>(count));

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
    internal ref TCounter GetRef(ulong value)
    {
        var storageIndex = _buckets.GetIndex(value) - _firstIndexOffset;
        if (storageIndex >= (nuint)Data.LongCount)
            return ref OverflowSlot;

        // TODO Remove this when thread-local cleans its own storage after reset
        // if (typeof(TAddition) == typeof(VolatileAddition))
        //     Volatile.ReadBarrier();

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
    /// Return <see cref="Percentile"/> struct with detailed information about the percentile, counts and the bucket where the percentile is found.
    /// </summary>
    /// <param name="rank"></param>
    /// <returns></returns>
    public override Percentile GetPercentile(double rank) => GetPercentile(rank, Data);

    /// <summary>
    /// Return <see cref="Percentile"/> struct with detailed information about the percentile, counts and the bucket where the percentile is found.
    /// </summary>
    /// <param name="rank">The percentile rank.</param>
    /// <param name="data"></param>
    /// <param name="existingTotalCount"></param>
    /// <returns>Returns the smallest value for which its percentile is greater or equal than the requested <paramref name="rank"/></returns>
    internal Percentile GetPercentile(double rank, UnsafeSpan<TCounter> data, ulong? existingTotalCount = null)
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

            if (typeof(TCounter) == typeof(uint) && !existingTotalCount.HasValue)
            {
                // TensorPrimitives.Sum can overflow for uint storage quite easily

                ulong total = 0L;
                for (nint i = 0; i < data.LongCount; i++)
                {
                    TCounter value = data.GetAtUnsafe(i);
                    ulong count = TtoUlong(value);
                    total += count;
                }

                existingTotalCount = total;
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
                    TCounter value = data.GetAtUnsafe(i);
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
    internal void Add(HdrHistogram<TCounter> other)
    {
        OverflowSlot += UlongToT<TCounter>(other.OverflowCount);
        TensorPrimitives.Add(other.Data.AsSpan(), Data.AsSpan(), Data.AsSpan());
    }
}
