using System.Diagnostics;
using System.Text;
using HdrHistogram;
using Picollo.Metrics;

namespace Picollo.Runner;

public class HdrHistogramBenches
{
    private static readonly long MaxValue = 24_000; // (long)MicroScope.OneSecondValue; // 7716549600; // 1000_000_000L * 3600;
    private static readonly int RandomPower = 1; // Skews values down
    private static readonly int SignificantDigits = 3; // Affects the footprint much more than max value
    private static readonly bool UseDoublePrecision = true; // Legacy allocates 2x more slots than needed to guarantee midpoint precision

    private static readonly int Rounds = 100;

    private static readonly long[] Values = InitValues();

    private static long[] InitValues()
    {
        Percentile.DefaultEquivalentValueSelection = EquivalentValueSelection.Interpolated;

        var count = 1_000_000;
        var values = new long[count];
        Random random = new Random(42);
        for (int i = 0; i < count; i++)
        {
            values[i] = (long)(20000 + random.NextDouble() * 2000 + random.NextDouble() * 1000 + random.NextDouble() * 5 +
                               random.NextDouble() * 250); // (long)(Math.Pow(random.NextDouble(), RandomPower) * MaxValue);
        }

        random.Shuffle(values);
        return values;
    }

    private static void PicolloWorkload(Metrics.HdrHistogram h, int runs, int threadId = 0)
    {
        var sw = Stopwatch.StartNew();

        for (int x = 0; x < runs; x++)
        {
            sw.Restart();

            for (int r = 0; r < Rounds; r++)
            {
                foreach (long value in Values)
                {
                    h.Record((ulong)value);
                }
            }

            sw.Stop();

            var totalOps = Rounds * Values.Length;
            var elapsed = sw.Elapsed;
            var perOp = elapsed.TotalNanoseconds / totalOps;
            Console.WriteLine($"[{threadId}] Elapsed: {elapsed}, perOp: {perOp:N2} ns");
        }
    }

    public static void PicolloBench(int runs = 10)
    {
        Console.WriteLine("# PicolloBench");
        var h = new HdrHistogram<uint>((UseDoublePrecision ? 0.5 : 1) / Math.Pow(10.0, SignificantDigits), 0, (ulong)MaxValue);
        Console.WriteLine($"Footprint in bytes: {h.FootprintInBytes:N0}");

        PicolloWorkload(h, runs);

        Console.WriteLine(
            $"Percentiles: P1 {h.GetPercentileValue(1):N0}, P50 {h.GetPercentileValue(50):N0}, P90 {h.GetPercentileValue(90):N0}, P99 {h.GetPercentileValue(99):N0}, P99.9 {h.GetPercentileValue(99.9):N0}");
        Console.WriteLine();
        // h.GetSummary().PrettyPrint();
        // h.GetSummary().PrettyPrint2();
        var summary = h.GetSummary();
        summary.PrettyPrint();

        for (int i = 0; i < 100_000_000; i++)
        {
            h.Record(23050);
        }
        
        
        h.GetSummary().PrettyPrintDiff(summary, "Delta summary", "A", "B");
        
    }

    public static void PicolloConcurrentBench(int runs = 10, int threads = 2, bool threadLocal = false, ulong? maxValue = null)
    {
        Console.WriteLine($"# PicolloConcurrentBench: {(threadLocal ? "thread-local" : "interlocked")}");
        Metrics.HdrHistogram h =
            threadLocal
                ? new ThreadLocalHdrHistogram<uint>((UseDoublePrecision ? 0.5 : 1) / Math.Pow(10.0, SignificantDigits), 0,
                    maxValue ?? (ulong)MaxValue, TimeSpan.FromMilliseconds(100))
                : new InterlockedUInt32HdrHistogram((UseDoublePrecision ? 0.5 : 1) / Math.Pow(10.0, SignificantDigits), 0,
                    maxValue ?? (ulong)MaxValue);

        if (!threadLocal)
            Console.WriteLine($"Footprint in bytes: {h.FootprintInBytes:N0}");

        var mre = new ManualResetEvent(false);
        List<Task> tasks = new();
        var finished = 0;
        for (int i = 0; i < threads; i++)
        {
            var threadId = i;
            var t = Task.Factory.StartNew(() =>
            {
                mre.WaitOne();

                PicolloWorkload(h, runs, threadId);

                if (Interlocked.Increment(ref finished) == threads)
                {
                    Console.WriteLine(
                        $"Percentiles: P1 {h.GetPercentileValue(1):N0}, P50 {h.GetPercentileValue(50):N0}, P90 {h.GetPercentileValue(90):N0}, P99 {h.GetPercentileValue(99):N0}, P99.9 {h.GetPercentileValue(99.9):N0}");

                    if (threadLocal)
                        Console.WriteLine($"Footprint in bytes: {h.FootprintInBytes:N0}");
                }
            }, TaskCreationOptions.LongRunning);
            tasks.Add(t);
        }

        Thread.Sleep(1000);
        mre.Set();
        Task.WaitAll(tasks);

        Thread.Sleep(100);
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();

        Console.WriteLine((h as ThreadLocalHdrHistogram<uint>)?.TotalCount);

        Console.WriteLine($"Children count: {(h as ThreadLocalHdrHistogram<uint>)?.GetChildrenCount()}");

        Console.WriteLine();
    }

    public static void PicolloThreadLocalBench(int runs = 10)
    {
        Console.WriteLine("# PicolloThreadLocalBench");

        var hStorage = new ThreadLocal<HdrHistogram<uint>>(() =>
            new HdrHistogram<uint>(0.5 / Math.Pow(10.0, SignificantDigits), 0, (ulong)MaxValue));
        var h = hStorage.Value!;

        Console.WriteLine($"Footprint in bytes: {h.FootprintInBytes:N0}");

        var sw = Stopwatch.StartNew();

        for (int x = 0; x < runs; x++)
        {
            sw.Restart();

            for (int r = 0; r < Rounds; r++)
            {
                foreach (long value in Values)
                {
                    hStorage.Value!.Record((ulong)value);
                }
            }

            sw.Stop();

            var totalOps = Rounds * Values.Length;
            var elapsed = sw.Elapsed;
            var perOp = elapsed.TotalNanoseconds / totalOps;
            Console.WriteLine($"Elapsed: {elapsed}, perOp: {perOp:N2} ns");
        }

        Console.WriteLine(
            $"Percentiles: P1 {h.GetPercentileValue(1):N0}, P50 {h.GetPercentileValue(50):N0}, P90 {h.GetPercentileValue(90):N0}, P99 {h.GetPercentileValue(99):N0}, P99.9 {h.GetPercentileValue(99.9):N0}");
        Console.WriteLine();
    }

    private static void LegacyWorkload(HistogramBase h, int runs, int threadId = 0)
    {
        var sw = Stopwatch.StartNew();
        int rounds = (Rounds / 10);

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

            var totalOps = (Rounds / 10) * Values.Length;
            var elapsed = sw.Elapsed;
            var perOp = elapsed.TotalNanoseconds / totalOps;
            Console.WriteLine($"[{threadId}] Elapsed: {elapsed}, perOp: {perOp:N2} ns");
        }
    }

    public static void LegacyBench(int runs = 10)
    {
        Console.WriteLine("# LegacyBench");

        var h = new IntHistogram(1, MaxValue, SignificantDigits);
        Console.WriteLine($"Footprint in bytes: {h.GetEstimatedFootprintInBytes():N0}");

        LegacyWorkload(h, runs);

        Console.WriteLine(
            $"Percentiles: P1 {h.GetValueAtPercentile(1):N0}, P50 {h.GetValueAtPercentile(50):N0}, P90 {h.GetValueAtPercentile(90):N0}, P99 {h.GetValueAtPercentile(99):N0}, P99.9 {h.GetValueAtPercentile(99.9):N0}");

        h.OutputPercentileDistribution(Console.Out);
        Console.WriteLine();
    }

    public static void LegacyConcurrentBench(int runs = 10, int threads = 2)
    {
        Console.WriteLine("# LegacyConcurrentBench");
        var h = new IntConcurrentHistogram(1, MaxValue, 3);
        Console.WriteLine($"Footprint in bytes: {h.GetEstimatedFootprintInBytes():N0}");

        var mre = new ManualResetEvent(false);
        List<Task> tasks = new();
        var finished = 0;
        for (int i = 0; i < threads; i++)
        {
            var threadId = i;
            var t = Task.Factory.StartNew(() =>
            {
                mre.WaitOne();

                LegacyWorkload(h, runs, threadId);

                if (Interlocked.Increment(ref finished) == threads)
                {
                    var p999Value = h.GetValueAtPercentile(99.9);
                    Console.WriteLine($"P99.9: {p999Value:N0}");

                    Console.WriteLine(
                        $"Percentiles: P1 {h.GetValueAtPercentile(1):N0}, P50 {h.GetValueAtPercentile(50):N0}, P90 {h.GetValueAtPercentile(90):N0}, P99 {h.GetValueAtPercentile(99):N0}, P99.9 {h.GetValueAtPercentile(99.9):N0}");
                }
            }, TaskCreationOptions.LongRunning);
            tasks.Add(t);
        }

        Thread.Sleep(1000);
        mre.Set();
        Task.WaitAll(tasks);
        Console.WriteLine();
    }

    public static void PicolloBucketEnumerationBench(int runs = 10)
    {
        Console.WriteLine("# PicolloBucketEnumerationBench");
        var h = new HdrHistogram<uint>((UseDoublePrecision ? 0.5 : 1) / Math.Pow(10.0, SignificantDigits), 0, (ulong)MaxValue);
        Console.WriteLine($"Footprint in bytes: {h.FootprintInBytes:N0}");

        PicolloWorkload(h, 1);

        h.GetSummary().PrettyPrint();

        var sw = Stopwatch.StartNew();

        for (int x = 0; x < runs; x++)
        {
            sw.Restart();

            ulong manualCount = 0;
            double acc = 0;
            var summary = new HdrHistogramSummary();
            int rounds = Rounds * 100;

            for (int r = 0; r < rounds; r++)
            {
                h.GetSummary(summary);
                // manualCount = 0UL;
                // foreach (var bucket in h.Buckets)
                // {
                    // manualCount += bucket.Count;
                // }
            }

            sw.Stop();

            if (summary.TotalCount != h.TotalCount && manualCount != h.TotalCount)
                throw new Exception($"manualCount != h.TotalCount");

            var totalOps = rounds;
            var elapsed = sw.Elapsed;
            var perOp = elapsed.TotalMicroseconds / totalOps;
            Console.WriteLine($"Elapsed: {elapsed}, perOp: {perOp:N2} us, StDev: {acc:N0}");
        }

        Console.WriteLine();
    }

    // https://github.com/HdrHistogram/HdrHistogram.NET/pull/169
    public static void Verify()
    {
        var hp = new HdrHistogram<uint>(0.5 / Math.Pow(10.0, SignificantDigits), 0, long.MaxValue);
        var hl = new IntHistogram(1, long.MaxValue, SignificantDigits);

        for (int i = 0; i < 8; i++)
        {
            hp.Record(1);
            hl.RecordValue(1);
        }

        hp.Record(1UL << 41);
        hl.RecordValue(1L << 41);

        hp.Record(1UL << 50);
        hl.RecordValue(1L << 50);

        var hp90 = hp.GetPercentile(90);
        var hl90 = hl.GetValueAtPercentile(90);

        Console.WriteLine($"Target P90: {(1UL << 41):N0}");
        Console.WriteLine($"Picollo P90: {hp90.GetValue(EquivalentValueSelection.UpperBound):N0}");
        Console.WriteLine($"Legacy P90: {hl90:N0}");
    }

    public static void BugRepro()
    {
        var h = new IntHistogram(1, long.MaxValue, SignificantDigits);

        for (int i = 0; i < 8; i++)
        {
            h.RecordValue(1);
        }

        h.RecordValue((1L << 41) + 1);

        h.RecordValue((1L << 50) + 1);

        var p90 = h.GetValueAtPercentile(90);

        Console.WriteLine($"Target: {(1UL << 41) + 1:N0}");
        Console.WriteLine($"Expected P90: {(1UL << 41) + (1UL << 31) - 1:N0}");
        Console.WriteLine($"Actual P90: {p90:N0}");
    }

    private static double _value;

    public static void DetectStaleMultiplyStore(int seconds = 2)
    {
        double maxBadSeen = 0;
        double expectedSign = 1;
        int bad = 0;

        long iterations = 1000_000;
        var rounds = 0;
        Console.Write($"Running for {seconds} seconds");
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));

        var mre = new ManualResetEvent(false);

        Task cleaner = Task.Factory.StartNew(() =>
        {
            mre.Set();

            var sw = Stopwatch.StartNew();
            var elapsed = sw.Elapsed;
            while (!cts.IsCancellationRequested)
            {
                rounds++;
                for (long i = 0; i < iterations; i++)
                {
                    expectedSign = -expectedSign;

                    _value = expectedSign;
                    var seen = Volatile.Read(ref _value);

                    if ((seen < 0) != (expectedSign < 0))
                    {
                        if (Math.Abs(seen) > maxBadSeen)
                            maxBadSeen = Math.Abs(seen);
                        // Console.WriteLine($"Bad {bad}, seen {seen:N0}, expected {expectedSign} in {sw.Elapsed.TotalMicroseconds:N0}");
                        bad++;
                    }
                }

                if (sw.Elapsed - elapsed >= TimeSpan.FromSeconds(1))
                {
                    Console.Write(".");
                    elapsed = sw.Elapsed;
                }
            }
        }, TaskCreationOptions.LongRunning);

        mre.WaitOne();

        while (!cts.IsCancellationRequested)
        {
            var c = 1.00001;
            for (long i = 0; i < iterations; i++)
            {
                _value *= c; // load, multiply, store
            }
        }

        cleaner.Wait();
        Console.WriteLine();
        Console.WriteLine($"iterations: {rounds * iterations:N0}");
        Console.WriteLine($"bad signs:  {bad:N0}");
        Console.WriteLine($"max bad seen:  {maxBadSeen:N0}");
    }
}