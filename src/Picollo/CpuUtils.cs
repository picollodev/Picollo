using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Picollo;

public static unsafe class CpuUtils
{
    public static nint GetCpu()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return (nint)GetCurrentProcessorNumber();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return sched_getcpu();
        return -1;
    }

    private static nint GetNativeThreadId()
    {
        if (OperatingSystem.IsWindows())
            return (nint)GetCurrentThreadId();
        if (OperatingSystem.IsLinux())
        {
            try
            {
                return gettid();
            }
            catch
            {
                return -1;
            }
        }

        return -1;
    }

    // public static LinuxPerfCounterSession CreateCpuCyclesSessionForTid(int tid)
    // {
    //     return LinuxPerfCounterSession
    //         .CreateForTid(tid)
    //         .AddHardwareCounter(PerfHwId.CpuCycles)
    //         .AddHardwareCounter(PerfHwId.Instructions)
    //         .AddHardwareCounter(PerfHwId.CacheReferences)
    //         .AddHardwareCounter(PerfHwId.CacheMisses)
    //         .AddHardwareCounter(PerfHwId.BranchInstructions)
    //         .AddHardwareCounter(PerfHwId.BranchMisses);
    // }
    //
    // public static LinuxPerfCounterSession CreateCpuCyclesSessionForCurrentThread()
    // {
    //     var tid = checked((int)GetNativeThreadId());
    //     if (tid <= 0)
    //         throw new InvalidOperationException("Unable to resolve current native thread ID.");
    //     return CreateCpuCyclesSessionForTid(tid);
    // }

    public static ProcessThread? GetCurrentProcessThread()
    {
        var tid = GetNativeThreadId();
        var proc = Process.GetCurrentProcess();
        for (var i = 0; i < proc.Threads.Count; i++)
        {
            ProcessThread t = proc.Threads[i];
            if (t.Id == tid)
                return t;
        }

        return null;
    }

    /// <summary>
    /// On Linux: lowers process priority (nice +10),
    /// restores current thread priority (nice 0),
    /// and pins current thread to a given logical core.
    /// </summary>
    public static unsafe bool PrepareBenchmarkThread(int coreId)
    {
        try
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return false;

            // Lower the whole process priority
            setpriority(PRIO_PROCESS, 0, 10);

            // Restore current thread to normal priority
            setpriority((int)GetNativeThreadId(), 0, 0);

            // Pin to the selected core
            unsafe
            {
                ulong* bits = stackalloc ulong[16];
                bits[coreId / 64] |= 1UL << (coreId % 64);
                return sched_setaffinity(0, (nint)(8 * 16), (nint)bits) == 0;
            }
        }
        catch
        {
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static double WarmupCpu()
    {
        const int blocks = 1024;

        // 1–2 ms warm-up suffices for most x86, and 3–5 ms for ARM-based CPUs to guarantee stable frequency before measurement
        long targetTicks = (long)(0.002 * Stopwatch.Frequency);
        var count = 0L;
        long stop;
        long start = Stopwatch.GetTimestamp();
        targetTicks += start;
        while (true)
        {
            AddChain256(blocks);
            count++;
            stop = Stopwatch.GetTimestamp();
            if (stop >= targetTicks)
                break;
        }

        var iterations = count * blocks * 256;
        var frequency = iterations / Stopwatch.GetElapsedTime(start, stop).TotalSeconds;
        return frequency;
    }

    private static long _sink;

    /// <summary>
    /// Runs for 256 cycles per block amortized.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static void AddChain256(int blocks)
    {
        // Based on benchmarks, it looks like the loop control is fully ILP-ed
        long x = 1;
        long y = blocks;
        while (blocks-- > 0)
        {
            // @formatter:off
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
                
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;

            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
                
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; // y += x;
            // @formatter:on
        }

        Volatile.Write(ref _sink, x + y); // observable; minimal overhead
    }

    /// <summary>
    /// Runs for 256 cycles per block amortized.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static void AddChain512(int blocks)
    {
        // Based on benchmarks, it looks like the loop control is fully ILP-ed
        long x = 1;
        long y = blocks;
        while (blocks-- > 0)
        {
            // @formatter:off
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
                
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;

            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
                
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
                
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;

            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
                
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; y += x;
            x += y; y += x; x += y; y += x; x += y; y += x; x += y; // y += x;
            // @formatter:on
        }

        Volatile.Write(ref _sink, x + y); // observable; minimal overhead
    }

    // Windows
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessorNumber();

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll")]
    private static extern nuint SetThreadAffinityMask(nint hThread, nuint dwMask);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    // Linux
    [DllImport("libc")]
    private static extern int sched_getcpu();

    [DllImport("libc")]
    private static extern int sched_setaffinity(int pid, nint cpusetsize, nint mask);

    [DllImport("libc")]
    private static extern int getpriority(int which, int who);

    [DllImport("libc")]
    private static extern int setpriority(int which, int who, int prio);

    [DllImport("libc")]
    private static extern int gettid();

    // Constants for getpriority/setpriority
    private const int PRIO_PROCESS = 0;
}