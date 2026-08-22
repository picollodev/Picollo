using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Picollo.PerfEvent;

internal static partial class NativeMethods
{
    private const string NativeLibrary = "picollo_native";

    [LibraryImport(NativeLibrary, EntryPoint = "is_available")]
    private static partial nuint IsNativeAvailable();

    public static bool IsSupported()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return false;
        }

        if (RuntimeInformation.OSArchitecture != Architecture.X64)
        {
            return false;
        }

        try
        {
            return IsNativeAvailable() == 1;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    [DllImport(NativeLibrary, EntryPoint = "read_perf_programmable_counters", CallingConvention = CallingConvention.Cdecl)]
    [SuppressGCTransition]
    public static extern int ReadPerfProgrammableCounters(nint perfEventMmapPages, nint counterValues, nuint length);

    public static bool TryReadCpuPmuInfo(
        out uint version,
        out uint programmableCounters,
        out uint programmableWidth,
        out uint ebxVectorLength,
        out uint unavailableEventsEbx,
        out uint fixedCounters,
        out uint fixedWidth)
    {
        return ReadCpuPmuInfo(
            out version,
            out programmableCounters,
            out programmableWidth,
            out ebxVectorLength,
            out unavailableEventsEbx,
            out fixedCounters,
            out fixedWidth) != 0;
    }

    public static int PerfEventOpen(in PerfEventAttr attr, int pid, int cpu, int groupFd, nuint flags)
    {
        nint perfEventOpenSyscallNumber = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => 298,
            Architecture.X86 => 336,
            Architecture.Arm64 => 241,
            Architecture.Arm => 364,
            _ => throw new PlatformNotSupportedException(
                $"perf_event_open syscall number is unknown for architecture {RuntimeInformation.ProcessArchitecture}.")
        };

        var fd = SysCallPerfEventOpen(perfEventOpenSyscallNumber, in attr, pid, cpu, groupFd, flags);

        if (fd < 0)
            ThrowLastPInvokeError("perf_event_open failed");

        return checked((int)fd);
    }

    public static PerfEventAttr CreateAttr(PerfTypeId type, ulong config, bool pinned, bool disabled = false, bool excludeKernel = false)
    {
        return new PerfEventAttr
        {
            Type = type,
            Size = (uint)Unsafe.SizeOf<PerfEventAttr>(),
            Config = config,
            SampleType = PerfEventSampleFormat.NONE,
            ReadFormat = PerfEventReadFormat.PERF_FORMAT_GROUP
                         | PerfEventReadFormat.PERF_FORMAT_ID
                         | PerfEventReadFormat.PERF_FORMAT_TOTAL_TIME_ENABLED
                         | PerfEventReadFormat.PERF_FORMAT_TOTAL_TIME_RUNNING,
            Flags = (disabled ? PerfEventAttrFlags.Disabled : 0UL)
                    | (excludeKernel ? (PerfEventAttrFlags.ExcludeKernel | PerfEventAttrFlags.ExcludeHv) : 0UL)
                    | (pinned ? PerfEventAttrFlags.Pinned : 0),
            WakeupEvents = 0,
            BPType = 0
        };
    }

    public static ulong GetEventId(int fd)
    {
        // _IOR('$', 7, __u64 *)
        const ulong PerfEventIocId = 0x80082407;
        int rc = ioctl(fd, PerfEventIocId, out ulong id);
        if (rc != 0)
            ThrowLastPInvokeError($"ioctl(PERF_EVENT_IOC_ID) failed for fd={fd}");
        return id;
    }

    private const nuint PerfIocFlagGroup = 1;

    // _IO('$', n) encodings from linux/ioctl.h
    private const ulong PerfEventIocEnable = 0x2400;
    private const ulong PerfEventIocDisable = 0x2401;
    private const ulong PerfEventIocReset = 0x2403;

    public static void ResetGroup(int fd) => IoctlGroup(fd, PerfEventIocReset, PerfIocFlagGroup);

    public static void EnableGroup(int fd) => IoctlGroup(fd, PerfEventIocEnable, PerfIocFlagGroup);

    public static void DisableGroup(int fd) => IoctlGroup(fd, PerfEventIocDisable, PerfIocFlagGroup);

    public static void ResetEvent(int fd) => IoctlEvent(fd, PerfEventIocReset);

    public static void EnableEvent(int fd) => IoctlEvent(fd, PerfEventIocEnable);

    public static void DisableEvent(int fd) => IoctlEvent(fd, PerfEventIocDisable);

    private static void IoctlGroup(int fd, ulong request, nuint arg)
    {
        int rc = ioctl(fd, request, arg);
        if (rc != 0)
            ThrowLastPInvokeError($"ioctl(0x{request:X}) failed for fd={fd}");
    }

    private static void IoctlEvent(int fd, ulong request)
    {
        int rc = ioctl(fd, request, 0);
        if (rc != 0)
            ThrowLastPInvokeError($"ioctl(0x{request:X}) failed for fd={fd}");
    }

    [DoesNotReturn]
    public static void ThrowLastPInvokeError(string message)
    {
        int error = Marshal.GetLastPInvokeError();
        throw new IOException($"{message}. errno={error} ({new Win32Exception(error).Message})");
    }

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern nint SysCallPerfEventOpen(nint number, in PerfEventAttr attr, int pid, int cpu, int groupFd, nuint flags);

    [DllImport(NativeLibrary, EntryPoint = "read_cpu_pmu_info", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ReadCpuPmuInfo(
        out uint version,
        out uint programmableCounters,
        out uint programmableWidth,
        out uint ebxVectorLength,
        out uint unavailableEventsEbx,
        out uint fixedCounters,
        out uint fixedWidth);

    [DllImport("libc", SetLastError = true, EntryPoint = "ioctl")]
    private static extern int ioctl(int fd, ulong request, nuint arg);

    [DllImport("libc", SetLastError = true, EntryPoint = "ioctl")]
    private static extern int ioctl(int fd, ulong request, out ulong value);

    [DllImport("libc", SetLastError = true)]
    public static extern nint read(int fd, nint buf, nuint count);

    [DllImport("libc", SetLastError = true)]
    public static extern nint mmap(nint addr, nuint length, int prot, int flags, int fd, long offset);

    [DllImport("libc", SetLastError = true)]
    public static extern int munmap(nint addr, nuint length);

    [DllImport("libc", SetLastError = true)]
    public static extern int close(int fd);
}
