using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Picollo.Metrics;
using Picollo.PerfEvent;

namespace Picollo.Runner;

public class PerfSessionSamples
{
    public static void PerfSessionGettingStarted()
    {
        if (!PerfEventCounterSession.TryGetNumberOfCounters(out uint fixedCounters, out uint programmableCounters))
            Console.WriteLine("CPU PMU: unavailable");

        Console.WriteLine($"CPU PMU: fixedCounters={fixedCounters}, programmableCounters={programmableCounters}");

        var counterSession = PerfEventCounterSession.Factory
            .WithTarget((int)0, -1) // By default, it's the current OS thread on any CPU core
            .WithPinned(true) // Schedule all or nothing
            .WithKernel(false) // Depends, for pure C# userspace code better to have it false
            .WithEnabled(true) // Start as enabled, usually the right choice unless there are reasons, e.g. multiple sessions
            // .WithHardwareCounters(PerfHwId.CpuCycles)
            // .WithHardwareCounters(PerfHwId.Instructions)
            // .WithHardwareCounters(PerfHwId.RefCpuCycles)
            .WithFixedCounters() // Same as the 3 commented above. They are usually available and do not consume programmable counter slots
            .WithHardwareCounter(PerfHardwareCounterId.BranchMisses)
            .WithCacheCounter(PerfCacheCounterId.L1DReadMiss)
            .WithCacheCounter(PerfCacheCounterId.L1IReadMiss)
            // Often it's trial & error, even the standard counters may be unavailable
            // E.g. the next two do not work on WSL
            // .WithCacheCounter(PerfCacheCounterId.LLReadAccess) // Poor man's proxy for L2 misses
            // .WithCacheCounter(PerfCacheCounterId.LLReadMiss)
            .Create();

        for (int i = 0; i < 10_000; i++)
        {
            // Record the current state of the counters
            // ForceSyscallRead forces the read to always use a syscall even if a RDPMC-based fast path is available.
            // It's more stable and atomic per session, but significantly slower.
            counterSession.Read(forceSyscallRead: true);

            // A helper that contains 256 simple instructions that cannot be reordered or optimized
            // The expected work is 256k cycles
            CpuUtils.AddChain256(1000);

            // Record the counters again. The implementation keeps track of the previous read result and can calculate deltas 
            counterSession.Read(forceSyscallRead: true);

            // Record counter delta values into per-counter HDR histogram  
            counterSession.Record(deltas: true);

            if (i == 1000)
                counterSession.Reset(); // Warm up
        }

        foreach (PerfEventCounter counter in counterSession.Counters)
        {
            counter.Histogram.GetSummary().PrettyPrint(counter.Name);
        }
    }

    public static void PerfSessionSorted()
    {
        if (!PerfEventCounterSession.TryGetNumberOfCounters(out uint fixedCounters, out uint programmableCounters))
            Console.WriteLine("CPU PMU: unavailable");

        Console.WriteLine($"CPU PMU: fixedCounters={fixedCounters}, programmableCounters={programmableCounters}");

        var values = new byte[1000_000];
        Random.Shared.NextBytes(values);

        var counterSession = PerfEventCounterSession.Factory
            .WithKernel(false)
            .WithEnabled(true)
            .WithHardwareCounter(PerfHardwareCounterId.CpuCycles)
            .WithHardwareCounter(PerfHardwareCounterId.BranchMisses)
            .Create();

        var summaryUnsortedCycles = new HdrHistogramSummary();
        var summaryUnsortedBranches = new HdrHistogramSummary();
        double acc = 0;

        for (int r = 0; r < 2; r++)
        {
            for (int i = 0; i < 1000; i++)
            {
                counterSession.Read(forceSyscallRead: true);

                foreach (byte value in values)
                {
                    if (value >= 128)
                        acc += 100;
                    else
                        acc *= 0.9999;
                }

                counterSession.Read(forceSyscallRead: true);

                counterSession.Record(deltas: true);

                if (i == 1000)
                    counterSession.Reset();
            }

            if (r == 0)
            {
                counterSession.Counters.Hardware.CpuCycles!.Histogram.GetSummary(summaryUnsortedCycles);
                counterSession.Counters.Hardware.BranchMisses!.Histogram.GetSummary(summaryUnsortedBranches);

                counterSession.Reset();
                Array.Sort(values);

                if (acc < 0)
                    throw new Exception("Do not optimize");
                acc = 0;
            }
        }

        var summarySortedCycles = counterSession.Counters.Hardware.CpuCycles!.Histogram.GetSummary();
        var summarySortedBranches = counterSession.Counters.Hardware.BranchMisses!.Histogram.GetSummary();

        summaryUnsortedCycles.PrettyPrintDiff(summarySortedCycles, "CpuCycles", "Unsorted", "Sorted");
        summaryUnsortedBranches.PrettyPrintDiff(summarySortedBranches, "BranchMisses", "Unsorted", "Sorted");

    }

    public static void PerfSessionSample()
    {
        var pinned = CpuUtils.PrepareBenchmarkThread(8);
        Console.WriteLine($"Pinned: {pinned}");

        var tid = CpuUtils.GetOsThreadId();
        using var perfSession =
            PerfEventCounterSession.Factory
                .WithTarget((int)tid, -1)
                .WithPinned(true)
                .WithKernel(false)
                .WithEnabled(true)
                .WithHardwareCounters()
                // .WithHardwareCounters(PerfHwId.CpuCycles)
                // .WithHardwareCounters(PerfHwId.Instructions)
                // .WithHardwareCounters(PerfHwId.RefCpuCycles)
                // .WithHardwareCounters(PerfHwId.BranchMisses)
                // .WithHardwareCounters(PerfSwIds.CpuClock)
                // .WithHardwareCounters(PerfSwIds.PageFaults)
                // .WithHardwareCounters(PerfSwIds.CpuMigrations)
                // .WithHardwareCounters(PerfSwIds.Dummy)
                .Create();

        foreach (PerfEventCounter counter in perfSession.Counters)
        {
            Console.WriteLine($"Overhead for counter {counter.Name} is: {counter.PairReadOverhead:N0}");
        }

        Console.WriteLine($"IsAvailable: {PerfHelpers.IsAvailable()}");

        Console.WriteLine($"hasUserTime: {perfSession.HasUserTime}, hasUserRdpmc: {perfSession.HasUserRdpmc}");

        if (!perfSession.HasUserRdpmc)
        {
            Console.WriteLine("RDPMC user access is not available; skipping RDPMC measurement loop to avoid native crash.");
            // return;
        }

        const ulong CounterMask48 = 0x0000FFFFFFFFFFFFUL;
        const int Cycles = 1000;
        const int CallsPerSample = 10_000;

        var sharedSamples = new List<nuint>(Cycles);

        CollectPerfEvent(perfSession);

        CollectAndReport48("inst", static () => PerfHelpers.ReadInstructionsRetired());

        for (int r = 0; r < 1; r++)
        {
            CollectAndReport48("core", static () => PerfHelpers.ReadCoreCycles());
        }

        CollectAndReport48("core_lfence", static () => PerfHelpers.ReadCoreCyclesLfence());
        CollectAndReport48("core_lfence_both", static () => PerfHelpers.ReadCoreCyclesLfenceBoth());
        CollectAndReport48("ref", static () => PerfHelpers.ReadReferenceCycles());

        CollectAndReport64("tsc", static () => PerfHelpers.ReadRdtsc());
        CollectAndReport64("tscp", static () => PerfHelpers.ReadRdtscp());
        CollectAndReport64("stopwatch", static () => unchecked((nuint)Stopwatch.GetTimestamp()));

        Console.WriteLine("--- 1M call cost measured in ref cycles ---");
        MeasureCallInst();
        MeasureCallCore();
        MeasureCallCoreLfence();
        MeasureCallCoreLfenceBoth();
        MeasureCallRef();
        MeasureCallRdtsc();
        MeasureCallRdtscp();
        MeasureCallStopwatch();

        return;

        nuint Delta48(nuint after, nuint before)
        {
            return (nuint)(((ulong)after - (ulong)before) & CounterMask48);
        }

        void CollectAndReport48(string name, Func<nuint> read)
        {
            sharedSamples.Clear();
            for (var cycle = 1; cycle <= Cycles; cycle++)
            {
                var before = read();
                CpuUtils.AddChain512(10000);
                var after = read();
                var delta = Delta48(after, before);
                sharedSamples.Add(delta);

                // Console.WriteLine($"cycle: {cycle}, {name}: {delta}");
            }

            PrintStats(name);
        }

        void CollectAndReport64(string name, Func<nuint> read)
        {
            sharedSamples.Clear();
            for (var cycle = 1; cycle <= Cycles; cycle++)
            {
                var before = read();
                CpuUtils.AddChain512(Workload.Count);
                var after = read();
                var delta = unchecked(after - before);
                sharedSamples.Add(delta);

                // Console.WriteLine($"cycle: {cycle}, {name}: {delta}");
            }

            PrintStats(name);
        }

        void CollectPerfEvent(PerfEventCounterSession session)
        {
            sharedSamples.Clear();
            for (var cycle = 1; cycle <= Cycles; cycle++)
            {
                session.Read();
                CpuUtils.AddChain512(Workload.Count);
                session.Read();
                var delta = session.Counters.Hardware.CpuCycles!.Delta.Value;
                sharedSamples.Add((nuint)delta);

                // Console.WriteLine($"cycle: {cycle}, {name}: {delta}");
            }

            PrintStats("session cycles");

            sharedSamples.Clear();
            for (var cycle = 1; cycle <= Cycles; cycle++)
            {
                session.Read();
                CpuUtils.AddChain512(Workload.Count);
                session.Read();
                var delta = session.Counters.Hardware.Instructions!.Delta.Value;
                sharedSamples.Add((nuint)delta);

                // Console.WriteLine($"cycle: {cycle}, {name}: {delta}");
            }

            PrintStats("session inst");

            sharedSamples.Clear();
            for (var cycle = 1; cycle <= Cycles; cycle++)
            {
                session.Read();
                CpuUtils.AddChain512(Workload.Count);
                session.Read();
                var delta = session.Counters.Hardware.RefCpuCycles!.Delta.Value;
                sharedSamples.Add((nuint)delta);

                // Console.WriteLine($"cycle: {cycle}, {name}: {delta}");
            }

            PrintStats("session refcycles");

            sharedSamples.Clear();
            for (var cycle = 1; cycle <= Cycles; cycle++)
            {
                session.Read();
                CpuUtils.AddChain512(Workload.Count);
                session.Read();
                var delta = session.Counters.Hardware.BranchMisses!.Delta.Value;
                sharedSamples.Add((nuint)delta);

                // Console.WriteLine($"cycle: {cycle}, {name}: {delta}");
            }

            PrintStats("session branches");
        }

        void MeasureCallInst()
        {
            nuint sink = 0;

            sharedSamples.Clear();
            for (var cycle = 1; cycle <= Cycles; cycle++)
            {
                var before = PerfHelpers.ReadReferenceCycles();
                for (var i = 0; i < CallsPerSample; i++)
                {
                    sink ^= PerfHelpers.ReadInstructionsRetired();
                }

                var after = PerfHelpers.ReadReferenceCycles();
                var delta = Delta48(after, before);
                sharedSamples.Add(delta);
            }

            if (sink == 0xFFFFFFFFFFFFFFFFUL)
            {
                Console.WriteLine("sink guard");
            }

            PrintStats("call_inst");
        }

        void MeasureCallCore()
        {
            nuint sink = 0;

            sharedSamples.Clear();
            for (var cycle = 1; cycle <= Cycles; cycle++)
            {
                var before = PerfHelpers.ReadReferenceCycles();
                for (var i = 0; i < CallsPerSample; i++)
                {
                    sink ^= PerfHelpers.ReadCoreCycles();
                }

                var after = PerfHelpers.ReadReferenceCycles();
                var delta = Delta48(after, before);
                sharedSamples.Add(delta);
            }

            if (sink == 0xFFFFFFFFFFFFFFFFUL)
            {
                Console.WriteLine("sink guard");
            }

            PrintStats("call_core");
        }

        void MeasureCallCoreLfence()
        {
            nuint sink = 0;

            sharedSamples.Clear();
            for (var cycle = 1; cycle <= Cycles; cycle++)
            {
                var before = PerfHelpers.ReadReferenceCycles();
                for (var i = 0; i < CallsPerSample; i++)
                {
                    sink ^= PerfHelpers.ReadCoreCyclesLfence();
                }

                var after = PerfHelpers.ReadReferenceCycles();
                var delta = Delta48(after, before);
                sharedSamples.Add(delta);
            }

            if (sink == 0xFFFFFFFFFFFFFFFFUL)
            {
                Console.WriteLine("sink guard");
            }

            PrintStats("call_core_lfence");
        }

        void MeasureCallCoreLfenceBoth()
        {
            nuint sink = 0;

            sharedSamples.Clear();
            for (var cycle = 1; cycle <= Cycles; cycle++)
            {
                var before = PerfHelpers.ReadReferenceCycles();
                for (var i = 0; i < CallsPerSample; i++)
                {
                    sink ^= PerfHelpers.ReadCoreCyclesLfenceBoth();
                }

                var after = PerfHelpers.ReadReferenceCycles();
                var delta = Delta48(after, before);
                sharedSamples.Add(delta);
            }

            if (sink == 0xFFFFFFFFFFFFFFFFUL)
            {
                Console.WriteLine("sink guard");
            }

            PrintStats("call_core_lfence_both");
        }

        void MeasureCallRef()
        {
            nuint sink = 0;

            sharedSamples.Clear();
            for (var cycle = 1; cycle <= Cycles; cycle++)
            {
                var before = PerfHelpers.ReadReferenceCycles();
                for (var i = 0; i < CallsPerSample; i++)
                {
                    sink ^= PerfHelpers.ReadReferenceCycles();
                }

                var after = PerfHelpers.ReadReferenceCycles();
                var delta = Delta48(after, before);
                sharedSamples.Add(delta);
            }

            if (sink == 0xFFFFFFFFFFFFFFFFUL)
            {
                Console.WriteLine("sink guard");
            }

            PrintStats("call_ref");
        }

        void MeasureCallRdtsc()
        {
            nuint sink = 0;

            sharedSamples.Clear();
            for (var cycle = 1; cycle <= Cycles; cycle++)
            {
                var before = PerfHelpers.ReadReferenceCycles();
                for (var i = 0; i < CallsPerSample; i++)
                {
                    sink ^= PerfHelpers.ReadRdtsc();
                }

                var after = PerfHelpers.ReadReferenceCycles();
                var delta = Delta48(after, before);
                sharedSamples.Add(delta);
            }

            if (sink == 0xFFFFFFFFFFFFFFFFUL)
            {
                Console.WriteLine("sink guard");
            }

            PrintStats("call_rdtsc");
        }

        void MeasureCallRdtscp()
        {
            nuint sink = 0;

            sharedSamples.Clear();
            for (var cycle = 1; cycle <= Cycles; cycle++)
            {
                var before = PerfHelpers.ReadReferenceCycles();
                for (var i = 0; i < CallsPerSample; i++)
                {
                    sink ^= PerfHelpers.ReadRdtscp();
                }

                var after = PerfHelpers.ReadReferenceCycles();
                var delta = Delta48(after, before);
                sharedSamples.Add(delta);
            }

            if (sink == 0xFFFFFFFFFFFFFFFFUL)
            {
                Console.WriteLine("sink guard");
            }

            PrintStats("call_rdtscp");
        }

        void MeasureCallStopwatch()
        {
            nuint sink = 0;

            sharedSamples.Clear();
            for (var cycle = 1; cycle <= Cycles; cycle++)
            {
                var before = PerfHelpers.ReadReferenceCycles();
                for (var i = 0; i < CallsPerSample; i++)
                {
                    sink ^= unchecked((nuint)Stopwatch.GetTimestamp());
                }

                var after = PerfHelpers.ReadReferenceCycles();
                var delta = Delta48(after, before);
                sharedSamples.Add(delta);
            }

            if (sink == 0xFFFFFFFFFFFFFFFFUL)
            {
                Console.WriteLine("sink guard");
            }

            PrintStats("call_stopwatch");
        }

        void PrintStats(string name)
        {
            var start = sharedSamples.Count / 2;
            var count = sharedSamples.Count - start;
            if (count <= 0)
            {
                Console.WriteLine($"{name}: avg: NaN, min: NaN, max: NaN, stdev: NaN, range_pct: NaN, stdev_pct: NaN");
                return;
            }

            var activeValues = new double[count];
            double sum = 0;
            double min = double.MaxValue;
            double max = double.MinValue;

            for (var i = start; i < sharedSamples.Count; i++)
            {
                var value = (double)sharedSamples[i];
                activeValues[i - start] = value;
                sum += value;
                if (value < min)
                {
                    min = value;
                }

                if (value > max)
                {
                    max = value;
                }
            }

            Array.Sort(activeValues);
            var median = activeValues[count / 2];

            var avg = sum / count;
            if (avg == 0)
            {
                Console.WriteLine($"{name}: median: {median:F3}, range_pct: NaN, stdev_pct: NaN");
                return;
            }

            double varianceSum = 0;
            for (var i = start; i < sharedSamples.Count; i++)
            {
                var value = (double)sharedSamples[i];
                var diff = value - avg;
                varianceSum += diff * diff;
            }

            var stdev = Math.Sqrt(varianceSum / count);
            var rangePct = ((max - min) / avg) * 100.0;
            var stdevPct = (stdev / avg) * 100.0;

            Console.WriteLine(
                $"{name}: avg: {avg:F3}, median: {median:F3}, min: {min:F3}, max: {max:F3}, stdev: {stdev:F3}, range_pct: {rangePct:F4}%, stdev_pct: {stdevPct:F4}%");
        }
    }

    internal static class Workload
    {
        public static volatile int Count = 10_000;
    }

    internal static class PerfHelpers
    {
        private const string NativeLibrary = "picollo_native";

        [DllImport(NativeLibrary, EntryPoint = "is_available", CallingConvention = CallingConvention.Cdecl)]
        [SuppressGCTransition]
        internal static extern nuint IsAvailable();

        [DllImport(NativeLibrary, EntryPoint = "read_instructions_retired", CallingConvention = CallingConvention.Cdecl)]
        [SuppressGCTransition]
        internal static extern nuint ReadInstructionsRetired();

        [DllImport(NativeLibrary, EntryPoint = "read_core_cycles", CallingConvention = CallingConvention.Cdecl)]
        [SuppressGCTransition]
        internal static extern nuint ReadCoreCycles();

        [DllImport(NativeLibrary, EntryPoint = "read_core_cycles_lfence", CallingConvention = CallingConvention.Cdecl)]
        [SuppressGCTransition]
        internal static extern nuint ReadCoreCyclesLfence();

        [DllImport(NativeLibrary, EntryPoint = "read_core_cycles_lfence_both", CallingConvention = CallingConvention.Cdecl)]
        [SuppressGCTransition]
        internal static extern nuint ReadCoreCyclesLfenceBoth();

        [DllImport(NativeLibrary, EntryPoint = "read_reference_cycles", CallingConvention = CallingConvention.Cdecl)]
        [SuppressGCTransition]
        internal static extern nuint ReadReferenceCycles();

        [DllImport(NativeLibrary, EntryPoint = "read_rdtsc", CallingConvention = CallingConvention.Cdecl)]
        [SuppressGCTransition]
        internal static extern nuint ReadRdtsc();

        [DllImport(NativeLibrary, EntryPoint = "read_rdtscp", CallingConvention = CallingConvention.Cdecl)]
        [SuppressGCTransition]
        internal static extern nuint ReadRdtscp();
    }
}