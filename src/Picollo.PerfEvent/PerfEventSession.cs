using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Picollo.PerfEvent;

using static Picollo.PerfEvent.NativeMethods;

namespace Picollo;

public sealed class PerfEventSession : IDisposable
{
    public static bool KeepCountersAlwaysRunning { get; set; } = true;

    private readonly int _tid;
    private PerfCounterState[] _counterStates = new PerfCounterState[4];
    private int _counterCount;
    private int _groupLeaderFd = -1;

    private bool _opened;
    private bool _running;
    private bool _disposed;
    private DateTime _startUtc;
    private DateTime _stopUtc;
    private ulong _timeEnabled;
    private ulong _timeRunning;
    private ulong _startTimeEnabled;
    private ulong _startTimeRunning;

    private PerfEventSession(int tid)
    {
        _tid = tid;
    }

    public static PerfEventSession CreateForTid(int tid)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("perf_event_open counters are supported only on Linux.");
        if (tid <= 0)
            throw new ArgumentOutOfRangeException(nameof(tid), "Thread ID must be a positive native Linux TID.");
        return new PerfEventSession(tid);
    }

    public PerfEventSession AddHardwareCounter(PerfHwId counter)
    {
        AddCounter(PerfTypeId.Hardware, (ulong)counter, $"hw:{counter}");
        return this;
    }

    public PerfEventSession AddSoftwareCounter(PerfSwIds counter)
    {
        AddCounter(PerfTypeId.Software, (ulong)counter, $"sw:{counter}");
        return this;
    }

    public PerfEventSession AddTracepointCounter(uint tracepointId)
    {
        throw new NotSupportedException($"{nameof(PerfTypeId.Tracepoint)} counters are not implemented yet.");
    }

    public PerfEventSession AddCacheCounter(PerfCacheId counter)
    {
        AddCounter(PerfTypeId.HardwareCache, (ulong)counter, $"cache:{counter}");
        return this;
    }

    public PerfEventSession AddRawCounter(ulong rawConfig)
    {
        throw new NotSupportedException($"{nameof(PerfTypeId.Raw)} counters are not implemented yet.");
    }

    public PerfEventSession AddBreakpointCounter(ulong bpType, ulong address, ulong length)
    {
        throw new NotSupportedException($"{nameof(PerfTypeId.Breakpoint)} counters are not implemented yet.");
    }

    public void Start()
    {
        ThrowIfDisposed();
        if (_running)
            throw new InvalidOperationException("Session is already running.");
        if (!_opened)
            OpenRequestedCounters();
        if (_groupLeaderFd < 0)
            throw new InvalidOperationException("No counters are available to start.");

        if (KeepCountersAlwaysRunning)
        {
            ReadIntoStateValues(out _startTimeEnabled, out _startTimeRunning);
        }
        else
        {
            ResetGroup(_groupLeaderFd);
            EnableGroup(_groupLeaderFd);

            _startTimeEnabled = 0;
            _startTimeRunning = 0;
            for (int i = 0; i < _counterCount; i++)
            {
                var state = _counterStates[i];
                state.Value = 0;
                _counterStates[i] = state;
            }
        }

        _timeEnabled = 0;
        _timeRunning = 0;
        _startUtc = DateTime.UtcNow;
        _stopUtc = default;
        _running = true;
    }

    public void Stop()
    {
        ThrowIfDisposed();
        if (!_running)
            throw new InvalidOperationException("Session is not running.");

        _stopUtc = DateTime.UtcNow;
        if (!KeepCountersAlwaysRunning)
            DisableGroup(_groupLeaderFd);

        ReadIntoStateDeltas(_startTimeEnabled, _startTimeRunning, out _timeEnabled, out _timeRunning);
        _running = false;
    }

    public PerfCounterSnapshot GetSnapshot()
    {
        return GetSnapshot(new PerfCounterSnapshot());
    }

    public PerfCounterSnapshot GetSnapshot(PerfCounterSnapshot snapshot)
    {
        ThrowIfDisposed();
        if (_running)
            throw new InvalidOperationException("Session is still running. Call Stop() before GetSnapshot().");
        if (!_opened)
            throw new InvalidOperationException("Session has not been started.");
        ArgumentNullException.ThrowIfNull(snapshot);

        BuildSnapshot(snapshot, _startUtc, _stopUtc, _timeEnabled, _timeRunning);
        return snapshot;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        CloseOpenFds();
        _disposed = true;
    }

    private void AddCounter(PerfTypeId type, ulong config, string name)
    {
        ThrowIfDisposed();
        if (_running || _opened)
            throw new InvalidOperationException("Cannot add counters after session has been opened or started.");

        if (_counterCount == _counterStates.Length)
            Array.Resize(ref _counterStates, _counterStates.Length * 2);

        _counterStates[_counterCount++] = new PerfCounterState
        {
            Type = type,
            Config = config,
            Name = name,
            Fd = -1,
            MmapPage = 0,
            Id = 0,
            Value = 0,
            IsAvailable = false,
            HasUserTime = false,
            HasUserRdpmc = false,
            OpenErrno = 0
        };
    }

    private void OpenRequestedCounters()
    {
        if (_counterCount == 0)
            throw new InvalidOperationException("No counters were added to the session.");

        bool anyOpened = TryOpenCounters(pinned: true);
        bool allOpened = CountAvailableCounters() == _counterCount;
        if (!allOpened)
        {
            CloseOpenFds();
            anyOpened = TryOpenCounters(pinned: false);
            if (!anyOpened)
                throw new IOException("Failed to open any requested perf counters.");
        }
        else if (!anyOpened)
        {
            throw new IOException("Failed to open any requested perf counters.");
        }

        _opened = true;
    }

    private bool TryOpenCounters(bool pinned)
    {
        _groupLeaderFd = -1;
        bool anyOpened = false;

        for (int i = 0; i < _counterCount; i++)
        {
            var counter = _counterStates[i];
            counter.Fd = -1;
            counter.MmapPage = 0;
            counter.Id = 0;
            counter.Value = 0;
            counter.IsAvailable = false;
            counter.HasUserTime = false;
            counter.HasUserRdpmc = false;
            counter.OpenErrno = 0;

            var attr = CreateAttr(counter.Type, counter.Config, pinned);
            int fd = PerfEventOpen(in attr, _tid, -1, _groupLeaderFd, 0);
            if (fd < 0)
            {
                counter.OpenErrno = Marshal.GetLastPInvokeError();
                _counterStates[i] = counter;
                continue;
            }

            if (_groupLeaderFd < 0)
                _groupLeaderFd = fd;

            try
            {
                counter.Fd = fd;
                counter.Id = GetEventId(fd);
                counter.MmapPage = mmap(0, (nuint)Environment.SystemPageSize, ProtRead, MapShared, fd, 0);
                if (counter.MmapPage == MapFailed)
                    ThrowLastPInvokeError($"mmap(perf fd={fd}) failed");

                ReadPerfMmapCaps(counter.MmapPage, out bool hasUserTime, out bool hasUserRdpmc);
                counter.HasUserTime = hasUserTime;
                counter.HasUserRdpmc = hasUserRdpmc;
                counter.IsAvailable = true;
                anyOpened = true;
                _counterStates[i] = counter;
            }
            catch
            {
                if (counter.MmapPage != 0 && counter.MmapPage != MapFailed)
                {
                    _ = munmap(counter.MmapPage, (nuint)Environment.SystemPageSize);
                    counter.MmapPage = 0;
                }

                _ = close(fd);
                counter.Fd = -1;
                counter.Id = 0;
                counter.IsAvailable = false;
                counter.HasUserTime = false;
                counter.HasUserRdpmc = false;
                counter.OpenErrno = Marshal.GetLastPInvokeError();
                _counterStates[i] = counter;
            }
        }

        return anyOpened;
    }

    private void ReadIntoStateValues(out ulong timeEnabled, out ulong timeRunning)
    {
        if (TryReadIntoStateValuesFastPath(out timeEnabled, out timeRunning))
            return;

        ReadIntoStateValuesByRead(out timeEnabled, out timeRunning);
    }

    private bool TryReadIntoStateValuesFastPath(out ulong timeEnabled, out ulong timeRunning)
    {
        timeEnabled = 0;
        timeRunning = 0;

        int availableCount = 0;
        int leaderIndex = -1;
        for (int i = 0; i < _counterCount; i++)
        {
            if (_counterStates[i].IsAvailable)
            {
                availableCount++;
                if (_counterStates[i].Fd == _groupLeaderFd)
                    leaderIndex = i;
            }
        }

        if (availableCount == 0)
            throw new InvalidOperationException("No available counters to read.");

        if (leaderIndex < 0)
        {
            for (int i = 0; i < _counterCount; i++)
            {
                if (_counterStates[i].IsAvailable)
                {
                    leaderIndex = i;
                    break;
                }
            }
        }

        for (int i = 0; i < _counterCount; i++)
        {
            var state = _counterStates[i];
            if (!state.IsAvailable)
                continue;

            if (state.MmapPage == 0 || state.MmapPage == MapFailed || !state.HasUserRdpmc)
                return false;
        }

        ulong leaderEnabled = 0;
        ulong leaderRunning = 0;
        bool gotLeaderTime = false;

        for (int i = 0; i < _counterCount; i++)
        {
            var state = _counterStates[i];
            if (!state.IsAvailable)
            {
                state.Value = 0;
                _counterStates[i] = state;
                continue;
            }

            nuint value = ReadPerfProgrammableCounter(state.MmapPage, out nuint enabled, out nuint running);
            if (value == nuint.MaxValue)
                return false;

            state.Value = (ulong)value;
            _counterStates[i] = state;

            if (i == leaderIndex)
            {
                leaderEnabled = (ulong)enabled;
                leaderRunning = (ulong)running;
                gotLeaderTime = true;
            }
        }

        if (!gotLeaderTime)
            return false;

        timeEnabled = leaderEnabled;
        timeRunning = leaderRunning;
        return true;
    }

    private void ReadIntoStateValuesByRead(out ulong timeEnabled, out ulong timeRunning)
    {
        int availableCount = 0;
        for (int i = 0; i < _counterCount; i++)
        {
            if (_counterStates[i].IsAvailable)
                availableCount++;
        }

        if (availableCount == 0)
            throw new InvalidOperationException("No available counters to read.");

        int ulongCount = 3 + availableCount * 2; // nr + time_enabled + time_running + [value,id] * N
        int byteCount = checked(ulongCount * sizeof(ulong));
        var raw = new ulong[ulongCount];

        unsafe
        {
            fixed (ulong* ptr = raw)
            {
                nint bytesRead = read(_groupLeaderFd, (nint)ptr, (nuint)byteCount);
                if (bytesRead < 0)
                    ThrowLastPInvokeError($"read(perf fd={_groupLeaderFd}) failed");
                if (bytesRead != byteCount)
                    throw new IOException($"read(perf fd={_groupLeaderFd}) returned {bytesRead} bytes, expected {byteCount}");
            }
        }

        ulong nr = raw[0];
        if (nr > (ulong)availableCount)
            throw new IOException($"perf read returned {nr} counters, expected <= {availableCount}.");

        timeEnabled = raw[1];
        timeRunning = raw[2];

        for (int i = 0; i < _counterCount; i++)
        {
            var state = _counterStates[i];
            if (!state.IsAvailable)
            {
                state.Value = 0;
                _counterStates[i] = state;
                continue;
            }

            state.Value = 0;
            _counterStates[i] = state;
        }

        int baseIndex = 3;
        for (int i = 0; i < (int)nr; i++)
        {
            ulong value = raw[baseIndex + i * 2];
            ulong id = raw[baseIndex + i * 2 + 1];
            int index = IndexOfId(id);
            if (index >= 0)
            {
                var state = _counterStates[index];
                state.Value = value;
                _counterStates[index] = state;
            }
        }
    }

    private void ReadIntoStateDeltas(ulong startTimeEnabled, ulong startTimeRunning, out ulong timeEnabled, out ulong timeRunning)
    {
        var startValues = new ulong[_counterCount];
        for (int i = 0; i < _counterCount; i++)
            startValues[i] = _counterStates[i].Value;

        ReadIntoStateValues(out var endTimeEnabled, out var endTimeRunning);

        for (int i = 0; i < _counterCount; i++)
        {
            var state = _counterStates[i];
            state.Value -= startValues[i];
            _counterStates[i] = state;
        }

        timeEnabled = endTimeEnabled - startTimeEnabled;
        timeRunning = endTimeRunning - startTimeRunning;
    }

    private void BuildSnapshot(PerfCounterSnapshot snapshot, DateTime startUtc, DateTime stopUtc, ulong timeEnabled, ulong timeRunning)
    {
        snapshot.Clear();
        snapshot.StartUtc = startUtc;
        snapshot.StopUtc = stopUtc;
        snapshot.TimeEnabled = timeEnabled;
        snapshot.TimeRunning = timeRunning;

        ref var hardware = ref snapshot.GetHardwareCounters();
        ref var software = ref snapshot.GetSoftwareCounters();
        ref var caches = ref snapshot.GetCacheCounters();

        for (int i = 0; i < _counterCount; i++)
        {
            var state = _counterStates[i];
            if (!state.IsAvailable)
                continue;

            if (state.Type == PerfTypeId.Hardware)
            {
                switch ((PerfHwId)state.Config)
                {
                    case PerfHwId.CpuCycles:
                        hardware.CpuCycles = state.Value;
                        break;
                    case PerfHwId.Instructions:
                        hardware.Instructions = state.Value;
                        break;
                    case PerfHwId.CacheReferences:
                        hardware.CacheReferences = state.Value;
                        break;
                    case PerfHwId.CacheMisses:
                        hardware.CacheMisses = state.Value;
                        break;
                    case PerfHwId.BranchInstructions:
                        hardware.BranchInstructions = state.Value;
                        break;
                    case PerfHwId.BranchMisses:
                        hardware.BranchMisses = state.Value;
                        break;
                    case PerfHwId.BusCycles:
                        hardware.BusCycles = state.Value;
                        break;
                    case PerfHwId.StalledCyclesFrontend:
                        hardware.StalledCyclesFrontend = state.Value;
                        break;
                    case PerfHwId.StalledCyclesBackend:
                        hardware.StalledCyclesBackend = state.Value;
                        break;
                    case PerfHwId.RefCpuCycles:
                        hardware.RefCpuCycles = state.Value;
                        break;
                }

                continue;
            }

            if (state.Type == PerfTypeId.Software)
            {
                switch ((PerfSwIds)state.Config)
                {
                    case PerfSwIds.CpuClock:
                        software.CpuClock = state.Value;
                        break;
                    case PerfSwIds.TaskClock:
                        software.TaskClock = state.Value;
                        break;
                    case PerfSwIds.PageFaults:
                        software.PageFaults = state.Value;
                        break;
                    case PerfSwIds.ContextSwitches:
                        software.ContextSwitches = state.Value;
                        break;
                    case PerfSwIds.CpuMigrations:
                        software.CpuMigrations = state.Value;
                        break;
                    case PerfSwIds.PageFaultsMin:
                        software.PageFaultsMin = state.Value;
                        break;
                    case PerfSwIds.PageFaultsMaj:
                        software.PageFaultsMaj = state.Value;
                        break;
                    case PerfSwIds.AlignmentFaults:
                        software.AlignmentFaults = state.Value;
                        break;
                    case PerfSwIds.EmulationFaults:
                        software.EmulationFaults = state.Value;
                        break;
                    case PerfSwIds.Dummy:
                        software.Dummy = state.Value;
                        break;
                    case PerfSwIds.BpfOutput:
                        software.BpfOutput = state.Value;
                        break;
                    case PerfSwIds.CgroupSwitches:
                        software.CgroupSwitches = state.Value;
                        break;
                }

                continue;
            }

            if (state.Type == PerfTypeId.HardwareCache)
            {
                switch ((PerfCacheId)state.Config)
                {
                    case PerfCacheId.L1DReadAccess:
                        caches.L1DReadAccess = state.Value;
                        break;
                    case PerfCacheId.L1DReadMiss:
                        caches.L1DReadMiss = state.Value;
                        break;
                    case PerfCacheId.L1DWriteAccess:
                        caches.L1DWriteAccess = state.Value;
                        break;
                    case PerfCacheId.L1DWriteMiss:
                        caches.L1DWriteMiss = state.Value;
                        break;
                    case PerfCacheId.L1IReadAccess:
                        caches.L1IReadAccess = state.Value;
                        break;
                    case PerfCacheId.L1IReadMiss:
                        caches.L1IReadMiss = state.Value;
                        break;
                    case PerfCacheId.L1IWriteAccess:
                        caches.L1IWriteAccess = state.Value;
                        break;
                    case PerfCacheId.L1IWriteMiss:
                        caches.L1IWriteMiss = state.Value;
                        break;
                    case PerfCacheId.LLReadAccess:
                        caches.LLReadAccess = state.Value;
                        break;
                    case PerfCacheId.LLReadMiss:
                        caches.LLReadMiss = state.Value;
                        break;
                    case PerfCacheId.LLWriteAccess:
                        caches.LLWriteAccess = state.Value;
                        break;
                    case PerfCacheId.LLWriteMiss:
                        caches.LLWriteMiss = state.Value;
                        break;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int IndexOfId(ulong id)
    {
        for (int i = 0; i < _counterCount; i++)
        {
            if (_counterStates[i].IsAvailable && _counterStates[i].Id == id)
                return i;
        }

        return -1;
    }

    private int CountAvailableCounters()
    {
        int available = 0;
        for (int i = 0; i < _counterCount; i++)
        {
            if (_counterStates[i].IsAvailable)
                available++;
        }

        return available;
    }

    private void CloseOpenFds()
    {
        for (int i = 0; i < _counterCount; i++)
        {
            var state = _counterStates[i];
            if (state.MmapPage != 0 && state.MmapPage != MapFailed)
            {
                _ = munmap(state.MmapPage, (nuint)Environment.SystemPageSize);
                state.MmapPage = 0;
            }

            if (state.Fd >= 0)
            {
                _ = close(state.Fd);
                state.Fd = -1;
            }

            state.HasUserTime = false;
            state.HasUserRdpmc = false;
            _counterStates[i] = state;
        }

        _groupLeaderFd = -1;
        _opened = false;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PerfEventSession));
    }

    private struct PerfCounterState
    {
        public PerfTypeId Type;
        public ulong Config;
        public string Name;
        public int Fd;
        public nint MmapPage;
        public ulong Id;
        public ulong Value;
        public bool IsAvailable;
        public bool HasUserTime;
        public bool HasUserRdpmc;
        public int OpenErrno;
    }

    private const int ProtRead = 0x1;
    private const int MapShared = 0x01;
    private static readonly nint MapFailed = -1;
}
