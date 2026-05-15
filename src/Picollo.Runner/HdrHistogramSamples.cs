using System;

namespace Picollo.Runner;

public static class HdrHistogramSamples
{
    public static void GettingStarted()
    {
        var h =
            Picollo.Metrics.HdrHistogram.Factory
                .WithRelativeError(0.01)
                .WithUInt32Storage()
                .WithMinTrackableValue(10000)
                .WithMaxTrackableValue(30000)
                .Create();
        
        var rng = new Random(0);

        for (int i = 0; i < 1000_000; i++)
            h.Record(20_000 + (ulong)((0.5-rng.NextDouble()) * 1000 + Math.Pow(rng.NextDouble(), 2) * 5000));
        
        var summary = h.GetSummary();
        Console.WriteLine($"P99: {summary.P99}");
        
        h.Reset();
        
        for (int i = 0; i < 2000_000; i++)
            h.Record(19_000 + (ulong)((0.5-rng.NextDouble()) * 500 + Math.Pow(rng.NextDouble(), 3) * 10000));
        
        // h.GetSummary(summary); // Can avoid summary alloc and reuse in place
        var summary2 = h.GetSummary();
        summary.PrettyPrint("Histogram 1");
        summary.PrettyPrintDiff(summary2, "Getting Started Diff", "Before", "After");
    }
}