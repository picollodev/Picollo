using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

// ReSharper disable InconsistentNaming

namespace Picollo.Metrics;

internal sealed class ThreadLocalHdrHistogram<T> : HdrHistogram
    where T : unmanaged, IBinaryInteger<T>, IUnsignedNumber<T>
{
    internal struct HistogramSlot
    {
        internal volatile HdrHistogram<T>? Value;
    }

    private static readonly ConcurrentDictionary<int, WeakReference<HistogramSlot[]?>> KnownSlotsByThreadId = new();

    [ThreadStatic]
    private static HistogramSlot[]? ts_slots;

    private static readonly List<bool> UsedTlsIndices = new(128);

    private readonly HdrHistogram<T> _accumulator;
    private readonly TimeSpan _accumulateInterval;
    private HdrHistogram<T>? _deadAccumulator;

    private HdrHistogram<T>?[] _children = new HdrHistogram<T>?[Environment.ProcessorCount];

    private volatile int _tlsIndex;
    private long _lastUpdateTicks;

    internal ThreadLocalHdrHistogram(
        double relativeError,
        ulong minTrackableValue,
        ulong maxTrackableValue,
        TimeSpan accumulateInterval)
        : base(relativeError, minTrackableValue, maxTrackableValue)
    {
        _tlsIndex = GetUnusedTlsIndex();
        _accumulator = new HdrHistogram<T>(relativeError, minTrackableValue, maxTrackableValue);
        _accumulateInterval = accumulateInterval;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private HdrHistogram<T> GetLocalHistogram()
    {
        HistogramSlot[]? slots = ts_slots;
        HdrHistogram<T>? histogram;
        int index = _tlsIndex;

        if (slots != null
            && index >= 0
            && index < slots.Length
            && (histogram = slots[index].Value) != null)
        {
            return histogram;
        }

        return GetLocalHistogramSlow();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private HdrHistogram<T> GetLocalHistogramSlow()
    {
        int index = _tlsIndex;
        if (index < 0)
            throw new ObjectDisposedException(typeof(T).Name);

        nuint capacity = (nuint)index + 1;

        HistogramSlot[]? storage = ts_slots;

        if (storage is null || capacity > (nuint)storage.LongLength)
        {
            var newStorage = new HistogramSlot[BitOperations.RoundUpToPowerOf2((ulong)capacity)];

            if (storage is not null)
                storage.AsSpan().CopyTo(newStorage.AsSpan().Slice(0, storage.Length));

            ts_slots = newStorage;

            if (KnownSlotsByThreadId.TryGetValue(Environment.CurrentManagedThreadId,
                    out WeakReference<HistogramSlot[]?>? wr))
                wr.SetTarget(newStorage);
            else
                KnownSlotsByThreadId[Environment.CurrentManagedThreadId] = new WeakReference<HistogramSlot[]?>(newStorage);
        }

        HdrHistogram<T>? histogram = ts_slots![index].Value;
        if (histogram == null)
            histogram = ts_slots[index].Value = AllocateOrReset(Environment.CurrentManagedThreadId);

        return histogram;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private HdrHistogram<T> AllocateOrReset(int threadId)
    {
        HdrHistogram<T>? histogram = null;
        HdrHistogram<T> acc = _accumulator;
        lock (this)
        {
            ObjectDisposedException.ThrowIf(_tlsIndex < 0, this);

            // Note: this.Version not _accumulator.Version
            // Allocate changes the structure of this object, and Accumulate should spin on this.Version,
            // while updating _accumulator.Version in the same manner. Somewhat head-breaking,
            // but it's just nested similar pattern.
            Interlocked.Increment(ref Version);
            try
            {
                var children = _children;
                int idx = -1;

                // Scan for dead threads
                for (int i = 0; i < children.Length; i++)
                {
                    var h = children[i];
                    if (h is null)
                    {
                        if (idx == -1)
                            idx = i;
                        continue;
                    }

                    if (h.OwnerThreadId <= 0)
                        throw new InvalidOperationException("Child histograms must be owned by a thread and have positive OwnerThreadId");

                    var tid = h.OwnerThreadId;

                    if (!KnownSlotsByThreadId.TryGetValue(tid, out var wr)
                        || !wr.TryGetTarget(out _))
                    {
                        // Slots were collected
                        if (wr is not null)
                            KnownSlotsByThreadId.TryRemove(tid, out _);

                        children[i] = null;
                        if (idx == -1)
                            idx = i;

                        if (h.ResetCount != acc.ResetCount)
                            continue;

                        Interlocked.CompareExchange(ref _deadAccumulator, h, null)?.Add(h); // this is so nice :)
                    }
                }

                // Scan for threadId match 
                for (int i = 0; i < children.Length; i++)
                {
                    var h = children[i];

                    if (h is null || h.OwnerThreadId != threadId)
                        continue;

                    if (h.ResetCount != acc.ResetCount)
                    {
                        // A reset was requested. The only safe place to clean up the thread-local storage is from the target thread.
                        h.Clear();
                        h.ResetCount = acc.ResetCount; // This thread could have skipped multiple resets
                        histogram = h;
                    }
                    else
                    {
                        // Thread id was reused
                        if (idx == -1)
                            idx = i;
                        else
                            children[i] = null; // TODO Should throw there? Found multiples

                        Interlocked.CompareExchange(ref _deadAccumulator, h, null)?.Add(h);
                    }
                }

                if (histogram is not null)
                    return histogram;

                if (idx == -1)
                {
                    idx = children.Length;
                    var newChildren = new HdrHistogram<T>?[children.Length * 2];
                    children.CopyTo(newChildren);
                    children = _children = newChildren;
                }

                histogram = new HdrHistogram<T>(acc.RelativeError, acc.MinTrackableValue, acc.MaxTrackableValue)
                {
                    OwnerThreadId = threadId
                };

                children[idx] = histogram;
                return histogram;
            }
            finally
            {
                // Note: this.Version not _accumulator.Version
                Interlocked.Increment(ref Version);
            }
        }
    }

    private void Accumulate()
    {
        var lockTaken = false;
        try
        {
            // If the delay since last update is too small, just return without trying to take the lock
            if (_tlsIndex < 0
                || Stopwatch.GetElapsedTime(Volatile.Read(ref _lastUpdateTicks)) <
                _accumulateInterval)
                return;

            Monitor.TryEnter(_accumulator, ref lockTaken);

            if (!lockTaken)
            {
                // Someone else is in Accumulate()/Reset() call.
                // Skip expensive aggregation and use current data optimistically.
                // The reads are protected by _accumulator.Version retry loop,
                // so readers will spin after the skipped lock until another thread finishes.  
                return;
            }

            // This thread owns the lock.
            // Do expensive TLS aggregation
            DoAccumulate();
        }
        finally
        {
            if (lockTaken)
                Monitor.Exit(_accumulator);
        }
    }

    private void DoAccumulate()
    {
        Interlocked.Increment(ref _accumulator.Version);
        try
        {
            var spinner = new SpinWait();
            while (true)
            {
                // Note: this.Version not _accumulator.Version, see the comment in Allocate().
                // Allocate() changes the structure of the TLS storage, so we must ensure we do not try to aggregate incomplete updates.  

                var version = Volatile.Read(ref Version);
                if ((version & 1) != 0)
                {
                    spinner.SpinOnce();
                    continue;
                }

                _accumulator.Clear();

                var children = _children;
                for (int i = 0; i < children.Length; i++)
                {
                    var histogram = children[i];
                    if (histogram is null)
                        continue;

                    var tid = histogram.OwnerThreadId;
                    if (!KnownSlotsByThreadId.TryGetValue(tid, out var wr)
                        || !wr.TryGetTarget(out _))
                    {
                        // Slots were collected
                        if (wr is not null)
                            KnownSlotsByThreadId.TryRemove(tid, out _);

                        children[i] = null;

                        if (histogram.ResetCount != _accumulator.ResetCount)
                            continue;

                        Interlocked.CompareExchange(ref _deadAccumulator, histogram, null)?.Add(histogram);
                        continue;
                    }

                    if (histogram.ResetCount != _accumulator.ResetCount) // Need to wait until the target thread tries to record
                        continue;

                    _accumulator.Add(histogram);
                }

                if (_deadAccumulator is { } da)
                    _accumulator.Add(da);

                if (version == Volatile.Read(ref Version))
                    break;
                spinner.Reset();
            }

            Volatile.Write(ref _lastUpdateTicks, Stopwatch.GetTimestamp());
        }
        finally
        {
            Interlocked.Increment(ref _accumulator.Version);
        }
    }

    public override void Reset()
    {
        lock (this)
        {
            Interlocked.Increment(ref Version);
            try
            {
                foreach (var histogram in _children)
                {
                    if (histogram is null)
                        continue;

                    if (KnownSlotsByThreadId.TryGetValue(histogram.OwnerThreadId, out var wr) && wr.TryGetTarget(out var slots))
                        slots[_tlsIndex].Value = null; // Next attempt to record on the thread will clear the thread's storage
                }

                Interlocked.Increment(ref _accumulator.ResetCount);

                _deadAccumulator = null;

                // Do not do _accumulator.Clear();
                // Since _accumulator.Version is not modified here, readers may still use the previous
                // accumulated snapshot until a later Accumulate() rebuilds _accumulator
                // So for a read method:
                // Accumulate()
                //                         <-- Reset()
                // ReadConsistent()
                // The read itself is structurally consistent. It will not see the Reset on that call, but that is intentional.
            }
            finally
            {
                Interlocked.Increment(ref Version);
            }
        }
    }

    private static int GetUnusedTlsIndex()
    {
        lock (UsedTlsIndices)
        {
            var idx = UsedTlsIndices.IndexOf(false);
            if (idx < 0)
            {
                idx = UsedTlsIndices.Count;
                UsedTlsIndices.Add(true);
            }

            return idx;
        }
    }

    // public bool IsDisposed => _tlsIndex < 0;

    private void DoDispose()
    {
        lock (this) // Protect from races in Allocate
        {
            var tlsIndex = _tlsIndex;
            if (tlsIndex < 0
                || tlsIndex != Interlocked.CompareExchange(ref _tlsIndex, ~tlsIndex, tlsIndex))
                return;

            // Dispose clears the TLS storage and release the tlsIndex for reuse, clears children and dead accumulator,
            // but it keeps the main accumulator with the latest data.
            // Attempts to write will lead to an ObjectDisposedException, but reads will succeed. 

            foreach (var histogram in _children)
            {
                if (histogram is null)
                    continue;

                var tid = histogram.OwnerThreadId;

                if (KnownSlotsByThreadId.TryGetValue(tid, out var wr))
                {
                    if (!wr.TryGetTarget(out var slots))
                    {
                        KnownSlotsByThreadId.TryRemove(tid, out _);
                        continue;
                    }

                    slots[tlsIndex].Value = null;
                }
            }

            lock (_accumulator)
            {
                DoAccumulate();
            }

            _children.AsSpan().Clear();
            _deadAccumulator = null;

            lock (UsedTlsIndices)
            {
                UsedTlsIndices[tlsIndex] = false;
            }
        }
    }

    public override void Dispose()
    {
        DoDispose();
        GC.SuppressFinalize(this);
    }

    ~ThreadLocalHdrHistogram() => DoDispose();

    ////////////////////////////////////////////////////////////////////////////////////////////////////////

    // The assumption here is that there is one monitoring thread

    public override ulong OverflowCount
    {
        get
        {
            Accumulate();
            return _accumulator.OverflowCount;
        }
    }

    public override ulong TotalCount
    {
        get
        {
            Accumulate();
            return _accumulator.TotalCount;
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override void Record(ulong value) => GetLocalHistogram().Record(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override void Record(ulong value, uint count) => GetLocalHistogram().Record(value, count);

    public override ulong GetPercentileValue(double rank, EquivalentValueSelection valueSelection = default)
    {
        Accumulate();
        return _accumulator.GetPercentileValue(rank, valueSelection);
    }

    public override void GetPercentileValues(ReadOnlySpan<double> sortedRanks, Span<ulong> values,
        EquivalentValueSelection valueSelection = default)
    {
        Accumulate();
        _accumulator.GetPercentileValues(sortedRanks, values, valueSelection);
    }

    public override void GetPercentiles(ReadOnlySpan<double> sortedRanks, Span<Percentile> percentiles)
    {
        Accumulate();
        _accumulator.GetPercentiles(sortedRanks, percentiles);
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

    internal override HdrHistogram GetSnapshotInternal()
    {
        Accumulate();
        return _accumulator.GetSnapshotInternal();
    }

    internal override Bucket GetBucketAtStorageIndex(nuint storageIndex) => _accumulator.GetBucketAtStorageIndex(storageIndex);
}