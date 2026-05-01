using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Shouldly;

namespace Picollo.PerfEvent.Tests;

[TestFixture]
public class NativeMethodTests
{
    [SetUp]
    public void Setup()
    {
    }

    [Test]
    public void TestIsSupported()
    {
        bool expected = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && 
                         RuntimeInformation.OSArchitecture == Architecture.X64;
        
        NativeMethods.IsSupported().ShouldBe(expected);
    }

    [Test]
    public void AddCounterOverloadsPopulateKnownCountersAndEnumerable()
    {
        var session = CreateSession();

        session
            .AddHardwareCounter(PerfHwId.CpuCycles, out var cycles)
            .AddHardwareCounter(PerfHwId.Instructions)
            .AddSoftwareCounter(PerfSwIds.ContextSwitches, out var contextSwitches)
            .AddCacheCounter(PerfCacheId.L1DReadMiss, out var l1dReadMiss);

        session.Counters.Count.ShouldBe(4);
        session.Counters[0].ShouldBeSameAs(cycles);
        session.Counters.Hardware.CpuCycles.ShouldBeSameAs(cycles);
        session.Counters.Hardware.Instructions.ShouldNotBeNull();
        session.Counters.Software.ContextSwitches.ShouldBeSameAs(contextSwitches);
        session.Counters.Caches.L1DReadMiss.ShouldBeSameAs(l1dReadMiss);
        session.Counters.Single(x => x.Type == PerfTypeId.Software).ShouldBeSameAs(contextSwitches);
    }

    [Test]
    public void DuplicateCounterDoesNotOverwriteKnownCounter()
    {
        var session = CreateSession();

        session.AddHardwareCounter(PerfHwId.CpuCycles, out var cycles);

        Should.Throw<InvalidOperationException>(() => session.AddHardwareCounter(PerfHwId.CpuCycles, out _));
        session.Counters.Hardware.CpuCycles.ShouldBeSameAs(cycles);
        session.Counters.Count.ShouldBe(1);
    }

    [Test]
    public unsafe void SnapshotCanUseCurrentOrDeltaCounterValues()
    {
        var session = CreateSession();
        session
            .AddHardwareCounter(PerfHwId.CpuCycles, out var cycles)
            .AddHardwareCounter(PerfHwId.Instructions, out var instructions);

        cycles.Index = 0;
        instructions.Index = 1;

        var current = new[]
        {
            new CounterValue { Value = 100, TimeEnabled = 100, TimeRunning = 50 },
            new CounterValue { Value = 20, TimeEnabled = 100, TimeRunning = 100 }
        };
        var previous = new[]
        {
            new CounterValue { Value = 40, TimeEnabled = 10, TimeRunning = 5 },
            new CounterValue { Value = 5, TimeEnabled = 20, TimeRunning = 10 }
        };

        fixed (CounterValue* currentPtr = current)
        fixed (CounterValue* previousPtr = previous)
        {
            session.CounterValuesPtr = currentPtr;
            session.PreviousCounterValuesPtr = previousPtr;

            var currentSnapshot = session.Counters.GetSnapshot();
            currentSnapshot.Hardware.CpuCycles.ShouldBe(200UL);
            currentSnapshot.Hardware.Instructions.ShouldBe(20UL);


            var deltaSnapshot = session.Counters.GetSnapshot(useDeltas: true);
            deltaSnapshot.Hardware.CpuCycles.ShouldBe(120UL);
            deltaSnapshot.Hardware.Instructions.ShouldBe(120UL);
        }
    }

    private static PerfEventCounterSession CreateSession()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
            RuntimeInformation.OSArchitecture != Architecture.X64)
        {
            Assert.Inconclusive("PerfEventCounterSession tests require Linux x64.");
        }

        return PerfEventCounterSession.New(Environment.ProcessId, 0);
    }
}
