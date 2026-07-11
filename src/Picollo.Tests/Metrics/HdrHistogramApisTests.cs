using System;
using System.Collections.Generic;
using NUnit.Framework;
using Picollo.Metrics;
using Shouldly;

namespace Picollo.Tests.Metrics;

[TestFixtureSource(nameof(HistogramImplementations))]
public class HdrHistogramApisTests
{
    private readonly Func<HdrHistogram> _factory;

    public HdrHistogramApisTests(Func<HdrHistogram> factory)
    {
        _factory = factory;
    }

    private static IEnumerable<TestFixtureData> HistogramImplementations()
    {
        yield return new TestFixtureData((Func<HdrHistogram>)HdrHistogram.Create) { TestName = "Simple" };
        yield return new TestFixtureData((Func<HdrHistogram>)(() => HdrHistogram.Factory.WithInterlocked().Create()))
        {
            TestName = "Interlocked"
        };
        yield return new TestFixtureData((Func<HdrHistogram>)(() => HdrHistogram.Factory.WithThreadLocal().Create()))
        {
            TestName = "ThreadLocal"
        };
    }

    [Test]
    public void should_record_and_get_summary()
    {
        var h = _factory();
        h.Record(1);
        h.GetSummary().TotalCount.ShouldBe(1UL);
    }
    
    [Test]
    public void should_record_and_get_summary_after_initial_reset()
    {
        var h = _factory();
        h.Reset();
        h.Record(1);
        h.GetSummary().TotalCount.ShouldBe(1UL);
    }
    
    [Test]
    public void should_record_and_get_summary_after_two_resets()
    {
        var h = _factory();
        h.Reset();
        h.Record(1);
        h.Reset();
        h.Record(1);
        h.GetSummary().TotalCount.ShouldBe(1UL);
    }
    
}