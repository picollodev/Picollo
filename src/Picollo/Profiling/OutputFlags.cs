using System;

namespace Picollo.Profiling;

[Flags]
public enum OutputFlags : long
{
    /// <summary>
    /// Always produce a summary with Total/Own/Own+ per node per time segment.
    /// </summary>
    None = 0,

    /// <summary>
    /// Produce simple caller/callee details per node, without full callstack details.
    /// </summary>
    WithCallCounters = 1L << 0,

    /// <summary>
    /// Produce Picolloscope output
    /// </summary>
    WithSamplingProfile = 1L << 1,
    
    // Not supported initially, may be dropped as Picolloscope output allows to rebuild call trees, no need to do it live.
    
    // /// <summary>
    // /// Produce simple caller/callee details per node, without full callstack details.
    // /// </summary>
    // WithCallTree = 1L << 2,
    //
    // /// <summary>
    // /// Write different segments to separate files, instead of adding segment marks in a single file.
    // /// </summary>
    // WithSegmentPerFile = 1L << 3,
    
    Default = WithCallCounters | WithSamplingProfile
}