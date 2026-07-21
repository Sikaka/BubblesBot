using BubblesBot.Bot.Modes;

namespace BubblesBot.Tests;

public sealed class DeliriumPacingPolicyTests
{
    [Fact]
    public void InitialLeadAllowsCrossingAndFirstPacks()
        => Assert.False(DeliriumPacingPolicy.ShouldThrottle(59, 0, 60, 12));

    [Fact]
    public void FastCharacterIsHeldAheadOfExpandingFront()
        => Assert.True(DeliriumPacingPolicy.ShouldThrottle(181, 10, 60, 12));

    [Fact]
    public void SameDistanceBecomesSafeAsFrontExpands()
        => Assert.False(DeliriumPacingPolicy.ShouldThrottle(180, 10, 60, 12));

    [Fact]
    public void NegativeConfigurationCannotCreateNegativeAllowance()
        => Assert.Equal(0, DeliriumPacingPolicy.ForwardAllowance(10, -20, -5));
}
