using System;
using System.Collections;
using System.Collections.Generic;

namespace Picollo.PerfEvent;

public sealed class PerfEventKnownCounters : IReadOnlyList<PerfEventCounter>
{
    public struct HardwareCounters
    {
        public PerfEventCounter? CpuCycles { get; internal set; }
        public PerfEventCounter? Instructions { get; internal set; }
        public PerfEventCounter? CacheReferences { get; internal set; }
        public PerfEventCounter? CacheMisses { get; internal set; }
        public PerfEventCounter? BranchInstructions { get; internal set; }
        public PerfEventCounter? BranchMisses { get; internal set; }
        public PerfEventCounter? BusCycles { get; internal set; }
        public PerfEventCounter? StalledCyclesFrontend { get; internal set; }
        public PerfEventCounter? StalledCyclesBackend { get; internal set; }
        public PerfEventCounter? RefCpuCycles { get; internal set; }
    }

    public struct SoftwareCounters
    {
        public PerfEventCounter? CpuClock { get; internal set; }
        public PerfEventCounter? TaskClock { get; internal set; }
        public PerfEventCounter? PageFaults { get; internal set; }
        public PerfEventCounter? ContextSwitches { get; internal set; }
        public PerfEventCounter? CpuMigrations { get; internal set; }
        public PerfEventCounter? PageFaultsMin { get; internal set; }
        public PerfEventCounter? PageFaultsMaj { get; internal set; }
        public PerfEventCounter? AlignmentFaults { get; internal set; }
        public PerfEventCounter? EmulationFaults { get; internal set; }
        public PerfEventCounter? Dummy { get; internal set; }
        public PerfEventCounter? BpfOutput { get; internal set; }
        public PerfEventCounter? CgroupSwitches { get; internal set; }
    }

    public struct CacheCounters
    {
        public PerfEventCounter? L1DReadAccess { get; internal set; }
        public PerfEventCounter? L1DReadMiss { get; internal set; }
        public PerfEventCounter? L1DWriteAccess { get; internal set; }
        public PerfEventCounter? L1DWriteMiss { get; internal set; }

        public PerfEventCounter? L1IReadAccess { get; internal set; }
        public PerfEventCounter? L1IReadMiss { get; internal set; }
        public PerfEventCounter? L1IWriteAccess { get; internal set; }
        public PerfEventCounter? L1IWriteMiss { get; internal set; }

        public PerfEventCounter? LLReadAccess { get; internal set; }
        public PerfEventCounter? LLReadMiss { get; internal set; }
        public PerfEventCounter? LLWriteAccess { get; internal set; }
        public PerfEventCounter? LLWriteMiss { get; internal set; }
    }

    private readonly List<PerfEventCounter> _counters;
    private HardwareCounters _hardware;
    private SoftwareCounters _software;
    private CacheCounters _caches;

    internal PerfEventKnownCounters(List<PerfEventCounter> counters)
    {
        _counters = counters;
    }

    public int Count => _counters.Count;

    public PerfEventCounter this[int index] => _counters[index];

    public ref readonly HardwareCounters Hardware => ref _hardware;
    public ref readonly SoftwareCounters Software => ref _software;
    public ref readonly CacheCounters Caches => ref _caches;

    public List<PerfEventCounter>.Enumerator GetEnumerator() => _counters.GetEnumerator();

    IEnumerator<PerfEventCounter> IEnumerable<PerfEventCounter>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal void SetCounter(PerfEventCounter counter)
    {
        switch (counter.Type)
        {
            case PerfTypeId.Hardware:
                SetHardwareCounter(counter);
                break;
            case PerfTypeId.Software:
                SetSoftwareCounter(counter);
                break;
            case PerfTypeId.HardwareCache:
                SetCacheCounter(counter);
                break;
        }
    }

    public PerfEventKnownCountersSnapshot GetSnapshot(bool useDeltas = false)
    {
        var snapshot = new PerfEventKnownCountersSnapshot();
        ToSnapshot(snapshot, useDeltas);
        return snapshot;
    }

    public void ToSnapshot(PerfEventKnownCountersSnapshot snapshot, bool useDeltas = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.PopulateFrom(this, useDeltas);
    }

    private void SetHardwareCounter(PerfEventCounter counter)
    {
        switch ((PerfHardwareCounterId)counter.Config)
        {
            case PerfHardwareCounterId.CpuCycles:
                _hardware.CpuCycles = counter;
                break;
            case PerfHardwareCounterId.Instructions:
                _hardware.Instructions = counter;
                break;
            case PerfHardwareCounterId.CacheReferences:
                _hardware.CacheReferences = counter;
                break;
            case PerfHardwareCounterId.CacheMisses:
                _hardware.CacheMisses = counter;
                break;
            case PerfHardwareCounterId.BranchInstructions:
                _hardware.BranchInstructions = counter;
                break;
            case PerfHardwareCounterId.BranchMisses:
                _hardware.BranchMisses = counter;
                break;
            case PerfHardwareCounterId.BusCycles:
                _hardware.BusCycles = counter;
                break;
            case PerfHardwareCounterId.StalledCyclesFrontend:
                _hardware.StalledCyclesFrontend = counter;
                break;
            case PerfHardwareCounterId.StalledCyclesBackend:
                _hardware.StalledCyclesBackend = counter;
                break;
            case PerfHardwareCounterId.RefCpuCycles:
                _hardware.RefCpuCycles = counter;
                break;
        }
    }

    private void SetSoftwareCounter(PerfEventCounter counter)
    {
        switch ((PerfSoftwareCounterId)counter.Config)
        {
            case PerfSoftwareCounterId.CpuClock:
                _software.CpuClock = counter;
                break;
            case PerfSoftwareCounterId.TaskClock:
                _software.TaskClock = counter;
                break;
            case PerfSoftwareCounterId.PageFaults:
                _software.PageFaults = counter;
                break;
            case PerfSoftwareCounterId.ContextSwitches:
                _software.ContextSwitches = counter;
                break;
            case PerfSoftwareCounterId.CpuMigrations:
                _software.CpuMigrations = counter;
                break;
            case PerfSoftwareCounterId.PageFaultsMin:
                _software.PageFaultsMin = counter;
                break;
            case PerfSoftwareCounterId.PageFaultsMaj:
                _software.PageFaultsMaj = counter;
                break;
            case PerfSoftwareCounterId.AlignmentFaults:
                _software.AlignmentFaults = counter;
                break;
            case PerfSoftwareCounterId.EmulationFaults:
                _software.EmulationFaults = counter;
                break;
            case PerfSoftwareCounterId.Dummy:
                _software.Dummy = counter;
                break;
            case PerfSoftwareCounterId.BpfOutput:
                _software.BpfOutput = counter;
                break;
            case PerfSoftwareCounterId.CgroupSwitches:
                _software.CgroupSwitches = counter;
                break;
        }
    }

    private void SetCacheCounter(PerfEventCounter counter)
    {
        switch ((PerfCacheCounterId)counter.Config)
        {
            case PerfCacheCounterId.L1DReadAccess:
                _caches.L1DReadAccess = counter;
                break;
            case PerfCacheCounterId.L1DReadMiss:
                _caches.L1DReadMiss = counter;
                break;
            case PerfCacheCounterId.L1DWriteAccess:
                _caches.L1DWriteAccess = counter;
                break;
            case PerfCacheCounterId.L1DWriteMiss:
                _caches.L1DWriteMiss = counter;
                break;
            case PerfCacheCounterId.L1IReadAccess:
                _caches.L1IReadAccess = counter;
                break;
            case PerfCacheCounterId.L1IReadMiss:
                _caches.L1IReadMiss = counter;
                break;
            case PerfCacheCounterId.L1IWriteAccess:
                _caches.L1IWriteAccess = counter;
                break;
            case PerfCacheCounterId.L1IWriteMiss:
                _caches.L1IWriteMiss = counter;
                break;
            case PerfCacheCounterId.LLReadAccess:
                _caches.LLReadAccess = counter;
                break;
            case PerfCacheCounterId.LLReadMiss:
                _caches.LLReadMiss = counter;
                break;
            case PerfCacheCounterId.LLWriteAccess:
                _caches.LLWriteAccess = counter;
                break;
            case PerfCacheCounterId.LLWriteMiss:
                _caches.LLWriteMiss = counter;
                break;
        }
    }
}