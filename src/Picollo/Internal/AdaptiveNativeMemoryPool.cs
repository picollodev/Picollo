using System;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Picollo.Internal;

/// <summary>
/// Allows to accomodate peaks but then realeases idle buffer after a pecified idle delay,
/// while trying to reduce oscilations. The target use case is to supply this pool
/// to <see cref="PipeOptions"/>, where the pipe owns the pool instance.
/// This implementation in not thread safe and designed for SPSC pattern,
/// where the producer rents and the consumer returns.
/// </summary>
internal sealed class AdaptiveNativeMemoryPool : MemoryPool<byte>
{
    private readonly int _smallBufferSize;
    private readonly int _largeBufferSize;

    private Deque<NativeMemoryBlock> _smallBuffers = new(128);
    private Deque<NativeMemoryBlock> _largeBuffers = new(16);

    private DateTime _firstReturn;

    internal readonly TimeSpan IdleDelay;
    internal readonly int LargeBufferMultiple;

    /// <summary>
    /// Total size of allocated and not dropped buffers from this pool. 
    /// </summary>
    internal long TotalAllocated;

    // 128kb can use mmap in Linux

    public AdaptiveNativeMemoryPool(int smallBufferSize = 64 * 1024, int largeBufferMultiple = 16, int idleDelaySeconds = 15)
    {
        _smallBufferSize = NormalizeSmallBufferSize(smallBufferSize);
        LargeBufferMultiple = Math.Clamp(largeBufferMultiple, 1, 32);
        _largeBufferSize = checked(_smallBufferSize * LargeBufferMultiple);
        IdleDelay = TimeSpan.FromSeconds(Math.Clamp(idleDelaySeconds, 5, 600));
    }

    internal static int NormalizeSmallBufferSize(int requestedSize) => checked((int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(4096, requestedSize)));

    public int SmallBufferCount => _smallBuffers.Count;
    public int LargeBufferCount => _largeBuffers.Count;

    public override int MaxBufferSize => _largeBufferSize;

    public override IMemoryOwner<byte> Rent(int minBufferSize = -1) => DoRent(minBufferSize);

    internal NativeMemoryBlock DoRent(int minBufferSize = -1)
    {
        var block = DoRentImpl(minBufferSize);
        block.Init();
        return block;
    }

    private NativeMemoryBlock DoRentImpl(int minBufferSize = -1)
    {
        if (minBufferSize <= _largeBufferSize)
        {
            var smallBuffers = _smallBuffers;

            if (minBufferSize <= _smallBufferSize)
            {
                lock (smallBuffers)
                {
                    if (smallBuffers.Count > 0)
                        return smallBuffers.RemoveLast();
                }
            }

            var largeBuffers = _largeBuffers;
            lock (largeBuffers)
            {
                if (largeBuffers.Count > 0)
                    return largeBuffers.RemoveLast();
            }

            int size = minBufferSize <= _smallBufferSize ? _smallBufferSize : _largeBufferSize;

            // Console.WriteLine($"Allocated {size:N0} native bytes. Total {NativeMemoryBlock.AllocatedNative + size:N0} bytes");
            var block = new NativeMemoryBlock(size, this);
            Interlocked.Add(ref TotalAllocated, block.AllocatedSize);
            return block;
        }

        return new NativeMemoryBlock(minBufferSize, null);
    }

    internal unsafe bool TryReturn(NativeMemoryBlock block)
    {
        var now = DateTime.UtcNow;

        if (_firstReturn == default)
            _firstReturn = now;

        var size = block.AllocatedSize;
        var storage = size == _smallBufferSize ? (_smallBuffers) : (_largeBuffers);
        bool returned;

        const int maxDrops = 8;
        Span<nint> droppedPointers = stackalloc nint[maxDrops];
        var dropCount = 0;

        lock (storage)
        {
            storage.AddLast(block);

            block.LastReturnedUtc = now;
            returned = true;

            if (_firstReturn != default && now - _firstReturn > IdleDelay)
            {
                var minStandby = block.AllocatedSize == _smallBufferSize ? LargeBufferMultiple : 1;

                while (dropCount < maxDrops && storage.Count > minStandby && IsBlockIdle(storage[minStandby - 1], now))
                {
                    var dropBlock = storage.RemoveFirst();
                    Interlocked.Add(ref TotalAllocated, -dropBlock.AllocatedSize);
                    dropBlock.DetachPool();
                    var ptr = dropBlock.DetachPointerForDrop();

                    droppedPointers[dropCount] = ptr;
                    dropCount++;
                }
            }
        }

        for (int i = 0; i < dropCount; i++)
        {
            NativeMemory.Free((void*)droppedPointers[i]);
        }

        // if (dropCount > 0)
        //     Console.WriteLine(
        //         $"Dropped {dropCount} blocks of size {block.AllocatedSize}. Total {(NativeMemoryBlock.AllocatedNative - block.Length):N0} bytes.");

        return returned;
    }

    private bool IsBlockIdle(NativeMemoryBlock block, DateTime now) => now - block.LastReturnedUtc > IdleDelay;

    protected override void Dispose(bool disposing)
    {
        var smallBuffers = Interlocked.Exchange(ref _smallBuffers, null!);
        DisposeBuffers(smallBuffers);

        Deque<NativeMemoryBlock>? largeBuffers = Interlocked.Exchange(ref _largeBuffers, null!);
        DisposeBuffers(largeBuffers);
        
        return;

        // Console.WriteLine($"Disposed native pool. Total {NativeMemoryBlock.AllocatedNative:N0} bytes");
        static void DisposeBuffers(Deque<NativeMemoryBlock>? buffers)
        {
            if (buffers != null!)
            {
                while (buffers.Count > 0)
                {
                    var block = buffers.RemoveLast();
                    Debug.Assert(block.Length == -1);
                    block.DetachPool();
                    block.Free();
                }
            }
        }
    }
}

/*
Alternative option: make this a process-wide shared pool, following MemoryPool<byte>.Shared ownership.
Pipes would never dispose it and would keep a small bounded SPSC cache for steady-state segment rotation,
falling back to the shared pool on misses and returning overflow to it. This requires making all global
rent/return bookkeeping thread-safe, removing disposal races, defining shared size classes (or keyed pools),
and ensuring final pipe cleanup bypasses the SPSC path when it can run from either endpoint thread.
A background task that drops idle buffers should be used for the shared pool.
*/
