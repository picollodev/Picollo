using System;
using System.Text;

namespace Picollo.PerfEvent;

public class PerfCounterSnapshot
{
    public struct HardwareCounters
    {
        public ulong? CpuCycles { get; internal set; }
        public ulong? Instructions { get; internal set; }
        public ulong? CacheReferences { get; internal set; }
        public ulong? CacheMisses { get; internal set; }
        public ulong? BranchInstructions { get; internal set; }
        public ulong? BranchMisses { get; internal set; }
        public ulong? BusCycles { get; internal set; }
        public ulong? StalledCyclesFrontend { get; internal set; }
        public ulong? StalledCyclesBackend { get; internal set; }
        public ulong? RefCpuCycles { get; internal set; }
    }

    public struct SoftwareCounters
    {
        public ulong? CpuClock { get; internal set; }
        public ulong? TaskClock { get; internal set; }
        public ulong? PageFaults { get; internal set; }
        public ulong? ContextSwitches { get; internal set; }
        public ulong? CpuMigrations { get; internal set; }
        public ulong? PageFaultsMin { get; internal set; }
        public ulong? PageFaultsMaj { get; internal set; }
        public ulong? AlignmentFaults { get; internal set; }
        public ulong? EmulationFaults { get; internal set; }
        public ulong? Dummy { get; internal set; }
        public ulong? BpfOutput { get; internal set; }
        public ulong? CgroupSwitches { get; internal set; }
    }

    public struct CacheCounters
    {
        public ulong? L1DReadAccess { get; internal set; }
        public ulong? L1DReadMiss { get; internal set; }
        public ulong? L1DWriteAccess { get; internal set; }
        public ulong? L1DWriteMiss { get; internal set; }

        public ulong? L1IReadAccess { get; internal set; }
        public ulong? L1IReadMiss { get; internal set; }
        public ulong? L1IWriteAccess { get; internal set; }
        public ulong? L1IWriteMiss { get; internal set; }

        public ulong? LLReadAccess { get; internal set; }
        public ulong? LLReadMiss { get; internal set; }
        public ulong? LLWriteAccess { get; internal set; }
        public ulong? LLWriteMiss { get; internal set; }
    }

    private HardwareCounters _hardware;
    private SoftwareCounters _software;
    private CacheCounters _caches;

    public DateTime StartUtc { get; internal set; }
    public DateTime StopUtc { get; internal set; }
    public ulong TimeEnabled { get; internal set; }
    public ulong TimeRunning { get; internal set; }

    public HardwareCounters Hardware => _hardware;
    public SoftwareCounters Software => _software;
    public CacheCounters Caches => _caches;

    internal ref HardwareCounters GetHardwareCounters() => ref _hardware;
    internal ref SoftwareCounters GetSoftwareCounters() => ref _software;
    internal ref CacheCounters GetCacheCounters() => ref _caches;

    public double AdjustedCycles =>
        TimeRunning == 0 || TimeEnabled == 0 || TimeRunning >= TimeEnabled
            ? Hardware.CpuCycles.GetValueOrDefault()
            : Hardware.CpuCycles.GetValueOrDefault() * (double)TimeEnabled / TimeRunning;

    public double InstructionsPerCycle
    {
        get
        {
            var cycles = Hardware.CpuCycles.GetValueOrDefault();
            return cycles == 0 ? 0 : Hardware.Instructions.GetValueOrDefault() / (double)cycles;
        }
    }

    public double CacheMissRatio
    {
        get
        {
            var refs = Hardware.CacheReferences.GetValueOrDefault();
            return refs == 0 ? 0 : Hardware.CacheMisses.GetValueOrDefault() / (double)refs;
        }
    }

    public double BranchMissRatio
    {
        get
        {
            var refs = Hardware.BranchInstructions.GetValueOrDefault();
            return refs == 0 ? 0 : Hardware.BranchMisses.GetValueOrDefault() / (double)refs;
        }
    }

    public void Clear()
    {
        StartUtc = default;
        StopUtc = default;
        TimeEnabled = default;
        TimeRunning = default;
        _hardware = default;
        _software = default;
        _caches = default;
    }

    public string Dump()
    {
        var sb = new StringBuilder(640);
        sb.AppendLine($"StartUtc: {StartUtc:O}");
        sb.AppendLine($"StopUtc: {StopUtc:O}");
        sb.AppendLine($"TimeEnabled: {TimeEnabled:N0}");
        sb.AppendLine($"TimeRunning: {TimeRunning:N0}");

        AppendHardware(sb, Hardware);
        AppendSoftware(sb, Software);
        AppendCaches(sb, Caches);

        sb.AppendLine($"AdjustedCycles: {AdjustedCycles:N0}");
        sb.AppendLine($"InstructionsPerCycle: {InstructionsPerCycle:N2}");

        return sb.ToString();

        static void AppendHardware(StringBuilder sb, HardwareCounters c)
        {
            sb.AppendLine("Hardware:");
            var hasCounters = false;
            hasCounters |= AppendNullable(sb, "CpuCycles", c.CpuCycles);
            hasCounters |= AppendNullable(sb, "Instructions", c.Instructions);
            hasCounters |= AppendNullable(sb, "CacheReferences", c.CacheReferences);
            hasCounters |= AppendNullable(sb, "CacheMisses", c.CacheMisses);
            hasCounters |= AppendNullable(sb, "BranchInstructions", c.BranchInstructions);
            hasCounters |= AppendNullable(sb, "BranchMisses", c.BranchMisses);
            hasCounters |= AppendNullable(sb, "BusCycles", c.BusCycles);
            hasCounters |= AppendNullable(sb, "StalledCyclesFrontend", c.StalledCyclesFrontend);
            hasCounters |= AppendNullable(sb, "StalledCyclesBackend", c.StalledCyclesBackend);
            hasCounters |= AppendNullable(sb, "RefCpuCycles", c.RefCpuCycles);
            if (!hasCounters)
                sb.AppendLine("  N/A");
        }

        static void AppendSoftware(StringBuilder sb, SoftwareCounters c)
        {
            sb.AppendLine("Software:");
            var hasCounters = false;
            hasCounters |= AppendNullable(sb, "CpuClock", c.CpuClock);
            hasCounters |= AppendNullable(sb, "TaskClock", c.TaskClock);
            hasCounters |= AppendNullable(sb, "PageFaults", c.PageFaults);
            hasCounters |= AppendNullable(sb, "ContextSwitches", c.ContextSwitches);
            hasCounters |= AppendNullable(sb, "CpuMigrations", c.CpuMigrations);
            hasCounters |= AppendNullable(sb, "PageFaultsMin", c.PageFaultsMin);
            hasCounters |= AppendNullable(sb, "PageFaultsMaj", c.PageFaultsMaj);
            hasCounters |= AppendNullable(sb, "AlignmentFaults", c.AlignmentFaults);
            hasCounters |= AppendNullable(sb, "EmulationFaults", c.EmulationFaults);
            hasCounters |= AppendNullable(sb, "Dummy", c.Dummy);
            hasCounters |= AppendNullable(sb, "BpfOutput", c.BpfOutput);
            hasCounters |= AppendNullable(sb, "CgroupSwitches", c.CgroupSwitches);
            if (!hasCounters)
                sb.AppendLine("  N/A");
        }

        static void AppendCaches(StringBuilder sb, CacheCounters c)
        {
            sb.AppendLine("Caches:");
            var hasCounters = false;
            hasCounters |= AppendNullable(sb, nameof(CacheCounters.L1DReadAccess), c.L1DReadAccess);
            hasCounters |= AppendNullable(sb, nameof(CacheCounters.L1DReadMiss), c.L1DReadMiss);
            hasCounters |= AppendNullable(sb, nameof(CacheCounters.L1DWriteAccess), c.L1DWriteAccess);
            hasCounters |= AppendNullable(sb, nameof(CacheCounters.L1DWriteMiss), c.L1DWriteMiss);

            hasCounters |= AppendNullable(sb, nameof(CacheCounters.L1IReadAccess), c.L1IReadAccess);
            hasCounters |= AppendNullable(sb, nameof(CacheCounters.L1IReadMiss), c.L1IReadMiss);
            hasCounters |= AppendNullable(sb, nameof(CacheCounters.L1IWriteAccess), c.L1IWriteAccess);
            hasCounters |= AppendNullable(sb, nameof(CacheCounters.L1IWriteMiss), c.L1IWriteMiss);

            hasCounters |= AppendNullable(sb, nameof(CacheCounters.LLReadAccess), c.LLReadAccess);
            hasCounters |= AppendNullable(sb, nameof(CacheCounters.LLReadMiss), c.LLReadMiss);
            hasCounters |= AppendNullable(sb, nameof(CacheCounters.LLWriteAccess), c.LLWriteAccess);
            hasCounters |= AppendNullable(sb, nameof(CacheCounters.LLWriteMiss), c.LLWriteMiss);

            if (!hasCounters)
                sb.AppendLine("  N/A");
        }

        static bool AppendNullable(StringBuilder sb, string name, ulong? value)
        {
            if (!value.HasValue)
                return false;

            sb.AppendLine($"  {name}: {value:N0}");
            return true;
        }
    }
}