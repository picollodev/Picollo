using System;
using System.Collections.Generic;
using System.Threading;
using System.Linq;

using static Picollo.PerfEvent.NativeMethods;

namespace Picollo.PerfEvent;

public class PerfEventCounterSession : IDisposable
{
    public int Pid { get; }
    public int Cpu { get; }
    private bool _pinned;
    private bool _enabled;
    private bool _withKernel;

    private int _state; // -1 disposed, 0 not opened, 1 opened

    private int _groupLeaderFd = -1;

    private readonly List<PerfCounter> _counters = new();
    private nint[] _counterMmaps = null!;


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
        throw new NotSupportedException($"{nameof(PerfTypeId.Tracepoint)} counters are not implemented yet.");
    }

    private PerfEventCounterSession AddRawCounter(ulong rawConfig)
    {
        throw new NotSupportedException($"{nameof(PerfTypeId.Raw)} counters are not implemented yet.");
    }

    private PerfEventCounterSession AddBreakpointCounter(ulong bpType, ulong address, ulong length)
    {
        throw new NotSupportedException($"{nameof(PerfTypeId.Breakpoint)} counters are not implemented yet.");
    }

    private void AddCounter(PerfTypeId type, ulong config)
    {
        EnsureNotOpened();

        if (_counters.Any(x => (x.Type, x.Config) == (type, config)))
            throw new InvalidOperationException($"Counter {PerfCounter.GetName(type, config)} is already added.");

        var counter = new PerfCounter(this, type, config);
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
            var pinned = _pinned;
            for (int i = 0; i < _counters.Count; i++)
            {
                var counter = _counters[i];
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

                counter.MmapPage = mmapPage;

                ReadPerfMmapCaps(counter.MmapPage, out bool hasUserTime, out bool hasUserRdpmc);
                counter.HasUserTime = hasUserTime;
                counter.HasUserRdpmc = hasUserRdpmc;
                counter.IsAvailable = true;
            }

            _counterMmaps = GC.AllocateArray<nint>(_counters.Count, true);
            for (int i = 0; i < _counters.Count; i++)
            {
                _counterMmaps[i] = _counters[i].MmapPage;
            }

            if (_enabled)
                EnableGroup(_groupLeaderFd);

            // TODO Must try to read group and check the result is not EoF
            
            ResetGroup(_groupLeaderFd);
        }
        catch
        {
            Dispose();
            throw;
        }

        return this;
    }

    private void EnsureNotOpened()
    {
        if (_state == 0)
            return;

        ObjectDisposedException.ThrowIf(_state < 0, this);

        throw new InvalidOperationException($"{nameof(PerfEventCounterSession)} is already opened");
    }

    private void Dispose(bool disposing)
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
        Dispose(true);
    }

    ~PerfEventCounterSession()
    {
        Dispose(false);
    }


    public class PerfCounter
    {
        public PerfEventCounterSession Session { get; }
        public PerfTypeId Type { get; }
        public ulong Config { get; }
        internal int Fd = -1;
        internal nint MmapPage;
        internal ulong Id;

        public ulong Value;

        public bool IsAvailable { get; internal set; }
        public bool HasUserTime { get; internal set; }
        public bool HasUserRdpmc { get; internal set; }

        internal PerfCounter(PerfEventCounterSession session, PerfTypeId type, ulong config)
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
}