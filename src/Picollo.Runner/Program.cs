using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

using Picollo;
using Picollo.PerfEvent;

var pinned = Picollo.CpuUtils.PrepareBenchmarkThread(8);
Console.WriteLine($"Pinned: {pinned}");

using var perfSession = PerfEventCounterSession
	.New(Environment.ProcessId)
    .WithPinned(true)
    .WithEnabled(true)
	.AddHardwareCounter(PerfHwId.CpuCycles)
	.AddHardwareCounter(PerfHwId.Instructions)
	.AddHardwareCounter(PerfHwId.RefCpuCycles);
perfSession.Open();
 
// perfSession.GetSnapshot();

// perfSession.Start();

Console.WriteLine($"IsAvailable: {PerfHelpers.IsAvailable()}");

Console.WriteLine($"hasUserTime: {perfSession.HasUserTime}, hasUserRdpmc: {perfSession.HasUserRdpmc}");

if (!perfSession.HasUserRdpmc)
{
	Console.WriteLine("RDPMC user access is not available; skipping RDPMC measurement loop to avoid native crash.");
	return;
}

const ulong CounterMask48 = 0x0000FFFFFFFFFFFFUL;
const int Cycles = 1000;
const int CallsPerSample = 10_000;

var sharedSamples = new List<nuint>(Cycles);

CollectAndReport48("inst", static () => PerfHelpers.ReadInstructionsRetired());

for(int r = 0; r < 1; r++){
	CollectAndReport48("core", static () => PerfHelpers.ReadCoreCycles());
}


CollectAndReport48("core_lfence", static () => PerfHelpers.ReadCoreCyclesLfence());
CollectAndReport48("core_lfence_both", static () => PerfHelpers.ReadCoreCyclesLfenceBoth());
CollectAndReport48("ref", static () => PerfHelpers.ReadReferenceCycles());

CollectAndReport48("fixed_inst", static () =>
{
	PerfHelpers.ReadFixedCounters(out var instructionsRetired, out _, out _);
	return instructionsRetired;
});
CollectAndReport48("fixed_core", static () =>
{
	PerfHelpers.ReadFixedCounters(out _, out var coreCycles, out _);
	return coreCycles;
});
CollectAndReport48("fixed_ref", static () =>
{
	PerfHelpers.ReadFixedCounters(out _, out _, out var referenceCycles);
	return referenceCycles;
});

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
MeasureCallFixedTriplet();

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

void MeasureCallFixedTriplet()
{
	nuint sink = 0;

	sharedSamples.Clear();
	for (var cycle = 1; cycle <= Cycles; cycle++)
	{
		var before = PerfHelpers.ReadReferenceCycles();
		for (var i = 0; i < CallsPerSample; i++)
		{
			PerfHelpers.ReadFixedCounters(out var a, out var b, out var c);
			// sink ^= a ^ b ^ c;
		}
		var after = PerfHelpers.ReadReferenceCycles();
		var delta = Delta48(after, before);
		sharedSamples.Add(delta);
	}

	if (sink == 0xFFFFFFFFFFFFFFFFUL)
	{
		Console.WriteLine("sink guard");
	}

	PrintStats("call_fixed_triplet");
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

	Console.WriteLine($"{name}: avg: {avg:F3}, median: {median:F3}, min: {min:F3}, max: {max:F3}, stdev: {stdev:F3}, range_pct: {rangePct:F4}%, stdev_pct: {stdevPct:F4}%");
}

internal static class Workload
{
	public static volatile int Count = 10000;
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

	[DllImport(NativeLibrary, EntryPoint = "read_fixed_counters", CallingConvention = CallingConvention.Cdecl)]
	[SuppressGCTransition]
	internal static extern void ReadFixedCounters(
		out nuint instructionsRetired,
		out nuint coreCycles,
		out nuint referenceCycles);
    
	[DllImport(NativeLibrary, EntryPoint = "read_rdtsc", CallingConvention = CallingConvention.Cdecl)]
	[SuppressGCTransition]
	internal static extern nuint ReadRdtsc();

	[DllImport(NativeLibrary, EntryPoint = "read_rdtscp", CallingConvention = CallingConvention.Cdecl)]
	[SuppressGCTransition]
	internal static extern nuint ReadRdtscp();
}