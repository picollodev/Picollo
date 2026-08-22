using System;
using System.Runtime.InteropServices;

namespace Picollo.Profiler.IpResolution;

[Flags]
public enum IpSampleFlags : byte
{
    None = 0,
    IsKernel = 0b_00_0001
}

[StructLayout(LayoutKind.Explicit, Size = Size)]
internal readonly struct IpSampleHeader
{
    public const int Size = 20;

    /// <summary>
    /// Nanos from CLOCK_MONOTONIC on Linux.
    /// </summary>
    [FieldOffset(0)]
    public readonly ulong Timestamp;

    [FieldOffset(8)]
    public readonly int Pid;

    [FieldOffset(12)]
    public readonly int Tid;

    [FieldOffset(16)]
    public readonly ushort CoreId;

    [FieldOffset(18)]
    public readonly IpSampleFlags Flags;

    [FieldOffset(19)]
    private readonly byte _reserved;

    // After the header:
    // [FieldOffset(20)]
    // ulong[N] IPs where N is inferred from the frame length

    public IpSampleHeader(ulong timestamp, int pid, int tid, ushort coreId, IpSampleFlags flags)
    {
        Timestamp = timestamp;
        Pid = pid;
        Tid = tid;
        CoreId = coreId;
        Flags = flags;
        _reserved = 0;
    }

    public bool IsKernel => (Flags & IpSampleFlags.IsKernel) != 0;
}