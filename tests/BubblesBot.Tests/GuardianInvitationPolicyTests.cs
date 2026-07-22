using BubblesBot.Bot.Strategies;

namespace BubblesBot.Tests;

public sealed class GuardianInvitationPolicyTests
{
    [Fact]
    public void ParsesTheFormedQuantityAndSafeMods()
    {
        var state = GuardianInvitationPolicy.EvaluateTooltip(
        [
            "Maven's Invitation: The Formed",
            "Item Quantity: +72%",
            "Monsters deal 100% extra Damage as Fire",
        ]);

        Assert.True(state.IsTheFormed);
        Assert.Equal(72, state.ItemQuantity);
        Assert.False(state.HasForbiddenModifier);
    }

    [Theory]
    [InlineData("Monsters reflect 18% of Physical Damage")]
    [InlineData("Monsters reflect 18% of Elemental Damage")]
    [InlineData("Players cannot Regenerate Life or Mana")]
    [InlineData("Players cannot Regenerate Mana")]
    public void RejectsConfiguredForbiddenModifiers(string modifier)
    {
        var state = GuardianInvitationPolicy.EvaluateTooltip(
            ["Maven's Invitation: The Formed", "Item Quantity: +65%", modifier]);

        Assert.True(state.HasForbiddenModifier);
        Assert.Contains(modifier, state.ForbiddenLines);
    }

    [Fact]
    public void MissingIdentityOrQuantityFailsClosed()
    {
        var state = GuardianInvitationPolicy.EvaluateTooltip(["Some Map"]);

        Assert.False(state.IsTheFormed);
        Assert.Equal(-1, state.ItemQuantity);
    }

    [Fact]
    public void BuildPolicyOverridesAreAppliedToInvitationMods()
    {
        var state = GuardianInvitationPolicy.EvaluateTooltip(
            ["Maven's Invitation: The Formed", "Item Quantity: +80%",
                "Monsters deal 98% extra Physical Damage as Fire"],
            ["monster.extra-fire=2"]);

        Assert.True(state.HasForbiddenModifier);
        Assert.Contains("Monsters deal 98% extra Physical Damage as Fire", state.ForbiddenLines);
    }
}
