using BubblesBot.Bot.Systems;

namespace BubblesBot.Tests;

public sealed class BotRunTimerTests
{
    [Fact]
    public void ExpiresAcrossOneContinuousArmedSession()
    {
        var timer = new BotRunTimer();
        timer.Observe(true, 10, TimeSpan.FromSeconds(1));

        var state = timer.Observe(true, 10, TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(2));

        Assert.True(state.Expired);
        Assert.Equal(TimeSpan.Zero, state.Remaining);
    }

    [Fact]
    public void DisarmResetsTheNextSession()
    {
        var timer = new BotRunTimer();
        timer.Observe(true, 10, TimeSpan.FromMinutes(1));
        timer.Observe(false, 10, TimeSpan.FromMinutes(9));

        var state = timer.Observe(true, 10, TimeSpan.FromMinutes(20));

        Assert.False(state.Expired);
        Assert.Equal(TimeSpan.Zero, state.Elapsed);
    }

    [Fact]
    public void ZeroLimitNeverExpires()
    {
        var timer = new BotRunTimer();
        timer.Observe(true, 0, TimeSpan.FromSeconds(1));

        var state = timer.Observe(true, 0, TimeSpan.FromDays(2));

        Assert.False(state.Expired);
        Assert.Null(state.Remaining);
    }
}
