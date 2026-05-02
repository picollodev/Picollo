using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

using Picollo.PerfEvent;

using static Picollo.PerfEvent.NativeMethods;

namespace Picollo;

public unsafe class PerfEventCounterSession : IDisposable
{
    public int Pid { get; }
    public int Cpu { get; }

    public bool HasUserRdpmc { get; private set; }
    public bool HasUserTime { get; private set; }

    private bool _pinned;
    private bool _enabled;
    private bool _withKernel;

    internal const int OverheadCalibrationIterations = 1000;

    private int _state; // -1 disposed, 0 not opened, 1 opened

    private int _groupLeaderFd = -1;

    private readonly List<PerfEventCounter> _counters = new();
    private nint[] _counterMmaps = null!;
    private nint* _counterMmapsPtr = null!;
    private ulong[] _counterIds = null!;
    private CounterValue[] _counterValues = null!;
    internal CounterValue* CounterValuesPtr = null!;
    private CounterValue[] _previousCounterValues = null!;
    internal CounterValue* PreviousCounterValuesPtr = null!;
    private GroupReader _groupReader;

    public PerfEventKnownCounters Counters { get; }

    private PerfEventCounterSession(int osThreadId, int cpu)
    {
        Pid = osThreadId;
        Cpu = cpu;
        Counters = new PerfEventKnownCounters(_counters);
    }

    public static PerfEventCounterSession New(int osThreadId = -1, int cpu = -1)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("perf_event_open counters are supported only on Linux.");

        if (osThreadId < 0) osThreadId = -1;
        if (cpu < 0) cpu = -1;

        if (osThreadId == -1 && cpu == -1)
            throw new InvalidOperationException("Either osThreadId or cpu must be set to non-negative value");

        return new PerfEventCounterSession(osThreadId, cpu);
    }

    public PerfEventCounterSession WithPinned(bool pinned = true)
    {
        EnsureNotOpened();
        _pinned = pinned;
        return this;
    }

    public PerfEventCounterSession WithKernel(bool withKernel = true)
    {
        EnsureNotOpened();
        _withKernel = withKernel;
        return this;
    }

    public PerfEventCounterSession WithEnabled(bool enabled = true)
    {
        EnsureNotOpened();
        _enabled = enabled;
        return this;
    }

    /// <summary>
    /// Adds counters for instructions, cycles and reference cycles.
    /// These counters should always be available and do not consume programmable counter slots. 
    /// </summary>
    public PerfEventCounterSession WithFixedCounters()
    {
        EnsureNotOpened();
        AddHardwareCounter(PerfHwId.Instructions);
        AddHardwareCounter(PerfHwId.CpuCycles);
        AddHardwareCounter(PerfHwId.RefCpuCycles);
        return this;
    }
    
    public PerfEventCounterSession WithHardwareCounters()
    {
        EnsureNotOpened();
        AddHardwareCounter(PerfHwId.Instructions);
        AddHardwareCounter(PerfHwId.CpuCycles);
        AddHardwareCounter(PerfHwId.RefCpuCycles);
        AddHardwareCounter(PerfHwId.BranchInstructions);
        AddHardwareCounter(PerfHwId.BranchMisses);
        AddHardwareCounter(PerfHwId.CacheReferences);
        AddHardwareCounter(PerfHwId.CacheMisses);
        return this;
    }

    public PerfEventCounterSession AddHardwareCounter(PerfHwId counter)
    {
        AddHardwareCounter(counter, out _);
        return this;
    }

    public PerfEventCounterSession AddHardwareCounter(PerfHwId counter, out PerfEventCounter handle)
    {
        handle = AddCounter(PerfTypeId.Hardware, (ulong)counter);
        return this;
    }

    public PerfEventCounterSession AddSoftwareCounter(PerfSwIds counter)
    {
        AddSoftwareCounter(counter, out _);
        return this;
    }

    public PerfEventCounterSession AddSoftwareCounter(PerfSwIds counter, out PerfEventCounter handle)
    {
        handle = AddCounter(PerfTypeId.Software, (ulong)counter);
        return this;
    }

    public PerfEventCounterSession AddCacheCounter(PerfCacheId counter)
    {
        AddCacheCounter(counter, out _);
        return this;
    }

    public PerfEventCounterSession AddCacheCounter(PerfCacheId counter, out PerfEventCounter handle)
    {
        handle = AddCounter(PerfTypeId.HardwareCache, (ulong)counter);
        return this;
    }

    private PerfEventCounterSession AddTracepointCounter(uint tracepointId)
    {
        throw new NotImplementedException($"{nameof(PerfTypeId.Tracepoint)} counters are not implemented yet.");
    }

    private PerfEventCounterSession AddRawCounter(ulong rawConfig)
    {
        throw new NotImplementedException($"{nameof(PerfTypeId.Raw)} counters are not implemented yet.");
    }

    private PerfEventCounterSession AddBreakpointCounter(ulong bpType, ulong address, ulong length)
    {
        throw new NotImplementedException($"{nameof(PerfTypeId.Breakpoint)} counters are not implemented yet.");
    }

    private PerfEventCounter AddCounter(PerfTypeId type, ulong config)
    {
        EnsureNotOpened();

        if (_counters.Any(x => (x.Type, x.Config) == (type, config)))
            throw new InvalidOperationException($"Counter {PerfEventCounter.GetName(type, config)} is already added.");

        var counter = new PerfEventCounter(this, type, config);
        _counters.Add(counter);
        Counters.SetCounter(counter);
        return counter;
    }

    public PerfEventCounterSession Open()
    {
        EnsureNotOpened();

        if (_counters.Count == 0)
            throw new InvalidOperationException("No counters were added");

        _state = 1;
        try
        {
            _groupLeaderFd = -1;
            HasUserRdpmc = true;
            HasUserTime = true;

            var pinned = _pinned;

            for (int i = 0; i < _counters.Count; i++)
            {
                var counter = _counters[i];
                counter.Index = i;

                var attr = CreateAttr(counter.Type, counter.Config, pinned, disabled: false, excludeKernel: !_withKernel);
                int fd = PerfEventOpen(in attr, Pid, Cpu, _groupLeaderFd, 0);

                counter.Fd = fd;

                if (_groupLeaderFd < 0)
                {
                    pinned = false;
                    _groupLeaderFd = fd;
                }

                counter.Id = GetEventId(fd);

                const int protRead = 0x1;
                const int mapShared = 0x01;
                var mmapPage = mmap(0, (nuint)Environment.SystemPageSize, protRead, mapShared, fd, 0);
                if (mmapPage < 0)
                    ThrowLastPInvokeError($"mmap(perf fd={fd}) failed");

                var hasCapUserRdpmc = (((PerfEventMMapPage*)mmapPage)->Capabilities & PerfEventMmapCapabilities.CapUserRdpmc) != 0;
                var hasCapUserTime = (((PerfEventMMapPage*)mmapPage)->Capabilities & PerfEventMmapCapabilities.CapUserTime) != 0;

                HasUserRdpmc &= hasCapUserRdpmc;
                HasUserTime &= hasCapUserTime;

                counter.MmapPage = mmapPage;
            }

            _counterMmaps = GC.AllocateArray<nint>(_counters.Count, true);
            _counterMmapsPtr = (nint*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_counterMmaps));

            _counterIds = GC.AllocateArray<ulong>(_counters.Count);
            for (int i = 0; i < _counters.Count; i++)
            {
                _counterMmaps[i] = _counters[i].MmapPage;
                _counterIds[i] = _counters[i].Id;
            }

            _counterValues = GC.AllocateArray<CounterValue>(_counters.Count, true);
            CounterValuesPtr = (CounterValue*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_counterValues));

            _previousCounterValues = GC.AllocateArray<CounterValue>(_counters.Count, true);
            PreviousCounterValuesPtr = (CounterValue*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_previousCounterValues));

            _groupReader = new GroupReader(_groupLeaderFd, _counterIds.Length);

            if (_enabled)
                EnableGroup(_groupLeaderFd);

            // For pinned we must try to read group and check the result is not EoF. Do that early here.
            ReadSlow();

            if (HasUserRdpmc)
                ReadFast();

            CalibrateOverhead();

            ResetGroup(_groupLeaderFd);
            Read();
        }
        catch
        {
            Dispose();
            throw;
        }

        return this;
    }

    private void CalibrateOverhead()
    {
        ResetGroup(_groupLeaderFd);

        for (int i = 0; i < OverheadCalibrationIterations; i++)
        {
            Read();
            Read();
        }

        for (int i = 0; i < OverheadCalibrationIterations; i++)
        {
            Read();
            Read();
            
            // TODO Use histograms
            
            foreach (PerfEventCounter counter in _counters)
            {
                var pairOverhead = counter.RawDelta.Value;
                counter.PairReadOverheadList.Add(pairOverhead);
                if ((i == 0 || pairOverhead > counter.PairReadOverhead.Value && pairOverhead > 0))
                {
                    counter.PairReadOverhead = new CounterValue { Value = pairOverhead };
                    // Console.WriteLine($"----> Set overhead for counter {counter.Name} to: {pairOverhead}");
                }

                Console.WriteLine($"PairOverhead for counter {counter.Name}: {pairOverhead}");
            }
        }

        foreach (PerfEventCounter counter in _counters)
        {
            counter.PairReadOverheadList.Sort();
            counter.PairReadOverhead = new CounterValue { Value = counter.PairReadOverheadList[counter.PairReadOverheadList.Count / 10] };
            Console.WriteLine($"----> Set overhead for counter {counter} {counter.RawDelta.Value} to: {counter.PairReadOverhead.Value}");
        }
    }

    /// <summary>
    /// Read counters
    /// </summary>
    /// <param name="forceSyscallRead">Use slower but atomic syscall read() even if the fast path is supported.</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.NoInlining)]
    public void Read(bool forceSyscallRead = false)
    {
        EnsureOpened();

        var tmp = PreviousCounterValuesPtr;
        PreviousCounterValuesPtr = CounterValuesPtr;
        CounterValuesPtr = tmp;

        if (!forceSyscallRead && HasUserRdpmc)
            ReadFast();
        else
            ReadSlow();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ReadFast()
    {
        if (ReadPerfProgrammableCounters((nint)_counterMmapsPtr, (nint)CounterValuesPtr, (nuint)_counters.Count) != 0)
            throw new InvalidOperationException("Cannot use the fast path despite all mmap advertised the support for CapUserRdpmc");
    }

    internal void ReadSlow()
    {
        var groupReader = _groupReader;
        groupReader.Read();

        var span = groupReader.Span;

        // Ensure order
        ulong[] counterIds = _counterIds;
        for (int i = 0; i < counterIds.Length; i++)
        {
            ulong expectedId = (uint)counterIds[i];

            GroupReader.ValueWithId current = span[i];

            if (current.Id != expectedId)
            {
                for (int j = i + 1; j < span.Length; j++)
                {
                    GroupReader.ValueWithId value = span[j];
                    if (value.Id != expectedId)
                        continue;

                    // span[i] = value; No need if the span is not reused
                    span[j] = current;
                    current = value;
                    break;
                }
            }

            CounterValuesPtr[i].Value = current.Value;
            CounterValuesPtr[i].TimeRunning = groupReader.TimeRunning;
            CounterValuesPtr[i].TimeEnabled = groupReader.TimeEnabled;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureNotOpened()
    {
        if (_state == 0)
            return;

        Throw(_state);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Throw(int state)
        {
            if (state < 0)
                throw new ObjectDisposedException(typeof(PerfEventCounterSession).FullName);
            throw new InvalidOperationException($"{nameof(PerfEventCounterSession)} is already opened");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureOpened()
    {
        if (_state == 1)
            return;

        Throw(_state);

        [DoesNotReturn]
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Throw(int state)
        {
            if (state < 0)
                throw new ObjectDisposedException(typeof(PerfEventCounterSession).FullName);
            throw new InvalidOperationException($"{nameof(PerfEventCounterSession)} is not opened");
        }
    }

    private void DoDispose()
    {
        var state = Interlocked.Exchange(ref _state, -1);
        if (state != 1)
            return;

        foreach (var counter in _counters)
        {
            try
            {
                if (counter.MmapPage >= 0)
                    _ = munmap(counter.MmapPage, (nuint)Environment.SystemPageSize);

                if (counter.Fd >= 0)
                    _ = close(counter.Fd);
            }
            catch
            {
                //
            }
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        DoDispose();
    }

    ~PerfEventCounterSession()
    {
        DoDispose();
    }

    private sealed class GroupReader
    {
        // Read format flags are set in NativeMethods.CreateAttr
        // struct read_format {
        //     u64 nr;            /* The number of events */
        //     u64 time_enabled;  /* if PERF_FORMAT_TOTAL_TIME_ENABLED */
        //     u64 time_running;  /* if PERF_FORMAT_TOTAL_TIME_RUNNING */
        //     struct {
        //         u64 value;     /* The value of the event */
        //         u64 id;        /* if PERF_FORMAT_ID */
        //     } values[nr];
        // };

        private readonly int _fd;

        private const int HeaderSize = 3 * sizeof(ulong);

        // ReSharper disable PrivateFieldCanBeConvertedToLocalVariable : GC tracking
        private readonly byte[] _buffer;
        private ulong* _bufferPtr;

        private readonly nuint _bufferLen;

        public GroupReader(int fd, int count)
        {
            _fd = fd;

            if (count <= 0)
                throw new ArgumentOutOfRangeException(nameof(count));

            if (count > 32)
                throw new NotSupportedException("Count > 32 is not supported");

            Count = count;

            _bufferLen = (nuint)(HeaderSize + count * Unsafe.SizeOf<ValueWithId>());

            _buffer = GC.AllocateArray<byte>((int)_bufferLen, true);
            _bufferPtr = (ulong*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_buffer));
        }

        public int Count { get; }

        public ulong Nr => _bufferPtr[0];

        public ulong TimeEnabled => _bufferPtr[1];

        public ulong TimeRunning => _bufferPtr[2];

        public Span<ValueWithId> Span => new((byte*)_bufferPtr + HeaderSize, checked((int)_bufferPtr[0]));

        public void Read()
        {
            nint bytesRead = read(_fd, (nint)_bufferPtr, _bufferLen);

            if (bytesRead == 0)
                throw new InvalidOperationException(
                    "perf_event read returned EOF; pinned event/group is likely unschedulable or in error state.");

            if (bytesRead < 0)
                ThrowLastPInvokeError("perf_event read failed.");

            if ((nuint)bytesRead != _bufferLen)
                throw new InvalidOperationException(
                    $"perf_event read returned unexpected byte count: {bytesRead}, expected {_bufferLen}.");

            if (Nr != (ulong)Count)
                throw new InvalidOperationException(
                    $"perf_event group read returned unexpected event count: {Nr}, expected {Count}.");
        }

        [StructLayout(LayoutKind.Sequential)]
        public readonly struct ValueWithId
        {
            public readonly ulong Value;
            public readonly ulong Id;

            public ValueWithId(ulong value, ulong id)
            {
                Value = value;
                Id = id;
            }

            public void Deconstruct(out ulong value, out ulong id)
            {
                value = this.Value;
                id = this.Id;
            }
        }
    }
}