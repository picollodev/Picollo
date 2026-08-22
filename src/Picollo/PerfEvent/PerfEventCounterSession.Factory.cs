using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Picollo.PerfEvent;

public partial class PerfEventCounterSession
{
    public static readonly bool IsSupported = OperatingSystem.IsLinux()
                                              && RuntimeInformation.OSArchitecture == Architecture.X64
                                              && NativeMethods.IsSupported();


    public static bool TryGetNumberOfCounters(out uint fixedCounters, out uint programmableCounters)
    {
        fixedCounters = 0;
        programmableCounters = 0;

        return IsSupported &&
               NativeMethods.TryReadCpuPmuInfo(out _, out programmableCounters, out _, out _,
                   out _, out fixedCounters, out _);
    }

    public static bool IsCounterSupported(PerfHardwareCounterId counterId) =>
        IsCounterSupported(PerfTypeId.Hardware, (ulong) counterId);

    public static bool IsCounterSupported(PerfCacheCounterId counterId) =>
        IsCounterSupported(PerfTypeId.HardwareCache, (ulong) counterId);

    private static bool IsCounterSupported(PerfTypeId type, ulong config)
    {
        if (!IsSupported)
            return false;

        var fd = -1;
        try
        {
            var attr = NativeMethods.CreateAttr(type, config, pinned: false, disabled: true, excludeKernel: true);
            fd = NativeMethods.PerfEventOpen(in attr, pid: 0, cpu: -1, groupFd: -1, flags: 0);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        finally
        {
            if (fd >= 0)
                _ = NativeMethods.close(fd);
        }
    }


    /// <summary>
    /// Returns a new session factory, which can be fluently configured and completed with a call to <see cref="PerfEventCounterSessionFactory.Create"/>.
    /// </summary>
    /// <seealso>https://man7.org/linux/man-pages/man2/perf_event_open.2.html</seealso>
    public static PerfEventCounterSessionFactory Factory => new(new PerfEventCounterSession());

    public readonly ref struct PerfEventCounterSessionFactory
    {
        internal readonly PerfEventCounterSession Session = null!;

        public PerfEventCounterSessionFactory() =>
            throw new InvalidOperationException(
                $"Cannot use {nameof(PerfEventCounterSession)}.{nameof(PerfEventCounterSessionFactory)} directly.");

        internal PerfEventCounterSessionFactory(PerfEventCounterSession session)
        {
            Session = session;
        }

        /// <summary>
        /// <para /> Sets the pid and cpu arguments specifying which process and CPU to monitor:
        /// <br /> <b>pid == 0 and cpu == -1</b>: This measures the calling process/thread on any CPU.
        /// <br /> <b>pid == 0 and cpu >= 0</b>: This measures the calling process/thread only when running on the specified CPU.
        /// <br /> <b>pid > 0 and cpu == -1</b>: This measures the specified process/thread on any CPU.
        /// <br /> <b>pid > 0 and cpu >= 0</b>:  This measures the specified process/thread only when running on the specified CPU.
        /// <br /> <b>pid == -1 and cpu >= 0</b>:  This measures all processes/threads on the specified CPU.
        /// This requires CAP_PERFMON (since Linux 5.8) or CAP_SYS_ADMIN capability
        /// or a `/proc/sys/kernel/perf_event_paranoid` value of less than 1.
        /// <br /> <b>pid == -1 and cpu == -1</b>: This setting is invalid and will return an error. 
        /// </summary>
        /// <seealso>https://man7.org/linux/man-pages/man2/perf_event_open.2.html</seealso>
        /// <param name="pid">OS thread/process id to monitor</param>
        /// <param name="cpu">CPU core id to monitor</param>
        /// <returns>A <see cref="PerfEventCounterSessionFactory"/> to configure a new session.</returns>
        /// <exception cref="PlatformNotSupportedException">The platform is not Linux x64</exception>
        /// <exception cref="InvalidOperationException">Both <paramref name="pid"/> and <paramref name="cpu"/> are negative.</exception>
        public PerfEventCounterSessionFactory WithTarget(int pid, int cpu)
        {
            if (pid < 0) pid = -1;
            if (cpu < 0) cpu = -1;

            if (pid == -1 && cpu == -1)
                throw new InvalidOperationException("Either osThreadId or cpu must be set to non-negative value");

            Session.EnsureNotOpened();
            Session.Pid = pid;
            Session.Cpu = cpu;
            return this;
        }

        /// <summary>
        /// Specifies that the counter should always be on the CPU if at all possible.
        /// It applies only to hardware counters and only to group leaders.
        /// If a pinned counter cannot be put onto the CPU (e.g., because there are not
        /// enough hardware counters or because of a conflict with some other event),
        /// then the session cannot be created. 
        /// </summary>
        public PerfEventCounterSessionFactory WithPinned(bool pinned = true)
        {
            Session.EnsureNotOpened();
            Session._pinned = pinned;
            return this;
        }

        /// <summary>
        /// If this is not set, the count excludes events that happen in kernel space.
        /// </summary>
        public PerfEventCounterSessionFactory WithKernel(bool withKernel = true)
        {
            Session.EnsureNotOpened();
            Session._withKernel = withKernel;
            return this;
        }

        /// <summary>
        /// Specifies whether the counter starts out disabled or enabled
        /// </summary>
        /// <param name="enabled"></param>
        /// <returns></returns>
        public PerfEventCounterSessionFactory WithEnabled(bool enabled = true)
        {
            Session.EnsureNotOpened();
            Session._enabled = enabled;
            return this;
        }

        /// <summary>
        /// Adds counters for <see cref="PerfHardwareCounterId.Instructions"/>, <see cref="PerfHardwareCounterId.CpuCycles"/> and <see cref="PerfHardwareCounterId.RefCpuCycles"/>.
        /// These counters should always be available on x64 and do not consume programmable counter slots. 
        /// </summary>
        public PerfEventCounterSessionFactory WithFixedCounters()
        {
            Session.EnsureNotOpened();
            WithHardwareCounter(PerfHardwareCounterId.Instructions);
            WithHardwareCounter(PerfHardwareCounterId.CpuCycles);
            WithHardwareCounter(PerfHardwareCounterId.RefCpuCycles);
            return this;
        }

        /// <summary>
        /// Add 7 hardware counters:
        /// <br /> <see cref="PerfHardwareCounterId.Instructions"/>
        /// <br /> <see cref="PerfHardwareCounterId.CpuCycles"/>
        /// <br /> <see cref="PerfHardwareCounterId.RefCpuCycles"/>
        /// <br /> <see cref="PerfHardwareCounterId.BranchInstructions"/>
        /// <br /> <see cref="PerfHardwareCounterId.BranchMisses"/>
        /// <br /> <see cref="PerfHardwareCounterId.CacheReferences"/>
        /// <br /> <see cref="PerfHardwareCounterId.CacheMisses"/>
        /// </summary>
        /// <returns></returns>
        public PerfEventCounterSessionFactory WithHardwareCounters()
        {
            Session.EnsureNotOpened();
            WithHardwareCounter(PerfHardwareCounterId.Instructions);
            WithHardwareCounter(PerfHardwareCounterId.CpuCycles);
            WithHardwareCounter(PerfHardwareCounterId.RefCpuCycles);
            WithHardwareCounter(PerfHardwareCounterId.BranchInstructions);
            WithHardwareCounter(PerfHardwareCounterId.BranchMisses);
            WithHardwareCounter(PerfHardwareCounterId.CacheReferences);
            WithHardwareCounter(PerfHardwareCounterId.CacheMisses);
            return this;
        }

        /// <summary>
        /// Add a hardware counter specified by <see cref="PerfHardwareCounterId"/>.
        /// </summary>
        public PerfEventCounterSessionFactory WithHardwareCounter(PerfHardwareCounterId hardwareCounterId)
        {
            WithHardwareCounter(hardwareCounterId, out _);
            return this;
        }

        /// <summary>
        /// Add a hardware counter specified by <see cref="PerfHardwareCounterId"/> and get the counter instance as <paramref name="counter"/>.
        /// </summary>
        public PerfEventCounterSessionFactory WithHardwareCounter(PerfHardwareCounterId hardwareCounterId, out PerfEventCounter counter)
        {
            counter = Session.AddCounter(PerfTypeId.Hardware, (ulong)hardwareCounterId);
            return this;
        }

        /// <summary>
        /// Add a software counter specified by <see cref="PerfSoftwareCounterId"/>.
        /// </summary>
        public PerfEventCounterSessionFactory WithSoftwareCounter(PerfSoftwareCounterId softwareCounterId)
        {
            WithSoftwareCounter(softwareCounterId, out _);
            return this;
        }

        /// <summary>
        /// Add a software counter specified by <see cref="PerfSoftwareCounterId"/> and get the counter instance as <paramref name="counter"/>.
        /// </summary>
        public PerfEventCounterSessionFactory WithSoftwareCounter(PerfSoftwareCounterId softwareCounterId, out PerfEventCounter counter)
        {
            counter = Session.AddCounter(PerfTypeId.Software, (ulong)softwareCounterId);
            return this;
        }

        /// <summary>
        /// Add a cache counter specified by <see cref="PerfCacheCounterId"/>.
        /// </summary>
        public PerfEventCounterSessionFactory WithCacheCounter(PerfCacheCounterId cacheCounterId)
        {
            WithCacheCounter(cacheCounterId, out _);
            return this;
        }

        /// <summary>
        /// Add a cache counter specified by <see cref="PerfCacheCounterId"/> and get the counter instance as <paramref name="counter"/>.
        /// </summary>
        public PerfEventCounterSessionFactory WithCacheCounter(PerfCacheCounterId cacheCounterId, out PerfEventCounter counter)
        {
            counter = Session.AddCounter(PerfTypeId.HardwareCache, (ulong)cacheCounterId);
            return this;
        }

        /// <summary>
        /// Add a raw counter specified by a config value.
        /// </summary>
        /// <remarks>
        /// For Intel, see: https://perfmon-events.intel.com/
        /// </remarks>
        public PerfEventCounterSessionFactory WithRawCounter(ulong config)
        {
            WithRawCounter(config, out _);
            return this;
        }

        /// <summary>
        /// Add a raw counter specified by a config value and get the counter instance as <paramref name="counter"/>.
        /// </summary>
        /// <remarks>
        /// For Intel, see: https://perfmon-events.intel.com/
        /// </remarks>
        public PerfEventCounterSessionFactory WithRawCounter(ulong config, out PerfEventCounter counter)
        {
            counter = Session.AddCounter(PerfTypeId.Raw, config);
            return this;
        }

        /// <summary>
        /// Calls the perf_event_open on all added counters and returns an active session.
        /// </summary>
        public PerfEventCounterSession Create()
        {
            Session.Open();
            return Session;
        }
        
        
        // TODO WithFastPathIfAvailable() - there is no way to always use the syscall, not just per read. But HasUserRdpmc can be just forced to false
    }
}
