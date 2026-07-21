using BubblesBot.Bot.Modes;

namespace BubblesBot.Tests;

public sealed class MapDeadlinePolicyTests
{
    [Fact]
    public void WholeMapDeadlineExpiresAtConfiguredLimit()
    {
        var start = TimeSpan.FromSeconds(10);
        Assert.False(MapDeadlinePolicy.IsExpired(start, start.Add(TimeSpan.FromMinutes(5.99)), 6));
        Assert.True(MapDeadlinePolicy.IsExpired(start, start.Add(TimeSpan.FromMinutes(6)), 6));
    }

    [Fact]
    public void DisabledOrUnstartedDeadlineNeverExpires()
    {
        Assert.False(MapDeadlinePolicy.IsExpired(TimeSpan.FromSeconds(1), TimeSpan.FromHours(1), 0));
        Assert.False(MapDeadlinePolicy.IsExpired(TimeSpan.Zero, TimeSpan.FromHours(1), 6));
    }

    [Fact]
    public void DeadlineDoesNotResetAtSubzoneBoundaries()
    {
        var mapStart = TimeSpan.FromMinutes(3);
        var afterSeveralSubzones = TimeSpan.FromMinutes(9);
        Assert.True(MapDeadlinePolicy.IsExpired(mapStart, afterSeveralSubzones, 6));
    }
}
