using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using Picollo.Internal;

// ReSharper disable InconsistentNaming
// ReSharper disable StaticMemberInGenericType

namespace Picollo.Metrics;

/*
Reset() clears TLS slots first
marks child histograms NeedsReset
Accumulate() skips NeedsReset
owner thread clears and reactivates its own child on reacquire
OverflowCount goes through Accumulate() / _accumulator
Dispose() clears TLS first, then does a final accumulate, then clears _children

-----------------------------------------------

// TODO Reset should be a logical generation cut, not an in-place clear.
// TODO Add per-child NeedsReset state.
// TODO In Reset(): clear this histogram's TLS slots first, then mark rooted children NeedsReset, then clear _deadAccumulator and _accumulator.
// TODO Do not clear child storage inside Reset(); owner thread should clear on first reuse.
// TODO In Accumulate(): skip children with NeedsReset.
// TODO On TLS miss, try to find existing child by OwnerThreadId before allocating a new one.
// TODO If matching child has NeedsReset, clear it on owner thread, unset NeedsReset, reinstall into TLS, and reuse it.
// TODO Dead-thread retirement: if child.NeedsReset, unlink without merging; otherwise merge into _deadAccumulator.
// TODO Use the same structural sync for Reset/reuse/retirement because they all mutate _children and child state.
// TODO OverflowCount should use Accumulate() / _accumulator, same as other reads.
// TODO Dispose order: clear TLS slots, do final Accumulate(), then clear _children and _deadAccumulator.

-----------------------------------------------

Reset() TODOs for HdrHistogram.Concurrent.Tls.cs

Change OverflowCount to use Accumulate() and read from _accumulator, so overflow follows the same consistency rules as percentile reads.

Introduce a per-child NeedsReset flag on HdrHistogram<T> or equivalent side metadata.

Rework Reset() to be a logical generation cut, not an in-place data clear:

clear this histogram’s TLS slot from every known thread container first
mark all currently rooted children as NeedsReset = true
clear _deadAccumulator
clear _accumulator
do not clear child storage in Reset() itself
Make Accumulate() skip children marked NeedsReset, so old-generation data is excluded immediately after reset.

Rework Allocate() / reacquire path:

after TLS miss, look for an existing child with matching OwnerThreadId before allocating
if found and NeedsReset, clear it on the owner thread, set NeedsReset = false, reinstall into TLS, and reuse it
allocate a new child only if no reusable child exists
Update dead-thread retirement logic in Allocate():

if a dead child has NeedsReset == true, unlink it without merging to _deadAccumulator
otherwise unlink it and merge to _deadAccumulator
Use structural synchronization consistently on this for reset/reacquire/dead-thread retirement, since those all mutate _children, TLS membership, and reset state.

Keep Dispose() finalization order as:

detach TLS slots first
final Accumulate()
clear _children
clear _deadAccumulator
That preserves as much still-rooted data as possible before teardown.
Add a short invariant comment near NeedsReset:

NeedsReset means “detached old-generation child; exclude from accumulation until its owner thread clears and reactivates it.”

*/

public sealed partial class ConcurrentHdrHistogram<T>
{
    internal struct HistogramSlot
    {
        internal volatile HdrHistogram<T>? Value;
    }

    private static readonly ConcurrentDictionary<int, WeakReference<HistogramSlot[]?>> KnownSlotsByThreadId = new();

    [ThreadStatic] private static HistogramSlot[]? ts_slots;

    private static readonly List<bool> UsedTlsIndices = new(128);

    private readonly HdrHistogram<T> _accumulator;
    private HdrHistogram<T>? _deadAccumulator;

    private HdrHistogram<T>?[] _children = new HdrHistogram<T>?[Environment.ProcessorCount];

    private volatile int _tlsIndex;
    private long _lastUpdateTicks;

    internal ConcurrentHdrHistogram(double relativeError = 0.001, ulong minTrackableValue = 0, ulong maxTrackableValue = ulong.MaxValue)
        : base(relativeError, minTrackableValue, maxTrackableValue)
    {
        _tlsIndex = GetUnusedTlsIndex();
        _accumulator = new HdrHistogram<T>(relativeError, minTrackableValue, maxTrackableValue);
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
    public void EnsureCapacityForIndex(nint index)
    {
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
            // _length = (nuint)newStorage.LongLength;

            if (KnownSlotsByThreadId.TryGetValue(Environment.CurrentManagedThreadId,
                    out WeakReference<HistogramSlot[]?>? wr))
                wr.SetTarget(newStorage);
            else
                KnownSlotsByThreadId[Environment.CurrentManagedThreadId] = new WeakReference<HistogramSlot[]?>(newStorage);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private HdrHistogram<T> GetLocalHistogramSlow()
    {
        int index = _tlsIndex;
        EnsureCapacityForIndex(index);

        return ts_slots![index].Value ??= Allocate(Environment.CurrentManagedThreadId);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private HdrHistogram<T> Allocate(int threadId)
    {
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

                        Interlocked.CompareExchange(ref _deadAccumulator, h, null)?.Add(h); // this is so nice :)
                    }
                }

                // Scan for threadId match 
                for (int i = 0; i < children.Length; i++)
                {
                    var h = children[i];

                    if (h is null || h.OwnerThreadId != threadId)
                        continue;

                    if (idx == -1)
                        idx = i;
                    else
                        children[i] = null; // TODO Should throw there? Found multiples

                    Interlocked.CompareExchange(ref _deadAccumulator, h, null)?.Add(h);
                }

                if (idx == -1)
                {
                    idx = children.Length;
                    var newChildren = new HdrHistogram<T>?[children.Length * 2];
                    children.CopyTo(newChildren);
                    children = _children = newChildren;
                }

                var acc = _accumulator;
                var histogram = new HdrHistogram<T>(acc.RelativeError, acc.MinTrackableValue, acc.MaxTrackableValue)
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
        // Only this method and Reset() take the lock over _accumulator
        // Here, we do not need to wait for the lock but rely on optimistic reader retries over _accumulator.Version.
        // Bot this methods and Reset() increment _accumulator.Version, so readers will spin.

        var lockTaken = false;
        try
        {
            // If the dealy since last update is too small, just return without trying to take the lock
            if (_tlsIndex < 0
                || Stopwatch.GetElapsedTime(Volatile.Read(ref _lastUpdateTicks)) <
                TimeSpan.FromMilliseconds(100)) // TODO Throttling freshness parameters
                return;

            Monitor.TryEnter(_accumulator, ref lockTaken);

            if (!lockTaken)
            {
                // Someone else is in Accumulate()/Reset() call.
                // Skip expensive aggregation and use current data optimistically.
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
                foreach (var histogram in _children)
                {
                    if (histogram is null)
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
        lock (_accumulator)
        {
            Interlocked.Increment(ref _accumulator.Version);
            try
            {
                _accumulator.Clear();
                _deadAccumulator = null;
                foreach (var histogram in _children)
                {
                    // TODO Need to swap the storage with a standby clean array, not just clear, because after concurrent clear 
                    //      the storage may end up with old_value + 1
                    histogram?.Clear();
                }
            }
            finally
            {
                Interlocked.Increment(ref _accumulator.Version);
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

            DoAccumulate();

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
                    if (!wr.TryGetTarget(out var threadContainer))
                    {
                        KnownSlotsByThreadId.TryRemove(tid, out _);
                        continue;
                    }

                    threadContainer[tlsIndex].Value = null;
                }
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

    ~ConcurrentHdrHistogram() => DoDispose();
}