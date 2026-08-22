using System;

namespace Picollo.Profiling;

[Flags]
public enum DiagnosticsFlags : long
{
    // Warn and above are always logged if file/console are enabled

    None = 0,

    WithInfo = 1L << 0,
    WithDebug = WithInfo | 1L << 1,

    // Warn/Error are always enabled

    /// <summary>
    /// Print logs to console
    /// </summary>
    WithConsole = 1L << 2,

    /// <summary>
    /// Write logs to a file
    /// </summary>
    WithFile = 1L << 3,

    WithPingPong = 1L << 4,
    
    /// <summary>
    /// Append /proc/pid/maps snapshots to a file in the log output dir
    /// </summary>
    WithProcMapsCopy = 1L << 4,

    /// <summary>
    /// Append .NET maps snapshots to a file in the log output dir
    /// </summary>
    WithNetMapsCopy = 1L << 5,

    WithNativeModulesDump = 1L << 6,
    WithNativeMethodsDump = 1L << 7,
    WithManagedModulesDump = 1L << 8,
    WithManagedMethodsDump = 1L << 9,

    Default = WithFile
}