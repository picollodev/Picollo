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

    
}