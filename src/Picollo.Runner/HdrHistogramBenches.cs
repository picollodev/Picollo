using System.Diagnostics;
using HdrHistogram;
using Picollo.Metrics;

namespace Picollo.Runner;

public class HdrHistogramBenches
{
    private static readonly long MaxValue = long.MaxValue;
    private static readonly int RandomPower = 4; // Skews values down
    private static readonly int SignificantDigits = 3; // Affects the footprint much more than max value

    private static readonly long[] Values = InitValues();

    private static long[] InitValues()
    {
        var count = 1_000_000;
        var values = new long[count];
        Random random = new Random(42);
        for (int i = 0; i < count; i++)
        {
            values[i] = (long)(Math.Pow(random.NextDouble(), RandomPower) * MaxValue);
        }

        random.Shuffle(values);
        return values;
    }

    public static void PicolloBench(int runs = 10)
    {
        Console.WriteLine("# Picollo HdrHistogram");

        var h = new HdrHistogram<uint>(1 / Math.Pow(10.0, SignificantDigits), (ulong)MaxValue);

        Console.WriteLine($"Footprint in bytes: {h.FootprintInBytes:N0}");

        var rounds = 100;

        var sw = Stopwatch.StartNew();

        for (int x = 0; x < runs; x++)
        {
            sw.Restart();

            for (int r = 0; r < rounds; r++)
            {
                foreach (long value in Values)
                {
                    h.Record((ulong)value);
                }
            }

            sw.Stop();

            var totalOps = rounds * Values.Length;
            var elapsed = sw.Elapsed;
            var perOp = elapsed.TotalNanoseconds / totalOps;
            Console.WriteLine($"Elapsed: {elapsed}, perOp: {perOp:N2} ns");
        }

        Console.WriteLine(
            $"Percentiles: P1 {h.GetValueAtPercentile(1):N0}, P50 {h.GetValueAtPercentile(50):N0}, P90 {h.GetValueAtPercentile(90):N0}, P99 {h.GetValueAtPercentile(99):N0}, P99.9 {h.GetValueAtPercentile(99.9):N0}");

        Console.WriteLine();
    }

    public static void LegacyBench(int runs = 10)
    {
        Console.WriteLine("# Legacy HdrHistogram");

        var h = new IntHistogram(1, MaxValue, SignificantDigits);
        Console.WriteLine($"Footprint in bytes: {h.GetEstimatedFootprintInBytes():N0}");

        var rounds = 100;

        var sw = Stopwatch.StartNew();

        for (int x = 0; x < runs; x++)
        {
            sw.Restart();

            for (int r = 0; r < rounds; r++)
            {
                foreach (long value in Values)
                {
                    h.RecordValue(value);
                }
            }

            sw.Stop();

            var totalOps = rounds * Values.Length;
            var elapsed = sw.Elapsed;
            var perOp = elapsed.TotalNanoseconds / totalOps;
            Console.WriteLine($"Elapsed: {elapsed}, perOp: {perOp:N2} ns");
        }

        Console.WriteLine(
            $"Percentiles: P1 {h.GetValueAtPercentile(1):N0}, P50 {h.GetValueAtPercentile(50):N0}, P90 {h.GetValueAtPercentile(90):N0}, P99 {h.GetValueAtPercentile(99):N0}, P99.9 {h.GetValueAtPercentile(99.9):N0}");
    }
}