using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Picollo.Metrics;

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
            h.Record(20_000 + (ulong)((0.5 - rng.NextDouble()) * 1000 + Math.Pow(rng.NextDouble(), 2) * 5000));

        h.Record(40000); // Outside the range

        // A summary has standard percentiles for ranks: [0, 1, 5, 10, 25, 50, 75, 90, 92.5, 95, 97.5, 99, 99.9, 99.99, 99.999, 100]
        // Plus total count, overflow count, mean, StDev, min/max trackable. 
        // The percentiles such as summary.P99 have not only the value, but detailed info about the bucket where the percentile is found.
        var summary = h.GetSummary();
        Console.WriteLine($"P99: {summary.P99}");

        h.Reset();

        // Different distribution
        for (int i = 0; i < 2000_000; i++)
            h.Record(19_000 + (ulong)((0.5 - rng.NextDouble()) * 500 + Math.Pow(rng.NextDouble(), 3) * 10000));

        // h.GetSummary(summary); // Can avoid summary alloc and reuse in place
        var summary2 = h.GetSummary();
        summary.PrettyPrint("Histogram Before");
        summary.PrettyPrintDiff(summary2, "Getting Started Diff", "Before", "After");
    }

    public static void GettingStartedSnapshot()
    {
        var h = Picollo.Metrics.HdrHistogram.Factory
            .WithRelativeError(0.01).WithUInt32Storage().WithMinTrackableValue(10000)
            .WithMaxTrackableValue(30000).Create();

        var rng = new Random(0);

        for (int i = 0; i < 1000_000; i++)
            h.Record(20_000 + (ulong)((0.5 - rng.NextDouble()) * 1000 + Math.Pow(rng.NextDouble(), 2) * 5000));

        h.Record(40000); // Outside the range

        var snapshot = h.GetSnapshot();
        var summary = snapshot.GetSummary();
        Console.WriteLine($"P99: {summary.P99}");

        // No reset: h.Reset();

        for (int i = 0; i < 2000_000; i++)
            h.Record(19_000 + (ulong)((0.5 - rng.NextDouble()) * 500 + Math.Pow(rng.NextDouble(), 3) * 10000));

        snapshot.Update(deltas: true); // The snapshot contains the deltas since the previous snapshot
        var summary2 = snapshot.GetSummary();

        summary.PrettyPrint("Histogram Before");
        summary.PrettyPrintDiff(summary2, "Getting Started Diff", "Before", "After");
    }

    public static void GettingStartedMonitoring(int seconds = 10)
    {
        var h = Picollo.Metrics.HdrHistogram.Factory
            .WithRelativeError(0.01).WithUInt64Storage().WithMinTrackableValue(10000)
            .WithMaxTrackableValue(30000).Create();

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var rng = new Random(0);

        var writerTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                var drift = (ulong)DateTime.UtcNow.TimeOfDay.TotalMilliseconds & 16383;
                h.Record(20_000 + (ulong)((0.5 - rng.NextDouble()) * 1000 + Math.Pow(rng.NextDouble(), 2) * drift));
            }
        });

        var monitoringTask = Task.Run(async () =>
        {
            var snapshot = h.GetSnapshot();
            var summary = snapshot.GetSummary();

            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(1000);

                // The snapshot will contain the difference from the previous update, no allocation in steady-state
                snapshot.Update(deltas: true);

                snapshot.GetSummary(summary); // Reuse the summary object

                // Allocates a string, convenient for exploratory analysis, but for Prod summary should be used directly with existing zero-alloc logging
                var oneLineSummary = summary.ToString(nameof(GettingStartedMonitoring));
                Console.WriteLine($"{summary.TimestampUtc:HH:mm:ss.fff} | {oneLineSummary}");
            }
        });

        Task.WaitAll(writerTask, monitoringTask);
        h.GetSummary().PrettyPrint("Aggregate");
    }

    public static void GettingStartedPercentiles()
    {
        var h = Picollo.Metrics.HdrHistogram.Factory
            .WithRelativeError(0.01).WithUInt32Storage().WithMinTrackableValue(10000)
            .WithMaxTrackableValue(30000).Create();

        var rng = new Random(0);

        for (int i = 0; i < 1000_000; i++)
            h.Record(20_000 + (ulong)((0.5 - rng.NextDouble()) * 1000 + Math.Pow(rng.NextDouble(), 2) * 5000));

        // Enumerables to get detailed bucket info
        // h.Buckets
        // h.BucketsWithValues
        // h.BucketPercentiles
        // h.BucketPercentilesWithValues

        foreach (var percentile in h.BucketPercentilesWithValues)
        {
            Console.WriteLine(percentile);
        }
    }

    public static void GettingStartedCustomPercentiles()
    {
        var h = Picollo.Metrics.HdrHistogram.Factory
            .WithRelativeError(0.01).WithUInt32Storage().WithMinTrackableValue(10000)
            .WithMaxTrackableValue(30000).Create();

        var rng = new Random(0);

        for (int i = 0; i < 1000_000; i++)
            h.Record(20_000 + (ulong)((0.5 - rng.NextDouble()) * 1000 + Math.Pow(rng.NextDouble(), 2) * 5000));

        ReadOnlySpan<double> ranks = [90.0, 91, 92, 93, 94, 95, 96, 97, 98];
        var percentiles = new Percentile[ranks.Length];
        var percentileValues = new ulong[ranks.Length];

        // Batch
        h.GetPercentiles(ranks, percentiles);
        h.GetPercentileValues(ranks, percentileValues);

        foreach (var percentile in percentiles)
        {
            Console.WriteLine(percentile);
        }

        // Scalar: each invocation calls the batch method with a single rank
        foreach (var rank in ranks)
        {
            var percentile = h.GetPercentile(rank);
            var percentileValue = h.GetPercentileValue(rank);
            Console.WriteLine($"{rank:N0}: {percentileValue:N0}");
        }
    }

    public static void GettingStartedScopes()
    {
        var h = Picollo.Metrics.HdrHistogram.Factory
            .WithMaxTrackableValue(NanoScope.OneSecondValue * 2).Create();

        using (h.GetTickScope())
        {
            Thread.Sleep(1000);
        }

        using (h.GetNanoScope())
        {
            Thread.Sleep(1000);
        }

        using (h.GetMicroScope())
        {
            Thread.Sleep(1000);
        }

        using (h.GetMilliScope())
        {
            Thread.Sleep(1000);
        }

        h.GetSummary().PrettyPrint();
    }

    public static void GettingStartedClockResolution()
    {
        var h = Picollo.Metrics.HdrHistogram.Factory.Create();

        for (long last = Stopwatch.GetTimestamp(), n = 0; n < 1000_000;)
        {
            long now;
            while ((now = Stopwatch.GetTimestamp()) == last) ;
            h.Record((ulong)(now - last));
            last = now;
            n++;
        }

        var summary = h.GetSummary();
        Console.WriteLine($"Stopwatch.Frequency: {Stopwatch.Frequency:N0}");
        Console.WriteLine(summary.P0);
        Console.WriteLine(summary.P50);
        Console.WriteLine(summary.P99);
        var meanMeasurableInNanos = summary.P50.Value * 1e9 / Stopwatch.Frequency;
        Console.WriteLine($"P50 measurable nanos: {meanMeasurableInNanos}");

    }
}