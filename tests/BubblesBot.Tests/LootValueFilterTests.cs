using System.Text.Json;
using BubblesBot.Bot.Behaviors.Loot;
using BubblesBot.Bot.Settings;
using BubblesBot.Core.Game;
using BubblesBot.Core.Knowledge;

namespace BubblesBot.Tests;

public sealed class LootValueFilterTests : IDisposable
{
    private readonly string _cachePath = Path.Combine(Path.GetTempPath(), $"bubbles-prices-{Guid.NewGuid():N}.json");
    private readonly PriceCatalog _catalog;
    private readonly ValueFilter _filter;

    public LootValueFilterTests()
    {
        var variants = new List<ItemPriceVariant>
        {
            new("Fireball", "SkillGem", 2, GemLevel: 20, GemQuality: 0, Corrupted: false),
            new("Fireball", "SkillGem", 25, Variant: "21c", GemLevel: 21, GemQuality: 0, Corrupted: true),
            new("Fireball", "SkillGem", 8, Variant: "20/20", GemLevel: 20, GemQuality: 20, Corrupted: false),
            new("12% increased Fire Damage", "ClusterJewel", 4, "8 passives", LevelRequired: 50),
            new("12% increased Fire Damage", "ClusterJewel", 30, "8 passives", LevelRequired: 84),
            new("12% increased Fire Damage", "ClusterJewel", 100, "12 passives", LevelRequired: 84),
            new("Voices", "UniqueJewel", 1_000_000, DetailsId: "voices-large-cluster-jewel"),
            new("Voices", "UniqueJewel", 60, "5 passives"),
            new("Voices", "UniqueJewel", 1, "7 passives"),
            new("Megalomaniac", "UniqueJewel", 5),
            new("Split Personality", "UniqueJewel", 1),
            new("Cheap Crown", "UniqueArmour", 1),
            new("Valuable Crown", "UniqueArmour", 15),
        };
        var flat = variants.GroupBy(x => x.Name).ToDictionary(x => x.Key, x => x.Max(y => y.ChaosValue));
        File.WriteAllText(_cachePath, JsonSerializer.Serialize(new
        {
            SchemaVersion = 2,
            RefreshedAt = DateTime.UtcNow,
            Entries = flat,
            Variants = variants,
        }));
        _catalog = new PriceCatalog("test", TimeSpan.FromDays(1), _cachePath);
        _filter = new ValueFilter(_catalog);
    }

    [Fact]
    public void GemQuoteUsesExactLevelAndQuality()
    {
        Assert.Equal(25, _catalog.QuoteGem("Fireball", 21, 0, true).ChaosValue);
        Assert.Equal(2, _catalog.QuoteGem("Fireball", 20, 0, false).ChaosValue);
    }

    [Fact]
    public void ClusterQuoteUsesPassiveCountAndHighestEligibleItemLevelBand()
    {
        Assert.Equal(4, _catalog.QuoteCluster("12% increased Fire Damage", 8, 83).ChaosValue);
        Assert.Equal(30, _catalog.QuoteCluster("12% increased Fire Damage", 8, 84).ChaosValue);
    }

    [Fact]
    public void UnknownVoicesRollIsConservativeButRetainsRange()
    {
        var quote = _catalog.QuoteUnique("Voices");
        Assert.Equal(1, quote.ChaosValue);
        Assert.Equal(1_000_000, quote.MaxChaosValue);
        Assert.Equal(60, _catalog.QuoteUnique("Voices", 5).ChaosValue);
    }

    [Fact]
    public void DefaultSimulacrumUniqueListUsesUnidentifiedArtMapping()
    {
        var settings = new LootSettings { FilterUniques = true, MinUniqueChaosValue = 100 };
        var item = Facts(
            name: "Large Cluster Jewel",
            rarity: EntityListReader.EntityRarity.Unique,
            identified: false,
            resource: "Art/2DItems/Jewels/UniqueJewelBase3.dds");

        var result = _filter.Evaluate(item, settings);

        Assert.True(result.ShouldTake);
        Assert.Equal("must-loot", result.Category);
        Assert.Contains("Voices", result.Reason);
    }

    [Fact]
    public void DefaultUniquePolicySkipsKnownOneChaosEquipmentAtTenChaosFloor()
    {
        var settings = new LootSettings();

        var cheap = _filter.Evaluate(Facts(
            "Cheap Crown", rarity: EntityListReader.EntityRarity.Unique), settings);
        var valuable = _filter.Evaluate(Facts(
            "Valuable Crown", rarity: EntityListReader.EntityRarity.Unique), settings);

        Assert.True(settings.FilterUniques);
        Assert.Equal(10, settings.MinUniqueChaosValue);
        Assert.False(cheap.ShouldTake);
        Assert.Contains("below 10c", cheap.Reason);
        Assert.True(valuable.ShouldTake);
    }

    [Theory]
    [InlineData("Art/2DItems/Jewels/UniqueJewelBase1.dds", "Split Personality")]
    [InlineData("Art/2DItems/Jewels/UniqueJewelBase2.dds", "Megalomaniac")]
    [InlineData("Art/2DItems/Jewels/UniqueJewelBase3.dds", "Voices")]
    [InlineData("Art/2DItems/Armours/Gloves/Hrimsorrow.dds", "Hrimsorrow")]
    public void EmbeddedArtMappingResolvesKnownUnidentifiedUnique(string art, string expected)
    {
        Assert.Contains(expected, UniqueArtMapping.Shared.Resolve(art));
    }

    [Fact]
    public void GemRulesKeepQualityTwentyAndFilterCheapZeroQuality()
    {
        var settings = new LootSettings
        {
            FilterSkillGems = true,
            MinGemChaosValue = 15,
            AlwaysLoot20QualityGems = true,
        };

        Assert.False(_filter.Evaluate(Facts("Fireball", gemLevel: 20, quality: 0), settings).ShouldTake);
        Assert.True(_filter.Evaluate(Facts("Fireball", gemLevel: 20, quality: 20), settings).ShouldTake);
        Assert.True(_filter.Evaluate(Facts("Fireball", gemLevel: 21, quality: 0, corrupted: true), settings).ShouldTake);
    }

    [Fact]
    public void GemMetadataWithUnreadableLevelDoesNotInheritHighestGenericVariant()
    {
        var settings = new LootSettings
        {
            FilterSkillGems = true,
            MinGemChaosValue = 15,
            AlwaysLoot20QualityGems = true,
        };

        var result = _filter.Evaluate(Facts("Fireball",
            path: "Metadata/Items/Gems/SkillGemFireball", quality: 10, gemLevel: 0), settings);

        Assert.True(result.ShouldTake); // fail-open for incomplete ground-item evidence
        Assert.Equal(0, result.ChaosValue);
        Assert.Equal("gem", result.Category);
        Assert.Contains("unpriced gem", result.Reason);
    }

    [Fact]
    public void ClusterFilterUsesNinjaVariantThreshold()
    {
        var settings = new LootSettings { FilterClusterJewels = true, MinClusterJewelChaosValue = 15 };
        var cheap = Facts("Large Cluster Jewel", path: ClusterPath, itemLevel: 83,
            clusterEnchant: "12% increased Fire Damage", clusterPassives: 8);
        var valuable = cheap with { ItemLevel = 84 };

        Assert.False(_filter.Evaluate(cheap, settings).ShouldTake);
        Assert.True(_filter.Evaluate(valuable, settings).ShouldTake);
    }

    [Fact]
    public void ManualPriceOverrideWinsGenericThreshold()
    {
        var settings = new LootSettings
        {
            MinChaosValue = 50,
            PriceOverrides = new List<string> { "Mystery Drop=75" },
        };
        var result = _filter.Evaluate(Facts("Mystery Drop"), settings);
        Assert.True(result.ShouldTake);
        Assert.Equal(75, result.ChaosValue);
        Assert.Equal("override", result.Category);
    }

    public void Dispose()
    {
        try { File.Delete(_cachePath); } catch { }
    }

    private const string ClusterPath = "Metadata/Items/Jewels/JewelPassiveTreeExpansionLarge";

    private static LootItemFacts Facts(
        string name,
        string path = "Metadata/Items/Test",
        EntityListReader.EntityRarity rarity = EntityListReader.EntityRarity.Normal,
        bool identified = true,
        int itemLevel = 85,
        int quality = 0,
        int gemLevel = 0,
        bool corrupted = false,
        string resource = "",
        string clusterEnchant = "",
        int clusterPassives = 0)
        => new(name, name, path, rarity, identified, itemLevel, quality, gemLevel,
            corrupted, 1, resource, clusterEnchant, clusterPassives);
}
