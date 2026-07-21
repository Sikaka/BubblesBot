using BubblesBot.Bot.Modes;

namespace BubblesBot.Bot.Strategies;

/// <summary>
/// One-time seed: converts the legacy flat map-farming settings (captured in
/// <see cref="LegacyFarmSettings"/>) into the two built-in strategies so a user's live-validated
/// values carry over exactly. "General mapping" mirrors the old preset 0, "Cloister Stacked
/// Decks" mirrors preset 1 — including the preset-1 semantics that were previously hardcoded
/// (corpse-ordered ritual chain, Cloister density bonus, remember-all loot backtracking,
/// always deposit).
///
/// <para>Pure and offline-testable. Persisting the seeds and pointing <c>ActiveStrategyId</c> at
/// one is the store's / BotApp's job.</para>
/// </summary>
public static class LegacySettingsMigration
{
    public const string GeneralMappingName = "General mapping";
    public const string CloisterStackedDecksName = "Cloister Stacked Decks";

    public static IReadOnlyList<FarmingStrategy> BuildSeeds(LegacyFarmSettings settings)
        => [GeneralMapping(settings), CloisterStackedDecks(settings)];

    public static FarmingStrategy GeneralMapping(LegacyFarmSettings settings)
    {
        var strategy = BuildShared(settings);
        strategy.Identity.Name = GeneralMappingName;
        strategy.Identity.Description =
            "Runs the configured pre-rolled map with the shared clear loop; mechanics as configured, no scarab recipe.";
        strategy.Loot.DepositAfterEachMap = settings.StackedDeckDepositToStash;
        return strategy;
    }

    public static FarmingStrategy CloisterStackedDecks(LegacyFarmSettings settings)
    {
        var strategy = BuildShared(settings);
        strategy.Identity.Name = CloisterStackedDecksName;
        strategy.Identity.Description =
            "Cloister scarabs + corpse-ordered Ritual chain for stacked decks. Deposits after every map; remembers all accepted loot.";

        strategy.Supply.Scarabs.Add(new ScarabLine
        {
            PathFragment = StackedDeckPolicy.CloisterScarabPathFragment,
            DisplayName = "Divination Scarab of the Cloister",
            CountPerMap = Math.Clamp(settings.StackedDeckCloisterScarabsPerMap, 0, 5),
        });

        // Preset-1 hardcoded semantics, now explicit strategy data.
        var ritual = strategy.Block<RitualBlock>()!;
        ritual.ChainOrdering = RitualChainOrdering.CloisterCorpses;
        ritual.CorpseMonsterPathFragment = StackedDeckPolicy.CloisterMonsterPathFragment;
        ritual.CorpseRadiusGrid = StackedDeckPolicy.RitualCorpseRadiusGrid;
        ritual.DensityWeight = StackedDeckPolicy.CloisterDensityWeight;

        strategy.Loot.BacktrackMinChaosOverride = 0f;   // remember every accepted label
        strategy.Loot.DepositAfterEachMap = true;       // preset 1 always deposited
        return strategy;
    }

    private static FarmingStrategy BuildShared(LegacyFarmSettings settings)
    {
        var now = DateTime.UtcNow;
        return new FarmingStrategy
        {
            Identity = new StrategyIdentity
            {
                Author = "migrated from settings",
                CreatedUtc = now,
                ModifiedUtc = now,
            },
            Supply = new SupplySection
            {
                SuppliesTabName = settings.MapFarmSupplyTabName,
                DumpTabName = settings.MapFarmDumpTabName,
                Map = new MapSupply
                {
                    Source = MapSource.AtlasStorage,
                    TargetMapName = settings.MapFarmTargetMapName,
                },
                CurrencyReserves =
                {
                    new CurrencyReserve { Item = "PortalScroll", MinCount = 1, Policy = ReservePolicy.RetainFullestStack },
                },
            },
            MapPrep = new MapPrepSection { AtlasNodeName = settings.MapFarmTargetMapName },
            Mechanics =
            {
                new ShrinesBlock { Enabled = settings.TakeShrines },
                new EldritchAltarsBlock
                {
                    Enabled = settings.AltarPolicy != 0,
                    Policy = (AltarChoicePolicy)Math.Clamp(settings.AltarPolicy, 0, 3),
                },
                new RitualBlock
                {
                    Enabled = settings.TakeRituals,
                    DeferUntilMapSweep = settings.DeferRitualsUntilMapSweep,
                    Shop = new RitualShopBlock
                    {
                        Enabled = settings.BuyRitualRewards,
                        RerollThresholdChaos = settings.RitualRerollThresholdChaos,
                        FinalBuyMinChaos = settings.RitualFinalBuyMinChaos,
                        MaxRerolls = settings.RitualMaxRerolls,
                    },
                },
                new MemoryTearsBlock { Enabled = settings.TakeMemoryTears },
            },
            Loot = new LootStrategySection(),
            Completion = new CompletionSection
            {
                TargetMaps = settings.StackedDeckTargetMaps,
                ExplorationDonePercent = settings.ExplorationDonePercent,
            },
            Campaign = new CampaignSection(),
            Limits = new LimitsSection(),   // MaxZoneMinutes null → inherit the profile failsafe
        };
    }
}
