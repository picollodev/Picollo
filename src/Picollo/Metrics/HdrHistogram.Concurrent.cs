using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Numerics.Tensors;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Picollo.Internal;

// ReSharper disable StaticMemberInGenericType

namespace Picollo.Metrics;

public sealed partial class ConcurrentHdrHistogram<T>
{
    private class ThreadContainer
    {
        public UnsafeSpan<WeakGCHandle<HdrHistogram<T>>> Slots;

        public WeakGCHandle<object> Sentinel;
        public int ThreadId;

        public ThreadContainer()
        {
            EnsureCapacity(UsedSlots.Capacity);
            Sentinel = new WeakGCHandle<object>(_sentinel ??= new());
            ThreadId = Environment.CurrentManagedThreadId;
            KnownSlotsByThreadId[ThreadId] = new WeakGCHandle<ThreadContainer>(this);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void EnsureCapacity(nint capacity)
        {
            if (capacity <= Slots.LongCount)
                return;

            var newSlots =
                new UnsafeSpan<WeakGCHandle<HdrHistogram<T>>>(
                    new WeakGCHandle<HdrHistogram<T>>[BitOperations.RoundUpToPowerOf2((uint)capacity)]);

            if(Slots.Count > 0)
                Slots.Span.CopyTo(newSlots.Span.Slice(0, Slots.Count));

            Slots = newSlots;
        }

        public bool IsThreadAlive => Sentinel.IsAllocated && Sentinel.TryGetTarget(out object? _);
    }

    // TODO This ThreadId-keyed weak map can go stale when a thread dies or its container is collected.
    // TODO Disposal then looks up a dead/missing container, skips slot cleanup, and leaves stale slot state behind.
    private static readonly ConcurrentDictionary<int, WeakGCHandle<ThreadContainer>> KnownSlotsByThreadId = new();

    [ThreadStatic] private static ThreadContainer? _threadLocalContainer;
    [ThreadStatic] private static object? _sentinel;

    // TODO Slot reuse assumes every per-thread weak slot for this index was cleared during disposal.
    // TODO If cleanup misses a thread, a later histogram can reuse the same slot index and observe stale slot contents.
    private static readonly List<bool> UsedSlots = new(128);


    private static nint GetUnusedSlot()
    {
        lock (UsedSlots)
        {
            var idx = UsedSlots.IndexOf(false);
            if (idx < 0)
            {
                idx = UsedSlots.Count;
                UsedSlots.Add(true);
            }

            return idx;
        }
    }

    private readonly nint _slotIndex;

    // TODO These strong refs retain histograms after their threads are gone, so long-lived instances can accumulate dead-thread state.
    // TODO The locked List is also fragile because readers enumerate it without locking, while Allocate mutates it under a lock.
    // TODO Replace with a manual array+count resized atomically and safely so readers can walk a stable snapshot.
    // TODO Histogram entries in that array should be nullable so disposal/cleanup can null dead slots and readers can just skip nulls.
    private readonly List<(int ThreadId, HdrHistogram<T> Histogram)> _children = new(Environment.ProcessorCount);
    private HdrHistogram<T> _accumulator;

    public ConcurrentHdrHistogram(double relativeError = 0.001, ulong minTrackableValue = 0, ulong maxTrackableValue = ulong.MaxValue) :
        base(relativeError, minTrackableValue, maxTrackableValue)
    {
        _slotIndex = GetUnusedSlot();
        _accumulator = new HdrHistogram<T>(relativeError, minTrackableValue, maxTrackableValue);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private HdrHistogram<T> GetLocalHistogram()
    {
        var index = _slotIndex;
        var threadLocalContainer = _threadLocalContainer ??= new ThreadContainer();
        if (index >= threadLocalContainer.Slots.LongCount)
            threadLocalContainer.EnsureCapacity(index + 1);

        ref var wr = ref threadLocalContainer.Slots.GetAtUnsafe(index);
        if (!wr.IsAllocated || !wr.TryGetTarget(out var histogram))
        {
            histogram = Allocate(threadLocalContainer.ThreadId);
            wr = new WeakGCHandle<HdrHistogram<T>>(histogram);
        }

        return histogram;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private HdrHistogram<T> Allocate(int threadId)
    {
        var histogram = new HdrHistogram<T>(_accumulator.RelativeError, _accumulator.MinTrackableValue, _accumulator.MaxTrackableValue);
        lock (_children)
        {
            _children.Add((threadId, histogram));
        }

        return histogram;
    }

    private void Accumulate()
    {
        foreach ((_, HdrHistogram<T> h) in _children)
        {
            _accumulator.OverflowSlot += h.OverflowSlot;
            TensorPrimitives.Add(h.Data.Span, _accumulator.Data.Span, _accumulator.Data.Span);
        }
    }

    private void DoDispose()
    {
        var accumulator = Interlocked.Exchange(ref _accumulator, null!);
        if (accumulator == null!)
            return;

        foreach ((int threadId, HdrHistogram<T> _) in _children)
        {
            if (KnownSlotsByThreadId.TryGetValue(threadId, out var wr))
            {
                if (!wr.IsAllocated || !wr.TryGetTarget(out var threadContainer))
                {
                    KnownSlotsByThreadId.TryRemove(threadId, out _);
                    continue;
                }

                // TODO Verify that the slot still belongs to this histogram instance before clearing it.
                // TODO Today a reused ThreadId or missed cleanup can make us clear the wrong reused slot or leave the old one behind.
                threadContainer.Slots[_slotIndex].Dispose();
                threadContainer.Slots[_slotIndex] = default;
            }
        }

        lock (UsedSlots)
        {
            UsedSlots[(int)_slotIndex] = false;
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        DoDispose();
    }

    ~ConcurrentHdrHistogram()
    {
        DoDispose();
    }
}