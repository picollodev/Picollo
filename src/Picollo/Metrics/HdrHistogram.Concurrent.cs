using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Picollo.Internal;

// ReSharper disable InconsistentNaming
// ReSharper disable StaticMemberInGenericType

namespace Picollo.Metrics;

public sealed partial class ConcurrentHdrHistogram<T>
{
    private static readonly ConcurrentDictionary<int, WeakReference<HdrHistogram<T>?[]?>> KnownSlotsByThreadId = new();

    [ThreadStatic]
    private static ResizeableArray<HdrHistogram<T>?> ts_slots;

    private static readonly List<bool> UsedSlots = new(128);

    private static nuint GetUnusedSlot()
    {
        lock (UsedSlots)
        {
            var idx = UsedSlots.IndexOf(false);
            if (idx < 0)
            {
                idx = UsedSlots.Count;
                UsedSlots.Add(true);
            }

            return (nuint)idx;
        }
    }

    private readonly nuint _slotIndex;

    private (int ThreadId, HdrHistogram<T>? Histogram)[] _children =
        new (int ThreadId, HdrHistogram<T>? Histogram)[Environment.ProcessorCount];

    private HdrHistogram<T> _accumulator;
    private HdrHistogram<T>? _deadAccumulator;

    public ConcurrentHdrHistogram(double relativeError = 0.001, ulong minTrackableValue = 0, ulong maxTrackableValue = ulong.MaxValue) :
        base(relativeError, minTrackableValue, maxTrackableValue)
    {
        _slotIndex = GetUnusedSlot();
        _accumulator = new HdrHistogram<T>(relativeError, minTrackableValue, maxTrackableValue);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private HdrHistogram<T> GetLocalHistogram()
    {
        // Here ??= is visibly worse
        var h = ts_slots.GetRef(_slotIndex);
        if (h is null)
            ts_slots.GetRef(_slotIndex) = h = Allocate(Environment.CurrentManagedThreadId);
        return h;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private HdrHistogram<T> Allocate(int threadId)
    {
        var histogram = new HdrHistogram<T>(_accumulator.RelativeError, _accumulator.MinTrackableValue, _accumulator.MaxTrackableValue);

        if (KnownSlotsByThreadId.TryGetValue(threadId, out WeakReference<HdrHistogram<T>?[]?>? weakReference))
            weakReference.SetTarget(ts_slots.Data);
        else
            KnownSlotsByThreadId[threadId] = new WeakReference<HdrHistogram<T>?[]?>(ts_slots.Data);

        lock (_accumulator)
        {
            var children = _children;
            int idx = -1;

            // Scan for dead threads
            for (int i = 0; i < children.Length; i++)
            {
                var (tid, h) = children[i];
                if (h is null)
                {
                    if (idx == -1)
                        idx = i;
                    continue;
                }

                if (!KnownSlotsByThreadId.TryGetValue(tid, out var wr)
                    || !wr.TryGetTarget(out _))
                {
                    // Slots were collected
                    if(wr is not null)
                        KnownSlotsByThreadId.TryRemove(tid, out _);
                    
                    if (_deadAccumulator is null)
                        _deadAccumulator = h;
                    else
                        _deadAccumulator.Add(h);

                    children[i] = default;
                    if (idx == -1)
                        idx = i;
                }
            }

            // Scan for threadId match 
            for (int i = 0; i < children.Length; i++)
            {
                var (tid, h) = children[i];
                if (tid == threadId)
                {
                    if (h is not null)
                    {
                        if (_deadAccumulator is null)
                            _deadAccumulator = h;
                        else
                            _deadAccumulator.Add(h);
                    }
                    else
                    {
                        throw new ApplicationException("Found a null histogram with non-zero threadId.");
                    }

                    idx = i;
                    break;
                }
            }

            if (idx == -1)
            {
                idx = children.Length;
                var newChildren = new (int ThreadId, HdrHistogram<T>? Histogram)[children.Length * 2];
                children.CopyTo(newChildren);
                children = _children = newChildren;
            }

            children[idx] = (threadId, histogram);
        }

        return histogram;
    }

    private void Accumulate()
    {
        _accumulator.Reset();
        foreach ((_, HdrHistogram<T>? histogram) in _children)
        {
            if (histogram is null)
                continue;
            _accumulator.Add(histogram);
        }

        if (_deadAccumulator is { } da)
            _accumulator.Add(da);
    }

    private void DoDispose()
    {
        var accumulator = Interlocked.Exchange(ref _accumulator, null!);
        if (accumulator == null!)
            return;

        _deadAccumulator = null;
        
        foreach ((int threadId, _) in _children)
        {
            if (KnownSlotsByThreadId.TryGetValue(threadId, out var wr))
            {
                if (!wr.TryGetTarget(out var threadContainer))
                {
                    KnownSlotsByThreadId.TryRemove(threadId, out _);
                    continue;
                }

                threadContainer[_slotIndex] = null;
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