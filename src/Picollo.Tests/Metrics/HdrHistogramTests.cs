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

        h.GetRef((ulong)h.BlockSize * 4) += 123;
        h.GetRef((ulong)h.BlockSize * 4 + 1).ShouldBe(123u);
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
        bucket100.Index.ShouldBe(0);

        h.Record(150);
        Bucket bucket150 = h.GetBucket(150);
        bucket150.IsOverflowBucket.ShouldBeFalse();
        bucket150.Start.ShouldBe(150ul);
        bucket150.Index.ShouldBe(50);
        h.GetBucket(150).Count.ShouldBe(1ul);

        h.OverflowCount.ShouldBe(2ul);

        h.GetPercentileValue(0).ShouldBe(100ul);
        h.GetPercentileValue(50).ShouldBe(100ul);
        h.GetPercentileValue(50.01).ShouldBe(150ul);
    }
}