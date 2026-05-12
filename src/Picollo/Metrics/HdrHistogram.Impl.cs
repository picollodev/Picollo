using System;
using System.Buffers;
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

    private HdrHistogram()
    {
    }

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
    public override ulong OverflowCount => ReadConsistent(static h => TtoUlong(ref h.OverflowSlot));

    public override ulong TotalCount => ReadConsistent(static h => h.GetTotalCount(h.Data));

    public override int FootprintInBytes =>
        (int)Data.ByteLength
        + 16 // this obj header 
        + 24 // Array obj header + dim + count
        + 8 + 8 + 8 // Data
        + 4 + 4 + 4 // Buckets
        + Unsafe.SizeOf<TCounter>() // OverflowSlot
        + 8 // MinTrackableValue
        + 8 // MaxTrackableValue
        + 4 + 4 // OwnerThreadId + ResetCount
        + 8 //_firstIndexOffset
        + 8 //Version
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

    public sealed override void Record(ulong value) => TAddition.Increment(ref GetRef(value));

    public sealed override void Record(ulong value, uint count) => TAddition.Add(ref GetRef(value), UlongToT<TCounter>(count));

    /// <summary>
    /// Returns <see cref="Bucket"/> details for the given value.
    /// </summary>
    public override Bucket GetBucket(ulong value)
    {
        var logicalIndex = HdrBuckets.GetLogicalIndexForValue(value);
        var storageIndex = logicalIndex - _firstIndexOffset;
        if (storageIndex >= (nuint)Data.LongCount)
            return new Bucket(OverflowCount, -1, default);

        ulong count;

        var spinner = new SpinWait();
        while (true)
        {
            var version = Volatile.Read(ref Version);
            if ((version & 1) != 0)
            {
                spinner.SpinOnce();
                continue;
            }

            count = TtoUlong(ref Data.GetAtUnsafe(storageIndex));

            if (version == Volatile.Read(ref Version))
                break;

            spinner.Reset();
        }

        return new Bucket(count, (int)storageIndex, HdrBuckets.GetBucketForIndex(logicalIndex));
    }

    internal override Bucket GetBucketAtStorageIndex(nuint storageIndex)
    {
        var count = TtoUlong(Data[storageIndex]);
        var logicalIndex = _firstIndexOffset + storageIndex;
        return new Bucket(count, (int)storageIndex, HdrBuckets.GetBucketForIndex(logicalIndex));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref TCounter GetRef(ulong value)
    {
        var storageIndex = HdrBuckets.GetLogicalIndexForValue(value) - _firstIndexOffset;
        if (storageIndex >= (nuint)Data.LongCount)
            return ref OverflowSlot;

        return ref Data.GetAtUnsafe(storageIndex);
    }

    public override ulong GetPercentileValue(double rank, EquivalentValueSelection valueSelection = default)
    {
        Span<double> ranks = stackalloc double[1];
        Span<Percentile> percentiles = stackalloc Percentile[1];
        ranks[0] = rank;
        GetPercentiles(ranks, percentiles);
        return percentiles[0].GetValue(valueSelection);
    }

    public override void GetPercentileValues(ReadOnlySpan<double> sortedRanks, Span<ulong> values,
        EquivalentValueSelection valueSelection = default)
    {
        if (values.Length < sortedRanks.Length)
            throw new ArgumentException("Target buffer must be at least as long as the percentile rank buffer.", nameof(values));

        if (sortedRanks.IsEmpty)
            return;

        const int stackallocLimit = 32;
        Percentile[]? rented = null;
        Span<Percentile> percentiles = sortedRanks.Length <= stackallocLimit
            ? stackalloc Percentile[stackallocLimit]
            : (rented = ArrayPool<Percentile>.Shared.Rent(sortedRanks.Length));

        try
        {
            var target = percentiles.Slice(0, sortedRanks.Length);
            GetPercentiles(sortedRanks, target);

            for (int i = 0; i < sortedRanks.Length; i++)
                values[i] = target[i].GetValue(valueSelection);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<Percentile>.Shared.Return(rented);
        }
    }


    public override Percentile GetPercentile(double rank)
    {
        Span<double> ranks = stackalloc double[1];
        Span<Percentile> percentiles = stackalloc Percentile[1];
        ranks[0] = rank;
        GetPercentiles(ranks, percentiles);
        return percentiles[0];
    }

    public override void GetPercentiles(ReadOnlySpan<double> sortedRanks, Span<Percentile> percentiles)
    {
        using var doGetSnapshotInternal = DoGetSnapshotInternal();
        doGetSnapshotInternal
            .GetPercentilesAndStats(sortedRanks, percentiles, out _, ref Unsafe.NullRef<double>(), ref Unsafe.NullRef<double>());
    }


    public override HdrHistogramSummary GetSummary(HdrHistogramSummary? reuseInstance = null)
    {
        var instance = reuseInstance ?? new HdrHistogramSummary();

        using var snapshot = DoGetSnapshotInternal();
        double mean = double.NaN;
        double stDev = double.NaN;
        snapshot.GetPercentilesAndStats(HdrHistogramSummary.SummaryRanks, instance.WriteablePercentiles, out var totalCount, ref mean,
            ref stDev);

        instance.MinTrackableValue = snapshot.MinTrackableValue;
        instance.MaxTrackableValue = snapshot.MaxTrackableValue;

        instance.TotalCount = totalCount;
        instance.OverflowCount = TtoUlong(ref snapshot.OverflowSlot);
        instance.Mean = mean;
        instance.StDev = stDev;

        return instance;
    }

    public override HdrHistogramSnapshot GetSnapshot() => new(this, GetSnapshotInternal());

    private void GetPercentilesAndStats(ReadOnlySpan<double> sortedRanks,
        Span<Percentile> percentiles,
        out ulong totalCount,
        ref double meanRef,
        ref double stDevRef)
    {
        if (percentiles.Length < sortedRanks.Length)
            throw new ArgumentException("Target buffer must be at least as long as the percentile rank buffer.", nameof(percentiles));

        if (!IsValid(sortedRanks))
            throw new ArgumentException(
                "The ranks for percentiles must be strictly increasing and each value must be in the [0, 100] range.", nameof(sortedRanks));

        totalCount = 0;
        double weightedSum = 0;
        var shouldComputeMean = !Unsafe.IsNullRef(ref meanRef);
        var shouldComputeStDev = shouldComputeMean && !Unsafe.IsNullRef(ref stDevRef);

        if (shouldComputeMean)
            meanRef = double.NaN;
        if (shouldComputeStDev)
            stDevRef = double.NaN;

        for (nint i = 0; i < Data.LongCount; i++)
        {
            ulong count = TtoUlong(ref Data.GetAtUnsafe(i));
            if (count == 0)
                continue;

            totalCount += count;

            var logicalIndex = (nuint)i + _firstIndexOffset;
            weightedSum += (double)HdrBuckets.GetBucketForIndex(logicalIndex).MidPoint * count;
        }

        if (totalCount == 0)
        {
            percentiles.Slice(0, sortedRanks.Length).Clear();

            return;
        }

        if (shouldComputeMean)
            meanRef = weightedSum / totalCount;

        ulong runningCountFromTail = 0;
        int rankIndex = sortedRanks.Length - 1;
        double squaredDeviationSum = 0;

        for (nint i = Data.LongCount - 1; i >= 0; i--)
        {
            ulong count = TtoUlong(ref Data.GetAtUnsafe(i));

            if (count == 0)
                continue;

            runningCountFromTail += count;

            var storageIndex = (nuint)i;
            var logicalIndex = storageIndex + _firstIndexOffset;
            var bucket = new Bucket(count, (int)storageIndex, HdrBuckets.GetBucketForIndex(logicalIndex));

            if (shouldComputeStDev)
            {
                double deviation = bucket.MidPoint - meanRef;
                squaredDeviationSum += deviation * deviation * count;
            }

            while (rankIndex >= 0)
            {
                double rank = sortedRanks[rankIndex];
                ulong targetCount = (ulong)Math.Ceiling(rank / 100.0 * totalCount);
                targetCount = Math.Clamp(targetCount, 1UL, totalCount);

                ulong targetCountFromTail = totalCount - targetCount + 1;

                if (runningCountFromTail < targetCountFromTail)
                    break;

                ulong runningCount = totalCount - (runningCountFromTail - count);
                percentiles[rankIndex] = new Percentile(rank, bucket, targetCount, runningCount);
                rankIndex--;
            }

            if (rankIndex < 0 && !shouldComputeStDev)
                return;
            // Otherwise continue to accumulate squaredDeviationSum for all buckets
        }

        if (!shouldComputeStDev)
            return;

        double variance = squaredDeviationSum / totalCount;
        stDevRef = Math.Sqrt(Math.Max(0, variance));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong GetTotalCount(UnsafeSpan<TCounter> data)
    {
        if (typeof(TCounter) == typeof(uint))
        {
            // TensorPrimitives.Sum can overflow for uint storage quite easily
            ulong total = 0L;
            for (nint i = 0; i < data.LongCount; i++)
                total += TtoUlong(data.GetAtUnsafe(i));

            return total;
        }

        return TtoUlong(TensorPrimitives.Sum(data.AsSpan()));
    }

    internal static bool IsValid(ReadOnlySpan<double> span)
    {
        for (var i = 1; i < span.Length; i++)
        {
            if (double.IsNaN(span[i - 1]) || double.IsInfinity(span[i - 1]) || span[i - 1] < 0 || span[i - 1] > 100)
                return false;

            if (span[i - 1] >= span[i])
                return false;
        }

        if (!span.IsEmpty && (double.IsNaN(span[^1]) || double.IsInfinity(span[^1]) || span[^1] < 0 || span[^1] > 100))
            return false;

        return true;
    }

    // Bad for public API, this should be exposed on the snapshot
    internal void Add(HdrHistogram<TCounter> other)
    {
        OverflowSlot += UlongToT<TCounter>(other.OverflowCount);
        TensorPrimitives.Add(other.Data.AsSpan(), Data.AsSpan(), Data.AsSpan());
    }

    private TResult ReadConsistent<TResult>(Func<HdrHistogram<TCounter, TAddition>, TResult> reader)
    {
        var spinner = new SpinWait();

        while (true)
        {
            var version = Volatile.Read(ref Version);
            if ((version & 1) != 0)
            {
                spinner.SpinOnce();
                continue;
            }

            TResult result = reader(this);

            if (version == Volatile.Read(ref Version))
                return result;

            spinner.Reset();
        }
    }

    public override void Dispose()
    {
        if (OwnerThreadId == int.MinValue)
        {
            Interlocked.Add(ref Version, 2); // Invalidate enumerators

            var array = Data.OwnerArray;
            Data = default;

            if (array is not null)
                ArrayPool<TCounter>.Shared.Return(array, true);

            // A public instance will never hit the path as it's not possible to set OwnerThreadId sentinel publicly
            Interlocked.CompareExchange(ref s_pool, this, null);
        }
    }

    // A pool of 1, just to reduce read allocs
    private static HdrHistogram<TCounter, TAddition>? s_pool;

    internal override HdrHistogram GetSnapshotInternal(HdrHistogram? baseHistogram = null)
    {
        var snapshot = DoGetSnapshotInternal();

        if (baseHistogram is null)
            return snapshot;

        if (baseHistogram is not HdrHistogram<TCounter, TAddition> baseSnapshot)
        {
            snapshot.Dispose();
            throw new ArgumentException("Base snapshot type is incompatible with this histogram.", nameof(baseHistogram));
        }

        if (baseSnapshot._firstIndexOffset != snapshot._firstIndexOffset
            || baseSnapshot.Data.Count != snapshot.Data.Count
            || baseSnapshot.MinTrackableValue != snapshot.MinTrackableValue
            || baseSnapshot.MaxTrackableValue != snapshot.MaxTrackableValue
            || baseSnapshot.HdrBuckets.RelativeError != snapshot.HdrBuckets.RelativeError)
        {
            snapshot.Dispose();
            throw new ArgumentException("Base snapshot shape is incompatible with this histogram.", nameof(baseHistogram));
        }

        if (snapshot.ResetCount != baseSnapshot.ResetCount)
            return snapshot;

        snapshot.OverflowSlot -= baseSnapshot.OverflowSlot;

        for (nint i = 0; i < snapshot.Data.LongCount; i++)
            snapshot.Data.GetAtUnsafe(i) -= baseSnapshot.Data.GetAtUnsafe(i);

        return snapshot;
    }

    private HdrHistogram<TCounter, TAddition> DoGetSnapshotInternal()
    {
        var instance = Interlocked.Exchange(ref s_pool, null) ?? new HdrHistogram<TCounter, TAddition>();
        instance.HdrBuckets = HdrBuckets;
        instance._firstIndexOffset = _firstIndexOffset;
        Interlocked.Add(ref instance.Version, 2);
        instance.MinTrackableValue = MinTrackableValue;
        instance.MaxTrackableValue = MaxTrackableValue;
        instance.OverflowSlot = default;
        instance.OwnerThreadId = int.MinValue;
        instance.ResetCount = ResetCount;

        var array = ArrayPool<TCounter>.Shared.Rent(Data.Count);
        instance.Data = new UnsafeSpan<TCounter>(array, 0, Data.Count);

        var spinner = new SpinWait();

        while (true)
        {
            var version = Volatile.Read(ref Version);
            if ((version & 1) != 0)
            {
                spinner.SpinOnce();
                continue;
            }

            Data.AsSpan().CopyTo(instance.Data.AsSpan());
            instance.OverflowSlot = OverflowSlot;

            if (version == Volatile.Read(ref Version))
                return instance;

            spinner.Reset();
        }
    }
}