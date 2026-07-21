using BubblesBot.Bot.Strategies;

namespace BubblesBot.Tests;

/// <summary>
/// The seed migration must carry the user's live-validated settings into the two built-in
/// strategies property-by-property — runtime adoption (M5) relies on "seeded strategy ==
/// intended-identical behavior".
/// </summary>
public sealed class LegacySettingsMigrationTests
{
    private static LegacyFarmSettings CustomizedSettings() => new()
    {
        MapFarmSupplyTabName = "MyMaps",
        MapFarmDumpTabName = "MyDump",
        MapFarmTargetMapName = "City Square",
        StackedDeckCloisterScarabsPerMap = 4,
        StackedDeckTargetMaps = 33,
        StackedDeckDepositToStash = false,
        TakeShrines = false,
        TakeRituals = true,
        DeferRitualsUntilMapSweep = false,
        BuyRitualRewards = false,
        RitualRerollThresholdChaos = 22f,
        RitualFinalBuyMinChaos = 7f,
        RitualMaxRerolls = 4,
        AltarPolicy = 3,
        TakeMemoryTears = false,
        ExplorationDonePercent = 70,
    };

    [Fact]
    public void BothSeedsValidate()
    {
        foreach (var seed in LegacySettingsMigration.BuildSeeds(CustomizedSettings()))
        {
            var result = StrategyValidator.Validate(seed);
            Assert.True(result.Ok, $"{seed.Identity.Name}: {string.Join("; ", result.Errors)}");
        }
    }

    [Fact]
    public void SharedSettingsCarryOverExactly()
    {
        var settings = CustomizedSettings();
        foreach (var seed in LegacySettingsMigration.BuildSeeds(settings))
        {
            Assert.Equal("MyMaps", seed.Supply.SuppliesTabName);
            Assert.Equal("MyDump", seed.Supply.DumpTabName);
            Assert.Equal("City Square", seed.Supply.Map.TargetMapName);
            Assert.Equal("City Square", seed.MapPrep.AtlasNodeName);
            Assert.Equal(33, seed.Completion.TargetMaps);
            Assert.Equal(70, seed.Completion.ExplorationDonePercent);
            Assert.False(seed.Block<MemoryTearsBlock>()!.Enabled);

            Assert.False(seed.Block<ShrinesBlock>()!.Enabled);

            var altars = seed.Block<EldritchAltarsBlock>()!;
            Assert.True(altars.Enabled);
            Assert.Equal(AltarChoicePolicy.Smart, altars.Policy);

            var ritual = seed.Block<RitualBlock>()!;
            Assert.True(ritual.Enabled);
            Assert.False(ritual.DeferUntilMapSweep);
            Assert.False(ritual.Shop.Enabled);
            Assert.Equal(22f, ritual.Shop.RerollThresholdChaos);
            Assert.Equal(7f, ritual.Shop.FinalBuyMinChaos);
            Assert.Equal(4, ritual.Shop.MaxRerolls);
        }
    }

    [Fact]
    public void GeneralMappingMirrorsPresetZero()
    {
        var seed = LegacySettingsMigration.GeneralMapping(CustomizedSettings());

        Assert.Empty(seed.Supply.Scarabs);
        Assert.False(seed.Loot.DepositAfterEachMap);            // follows StackedDeckDepositToStash
        Assert.Null(seed.Loot.BacktrackMinChaosOverride);       // inherit profile threshold
        Assert.Equal(RitualChainOrdering.NearestFirst, seed.Block<RitualBlock>()!.ChainOrdering);
        Assert.Equal(0d, seed.Block<RitualBlock>()!.DensityWeight);
    }

    [Fact]
    public void CloisterSeedMirrorsPresetOneHardcodedSemantics()
    {
        var seed = LegacySettingsMigration.CloisterStackedDecks(CustomizedSettings());

        var scarab = Assert.Single(seed.Supply.Scarabs);
        Assert.Equal("ScarabDivinationCardsNew1", scarab.PathFragment);
        Assert.Equal(4, scarab.CountPerMap);

        var ritual = seed.Block<RitualBlock>()!;
        Assert.Equal(RitualChainOrdering.CloisterCorpses, ritual.ChainOrdering);
        Assert.Equal("DemonFemale", ritual.CorpseMonsterPathFragment);
        Assert.Equal(45f, ritual.CorpseRadiusGrid);
        Assert.Equal(12d, ritual.DensityWeight);

        Assert.True(seed.Loot.DepositAfterEachMap);             // preset 1 always deposited
        Assert.Equal(0f, seed.Loot.BacktrackMinChaosOverride);  // preset 1 remembered everything
    }

    [Fact]
    public void AltarPolicySkipDisablesTheBlock()
    {
        var settings = CustomizedSettings();
        settings.AltarPolicy = 0;
        var seed = LegacySettingsMigration.GeneralMapping(settings);
        var altars = seed.Block<EldritchAltarsBlock>()!;
        Assert.False(altars.Enabled);
        Assert.Equal(AltarChoicePolicy.Skip, altars.Policy);
    }

    [Fact]
    public void LoadFromCapturesRawConfigValues()
    {
        var path = Path.Combine(Path.GetTempPath(), "BubblesBot.Tests", Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // camelCase keys, plus unrelated members that must be ignored.
        File.WriteAllText(path, """
        { "mapFarmTargetMapName": "Dunes", "stackedDeckCloisterScarabsPerMap": 3,
          "altarPolicy": 2, "takeMemoryTears": false, "botActive": true, "activeMode": 4 }
        """);
        try
        {
            var legacy = LegacyFarmSettings.LoadFrom(path);
            Assert.Equal("Dunes", legacy.MapFarmTargetMapName);
            Assert.Equal(3, legacy.StackedDeckCloisterScarabsPerMap);
            Assert.Equal(2, legacy.AltarPolicy);
            Assert.False(legacy.TakeMemoryTears);
            Assert.Equal("Supplies", legacy.MapFarmSupplyTabName);   // absent key → default
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadFromMissingFileReturnsDefaults()
    {
        var legacy = LegacyFarmSettings.LoadFrom(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".json"));
        Assert.Equal("City Square", legacy.MapFarmTargetMapName);
        Assert.Equal(5, legacy.StackedDeckCloisterScarabsPerMap);
        Assert.True(legacy.TakeShrines);
    }
}
