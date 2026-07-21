using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;

namespace BubblesBot.Tests;

public sealed class MovementProgressWatchdogTests
{
    [Fact]
    public void StationaryMovementTriggersOnlyAfterThreshold()
    {
        var watchdog = new MovementProgressWatchdog(2, TimeSpan.FromMilliseconds(1200));
        var cell = new Vector2i { X = 10, Y = 10 };

        Assert.False(watchdog.Observe(cell, TimeSpan.Zero));
        Assert.False(watchdog.Observe(cell, TimeSpan.FromMilliseconds(1200)));
        Assert.True(watchdog.Observe(cell, TimeSpan.FromMilliseconds(1201)));
    }

    [Fact]
    public void RealProgressRestartsStuckWindow()
    {
        var watchdog = new MovementProgressWatchdog(2, TimeSpan.FromMilliseconds(1200));
        watchdog.Observe(new Vector2i { X = 10, Y = 10 }, TimeSpan.Zero);

        Assert.False(watchdog.Observe(new Vector2i { X = 13, Y = 10 }, TimeSpan.FromMilliseconds(1000)));
        Assert.False(watchdog.Observe(new Vector2i { X = 13, Y = 10 }, TimeSpan.FromMilliseconds(2000)));
        Assert.True(watchdog.Observe(new Vector2i { X = 13, Y = 10 }, TimeSpan.FromMilliseconds(2201)));
    }
}
