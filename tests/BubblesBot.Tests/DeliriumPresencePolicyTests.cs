using BubblesBot.Bot.Modes;

namespace BubblesBot.Tests;

public sealed class DeliriumPresencePolicyTests
{
    [Fact]
    public void HealthyScanSettlesMirrorAbsenceAfterFiveSeconds()
    {
        Assert.False(DeliriumPresencePolicy.IsNotPresent(true, 4.99));
        Assert.True(DeliriumPresencePolicy.IsNotPresent(true, 5.0));
    }

    [Fact]
    public void UnhealthyScanNeverProvesAbsence()
        => Assert.False(DeliriumPresencePolicy.IsNotPresent(false, 30));
}
