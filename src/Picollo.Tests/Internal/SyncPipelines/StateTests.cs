using NUnit.Framework;
using Picollo.Internal.SyncPipelines;
using Shouldly;

namespace Picollo.Tests.Internal.SyncPipelines;

[TestFixture]
public class StateTests
{
    [Test]
    public void AddAndSubtractRoundTrip()
    {
        var start = SyncPipeBase.FlaggedPosition.FromValue(123);

        var end = start + 456;

        end.Value.ShouldBe(579);
        (end - start).ShouldBe(456);
        (end - start.Value).ShouldBe(456L);
    }

    [Test]
    public void SubtractReturnsNegativeDelta()
    {
        var start = SyncPipeBase.FlaggedPosition.FromValue(123);
        var end = start + 456;

        (start - end).ShouldBe(-456);
    }

    [Test]
    public void AddWrapsAndSubtractReturnsAddition()
    {
        var start = SyncPipeBase.FlaggedPosition.FromValue(long.MaxValue - 3);
        const long addition = 10;

        var end = start + addition;

        end.Value.ShouldBe(6);
        (end - start).ShouldBe((long)addition);
        (end - start.Value).ShouldBe(addition);
    }

    [Test]
    public void SubtractAcrossWrapReturnsNegativeDeltaInReverse()
    {
        var start = SyncPipeBase.FlaggedPosition.FromValue(long.MaxValue - 3);
        var end = start + 10;

        (start - end).ShouldBe(-10);
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void SubtractIgnoresFlags(bool startFlag, bool endFlag)
    {
        var start = SyncPipeBase.FlaggedPosition.FromValue(long.MaxValue - 3).SetFlag(startFlag);
        var end = (start.ClearFlag() + 10).SetFlag(endFlag);

        (end - start).ShouldBe(10);
    }

    [Test]
    public void AddClearFlagWrapsAndClearsFlag()
    {
        var start = SyncPipeBase.FlaggedPosition.FromValue(long.MaxValue - 3).SetFlag();

        var end = start.AddClearFlag(10);

        end.Value.ShouldBe(6);
        end.IsFlagSet.ShouldBeFalse();
        (end - start).ShouldBe(10);
    }
}
