using BubblesBot.Bot.Systems;

namespace BubblesBot.Tests;

public sealed class MovementOwnershipTests
{
    [Fact]
    public void Owner_scoped_release_cannot_cancel_another_behavior()
    {
        var active = new object();
        var inactiveSibling = new object();

        Assert.False(MovementSystem.CanRelease(active, inactiveSibling));
        Assert.True(MovementSystem.CanRelease(active, active));
    }

    [Fact]
    public void Ownerless_release_is_an_unconditional_mode_stop()
    {
        Assert.True(MovementSystem.CanRelease(new object(), requestedOwner: null));
        Assert.True(MovementSystem.CanRelease(currentOwner: null, requestedOwner: null));
    }
}
