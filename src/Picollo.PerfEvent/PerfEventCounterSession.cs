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
    private bool _pinned;
    private bool _enabled;
    private bool _withKernel;

    private int _state; // -1 disposed, 0 not opened, 1 opened

    private int _groupLeaderFd = -1;
    private bool _allHaveCapRdpmc;

    private readonly List<PerfEventCounter> _counters = new();
    private nint[] _counterMmaps = null!;
    private nint* _counterMmapsPtr = null!;
    private ulong[] _counterIds = null!;
    private CounterValue[] _counterValues = null!;
    private CounterValue* _counterValuesPtr = null!;
    private CounterValue[] _previousCounterValues = null!;
    private CounterValue* _previousCounterValuesPtr = null!;
    private GroupReader _groupReader;

    public bool HasFastPath => _allHaveCapRdpmc;

    private PerfEventCounterSession(int osThreadId, int cpu)
    {
        Pid = osThreadId;
        Cpu = cpu;
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

    public PerfEventCounterSession AddHardwareCounter(PerfHwId counter)
    {
        AddCounter(PerfTypeId.Hardware, (ulong)counter);
        return this;
    }

    public PerfEventCounterSession AddSoftwareCounter(PerfSwIds counter)
    {
        AddCounter(PerfTypeId.Software, (ulong)counter);
        return this;
    }

    public PerfEventCounterSession AddCacheCounter(PerfCacheId counter)
    {
        AddCounter(PerfTypeId.HardwareCache, (ulong)counter);
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

    private void AddCounter(PerfTypeId type, ulong config)
    {
        EnsureNotOpened();

        if (_counters.Any(x => (x.Type, x.Config) == (type, config)))
            throw new InvalidOperationException($"Counter {PerfEventCounter.GetName(type, config)} is already added.");

        var counter = new PerfEventCounter(this, type, config);
        _counters.Add(counter);
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
            _allHaveCapRdpmc = true;

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

                var hasCapRdpmc = (((PerfEventMMapPage*)mmapPage)->Capabilities & PerfEventMmapCapabilities.CapUserRdpmc) != 0;
                _allHaveCapRdpmc &= hasCapRdpmc;

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
            _counterValuesPtr = (CounterValue*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_counterValues));

            _previousCounterValues = GC.AllocateArray<CounterValue>(_counters.Count, true);
            _previousCounterValuesPtr = (CounterValue*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_previousCounterValues));

            _groupReader = new GroupReader(_groupLeaderFd, _counterIds.Length);
            
            if (_enabled)
                EnableGroup(_groupLeaderFd);

            // TODO Must try to read group and check the result is not EoF
            ReadSlow();
            
            if(_allHaveCapRdpmc)
                ReadFast();

            ResetGroup(_groupLeaderFd);
        }
        catch
        {
            Dispose();
            throw;
        }

        return this;
    }

    public void Read()
    {
        EnsureOpened();

        var tmp = _previousCounterValuesPtr;
        _previousCounterValuesPtr = _counterValuesPtr;
        _counterValuesPtr = tmp;

        if (_allHaveCapRdpmc)
            ReadFast();
        else
            ReadSlow();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ReadFast()
    {
        if (ReadPerfProgrammableCounters((nint)_counterMmapsPtr, (nint)_counterValuesPtr, (nuint)_counters.Count) != 0)
            throw new InvalidOperationException("Cannot read counters using the fast path despite all mmap advertised the support for CapUserRdpmc");
    }

    internal void ReadSlow()
    {
        _groupReader.Read();
        // TODO Read into _counterValuesPtr
        
        var span = _groupReader.Current;
        
        // Fix-up order
        ulong[] counterIds = _counterIds;
        for (int i = 0; i < counterIds.Length; i++)
        {
            ulong expectedId = (uint)counterIds[i];
            
            if(span[i].Id == expectedId)
                continue;
            
            // TODO
        }
        
        throw new NotImplementedException();
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
                if (counter.MmapPage > 0)
                    _ = munmap(counter.MmapPage, (nuint)Environment.SystemPageSize);

                if (counter.Fd > 0)
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


    public class PerfEventCounter
    {
        public PerfEventCounterSession Session { get; }
        public PerfTypeId Type { get; }
        public ulong Config { get; }
        internal int Fd = -1;
        internal nint MmapPage;
        internal ulong Id;
        internal int Index;

        public unsafe bool HasUserTime => MmapPage > 0 && (((PerfEventMMapPage*)MmapPage)->Capabilities & PerfEventMmapCapabilities.CapUserTime) != 0;
        public unsafe bool HasUserRdpmc => MmapPage > 0 && (((PerfEventMMapPage*)MmapPage)->Capabilities & PerfEventMmapCapabilities.CapUserRdpmc) != 0;

        internal PerfEventCounter(PerfEventCounterSession session, PerfTypeId type, ulong config)
        {
            Session = session;
            Type = type;
            Config = config;
        }

        // TODO Add Value and time tracking

        public string Name => field ??= GetName(Type, Config);

        internal static string GetName(PerfTypeId type, ulong config)
        {
            string subName = "";
            switch (type)
            {
                case PerfTypeId.Hardware:
                    subName = $"{((PerfHwId)config):G}";
                    break;
                case PerfTypeId.Software:
                    subName = $"{((PerfSwIds)config):G}";
                    break;
                case PerfTypeId.Tracepoint:
                    break;
                case PerfTypeId.HardwareCache:
                    subName = $"{((PerfCacheId)config):G}";
                    break;
                case PerfTypeId.Raw:
                    break;
                case PerfTypeId.Breakpoint:
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            var name = $"{type:G}:{subName}";
            return name;
        }

        public override string ToString()
        {
            return Name; // TODO Add value
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CounterValue
    {
        public ulong Value;
        public ulong TimeEnabled;
        public ulong TimeRunning;

        public ulong ScaledValue => ScaleToUInt64(Value, TimeEnabled, TimeRunning);

        private static ulong ScaleToUInt64(ulong value, ulong timeEnabled, ulong timeRunning)
        {
            if (timeEnabled == timeRunning)
                return value;

            if (timeRunning == 0)
                return 0;

            return (ulong)((double)value * timeEnabled / timeRunning);
        }
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
        private readonly byte[] _currentBuffer;
        private ulong* _currentBufferPtr;

        private readonly byte[] _previousBuffer;
        private ulong* _previousBufferPtr;

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

            _currentBuffer = GC.AllocateArray<byte>((int)_bufferLen, true);
            _currentBufferPtr = (ulong*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_currentBuffer));

            _previousBuffer = GC.AllocateArray<byte>((int)_bufferLen, true);
            _previousBufferPtr = (ulong*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_previousBuffer));
        }

        public int Count { get; }

        public ulong Nr => _currentBufferPtr[0];

        public ulong TimeEnabled => _currentBufferPtr[1];

        public ulong TimeRunning => _currentBufferPtr[2];

        public ref ValueWithId this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Count)
                    throw new ArgumentOutOfRangeException(nameof(index));

                return ref Unsafe.AsRef<ValueWithId>((byte*)_currentBufferPtr + HeaderSize + (uint)(index * Unsafe.SizeOf<ValueWithId>()));
            }
        }

        public Span<ValueWithId> Current => new((byte*)_currentBufferPtr + HeaderSize, checked((int)_currentBufferPtr[0]));
        public Span<ValueWithId> Previous => new((byte*)_previousBufferPtr + HeaderSize, checked((int)_previousBufferPtr[0]));

        public void Read()
        {
            var tmp = _previousBufferPtr;
            _previousBufferPtr = _currentBufferPtr;
            _currentBufferPtr = tmp;

            nint bytesRead = read(_fd, (nint)_currentBufferPtr, _bufferLen);

            if (bytesRead == 0)
                throw new InvalidOperationException("perf_event read returned EOF; pinned event/group is likely unschedulable or in error state.");

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