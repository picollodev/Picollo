using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
        var h = new HdrHistogram<uint>(relativeError, 0, ulong.MaxValue);

        h.GetRef(0)++;
        h.GetRef(1)++;
        h.GetRef(ulong.MaxValue)++;

        h.GetRef((ulong)h.BlockSize) += 12;
        h.GetRef((ulong)h.BlockSize + 1) += 12;
        h.GetRef((ulong)h.BlockSize).ShouldBe(12u);
        h.GetRef((ulong)h.BlockSize + 1).ShouldBe(12u);

        h.GetRef((ulong)h.BlockSize * 2) += 12;
        h.GetRef((ulong)h.BlockSize * 2 + 1) += 12;
        h.GetRef((ulong)h.BlockSize * 2).ShouldBe(24u);
        h.GetRef((ulong)h.BlockSize * 2 + 1).ShouldBe(24u);
        Unsafe.AreSame(ref h.GetRef((ulong)h.BlockSize * 2), ref h.GetRef((ulong)h.BlockSize * 2 + 1)).ShouldBeTrue();

        h.GetBucket((ulong)h.BlockSize * 2).Start.ShouldBe((ulong)h.BlockSize * 2);
        h.GetBucket((ulong)h.BlockSize * 2).Step.ShouldBe(2UL);

        h.GetRef((ulong)h.BlockSize * 4) += 123;
        h.GetRef((ulong)h.BlockSize * 4 + 1).ShouldBe(123u);

        Bucket bucket4 = h.GetBucket((ulong)h.BlockSize * 4);
        Console.WriteLine(bucket4.ToString());
        bucket4.Start.ShouldBe((ulong)h.BlockSize * 4);
        bucket4.Step.ShouldBe(4UL);
        bucket4.Count.ShouldBe(123UL);
        bucket4.MidPoint.ShouldBe((ulong)h.BlockSize * 4 + 2);
        bucket4.NextBucketStart.ShouldBe((ulong)h.BlockSize * 4 + 4);
        bucket4.HdrBucket.IndexInBlock.ShouldBe(0U);
        bucket4.HdrBucket.BlockSize.ShouldBe(h.BlockSize);
        bucket4.LogicalIndex.ShouldBe(h.BlockSize * 3);
        bucket4.StorageIndex.ShouldBe(h.BlockSize * 3);
    }

    [Test]
    public void GetPercentile()
    {
        var h = new HdrHistogram<uint>(0.01, 0, ulong.MaxValue);
        h.Record(1);
        h.Record(2);
        h.Record(3);
        h.Record(4);
        h.Record(5);
        h.GetPercentileValue(rank: 0).ShouldBe(1ul);
        h.GetPercentileValue(rank: 10).ShouldBe(1ul);
        h.GetPercentileValue(rank: 20).ShouldBe(1ul);
        h.GetPercentileValue(rank: 40).ShouldBe(2ul);
        h.GetPercentileValue(rank: 79).ShouldBe(4ul);
        h.GetPercentileValue(rank: 80).ShouldBe(4ul);
        h.GetPercentileValue(rank: 81).ShouldBe(5ul);
        h.GetPercentileValue(rank: 100).ShouldBe(5ul);
    }

    [Test]
    public void GetPercentiles()
    {
        var h = new HdrHistogram<uint>(0.01, 0, ulong.MaxValue);
        h.Record(1);
        h.Record(2);
        h.Record(3);
        h.Record(4);
        h.Record(5);

        Span<double> ranks = [0, 40, 80, 100];
        Span<Percentile> percentiles = stackalloc Percentile[ranks.Length];
        h.GetPercentiles(ranks, percentiles);

        percentiles[0].Value.ShouldBe(1ul);
        percentiles[1].Value.ShouldBe(2ul);
        percentiles[2].Value.ShouldBe(4ul);
        percentiles[3].Value.ShouldBe(5ul);
    }

    [Test]
    public void GetPercentilesRejectsInvalidRanks()
    {
        var h = new HdrHistogram<uint>(0.01, 0, ulong.MaxValue);
        h.Record(1);

        Should.Throw<ArgumentException>(() =>
        {
            Span<double> duplicateRanks = [10, 10];
            Span<Percentile> percentiles = stackalloc Percentile[duplicateRanks.Length];
            h.GetPercentiles(duplicateRanks, percentiles);
        });

        Should.Throw<ArgumentException>(() =>
        {
            Span<double> outOfRangeRanks = [-1, 10];
            Span<Percentile> percentiles = stackalloc Percentile[outOfRangeRanks.Length];
            h.GetPercentiles(outOfRangeRanks, percentiles);
        });

        Should.Throw<ArgumentException>(() =>
        {
            Span<double> nanRanks = [10, double.NaN];
            Span<Percentile> percentiles = stackalloc Percentile[nanRanks.Length];
            h.GetPercentiles(nanRanks, percentiles);
        });
    }

    [Test]
    public void OverflowCountIncreasesForValuesOutsideRange()
    {
        var h = new HdrHistogram<ulong>(relativeError: 0.0001, minTrackableValue: 100, maxTrackableValue: 200);

        h.OverflowCount.ShouldBe(0ul);

        // value below the trackable range
        h.Record(99);
        h.OverflowCount.ShouldBe(1ul);
        h.GetBucket(99).IsOverflowBucket.ShouldBeTrue();
        h.GetBucket(99).Count.ShouldBe(1ul);

        // value above the trackable range
        h.Record(201);
        h.OverflowCount.ShouldBe(2ul);
        h.GetBucket(201).IsOverflowBucket.ShouldBeTrue();
        h.GetBucket(201).Count.ShouldBe(2ul);

        h.Record(100);
        Bucket bucket100 = h.GetBucket(100);
        bucket100.IsOverflowBucket.ShouldBeFalse();
        bucket100.Count.ShouldBe(1ul);
        bucket100.Start.ShouldBe(100ul);
        bucket100.Step.ShouldBe(1ul);
        bucket100.StorageIndex.ShouldBe(0);

        h.Record(150);
        Bucket bucket150 = h.GetBucket(150);
        bucket150.IsOverflowBucket.ShouldBeFalse();
        bucket150.Start.ShouldBe(150ul);
        bucket150.StorageIndex.ShouldBe(50);
        h.GetBucket(150).Count.ShouldBe(1ul);

        h.OverflowCount.ShouldBe(2ul);

        h.GetPercentileValue(0).ShouldBe(100ul);
        h.GetPercentileValue(50).ShouldBe(100ul);
        h.GetPercentileValue(50.01).ShouldBe(150ul);
    }

    [Test]
    public void ShouldIterateBuckets()
    {
        var h = new HdrHistogram<ulong>(relativeError: 0.01, minTrackableValue: 50, maxTrackableValue: 100);
        h.Buckets.Count().ShouldBe(51);
        h.BucketPercentiles.Count().ShouldBe(51);
        h.BucketsWithValues.Count().ShouldBe(0);
        h.BucketPercentilesWithValues.Count().ShouldBe(0);

        for (int i = 0; i < 10; i++)
        {
            h.Record(50);
            h.Buckets.Count().ShouldBe(51);
            h.BucketPercentiles.Count().ShouldBe(51);
            h.BucketsWithValues.Count().ShouldBe(1);
            h.BucketPercentilesWithValues.Count().ShouldBe(1);
        }

        h.Record(100);
        h.Buckets.Count().ShouldBe(51);
        h.BucketPercentiles.Count().ShouldBe(51);
        h.BucketsWithValues.Count().ShouldBe(2);
        h.BucketPercentilesWithValues.Count().ShouldBe(2);

        h.Record(75);
        h.Buckets.Count().ShouldBe(51);
        h.BucketPercentiles.Count().ShouldBe(51);
        h.BucketsWithValues.Count().ShouldBe(3);
        h.BucketPercentilesWithValues.Count().ShouldBe(3);

        Should.Throw<InvalidOperationException>(() =>
        {
            _ = (new Buckets()).Count();
        });

        Should.Throw<InvalidOperationException>(() =>
        {
            var e = h.Buckets.GetEnumerator();
            e.Dispose();
            e.MoveNext();
        });

        Should.Throw<InvalidOperationException>(() =>
        {
            _ = (new BucketPercentiles()).Count();
        });

        Should.Throw<InvalidOperationException>(() =>
        {
            var e = h.BucketPercentiles.GetEnumerator();
            e.Dispose();
            e.MoveNext();
        });

        h.GetBucket(50).Start.ShouldBe(50ul);

        h.GetBucket(500).IsOverflowBucket.ShouldBeTrue();
        h.GetBucket(500).IsValid.ShouldBeFalse();
    }

    [Test]
    public void SummarySerializationRoundtrip()
    {
        var h = new HdrHistogram<ulong>(relativeError: 0.001, minTrackableValue: 200, maxTrackableValue: 100000);

        for (ulong v = 0; v <= 2000; v++)
            h.Record(v * 100);

        var summary = h.GetSummary();

        var opt = new JsonSerializerOptions {WriteIndented = true};

        string json = JsonSerializer.Serialize(summary, opt);
        Console.WriteLine(json);
        var summary2 = JsonSerializer.Deserialize<HdrHistogramSummary>(json);

        summary2.ShouldNotBeNull();
        summary2.ShouldBe(summary);

        summary.Percentiles.ToArray().Select(x => x.Value)
            .SequenceEqual(summary2.Percentiles.ToArray().Select(x => x.Value))
            .ShouldBeTrue();
    }
}