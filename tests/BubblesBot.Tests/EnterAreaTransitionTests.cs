using BubblesBot.Bot.Behaviors.Interact;

namespace BubblesBot.Tests;

public sealed class EnterAreaTransitionTests
{
    [Theory]
    [InlineData(10u, 20u, true, true)]
    [InlineData(10u, 20u, false, false)]
    [InlineData(10u, 0u, true, false)]
    [InlineData(0u, 20u, true, false)]
    [InlineData(10u, 10u, true, false)]
    public void Completion_requires_own_click_and_two_nonzero_distinct_hashes(
        uint origin, uint observed, bool clicked, bool expected)
    {
        Assert.Equal(expected,
            EnterAreaTransition.IsConfirmedAreaChange(origin, observed, clicked));
    }
}
