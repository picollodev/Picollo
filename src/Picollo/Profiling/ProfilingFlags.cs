using System;

namespace Picollo.Profiling;

[Flags]
public enum ProfilingFlags : long
{
    /// <summary>
    /// Leaf-only sampling
    /// </summary>
    None = 0,
    DisableInlining = 1L << 0,

    WithCallChain = 1L << 1,
    WithGC = 1L << 2,
    WithGCTicks = 1L << 3,
    WithGCAllocations = 1L << 4,

    Default = WithCallChain | WithGC

    // TODO Different features to be added later
}