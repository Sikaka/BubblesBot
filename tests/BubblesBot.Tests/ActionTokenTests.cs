using BubblesBot.Bot.Input;

namespace BubblesBot.Tests;

public sealed class ActionTokenTests
{
    [Fact]
    public void VerifiedActionConfirmsWithLatencyEvidence()
    {
        var confirmed = false;
        var start = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc);
        var token = new ActionToken(7, "test", start, 1000, () => confirmed, TimeSpan.Zero);
        token.MarkDispatched(start.AddMilliseconds(25), TimeSpan.FromMilliseconds(25));

        Assert.False(token.Poll(start.AddMilliseconds(100), TimeSpan.FromMilliseconds(100)));
        confirmed = true;
        Assert.True(token.Poll(start.AddMilliseconds(120), TimeSpan.FromMilliseconds(120)));
        Assert.Equal(ActionOutcome.Confirmed, token.Outcome);
        Assert.Equal(start.AddMilliseconds(120), token.ConfirmedAt);
    }

    [Fact]
    public void VerifiedActionTimesOutFromDispatchNotRequest()
    {
        var start = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc);
        var token = new ActionToken(8, "test", start, 100, () => false, TimeSpan.Zero);
        token.MarkDispatched(start.AddSeconds(1), TimeSpan.FromSeconds(1));

        Assert.False(token.Poll(start.AddMilliseconds(1050), TimeSpan.FromMilliseconds(1050)));
        Assert.True(token.Poll(start.AddMilliseconds(1100), TimeSpan.FromMilliseconds(1100)));
        Assert.Equal(ActionOutcome.TimedOut, token.Outcome);
    }

    [Fact]
    public void PredicateLessActionSettlesWithoutClaimingConfirmation()
    {
        var start = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc);
        var token = new ActionToken(9, "test", start, 50, null, TimeSpan.Zero);
        token.MarkDispatched(start, TimeSpan.Zero);

        Assert.True(token.Poll(start.AddMilliseconds(50), TimeSpan.FromMilliseconds(50)));
        Assert.Equal(ActionOutcome.SettledUnverified, token.Outcome);
        Assert.Null(token.ConfirmedAt);
    }
}
