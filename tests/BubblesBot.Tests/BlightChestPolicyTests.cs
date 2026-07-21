using BubblesBot.Bot.Modes;
using BubblesBot.Bot.Settings;

namespace BubblesBot.Tests;

public sealed class BlightChestPolicyTests
{
    [Theory]
    [InlineData("Metadata/Chests/BlightCurrencyLow", BlightChestCategory.Currency)]
    [InlineData("Metadata/Chests/BlightMushrunes9", BlightChestCategory.Oils)]
    [InlineData("Metadata/Chests/BlightDivinationCardsStackedDeck", BlightChestCategory.DivinationCards)]
    [InlineData("Metadata/Chests/BlightFragments", BlightChestCategory.Fragments)]
    [InlineData("Metadata/Chests/BlightEssences", BlightChestCategory.Essences)]
    [InlineData("Metadata/Chests/BlightTrinkets", BlightChestCategory.Jewels)]
    [InlineData("Metadata/Chests/BlightArmourGeneric", BlightChestCategory.Equipment)]
    [InlineData("Metadata/Chests/BlightWeaponSynthesis", BlightChestCategory.Equipment)]
    [InlineData("Metadata/Chests/BlightTalismanT1", BlightChestCategory.Talismans)]
    [InlineData("Metadata/Chests/BlightSomethingNew", BlightChestCategory.Other)]
    public void Classifies_observed_reward_paths(string path, BlightChestCategory expected)
    {
        Assert.Equal(expected, BlightChestPolicy.Classify(path));
    }

    [Fact]
    public void All_categories_are_enabled_by_default()
    {
        var settings = new BotSettings();
        foreach (var category in Enum.GetValues<BlightChestCategory>())
        {
            var path = category switch
            {
                BlightChestCategory.Currency => "Metadata/Chests/BlightCurrencyLow",
                BlightChestCategory.Oils => "Metadata/Chests/BlightMushrunes9",
                BlightChestCategory.DivinationCards => "Metadata/Chests/BlightDivinationCards",
                BlightChestCategory.Fragments => "Metadata/Chests/BlightFragments",
                BlightChestCategory.Essences => "Metadata/Chests/BlightEssences",
                BlightChestCategory.Jewels => "Metadata/Chests/BlightTrinkets",
                BlightChestCategory.Equipment => "Metadata/Chests/BlightWeaponGeneric",
                BlightChestCategory.Talismans => "Metadata/Chests/BlightTalismanT1",
                _ => "Metadata/Chests/BlightSomethingNew",
            };
            Assert.True(BlightChestPolicy.IsEnabled(path, settings));
        }
    }

    [Fact]
    public void Disabled_category_does_not_disable_other_categories()
    {
        var settings = new BotSettings { BlightChestEquipment = false };
        Assert.False(BlightChestPolicy.IsEnabled("Metadata/Chests/BlightWeaponGeneric", settings));
        Assert.True(BlightChestPolicy.IsEnabled("Metadata/Chests/BlightCurrencyLow", settings));
    }
}
