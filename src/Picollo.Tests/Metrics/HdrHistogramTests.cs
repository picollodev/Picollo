using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

using NUnit.Framework;

using Picollo.Metrics;

using Shouldly;

namespace Picollo.Tests.Metrics;

[TestFixture]
public class HdrHistogramTests
{
    [TestCase(0.1)]
    [TestCase(0.01)]
    [TestCase(0.005)]
    [TestCase(0.001)]
    [TestCase(0.0005)]
    [TestCase(0.0001)]
    [TestCase(0.00001)]
    public void QuantizationStartsAfterSecondBucket(double relativeError)
    {
        var h = new HdrHistogram<uint>(relativeError);

        h.GetRef(0)++;
        h.GetRef(1)++;
        h.GetRef(ulong.MaxValue)++;

        h.GetRef((ulong)h.BucketSize) += 12;
        h.GetRef((ulong)h.BucketSize + 1) += 12;
        h.GetRef((ulong)h.BucketSize).ShouldBe(12u);
        h.GetRef((ulong)h.BucketSize + 1).ShouldBe(12u);

        h.GetRef((ulong)h.BucketSize * 2) += 12;
        h.GetRef((ulong)h.BucketSize * 2 + 1) += 12;
        h.GetRef((ulong)h.BucketSize * 2).ShouldBe(24u);
        h.GetRef((ulong)h.BucketSize * 2 + 1).ShouldBe(24u);
        Unsafe.AreSame(ref h.GetRef((ulong)h.BucketSize * 2), ref h.GetRef((ulong)h.BucketSize * 2 + 1)).ShouldBeTrue();

        h.GetRef((ulong)h.BucketSize * 4) += 123;
        h.GetRef((ulong)h.BucketSize * 4 + 1).ShouldBe(123u);
    }

    [Test, Explicit]
    public void GetRefBench()
    {
        var count = 1_000_000;
        var values = new ulong[count];
        Random random = new Random();
        for (int i = 0; i < count; i++)
        {
            values[i] = (ulong)random.NextInt64();
        }

        random.Shuffle(values);

        var h = new HdrHistogram<ulong>(0.001);

        for (int x = 0; x < 50; x++)
        {
            var rounds = 200;
            var sw = Stopwatch.StartNew();
            for (int r = 0; r < rounds; r++)
            {
                foreach (ulong value in values)
                {
                    // Interlocked.Increment(ref h.GetRef(value));
                    h.Increment(value);
                }
            }

            sw.Stop();

            var totalOps = rounds * count;
            var elapsed = sw.Elapsed;
            var perOp = elapsed.TotalNanoseconds / totalOps;
            Console.WriteLine($"Elapsed: {elapsed}, perOp: {perOp:N2} ns");
        }
    }

    // [Test, Explicit]
    // public void GetRefBenchHdr()
    // {
    //     var count = 10_000_000;
    //     var values = new long[count];
    //     Random random = new Random();
    //     for (int i = 0; i < count; i++)
    //     {
    //         values[i] = random.NextInt64(1, long.MaxValue);
    //     }
    //
    //     random.Shuffle(values);
    //
    //     var h = new LongHistogram(1, long.MaxValue, 3);
    //     var sizeInBytes = h.GetEstimatedFootprintInBytes();
    //     Console.WriteLine($"Size in bytes: {sizeInBytes:N0}");
    //     for (int x = 0; x < 50; x++)
    //     {
    //         var rounds = 10;
    //         var sw = Stopwatch.StartNew();
    //         for (int r = 0; r < rounds; r++)
    //         {
    //             foreach (long value in values)
    //             {
    //                 h.RecordValue(value);
    //             }
    //         }
    //
    //         sw.Stop();
    //
    //         var totalOps = rounds * count;
    //         var elapsed = sw.Elapsed;
    //         var perOp = elapsed.TotalNanoseconds / totalOps;
    //         Console.WriteLine($"Elapsed: {elapsed}, perOp: {perOp:N2} ns");
    //     }
    // }
}