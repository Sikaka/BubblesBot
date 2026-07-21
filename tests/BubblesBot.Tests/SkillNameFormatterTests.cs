using BubblesBot.Core.Snapshot;

namespace BubblesBot.Tests;

public sealed class SkillNameFormatterTests
{
    [Theory]
    [InlineData("ShieldCharge", "Shield Charge")]
    [InlineData("PenanceMark", "Penance Mark")]
    [InlineData("PlayerMelee", "Basic attack")]
    [InlineData("Move", "Move only")]
    public void HumanizesInternalActorSkillNames(string internalName, string expected)
        => Assert.Equal(expected, SkillNameFormatter.ToDisplayName(internalName));

    [Fact]
    public void UsesExactEquippedGemDisplayName()
        => Assert.Equal("Righteous Fire",
            SkillNameFormatter.ToDisplayName("RighteousFire", ["Righteous Fire"]));

    [Fact]
    public void ResolvesAlternateInternalNameToUniqueTransfiguredGem()
        => Assert.Equal("Firestorm of Pelting",
            SkillNameFormatter.ToDisplayName("FirestormAltY",
                ["Firestorm of Pelting", "Increased Area of Effect Support"]));

    [Fact]
    public void KeepsHumanizedInternalNameWhenAlternateMatchIsAmbiguous()
        => Assert.Equal("Firestorm Alt Y",
            SkillNameFormatter.ToDisplayName("FirestormAltY",
                ["Firestorm of Pelting", "Firestorm of Meteors"]));
}
