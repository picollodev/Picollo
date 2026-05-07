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

public sealed partial class ConcurrentHdrHistogram<T>
{
    private static readonly ConcurrentDictionary<int, WeakReference<HdrHistogram<T>?[]?>> KnownSlotsByThreadId = new();

    [ThreadStatic] private static TlsStorage ts_slots;

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
        // Here ??= is visibly worse
        var h = ts_slots.GetRef(_tlsIndex);
        if (h is null)
            ts_slots.GetRef(_tlsIndex) = h = Allocate(Environment.CurrentManagedThreadId);
        return h;
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

                    threadContainer[tlsIndex] = null;
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

    internal struct TlsStorage
    {
        private HdrHistogram<T>?[]? _data;
        private nuint _length;

        public HdrHistogram<T>?[]? Data => _data;
        public nuint Length => _length;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void EnsureCapacityForIndex(nint index)
        {
            if (index < 0)
                throw new ObjectDisposedException(typeof(T).Name);

            nuint capacity = (nuint)index + 1;

            HdrHistogram<T>?[]? storage = _data;

            if (storage is null || capacity > (nuint)storage.LongLength)
            {
                var newStorage = new HdrHistogram<T>?[BitOperations.RoundUpToPowerOf2((ulong)capacity)];

                if (storage is not null)
                    storage.AsSpan().CopyTo(newStorage.AsSpan().Slice(0, storage.Length));

                _data = newStorage;
                _length = (nuint)newStorage.LongLength;

                if (KnownSlotsByThreadId.TryGetValue(Environment.CurrentManagedThreadId, out WeakReference<HdrHistogram<T>?[]?>? wr))
                    wr.SetTarget(newStorage);
                else
                    KnownSlotsByThreadId[Environment.CurrentManagedThreadId] = new WeakReference<HdrHistogram<T>?[]?>(newStorage);
            }
        }

        /// <summary>
        /// Get an item at <paramref name="index"/> without bound checks.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref HdrHistogram<T>? GetRef(nint index)
        {
            if ((uint)index >= Length)
                EnsureCapacityForIndex(index);

#if DEBUG
            return ref _data![index];
#else
            return ref _data!.GetAtUnsafe((nuint)index);
#endif
        }
    }
}