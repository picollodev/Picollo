using System;
using System.Linq;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Picollo.PerfEvent;
using Shouldly;

namespace Picollo.Tests.PerfEvent;

[TestFixture]
public class CountersTests
{
    [SetUp]
    public void Setup()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
            RuntimeInformation.OSArchitecture != Architecture.X64)
        {
            Assert.Inconclusive("PerfEventCounterSession tests require Linux x64.");
        }
    }

    [Test]
    public void AddCounterOverloadsPopulateKnownCountersAndEnumerable()
    {
        var session =
            PerfEventCounterSession.Factory
                .WithTarget(Environment.ProcessId, 0)
                .WithHardwareCounter(PerfHardwareCounterId.CpuCycles, out var cycles)
                .WithHardwareCounter(PerfHardwareCounterId.Instructions)
                .WithSoftwareCounter(PerfSoftwareCounterId.ContextSwitches, out var contextSwitches)
                .WithCacheCounter(PerfCacheCounterId.L1DReadMiss, out var l1dReadMiss)
                .Session;

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
        var factory = PerfEventCounterSession.Factory
            .WithTarget(Environment.ProcessId, 0)
            .WithHardwareCounter(PerfHardwareCounterId.CpuCycles, out var cycles);

        var session = factory.Session;

        try
        {
            factory.WithHardwareCounter(PerfHardwareCounterId.CpuCycles, out _);
            Assert.Fail();
        }
        catch
        {
            //
        }

        session.Counters.Hardware.CpuCycles.ShouldBeSameAs(cycles);
        session.Counters.Count.ShouldBe(1);
    }

    [Test]
    public unsafe void SnapshotCanUseCurrentOrDeltaCounterValues()
    {
        var session = PerfEventCounterSession.Factory
            .WithTarget(Environment.ProcessId, 0)
            .WithHardwareCounter(PerfHardwareCounterId.CpuCycles, out var cycles)
            .WithHardwareCounter(PerfHardwareCounterId.Instructions, out var instructions)
            .Session;

        cycles.Index = 0;
        instructions.Index = 1;

        var current = new[]
        {
            new PerfEventCounterValue { Value = 100, TimeEnabled = 100, TimeRunning = 50 },
            new PerfEventCounterValue { Value = 20, TimeEnabled = 100, TimeRunning = 80 }
        };
        var previous = new[]
        {
            new PerfEventCounterValue { Value = 40, TimeEnabled = 10, TimeRunning = 5 },
            new PerfEventCounterValue { Value = 5, TimeEnabled = 20, TimeRunning = 10 }
        };

        fixed (PerfEventCounterValue* currentPtr = current)
        fixed (PerfEventCounterValue* previousPtr = previous)
        {
            session.CounterValuesPtr = currentPtr;
            session.PreviousCounterValuesPtr = previousPtr;

            var currentSnapshot = session.Counters.GetSnapshot();
            currentSnapshot.Hardware.CpuCycles.ShouldBe(200UL);
            currentSnapshot.Hardware.Instructions.ShouldBe(25UL);

            var deltaSnapshot = session.Counters.GetSnapshot(useDeltas: true);
            deltaSnapshot.Hardware.CpuCycles.ShouldBe(120UL);
            deltaSnapshot.Hardware.Instructions.ShouldBe(15ul * 80 / 70);
        }
    }
}