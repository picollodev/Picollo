using System;
using System.Diagnostics.CodeAnalysis;

namespace Picollo.PerfEvent;

// perf_type_id
public enum PerfTypeId : uint
{
    Hardware = 0,
    Software = 1,

    // Tracepoint = 2,
    HardwareCache = 3,
    Raw = 4,
    // Breakpoint = 5
}

public enum PerfHardwareCounterId : ulong
{
    /// <summary>
    /// Total cycles.  Be wary of what happens during CPU frequency scaling.
    /// </summary>
    CpuCycles = 0,

    /// <summary>
    /// Retired instructions.  Be careful, these can be affected by various issues, most notably
    /// hardware interrupt counts.
    /// </summary>
    Instructions = 1,

    /// <summary>
    /// Cache accesses.  Usually this indicates Last Level Cache accesses but this may vary
    /// depending on your CPU.  This may include prefetches and coherency messages; again, this
    /// depends on the design of your CPU.
    /// </summary>
    CacheReferences = 2,

    /// <summary>
    /// Cache misses.  Usually this indicates Last Level Cache misses; this is intended to be used
    /// in conjunction with the <see cref="CacheReferences"/> event to calculate cache miss rates.
    /// </summary>
    CacheMisses = 3,

    /// <summary>
    /// Retired branch instructions.
    /// </summary>
    BranchInstructions = 4,

    /// <summary>
    /// Mispredicted branch instructions.
    /// </summary>
    BranchMisses = 5,

    /// <summary>
    /// Bus cycles, which can be different from total cycles.
    /// </summary>
    BusCycles = 6,

    /// <summary>
    /// Stalled cycles during issue.
    /// </summary>
    StalledCyclesFrontend = 7,

    /// <summary>
    /// Stalled cycles during retirement.
    /// </summary>
    StalledCyclesBackend = 8,

    /// <summary>
    /// Total cycles; not affected by CPU frequency scaling.
    /// </summary>
    RefCpuCycles = 9
}

public enum PerfSoftwareCounterId : ulong
{
    /// <summary>
    /// This reports the CPU clock, a high-resolution per-CPU timer.
    /// </summary>
    CpuClock = 0,

    /// <summary>
    /// This reports a clock count specific to the task that is running.
    /// </summary>
    TaskClock = 1,

    /// <summary>
    /// This reports the number of page faults.
    /// </summary>
    PageFaults = 2,

    /// <summary>
    /// This counts context switches.  Until Linux  2.6.34, these were all reported as user-space
    /// events, after that they are reported as happening in the kernel.
    /// </summary>
    ContextSwitches = 3,

    /// <summary>
    /// This reports the number of times the process has migrated to a new CPU.
    /// </summary>
    CpuMigrations = 4,

    /// <summary>
    /// This counts the number of minor page faults. These did not require disk I/O to handle.
    /// </summary>
    PageFaultsMin = 5,

    /// <summary>
    /// This counts the number of major page faults. These required disk I/O to handle.
    /// </summary>
    PageFaultsMaj = 6,

    /// <summary>
    /// This counts the number of alignment faults. These happen when unaligned memory accesses
    /// happen; the kernel can handle these but it reduces performance.  This happens only on some
    /// architectures (never on x86).
    /// </summary>
    AlignmentFaults = 7,

    /// <summary>
    /// This counts the number of emulation faults. The kernel sometimes traps on unimplemented
    /// instructions and emulates them for user space. This can negatively impact performance.
    /// </summary>
    EmulationFaults = 8,

    /// <summary>
    /// This is a placeholder event that counts nothing.  Informational sample record types
    /// such as mmap or comm must be associated with an active event.  This dummy event allows
    /// gathering such records without requiring a counting event.
    /// </summary>
    Dummy = 9,

    /// <summary>
    /// This is used to generate raw sample data from BPF.  BPF programs can write to this event
    /// using bpf_perf_event_output helper.
    /// </summary>
    BpfOutput = 10,

    /// <summary>
    /// This counts context switches to a task in a different cgroup.  In other words, if the next
    /// task is in the same cgroup, it won't count the switch.
    /// </summary>
    CgroupSwitches = 11
}

internal enum PerfHwCacheId : ulong
{
    L1D = 0,
    L1I = 1,
    LL = 2,
    DTLB = 3,
    ITLB = 4,
    BPU = 5,
    Node = 6
}

internal enum PerfHwCacheOpId : ulong
{
    Read = 0,
    Write = 1,
    Prefetch = 2
}

internal enum PerfHwCacheOpResultId : ulong
{
    Access = 0,
    Miss = 1
}

[SuppressMessage("ReSharper", "ShiftExpressionZeroLeftOperand")]
public enum PerfCacheCounterId : ulong
{
    L1DReadAccess = PerfHwCacheId.L1D | (PerfHwCacheOpId.Read << 8) | (PerfHwCacheOpResultId.Access << 16),
    L1DReadMiss = PerfHwCacheId.L1D | (PerfHwCacheOpId.Read << 8) | (PerfHwCacheOpResultId.Miss << 16),
    L1DWriteAccess = PerfHwCacheId.L1D | (PerfHwCacheOpId.Write << 8) | (PerfHwCacheOpResultId.Access << 16),
    L1DWriteMiss = PerfHwCacheId.L1D | (PerfHwCacheOpId.Write << 8) | (PerfHwCacheOpResultId.Miss << 16),

    L1IReadAccess = PerfHwCacheId.L1I | (PerfHwCacheOpId.Read << 8) | (PerfHwCacheOpResultId.Access << 16),
    L1IReadMiss = PerfHwCacheId.L1I | (PerfHwCacheOpId.Read << 8) | (PerfHwCacheOpResultId.Miss << 16),
    L1IWriteAccess = PerfHwCacheId.L1I | (PerfHwCacheOpId.Write << 8) | (PerfHwCacheOpResultId.Access << 16),
    L1IWriteMiss = PerfHwCacheId.L1I | (PerfHwCacheOpId.Write << 8) | (PerfHwCacheOpResultId.Miss << 16),

    LLReadAccess = PerfHwCacheId.LL | (PerfHwCacheOpId.Read << 8) | (PerfHwCacheOpResultId.Access << 16),
    LLReadMiss = PerfHwCacheId.LL | (PerfHwCacheOpId.Read << 8) | (PerfHwCacheOpResultId.Miss << 16),
    LLWriteAccess = PerfHwCacheId.LL | (PerfHwCacheOpId.Write << 8) | (PerfHwCacheOpResultId.Access << 16),
    LLWriteMiss = PerfHwCacheId.LL | (PerfHwCacheOpId.Write << 8) | (PerfHwCacheOpResultId.Miss << 16)
}

// perf_event_sample_format
[Flags]
internal enum PerfEventSampleFormat : ulong
{
    NONE = 0,
    PERF_SAMPLE_IP = 1U << 0,
    PERF_SAMPLE_TID = 1U << 1,
    PERF_SAMPLE_TIME = 1U << 2,
    PERF_SAMPLE_ADDR = 1U << 3,
    PERF_SAMPLE_READ = 1U << 4,
    PERF_SAMPLE_CALLCHAIN = 1U << 5,
    PERF_SAMPLE_ID = 1U << 6,
    PERF_SAMPLE_CPU = 1U << 7,
    PERF_SAMPLE_PERIOD = 1U << 8,
    PERF_SAMPLE_STREAM_ID = 1U << 9,
    PERF_SAMPLE_RAW = 1U << 10,
    PERF_SAMPLE_BRANCH_STACK = 1U << 11,
    PERF_SAMPLE_REGS_USER = 1U << 12,
    PERF_SAMPLE_STACK_USER = 1U << 13,
    PERF_SAMPLE_WEIGHT = 1U << 14,
    PERF_SAMPLE_DATA_SRC = 1U << 15,
    PERF_SAMPLE_IDENTIFIER = 1U << 16,
    PERF_SAMPLE_TRANSACTION = 1U << 17,
    PERF_SAMPLE_REGS_INTR = 1U << 18,
    PERF_SAMPLE_PHYS_ADDR = 1U << 19,
    PERF_SAMPLE_AUX = 1U << 20, // 5.5
    PERF_SAMPLE_CGROUP = 1U << 21, // 5.7
    PERF_SAMPLE_MAX = 1U << 22, /* non-ABI */
}

// perf_event_read_format
[Flags]
internal enum PerfEventReadFormat : ulong
{
    PERF_FORMAT_TOTAL_TIME_ENABLED = 1U << 0,
    PERF_FORMAT_TOTAL_TIME_RUNNING = 1U << 1,
    PERF_FORMAT_ID = 1U << 2,
    PERF_FORMAT_GROUP = 1U << 3,
    PERF_FORMAT_LOST = 1U << 4,
    PERF_FORMAT_MAX = 1U << 5, /* non-ABI */
}

// perf_event_attr_flags
[Flags]
internal enum PerfEventAttrFlags : ulong
{
    Disabled = 1, /* off by default        */
    Inherit = 1 << 1, /* children inherit it   */
    Pinned = 1 << 2, /* must always be on PMU */
    Exclusive = 1 << 3, /* only group on PMU     */
    ExcludeUser = 1 << 4, /* don't count user      */
    ExcludeKernel = 1 << 5, /* ditto kernel          */
    ExcludeHv = 1 << 6, /* ditto hypervisor      */
    ExcludeIdle = 1 << 7, /* don't count when idle */
    MMap = 1 << 8, /* include mmap data     */
    Comm = 1 << 9, /* include comm data     */
    Freq = 1 << 10, /* use freq, not period  */
    InheritStat = 1 << 11, /* per task counts       */
    EnableOnExec = 1 << 12, /* next exec enables     */
    Task = 1 << 13, /* trace fork/exit       */
    Watermark = 1 << 14, /* wakeup_watermark      */

    /*
     * precise_ip:
     *
     *  0 - SAMPLE_IP can have arbitrary skid
     *  1 - SAMPLE_IP must have constant skid
     *  2 - SAMPLE_IP requested to have 0 skid
     *  3 - SAMPLE_IP must have 0 skid
     */

    PreciseIP1 = 1 << 15,
    PreciseIP2 = 1 << 16,
    MMapData = 1 << 17, /* non-exec mmap data    */
    SampleIdAll = 1 << 18, /* sample_type all events */
    ExcludeHost = 1 << 19, /* don't count in host   */
    ExcludeGuest = 1 << 20, /* don't count in guest  */
    ExcludeCallChainKernel = 1 << 21, /* exclude kernel callchains */
    ExcludeCallChainUser = 1 << 22, /* exclude user callchains */
    MMap2 = 1 << 23, /* include mmap with inode data     */
    CommExec = 1 << 24, /* flag comm events that are due to an exec */
    UseClockId = 1 << 25, /* use @clockid for time fields */
    ContextSwitch = 1 << 26, /* context switch data */
    WriteBackward = 1 << 27, /* Write ring buffer from end to beginning */
    Namespaces = 1 << 28, /* include namespaces data */
    KSymbol = 1 << 29, // 5.1           /* context switch data */
    BPFEvent = 1 << 30, // 5.1          /* include bpf events */
    AUXOutput = 1UL << 31, // 5.4       /* generate AUX records instead of events */
    CGroup = 1UL << 32, // 5.7          /* include cgroup events */
    TextPoke = 1UL << 33, /* include text poke events */
    BuildId = 1UL << 34, /* use build ID in mmap2 events */
    InheritThread = 1UL << 35, /* children only inherit if cloned with CLONE_THREAD */
    RemoveOnExec = 1UL << 36, /* event is removed from task on exec */
    Sigtrap = 1UL << 37, /* send synchronous SIGTRAP on event */
}

[Flags]
internal enum PerfEventBreakpointType : uint
{
    HW_BREAKPOINT_EMPTY = 0,
    HW_BREAKPOINT_R = 1,
    HW_BREAKPOINT_W = 2,
    HW_BREAKPOINT_RW = HW_BREAKPOINT_R | HW_BREAKPOINT_W,
    HW_BREAKPOINT_X = 4,
    HW_BREAKPOINT_INVALID = HW_BREAKPOINT_RW | HW_BREAKPOINT_X,
}

[Flags]
internal enum PerfBranchSampleType : ulong
{
    PERF_SAMPLE_BRANCH_USER = 1U << PerfBranchSampleTypeShift.PERF_SAMPLE_BRANCH_USER_SHIFT,
    PERF_SAMPLE_BRANCH_KERNEL = 1U << PerfBranchSampleTypeShift.PERF_SAMPLE_BRANCH_KERNEL_SHIFT,
    PERF_SAMPLE_BRANCH_HV = 1U << PerfBranchSampleTypeShift.PERF_SAMPLE_BRANCH_HV_SHIFT,
    PERF_SAMPLE_BRANCH_ANY = 1U << PerfBranchSampleTypeShift.PERF_SAMPLE_BRANCH_ANY_SHIFT,
    PERF_SAMPLE_BRANCH_ANY_CALL = 1U << PerfBranchSampleTypeShift.PERF_SAMPLE_BRANCH_ANY_CALL_SHIFT,
    PERF_SAMPLE_BRANCH_ANY_RETURN = 1U << PerfBranchSampleTypeShift.PERF_SAMPLE_BRANCH_ANY_RETURN_SHIFT,
    PERF_SAMPLE_BRANCH_IND_CALL = 1U << PerfBranchSampleTypeShift.PERF_SAMPLE_BRANCH_IND_CALL_SHIFT,
    PERF_SAMPLE_BRANCH_ABORT_TX = 1U << PerfBranchSampleTypeShift.PERF_SAMPLE_BRANCH_ABORT_TX_SHIFT,
    PERF_SAMPLE_BRANCH_IN_TX = 1U << PerfBranchSampleTypeShift.PERF_SAMPLE_BRANCH_IN_TX_SHIFT,
    PERF_SAMPLE_BRANCH_NO_TX = 1U << PerfBranchSampleTypeShift.PERF_SAMPLE_BRANCH_NO_TX_SHIFT,
    PERF_SAMPLE_BRANCH_COND = 1U << PerfBranchSampleTypeShift.PERF_SAMPLE_BRANCH_COND_SHIFT,
    PERF_SAMPLE_BRANCH_CALL_STACK = 1U << PerfBranchSampleTypeShift.PERF_SAMPLE_BRANCH_CALL_STACK_SHIFT,
    PERF_SAMPLE_BRANCH_IND_JUMP = 1U << PerfBranchSampleTypeShift.PERF_SAMPLE_BRANCH_IND_JUMP_SHIFT,
    PERF_SAMPLE_BRANCH_CALL = 1U << PerfBranchSampleTypeShift.PERF_SAMPLE_BRANCH_CALL_SHIFT,
    PERF_SAMPLE_BRANCH_NO_FLAGS = 1U << PerfBranchSampleTypeShift.PERF_SAMPLE_BRANCH_NO_FLAGS_SHIFT,
    PERF_SAMPLE_BRANCH_NO_CYCLES = 1U << PerfBranchSampleTypeShift.PERF_SAMPLE_BRANCH_NO_CYCLES_SHIFT,
    PERF_SAMPLE_BRANCH_TYPE_SAVE = 1U << PerfBranchSampleTypeShift.PERF_SAMPLE_BRANCH_TYPE_SAVE_SHIFT,
    PERF_SAMPLE_BRANCH_MAX = 1U << PerfBranchSampleTypeShift.PERF_SAMPLE_BRANCH_MAX_SHIFT,
}

// perf_branch_sample_type_shift
internal enum PerfBranchSampleTypeShift
{
    PERF_SAMPLE_BRANCH_USER_SHIFT = 0, /* user branches */
    PERF_SAMPLE_BRANCH_KERNEL_SHIFT = 1, /* kernel branches */
    PERF_SAMPLE_BRANCH_HV_SHIFT = 2, /* hypervisor branches */
    PERF_SAMPLE_BRANCH_ANY_SHIFT = 3, /* any branch types */
    PERF_SAMPLE_BRANCH_ANY_CALL_SHIFT = 4, /* any call branch */
    PERF_SAMPLE_BRANCH_ANY_RETURN_SHIFT = 5, /* any return branch */
    PERF_SAMPLE_BRANCH_IND_CALL_SHIFT = 6, /* indirect calls */
    PERF_SAMPLE_BRANCH_ABORT_TX_SHIFT = 7, /* transaction aborts */
    PERF_SAMPLE_BRANCH_IN_TX_SHIFT = 8, /* in transaction */
    PERF_SAMPLE_BRANCH_NO_TX_SHIFT = 9, /* not in transaction */
    PERF_SAMPLE_BRANCH_COND_SHIFT = 10, /* conditional branches */
    PERF_SAMPLE_BRANCH_CALL_STACK_SHIFT = 11, /* call/ret stack */
    PERF_SAMPLE_BRANCH_IND_JUMP_SHIFT = 12, /* indirect jumps */
    PERF_SAMPLE_BRANCH_CALL_SHIFT = 13, /* direct call */
    PERF_SAMPLE_BRANCH_NO_FLAGS_SHIFT = 14, /* no flags */
    PERF_SAMPLE_BRANCH_NO_CYCLES_SHIFT = 15, /* no cycles */
    PERF_SAMPLE_BRANCH_TYPE_SAVE_SHIFT = 16, /* save branch type */
    PERF_SAMPLE_BRANCH_MAX_SHIFT, /* non-ABI */
}

internal enum ClockConstants
{
    Realtime = 0,
    Monotonic = 1,
    MonotonicRaw = 4,
}

// perf_event_type
internal enum PerfEventType : uint
{
    PERF_RECORD_MMAP = 1,
    PERF_RECORD_LOST = 2,
    PERF_RECORD_COMM = 3,
    PERF_RECORD_EXIT = 4,
    PERF_RECORD_THROTTLE = 5,
    PERF_RECORD_UNTHROTTLE = 6,
    PERF_RECORD_FORK = 7,
    PERF_RECORD_READ = 8,
    PERF_RECORD_SAMPLE = 9,
    PERF_RECORD_MMAP2 = 10,
    PERF_RECORD_AUX = 11,
    PERF_RECORD_ITRACE_START = 12,
    PERF_RECORD_LOST_SAMPLES = 13,
    PERF_RECORD_SWITCH = 14,
    PERF_RECORD_SWITCH_CPU_WIDE = 15,
    PERF_RECORD_NAMESPACES = 16,
    PERF_RECORD_KSYMBOL = 17, // 5.1
    PERF_RECORD_BPF_EVENT = 18, // 5.1
    PERF_RECORD_CGROUP = 19, // 5.7
    PERF_RECORD_TEXT_POKE = 20, // 5.9
    PERF_RECORD_AUX_OUTPUT_HW_ID = 21,
    PERF_RECORD_CALLCHAIN_DEFERRED = 22,
    PERF_RECORD_MAX,
}