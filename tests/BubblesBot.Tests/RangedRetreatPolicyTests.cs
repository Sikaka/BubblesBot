using BubblesBot.Bot.Systems;

namespace BubblesBot.Tests;

public sealed class RangedRetreatPolicyTests
{
    [Fact]
    public void FlickerStrike_IsRecognizedAsTeleportingHoldAttack()
    {
        var slot = new BubblesBot.Bot.Settings.SkillSlot
        {
            Name = "Flicker Strike (Q)",
            HoldToRepeat = true,
            Role = BubblesBot.Bot.Settings.SkillRole.Attack,
        };

        Assert.True(CombatCoordinator.IsFlickerStrike(slot));
        slot.HoldToRepeat = false;
        Assert.False(CombatCoordinator.IsFlickerStrike(slot));
    }

    [Theory]
    [InlineData(25, 55, 45, true)]
    [InlineData(55, 55, 45, true)]
    [InlineData(19, 55, 45, false)]
    [InlineData(56, 55, 45, false)]
    public void ShouldAttackWhileDashRecharges_RequiresSafeInRangeTarget(
        float distance, float range, float standoff, bool expected)
        => Assert.Equal(expected,
            RangedRetreatPolicy.ShouldAttackWhileDashRecharges(distance, range, standoff));

    [Fact]
    public void ShouldAttackWhileDashRecharges_RejectsInvalidRange()
        => Assert.False(RangedRetreatPolicy.ShouldAttackWhileDashRecharges(30, 0, 45));
}
