using BubblesBot.Bot.Strategies;

namespace BubblesBot.Tests;

public sealed class FarmingStrategySerializationTests
{
    private static FarmingStrategy FullStrategy()
    {
        var strategy = LegacySettingsMigration.CloisterStackedDecks(new LegacyFarmSettings());
        var altars = strategy.Block<EldritchAltarsBlock>()!;
        altars.Enabled = true;
        altars.Policy = AltarChoicePolicy.Smart;
        altars.WeightOverrides["IncreasedQuantityofItemsfoundinthisArea"] = 120;
        strategy.Mechanics.Add(new StrongboxesBlock { Enabled = false, SweepBias = 10f });
        strategy.Mechanics.Add(new DeliriumBlock
        {
            Enabled = true,
            InitialFogLeadGrid = 75,
            MaxForwardGridPerSecond = 14,
            MaximumPackDwellSeconds = 4.5,
            MinimumRewardWaitSeconds = 45,
        });
        strategy.Limits.MaxZoneMinutes = 12;
        strategy.Limits.MaxMapMinutes = 6;
        strategy.Loot.BacktrackMinChaosOverride = 3.5f;
        return strategy;
    }

    [Fact]
    public void RoundTripsEverySection()
    {
        var expected = FullStrategy();
        var actual = StrategySerialization.Parse(StrategySerialization.Serialize(expected));

        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
        Assert.Equal(expected.Identity.Id, actual.Identity.Id);
        Assert.Equal(expected.Identity.Name, actual.Identity.Name);
        Assert.Equal(expected.Supply.SuppliesTabName, actual.Supply.SuppliesTabName);
        Assert.Equal(expected.Supply.Map.TargetMapName, actual.Supply.Map.TargetMapName);
        Assert.Equal(expected.Supply.Scarabs.Count, actual.Supply.Scarabs.Count);
        Assert.Equal(expected.Supply.Scarabs[0].PathFragment, actual.Supply.Scarabs[0].PathFragment);
        Assert.Equal(expected.Supply.Scarabs[0].CountPerMap, actual.Supply.Scarabs[0].CountPerMap);
        Assert.Equal(expected.MapPrep.AtlasNodeName, actual.MapPrep.AtlasNodeName);
        Assert.Equal(expected.Mechanics.Count, actual.Mechanics.Count);

        var ritual = actual.Block<RitualBlock>()!;
        Assert.Equal(RitualChainOrdering.CloisterCorpses, ritual.ChainOrdering);
        Assert.Equal("DemonFemale", ritual.CorpseMonsterPathFragment);
        Assert.Equal(45f, ritual.CorpseRadiusGrid);
        Assert.Equal(12d, ritual.DensityWeight);
        Assert.Equal(15f, ritual.Shop.RerollThresholdChaos);

        var altars = actual.Block<EldritchAltarsBlock>()!;
        Assert.Equal(AltarChoicePolicy.Smart, altars.Policy);
        Assert.Equal(120, altars.WeightOverrides["IncreasedQuantityofItemsfoundinthisArea"]);

        Assert.Equal(3.5f, actual.Loot.BacktrackMinChaosOverride);
        Assert.True(actual.Loot.DepositAfterEachMap);
        Assert.Equal(12, actual.Limits.MaxZoneMinutes);
        Assert.Equal(6, actual.Limits.MaxMapMinutes);
        Assert.Equal(expected.Completion.TargetMaps, actual.Completion.TargetMaps);
        Assert.Equal(CampaignMode.None, actual.Campaign.Mode);
        Assert.False(actual.Block<StrongboxesBlock>()!.Enabled);
        Assert.Equal(10f, actual.Block<StrongboxesBlock>()!.SweepBias);
        Assert.Equal(75f, actual.Block<DeliriumBlock>()!.InitialFogLeadGrid);
        Assert.Equal(14f, actual.Block<DeliriumBlock>()!.MaxForwardGridPerSecond);
        Assert.Equal(4.5, actual.Block<DeliriumBlock>()!.MaximumPackDwellSeconds);
        Assert.Equal(45, actual.Block<DeliriumBlock>()!.MinimumRewardWaitSeconds);
    }

    [Fact]
    public void EnumsSerializeAsCamelCaseStrings()
    {
        var json = StrategySerialization.Serialize(FullStrategy());
        Assert.Contains("\"cloisterCorpses\"", json);
        Assert.Contains("\"atlasStorage\"", json);
        Assert.Contains("\"smart\"", json);
        Assert.Contains("\"type\": \"ritual\"", json);
    }

    [Fact]
    public void UnknownMechanicTypeFailsClosed()
    {
        var json = StrategySerialization.Serialize(FullStrategy())
            .Replace("\"type\": \"shrines\"", "\"type\": \"harvest\"");
        Assert.Throws<StrategyFormatException>(() => StrategySerialization.Parse(json));
    }

    [Fact]
    public void UnknownRootFieldFailsClosed()
    {
        var json = StrategySerialization.Serialize(FullStrategy())
            .Replace("\"identity\"", "\"bonusJuice\": 1,\n  \"identity\"");
        var ex = Assert.Throws<StrategyFormatException>(() => StrategySerialization.Parse(json));
        Assert.Contains("bonusJuice", ex.Message);
    }

    [Fact]
    public void UnknownBlockFieldFailsClosed()
    {
        var json = StrategySerialization.Serialize(FullStrategy())
            .Replace("\"deferUntilMapSweep\"", "\"autoWin\": true,\n      \"deferUntilMapSweep\"");
        Assert.Throws<StrategyFormatException>(() => StrategySerialization.Parse(json));
    }

    [Fact]
    public void UnknownEnumValueFailsClosed()
    {
        var json = StrategySerialization.Serialize(FullStrategy())
            .Replace("\"cloisterCorpses\"", "\"quantumOrdering\"");
        Assert.Throws<StrategyFormatException>(() => StrategySerialization.Parse(json));
    }

    [Fact]
    public void MissingSchemaVersionIsRejected()
    {
        Assert.Throws<StrategyFormatException>(() => StrategySerialization.Parse("{ \"identity\": {} }"));
    }

    [Fact]
    public void NewerSchemaVersionIsRejectedWithoutPartialRead()
    {
        var json = StrategySerialization.Serialize(FullStrategy())
            .Replace($"\"schemaVersion\": {StrategyMigrations.CurrentSchemaVersion}", "\"schemaVersion\": 999");
        var ex = Assert.Throws<StrategyFormatException>(() => StrategySerialization.Parse(json));
        Assert.Contains("newer", ex.Message);
    }

    [Fact]
    public void PreHistorySchemaVersionIsRejected()
    {
        var json = StrategySerialization.Serialize(FullStrategy())
            .Replace($"\"schemaVersion\": {StrategyMigrations.CurrentSchemaVersion}", "\"schemaVersion\": 0");
        Assert.Throws<StrategyFormatException>(() => StrategySerialization.Parse(json));
    }

    [Fact]
    public void DiscriminatorMayAppearAfterOtherBlockProperties()
    {
        var json = """
        {
          "schemaVersion": 1,
          "identity": { "id": "abc123", "name": "Out of order" },
          "mechanics": [ { "enabled": true, "type": "shrines" } ]
        }
        """;
        var strategy = StrategySerialization.Parse(json);
        Assert.True(strategy.IsEnabled<ShrinesBlock>());
    }

    [Fact]
    public void OmittedSectionsGetDefaults()
    {
        var strategy = StrategySerialization.Parse("""
        {
          "schemaVersion": 1,
          "identity": { "id": "abc123", "name": "Minimal" }
        }
        """);
        Assert.Equal(MapSource.AtlasStorage, strategy.Supply.Map.Source);
        Assert.Null(strategy.Loot.BacktrackMinChaosOverride);
        Assert.Null(strategy.Limits.MaxZoneMinutes);
        Assert.Equal(0, strategy.Limits.MaxMapMinutes);
        Assert.Empty(strategy.Mechanics);
        Assert.Equal(CampaignMode.None, strategy.Campaign.Mode);
    }
}
