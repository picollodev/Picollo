using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Picollo.PerfEvent;
using static Picollo.PerfEvent.NativeMethods;

namespace Picollo.Profiler.PerfEvent;

[StructLayout(LayoutKind.Sequential)]
internal readonly struct PerfEventRecordHeader
{
    public readonly PerfEventType Type;
    public readonly ushort Misc;
    public readonly ushort Size;
}

internal static class PerfRecordMisc
{
    /*
     * The current state of perf_event_header::misc bits usage:
     * ('|' used bit, '-' unused bit)
     *
     *  012         CDEF
     *  |||---------||||
     *
     *  Where:
     *    0-2     CPUMODE_MASK
     *
     *    C       PROC_MAP_PARSE_TIMEOUT
     *    D       MMAP_DATA / COMM_EXEC / FORK_EXEC / SWITCH_OUT
     *    E       MMAP_BUILD_ID / EXACT_IP / SCHED_OUT_PREEMPT
     *    F       (reserved)
     */

    public const ushort PERF_RECORD_MISC_CPUMODE_MASK = 7;
    public const ushort PERF_RECORD_MISC_CPUMODE_UNKNOWN = 0;
    public const ushort PERF_RECORD_MISC_KERNEL = 1;
    public const ushort PERF_RECORD_MISC_USER = 2;
    public const ushort PERF_RECORD_MISC_HYPERVISOR = 3;
    public const ushort PERF_RECORD_MISC_GUEST_KERNEL = 4;
    public const ushort PERF_RECORD_MISC_GUEST_USER = 5;

    /*
     * Indicates that /proc/PID/maps parsing are truncated by time out.
     */
    public const ushort PERF_RECORD_MISC_PROC_MAP_PARSE_TIMEOUT = (1 << 12);

    /*
     * Following PERF_RECORD_MISC_* are used on different
     * events, so can reuse the same bit position:
     *
     *   PERF_RECORD_MISC_MMAP_DATA  - PERF_RECORD_MMAP* events
     *   PERF_RECORD_MISC_COMM_EXEC  - PERF_RECORD_COMM event
     *   PERF_RECORD_MISC_FORK_EXEC  - PERF_RECORD_FORK event (perf internal)
     *   PERF_RECORD_MISC_SWITCH_OUT - PERF_RECORD_SWITCH* events
     */
    public const ushort PERF_RECORD_MISC_MMAP_DATA = (1 << 13);
    public const ushort PERF_RECORD_MISC_COMM_EXEC = (1 << 13);
    public const ushort PERF_RECORD_MISC_FORK_EXEC = (1 << 13);

    public const ushort PERF_RECORD_MISC_SWITCH_OUT = (1 << 13);

    /*
     * These PERF_RECORD_MISC_* flags below are safely reused
     * for the following events:
     *
     *   PERF_RECORD_MISC_EXACT_IP           - PERF_RECORD_SAMPLE of precise events
     *   PERF_RECORD_MISC_SWITCH_OUT_PREEMPT - PERF_RECORD_SWITCH* events
     *   PERF_RECORD_MISC_MMAP_BUILD_ID      - PERF_RECORD_MMAP2 event
     *
     *
     * PERF_RECORD_MISC_EXACT_IP:
     *   Indicates that the content of PERF_SAMPLE_IP points to
     *   the actual instruction that triggered the event. See also
     *   perf_event_attr::precise_ip.
     *
     * PERF_RECORD_MISC_SWITCH_OUT_PREEMPT:
     *   Indicates that thread was preempted in TASK_RUNNING state.
     *
     * PERF_RECORD_MISC_MMAP_BUILD_ID:
     *   Indicates that mmap2 event carries build ID data.
     */
    public const ushort PERF_RECORD_MISC_EXACT_IP = (1 << 14);
    public const ushort PERF_RECORD_MISC_SWITCH_OUT_PREEMPT = (1 << 14);

    public const ushort PERF_RECORD_MISC_MMAP_BUILD_ID = (1 << 14);

    /*
     * Reserve the last bit to indicate some extended misc field
     */
    public const ushort PERF_RECORD_MISC_EXT_RESERVED = (1 << 15);
}

internal readonly unsafe struct PerfEventRecord
{
    public readonly byte* Pointer;
    public readonly int Length;

    public PerfEventRecord(byte* pointer, int length)
    {
        Pointer = pointer;
        Length = length;
    }

    public ref readonly PerfEventRecordHeader Header => ref Unsafe.As<byte, PerfEventRecordHeader>(ref *Pointer);

    public ReadOnlySpan<byte> Payload =>
        new(Pointer + Unsafe.SizeOf<PerfEventRecordHeader>(), Length - Unsafe.SizeOf<PerfEventRecordHeader>());
}

[DebuggerDisplay("{ToString(),nq}")]
internal sealed unsafe class PerfEventSamplingSession : IDisposable
{
    private const int DataPages = 128;
    private static readonly int HeaderSize = Unsafe.SizeOf<PerfEventRecordHeader>();
    private const nuint ScratchInitialSize = 4096;
    private ulong _sampleFrequency;
    private const int WakeupEvents = 100; // TODO Review the value when using poll()

    private int _fd;
    private nint _mmapPage;
    private readonly nuint _mmapLength;
    private readonly byte* _data;
    private readonly int _dataSize;
    private readonly ulong _dataSizeMask;
    private byte* _scratch;
    private nuint _scratchCapacity;
    private bool _enabled;
    private int _disposed;

    public PerfEventSamplingSession(int tid, ulong sampleFrequency = 1000)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.OSArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("PerfEventSamplingSession is supported only on Linux x64.");

        Tid = tid;

        _sampleFrequency = Math.Clamp(sampleFrequency, 100, 10000);

        try
        {
            var attr = CreateSamplingAttr();
            _fd = PerfEventOpen(in attr, tid, cpu: -1, groupFd: -1, flags: 0);

            const int protRead = 0x1;
            const int protWrite = 0x2;
            const int mapShared = 0x01;

            int pageSize = Environment.SystemPageSize;
            _mmapLength = (nuint)(pageSize * (1 + DataPages));
            _mmapPage = mmap(0, _mmapLength, protRead | protWrite, mapShared, _fd, 0);

            if (_mmapPage == -1)
                ThrowLastPInvokeError($"mmap(perf sampling fd={_fd}) failed");

            var mmapPage = (PerfEventMMapPage*)_mmapPage;
            if (mmapPage->DataOffset > int.MaxValue || mmapPage->DataSize > int.MaxValue)
                throw new NotSupportedException("perf_event mmap data buffer is too large.");

            int dataOffset = (int)mmapPage->DataOffset;
            _dataSize = (int)mmapPage->DataSize;

            if (dataOffset <= 0 || _dataSize <= 0)
                throw new InvalidOperationException("perf_event mmap did not expose a data ring.");

            _dataSizeMask = (ulong)(_dataSize - 1);

            _data = (byte*)_mmapPage + dataOffset;

            _scratch = (byte*)NativeMemory.Alloc(ScratchInitialSize);
            _scratchCapacity = ScratchInitialSize;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public int Tid { get; }

    public int Fd => _fd;

    private PerfEventAttr CreateSamplingAttr()
    {
        return new PerfEventAttr
        {
            Type = PerfTypeId.Software,
            Size = (uint)Unsafe.SizeOf<PerfEventAttr>(),
            Config = (ulong)PerfSoftwareCounterId.CpuClock,
            SampleFreq = _sampleFrequency,
            SampleMaxStack = 127,
            SampleType = PerfEventSampleFormat.PERF_SAMPLE_IP
                         | PerfEventSampleFormat.PERF_SAMPLE_TID
                         | PerfEventSampleFormat.PERF_SAMPLE_TIME
                         | PerfEventSampleFormat.PERF_SAMPLE_CPU
                         | PerfEventSampleFormat.PERF_SAMPLE_PERIOD
                         | PerfEventSampleFormat.PERF_SAMPLE_CALLCHAIN,
            ReadFormat = 0,
            Flags = PerfEventAttrFlags.Disabled
                    | PerfEventAttrFlags.ExcludeCallChainKernel
                    | PerfEventAttrFlags.Freq
                    | PerfEventAttrFlags.MMap
                    | PerfEventAttrFlags.Comm
                    | PerfEventAttrFlags.Task
                    | PerfEventAttrFlags.SampleIdAll
                    | PerfEventAttrFlags.MMap2
                    | PerfEventAttrFlags.CommExec
                    // | PerfEventAttrFlags.ContextSwitch TODO Can generate many events in general
                    // | PerfEventAttrFlags.KSymbol // Not needed as long as kernel symbols are not being resolved
                    // | PerfEventAttrFlags.TextPoke
                    | PerfEventAttrFlags.BuildId
                    | PerfEventAttrFlags.UseClockId
                    | PerfEventAttrFlags.Watermark,
            WakeupEvents = WakeupEvents,
            WakeupWatermark = 16 * 1024,
            ClockId = ClockConstants.Monotonic
        };
    }


    public void Enable()
    {
        EnsureNotDisposed();
        ResetEvent(_fd);
        EnableEvent(_fd);
        _enabled = true;
    }

    public void Disable()
    {
        EnsureNotDisposed();
        DisableEvent(_fd);
        _enabled = false;
    }

    public void Consume(Action<PerfEventRecord>? action)
    {
        foreach (var record in this)
        {
            action?.Invoke(record);
        }
    }

    public int Consume(PipeWriter writer)
    {
        EnsureNotDisposed();

        var mmapPage = (PerfEventMMapPage*)_mmapPage;
        ulong head = Volatile.Read(ref mmapPage->DataHead);
        Thread.MemoryBarrier();
        ulong tail = Volatile.Read(ref mmapPage->DataTail);
        int length = (int)(head - tail);

        if (length > 0)
        {
            int offset = (int)(tail & _dataSizeMask);
            int bytesToEnd = _dataSize - offset;
            var dst = writer.GetSpan(sizeof(int) + sizeof(int) + length);
            BinaryPrimitives.WriteInt32LittleEndian(dst, length);
            BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(sizeof(int)), Tid);

            if (length <= bytesToEnd)
            {
                new ReadOnlySpan<byte>(_data + offset, length).CopyTo(dst.Slice(sizeof(int) + sizeof(int), length));
            }
            else
            {
                new ReadOnlySpan<byte>(_data + offset, bytesToEnd).CopyTo(dst.Slice(sizeof(int) + sizeof(int), bytesToEnd));
                new ReadOnlySpan<byte>(_data, length - bytesToEnd).CopyTo(dst.Slice(sizeof(int) + sizeof(int) + bytesToEnd,
                    length - bytesToEnd));
            }

            writer.Advance(sizeof(int) + sizeof(int) + length);
            tail = head;

            Thread.MemoryBarrier();
            Volatile.Write(ref mmapPage->DataTail, tail);
        }

        return length > 0 ? sizeof(int) + sizeof(int) + length : 0;
    }

    public Enumerator GetEnumerator()
    {
        EnsureNotDisposed();
        return new Enumerator(this);
    }

    public ref struct Enumerator
    {
        private readonly PerfEventSamplingSession _session;
        private readonly PerfEventMMapPage* _mmapPage;
        private readonly ulong _head;
        private ulong _tail;

        public Enumerator(PerfEventSamplingSession session)
        {
            var mmapPage = (PerfEventMMapPage*)session._mmapPage;
            ulong head = Volatile.Read(ref mmapPage->DataHead);
            Thread.MemoryBarrier();
            ulong tail = Volatile.Read(ref mmapPage->DataTail);

            _session = session;
            _mmapPage = mmapPage;
            _head = head;
            _tail = tail;
            Current = default;
        }

        public PerfEventRecord Current { get; private set; }

        public bool MoveNext()
        {
            if (_tail >= _head)
                return false;

            ulong tail = _tail;
            int offset = (int)(tail & _session._dataSizeMask);
            int bytesToEnd = _session._dataSize - offset;
            byte* ptr = _session._data + offset;
            int size;

            if (bytesToEnd >= HeaderSize
                && bytesToEnd >= (size = Unsafe.AsRef<PerfEventRecordHeader>(ptr).Size)
                && size > HeaderSize)
            {
                Current = new PerfEventRecord(ptr, size);
                _tail = tail + (uint)size;
                return true;
            }

            return MoveNextSlow();
        }


        private bool MoveNextSlow()
        {
            if (_tail >= _head)
                return false;

            ulong tail = _tail;
            int offset = (int)(tail & _session._dataSizeMask);
            int bytesToEnd = _session._dataSize - offset;
            byte* ptr = _session._data + offset;
            int size;

            if (bytesToEnd >= HeaderSize)
            {
                size = Unsafe.AsRef<PerfEventRecordHeader>(ptr).Size;
                if (size < HeaderSize)
                    throw new InvalidDataException($"perf_event record has invalid size {size}.");

                if (bytesToEnd >= size)
                {
                    Current = new PerfEventRecord(ptr, size);
                    _tail = tail + (uint)size;
                    return true;
                }
            }
            else
            {
                Span<byte> header = stackalloc byte[HeaderSize];
                int copied = 0;
                ulong pos = tail;

                while (copied < HeaderSize)
                {
                    offset = (int)(pos & _session._dataSizeMask);
                    bytesToEnd = _session._dataSize - offset;
                    int count = Math.Min(HeaderSize - copied, bytesToEnd);
                    new ReadOnlySpan<byte>(_session._data + offset, count).CopyTo(header.Slice(copied, count));
                    copied += count;
                    pos += (uint)count;
                }

                size = MemoryMarshal.Read<PerfEventRecordHeader>(header).Size;
                if (size < HeaderSize)
                    throw new InvalidDataException($"perf_event record has invalid size {size}.");
            }

            if ((nuint)size > _session._scratchCapacity)
            {
                _session._scratch = (byte*)NativeMemory.Realloc(_session._scratch, (nuint)size);
                _session._scratchCapacity = (nuint)size;
            }

            var record = new Span<byte>(_session._scratch, size);
            int written = 0;
            ulong pos2 = tail;

            while (written < size)
            {
                offset = (int)(pos2 & _session._dataSizeMask);
                bytesToEnd = _session._dataSize - offset;
                int count = Math.Min(size - written, bytesToEnd);
                new ReadOnlySpan<byte>(_session._data + offset, count).CopyTo(record.Slice(written, count));
                written += count;
                pos2 += (uint)count;
            }

            Current = new PerfEventRecord(_session._scratch, size);
            _tail = tail + (uint)size;
            return true;
        }

        public void Dispose()
        {
            Thread.MemoryBarrier();
            Volatile.Write(ref _mmapPage->DataTail, _tail);
        }
    }

    public override string ToString() => $"Tid={Tid}, Fd={_fd}, Enabled={(_enabled ? 1 : 0)}";

    private void EnsureNotDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(PerfEventSamplingSession));
    }

    public void DoDispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            if (_enabled && _fd >= 0)
                DisableEvent(_fd);
        }
        catch
        {
            //
        }

        NativeMemory.Free(_scratch);

        try
        {
            if (_mmapPage >= 0)
                _ = munmap(_mmapPage, _mmapLength);
        }
        catch
        {
            //
        }

        try
        {
            if (_fd >= 0)
                _ = close(_fd);
        }
        catch
        {
            //
        }

        _enabled = false;
        _scratch = null;
        _scratchCapacity = 0;
        _mmapPage = -1;
        _fd = -1;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        DoDispose();
    }

    ~PerfEventSamplingSession() => DoDispose();
}