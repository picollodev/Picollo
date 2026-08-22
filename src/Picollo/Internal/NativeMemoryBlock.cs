using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Picollo.Internal;

internal sealed unsafe class NativeMemoryBlock : MemoryManager<byte>
{
    public static long AllocatedNative { get; private set; }

    private AdaptiveNativeMemoryPool? _pool;

    private byte* _pointer;

    private volatile int _length;
    private volatile int _capacity;

    internal DateTime LastReturnedUtc;

    public int AllocatedSize { get; }

    private NativeSequenceSegment? _segment;
    
    public NativeMemoryBlock(int capacity, AdaptiveNativeMemoryPool? pool)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        AllocatedSize = capacity;
        Capacity = capacity;
        _pool = pool;
        _pointer = (byte*)NativeMemory.Alloc((nuint)capacity);

        // GC.AddMemoryPressure(length); - we want to avoid GC
        AllocatedNative += capacity;
    }

    public int Capacity
    {
        get => _capacity;
        private set
        {
            _capacity = value;
            Debug.Assert(_length <= _capacity); // On Dispose, Length is set to -1 fist
        }
    }

    public int Length
    {
        get => _length;
        set
        {
            _length = value;
            Debug.Assert(_length <= _capacity); // On Dispose, Length is set to -1 fist
        }
    }

    public bool IsComplete => Capacity == Length;

    internal bool IsPooled => _pool is not null;
    internal bool IsDisposed => _length < 0;

    public Memory<byte> WrittenMemory => Memory.Slice(0, Length);
    public Span<byte> WrittenSpan => GetSpan().Slice(0, Length);

    public int RemainingLength => Capacity - Length;
    
    public Memory<byte> RemainingMemory
    {
        get
        {
            int length = Length;
            int capacity = Capacity;
            return Memory.Slice(length, capacity - length);
        }
    }

    public Span<byte> RemainingSpan => GetSpan().Slice(Length, RemainingLength);

    private bool IsMemoryFreed => _pointer == null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override Span<byte> GetSpan()
    {
        ObjectDisposedException.ThrowIf(IsMemoryFreed, this);
        return new Span<byte>(_pointer, AllocatedSize);
    }

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        ObjectDisposedException.ThrowIf(IsMemoryFreed, this);

        if ((uint)elementIndex > (uint)AllocatedSize)
            throw new ArgumentOutOfRangeException(nameof(elementIndex));

        return new MemoryHandle(_pointer + elementIndex);
    }

    public override void Unpin()
    {
        // No-op: native memory is already immovable.
    }

    internal void Complete()
    {
        Capacity = Length;
    }

    public void Init()
    {
        Capacity = AllocatedSize;
        Length = 0;
        _segment?.Init();
    }

    protected override void Dispose(bool disposing)
    {
        ObjectDisposedException.ThrowIf(IsMemoryFreed, this);
        ObjectDisposedException.ThrowIf(Length < 0, this);
        Length = -1;
        Capacity = -1;

        _segment?.Reset();

        if (_pool?.TryReturn(this) ?? false)
            return;

        _segment = null;
        Free();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void DetachPool() => _pool = null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal nint DetachPointerForDrop()
    {
        Debug.Assert(IsDisposed);

        Length = -1;
        Capacity = -1;
        AllocatedNative -= AllocatedSize;

        byte* ptr = _pointer;
        _pointer = null;
        return (nint)ptr;
    }

    internal void Free()
    {
        _pool = null;
        byte* ptr = _pointer;
        _pointer = null;

        NativeMemory.Free(ptr);
        // GC.RemoveMemoryPressure(Length);
        AllocatedNative -= AllocatedSize;
    }

    public NativeSequenceSegment Segment => _segment ??= new NativeSequenceSegment(this);

    public class NativeSequenceSegment : ReadOnlySequenceSegment<byte>
    {
        public NativeMemoryBlock MemoryBlock { get; }

        public NativeSequenceSegment(NativeMemoryBlock memoryBlock)
        {
            MemoryBlock = memoryBlock;
            Init();
        }

        public int Length => MemoryBlock.Length;

        public bool IsComplete([NotNullWhen((true))] out NativeSequenceSegment? next)
        {
            next = NextNative;
            return next is not null;
        }

        public bool IsCompleteVolatile([NotNullWhen((true))] out NativeSequenceSegment? next)
        {
            next = NextNativeVolatile;
            return next is not null;
        }

        public bool IsVoid([NotNullWhen((true))] out NativeSequenceSegment? next) => IsCompleteVolatile(out next) && Length == 0;

        /// <summary>
        /// Complete the current memory block and sets the next node.
        /// <see cref="IsCompleteVolatile"/> for a segment depends on the next node presence, and not on memory block completeness. But both must be set. 
        /// </summary>
        /// <param name="next"></param>
        /// <param name="nextRunningIndex"></param>
        public void Complete(NativeSequenceSegment next, long nextRunningIndex)
        {
            MemoryBlock.Complete();
            Memory = MemoryBlock.WrittenMemory;
            Debug.Assert(RunningIndex + Length == nextRunningIndex);
            next.RunningIndex = nextRunningIndex;
            Volatile.WriteBarrier();
            Next = next;
        }

        internal void Init()
        {
            Memory = MemoryBlock.Memory;
        }

        internal void Reset()
        {
            Memory = default;
            Next = null;
            RunningIndex = 0;
        }

        public NativeSequenceSegment? NextNative
        {
            get => Unsafe.As<NativeSequenceSegment>(Next);
            set => Next = value;
        }

        public NativeSequenceSegment? NextNativeVolatile
        {
            get
            {
                var next = Unsafe.As<NativeSequenceSegment>(Next);
                Volatile.ReadBarrier();
                return next;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static NativeSequenceSegment SkipVoid(ref NativeSequenceSegment segment)
        {
            if (segment.IsVoid(out var next))
                return DoSkipVoid(ref segment, next);

            return segment;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static NativeSequenceSegment DoSkipVoid(ref NativeSequenceSegment segment, NativeSequenceSegment? next)
        {
            ArgumentNullException.ThrowIfNull(next);
            
            do
            {
                Debug.Assert(segment.MemoryBlock.IsComplete);
                Debug.Assert(next.RunningIndex == segment.RunningIndex + segment.Length,
                    "Skipping a void segment must preserve a contiguous running index.");
                ((IDisposable)segment.MemoryBlock).Dispose();
                segment = next;
            } while (segment.IsVoid(out next));

            return segment;
        }
    }
}

/*
Length and Capacity could share one naturally aligned 64-bit state. This keeps
individual writes as direct 32-bit stores while allowing RemainingLength to use
one combined load:

[StructLayout(LayoutKind.Explicit, Size = 8)]
struct BlockState
{
    [FieldOffset(0)] public ulong Packed;
    [FieldOffset(0)] public int Length;   // Low 32 bits on little-endian targets.
    [FieldOffset(4)] public int Capacity;

    public readonly int RemainingLength
    {
        get
        {
            ulong packed = Packed;       // Force one non-volatile 64-bit load.
            return (int)(packed >> 32) - (int)packed;
        }
    }
}

The ulong member must remain 8-byte aligned; do not reduce the struct packing to
four bytes. Length and Capacity can be written independently without a packed
read-modify-write. A combined snapshot may therefore contain one old and one new
half, which is intentional; the aligned 64-bit load itself remains atomic.

Ordinary access should be the default. If a combined volatile snapshot is ever
needed, use Volatile.Read(ref state.Packed) explicitly. SyncPipe already publishes
block changes through Flushed or NativeSequenceSegment.Next, so reader-side block
loads following those acquire points do not need another volatile access. This
avoids LDAR/STLR instructions on the writer's hot block path on ARM; on x64 the
packing is expected to be neutral or slightly beneficial. The field order shown
above assumes the supported targets remain little-endian.
*/
