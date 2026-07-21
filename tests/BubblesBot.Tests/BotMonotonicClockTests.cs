using BubblesBot.Bot.Systems;

namespace BubblesBot.Tests;

public sealed class BotMonotonicClockTests
{
    [Fact]
    public void ElapsedSince_NeverObservedSentinel_IsImmediatelyEligible()
    {
        Assert.Equal(TimeSpan.MaxValue, BotMonotonicClock.ElapsedSince(TimeSpan.MinValue));
    }

    [Fact]
    public void ElapsedSince_FutureTimestamp_DoesNotGoNegative()
    {
        Assert.Equal(TimeSpan.Zero, BotMonotonicClock.ElapsedSince(TimeSpan.MaxValue));
    }
}
