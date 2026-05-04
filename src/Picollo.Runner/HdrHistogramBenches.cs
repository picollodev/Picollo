using System.Diagnostics;

using HdrHistogram;

using Picollo.Metrics;

namespace Picollo.Runner;

public class HdrHistogramBenches
{
    public static void PicolloBench(int runs = 10)
    {
        var count = 1_000_000;
        var values = new ulong[count];
        Random random = new Random(42);
        for (int i = 0; i < count; i++)
        {
            values[i] = (ulong)(Math.Pow(random.NextDouble(), 10) * random.NextDouble() * 100L * int.MaxValue);
        }

        random.Shuffle(values);

        var h = Metrics.HdrHistogram.Create();
        Console.WriteLine($"Footprint in bytes: {h.FootprintInBytes:N0}");

        for (int x = 0; x < runs; x++)
        {
            var rounds = 200;
            var sw = Stopwatch.StartNew();
            for (int r = 0; r < rounds; r++)
            {
                foreach (ulong value in values)
                {
                    // Interlocked.Increment(ref h.GetRef(value));
                    h.Add(value, 1);
                }
            }

            sw.Stop();

            var totalOps = rounds * count;
            var elapsed = sw.Elapsed;
            var perOp = elapsed.TotalNanoseconds / totalOps;
            Console.WriteLine($"Elapsed: {elapsed}, perOp: {perOp:N2} ns");
        }

        Console.WriteLine($"Min: {h.MinValue:N0}, Max: {h.MaxValue:N0}, {ulong.MaxValue:N0}");
    }

    public static void LegacyBench(int runs = 10)
    {
        var count = 10_000_000;
        var values = new long[count];
        Random random = new Random(42);
        for (int i = 0; i < count; i++)
        {
            values[i] = (long)(Math.Pow(random.NextDouble(), 10) * random.NextDouble() * 100L * int.MaxValue);
        }

        random.Shuffle(values);

        var h = new LongHistogram(1, long.MaxValue, 3);
        Console.WriteLine($"Footprint in bytes: {h.GetEstimatedFootprintInBytes():N0}");

        for (int x = 0; x < runs; x++)
        {
            var rounds = 10;
            var sw = Stopwatch.StartNew();
            for (int r = 0; r < rounds; r++)
            {
                foreach (long value in values)
                {
                    h.RecordValue(value);
                }
            }

            sw.Stop();

            var totalOps = rounds * count;
            var elapsed = sw.Elapsed;
            var perOp = elapsed.TotalNanoseconds / totalOps;
            Console.WriteLine($"Elapsed: {elapsed}, perOp: {perOp:N2} ns");
        }

    }
}