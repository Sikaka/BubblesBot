using BubblesBot.Bot.Modes;

namespace BubblesBot.Tests;

public sealed class RitualShopPolicyTests
{
    [Theory]
    [InlineData("Metadata/Items/Currency/CurrencyUpgradeMagicToRare")]
    [InlineData("Metadata/Items/Scarabs/ScarabDivination")]
    public void GenericCurrencyAndScarabsAreLiquidFiller(string path)
        => Assert.True(RitualShopPolicy.IsLiquidFillerPath(path));

    [Theory]
    [InlineData("Metadata/Items/Currency/CurrencyEssenceEnvy3")]
    [InlineData("Metadata/Items/Currency/AncestralOmenOnChromaticAddWhiteSockets")]
    [InlineData("Metadata/Items/Currency/Mushrune11")]
    [InlineData("Metadata/Items/Currency/CurrencyRitualSplinter")]
    [InlineData("Metadata/Items/DivinationCards/DivinationCardTheGambler")]
    public void SpecializedRewardsMustClearTheOrdinaryValueFloor(string path)
        => Assert.False(RitualShopPolicy.IsLiquidFillerPath(path));

    [Fact]
    public void RerollPhaseOnlyBuysAboveHighValueThreshold()
    {
        var offers = new[]
        {
            new RitualShopCandidate(0, 14.9f, false),
            new RitualShopCandidate(1, 15f, false),
            new RitualShopCandidate(2, 40f, false),
        };
        Assert.Equal(2, RitualShopPolicy.SelectIndex(offers, true, 15f, 3f));
    }

    [Fact]
    public void RerollPhaseReturnsNoneWhenBestOfferIsBelowThreshold()
    {
        var offers = new[]
        {
            new RitualShopCandidate(0, 12f, false),
            new RitualShopCandidate(1, 2f, true),
        };
        Assert.Null(RitualShopPolicy.SelectIndex(offers, true, 15f, 3f));
    }

    [Fact]
    public void FinalPhaseBuysValuableOfferBeforeLiquidFiller()
    {
        var offers = new[]
        {
            new RitualShopCandidate(0, 1f, true),
            new RitualShopCandidate(1, 8f, false),
        };
        Assert.Equal(1, RitualShopPolicy.SelectIndex(offers, false, 15f, 3f));
    }

    [Fact]
    public void FinalPhasePrefersLiquidFillerOverNominalLowValueCard()
    {
        var offers = new[]
        {
            new RitualShopCandidate(0, 1f, false),
            new RitualShopCandidate(1, 0.4f, true),
            new RitualShopCandidate(2, 0.8f, true),
        };
        Assert.Equal(2, RitualShopPolicy.SelectIndex(offers, false, 15f, 3f));
    }
}
