using Cictna.Industrial.OpcUa.Internal;

namespace Cictna.Industrial.OpcUa.Tests;

/// <summary>Sequence number arithmetic (wraps from 4294967295 to 1, never 0) and reconnect backoff.</summary>
public sealed class SubscriptionLogicTests
{
    [Theory]
    [InlineData(0u, 1u)]
    [InlineData(1u, 2u)]
    [InlineData(uint.MaxValue - 1, uint.MaxValue)]
    [InlineData(uint.MaxValue, 1u)] // 0 is skipped (Part 4, 7.25)
    public void NextWrapsToOne(uint current, uint next) => Assert.Equal(next, SubscriptionEngine.Next(current));

    [Theory]
    [InlineData(2u, 1u)]
    [InlineData(1u, uint.MaxValue)]
    public void PreviousWrapsToTheTop(uint current, uint previous) => Assert.Equal(previous, SubscriptionEngine.Previous(current));

    [Theory]
    [InlineData(5u, 5u, 0u)]
    [InlineData(5u, 8u, 3u)]
    [InlineData(uint.MaxValue, 1u, 1u)]
    [InlineData(uint.MaxValue - 1, 2u, 3u)] // max-1 -> max -> 1 -> 2
    public void DistanceCountsStepsAcrossTheWrap(uint from, uint to, uint steps) => Assert.Equal(steps, SubscriptionEngine.Distance(from, to));

    [Fact]
    public void WalkingNextCoversExactlyTheDistance()
    {
        var from = uint.MaxValue - 3;
        var to = 4u;
        var steps = 0u;
        for (var s = from; s != to; s = SubscriptionEngine.Next(s))
        {
            steps++;
        }
        Assert.Equal(steps, SubscriptionEngine.Distance(from, to));
    }

    [Theory]
    [InlineData(10u, 10u, true)]   // the same message again
    [InlineData(9u, 10u, true)]    // older
    [InlineData(11u, 10u, false)]  // newer
    [InlineData(uint.MaxValue, 2u, true)]  // older across the wrap
    [InlineData(1u, uint.MaxValue, false)] // newer across the wrap
    public void DuplicatesAndOldMessagesAreRecognized(uint sequence, uint lastDelivered, bool atOrBefore) =>
        Assert.Equal(atOrBefore, SubscriptionEngine.IsAtOrBefore(sequence, lastDelivered));

    [Fact]
    public void BackoffGrowsAndIsCapped()
    {
        var options = new ReconnectOptions { InitialDelay = TimeSpan.FromSeconds(1), MaxDelay = TimeSpan.FromSeconds(30), Multiplier = 2, Jitter = 0 };
        var random = new Random(1);
        Assert.Equal(TimeSpan.FromSeconds(1), options.DelayFor(1, random));
        Assert.Equal(TimeSpan.FromSeconds(2), options.DelayFor(2, random));
        Assert.Equal(TimeSpan.FromSeconds(16), options.DelayFor(5, random));
        Assert.Equal(TimeSpan.FromSeconds(30), options.DelayFor(6, random));
        Assert.Equal(TimeSpan.FromSeconds(30), options.DelayFor(1000, random)); // no overflow
    }

    [Fact]
    public void JitterStaysWithinItsBand()
    {
        var options = new ReconnectOptions { InitialDelay = TimeSpan.FromSeconds(10), Jitter = 0.2 };
        var random = new Random(7);
        var delays = Enumerable.Range(0, 1000).Select(_ => options.DelayFor(1, random).TotalSeconds).ToArray();
        Assert.All(delays, d => Assert.InRange(d, 8.0, 12.0));
        Assert.True(delays.Max() - delays.Min() > 2, "jitter should actually spread the delays");
    }
}
