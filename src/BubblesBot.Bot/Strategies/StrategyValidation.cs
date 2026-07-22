using BubblesBot.Bot.Modes;

namespace BubblesBot.Bot.Strategies;

/// <summary>Outcome of validating one strategy document. Errors block activation; warnings don't.</summary>
public sealed class StrategyValidationResult
{
    private readonly List<string> _errors = new();
    private readonly List<string> _warnings = new();

    public IReadOnlyList<string> Errors => _errors;
    public IReadOnlyList<string> Warnings => _warnings;
    public bool Ok => _errors.Count == 0;

    public void Error(string message) => _errors.Add(message);
    public void Warn(string message) => _warnings.Add(message);
}

/// <summary>
/// Semantic validation of a parsed strategy against what THIS build can actually execute.
/// The serializer already rejected unknown types/fields; this layer rejects known-but-
/// unsupported capabilities (fail closed: a strategy never silently runs degraded) and
/// nonsensical values. Runs on save (drafts may carry errors), import (errors reject the
/// file), and activation (errors refuse to arm).
/// </summary>
public static class StrategyValidator
{
    /// <summary>
    /// Mechanic ids the runtime can execute today — the single source of truth is
    /// <see cref="Mechanics.MechanicCatalog"/>, which validation gates enabled blocks against.
    /// </summary>
    public static IReadOnlySet<string> SupportedMechanics => Mechanics.MechanicCatalog.Supported;

    public static StrategyValidationResult Validate(FarmingStrategy strategy)
    {
        var result = new StrategyValidationResult();
        ValidateIdentity(strategy.Identity, result);
        ValidateSupply(strategy, result);
        ValidateMapPrep(strategy, result);
        ValidateMechanics(strategy, result);
        ValidateLoot(strategy.Loot, result);
        ValidateCompletion(strategy.Completion, result);
        ValidateCampaign(strategy.Campaign, result);
        ValidateLimits(strategy.Limits, result);
        return result;
    }

    private static void ValidateIdentity(StrategyIdentity identity, StrategyValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(identity.Id)) result.Error("identity.id must be non-empty");
        if (string.IsNullOrWhiteSpace(identity.Name)) result.Error("identity.name must be non-empty");
    }

    private static void ValidateSupply(FarmingStrategy strategy, StrategyValidationResult result)
    {
        var supply = strategy.Supply;
        if (supply.Map.Source is not (MapSource.AtlasStorage or MapSource.PlayerInventory))
            result.Error($"supply.map.source '{supply.Map.Source}' is not supported in this build (use atlasStorage or playerInventory)");
        if (string.IsNullOrWhiteSpace(supply.Map.TargetMapName))
            result.Error("supply.map.targetMapName must be non-empty");
        if (supply.Map.CarriedMapBuffer is < 1 or > 20)
            result.Error("supply.map.carriedMapBuffer must be 1..20");
        if (strategy.Loot.DepositAfterEachMap && string.IsNullOrWhiteSpace(supply.DumpTabName))
            result.Error("supply.dumpTabName must be set when loot.depositAfterEachMap is on");
        if (string.IsNullOrWhiteSpace(supply.SuppliesTabName))
            result.Warn("supply.suppliesTabName is empty; stash withdrawal cannot restock when atlas-side supplies run out");

        var scarabTotal = 0;
        for (var i = 0; i < supply.Scarabs.Count; i++)
        {
            var line = supply.Scarabs[i];
            if (string.IsNullOrWhiteSpace(line.PathFragment))
                result.Error($"supply.scarabs[{i}].pathFragment must be non-empty (metadata identity is the only verifiable key)");
            if (line.CountPerMap is < 0 or > 5)
                result.Error($"supply.scarabs[{i}].countPerMap must be 0..5");
            scarabTotal += Math.Max(0, line.CountPerMap);
        }
        if (scarabTotal > 5)
            result.Error($"scarab recipe totals {scarabTotal} per map; the map device has 5 scarab slots");

        for (var i = 0; i < supply.CurrencyReserves.Count; i++)
        {
            var reserve = supply.CurrencyReserves[i];
            if (string.IsNullOrWhiteSpace(reserve.Item))
                result.Error($"supply.currencyReserves[{i}].item must be non-empty");
            if (reserve.MinCount < 0)
                result.Error($"supply.currencyReserves[{i}].minCount must be >= 0");
        }
        if (strategy.Completion.RequireBossKill
            && BubblesBot.Core.Knowledge.MapBossCatalog.HasSeparateBossArena(
                supply.Map.TargetMapName)
            && !supply.CurrencyReserves.Any(reserve =>
                reserve.Item.Equals("PortalScroll", StringComparison.OrdinalIgnoreCase)
                && reserve.MinCount >= 2))
            result.Warn("separate-arena boss checkpointing needs a PortalScroll reserve of at least 2 (checkpoint + successful exit)");
    }

    private static void ValidateMapPrep(FarmingStrategy strategy, StrategyValidationResult result)
    {
        var prep = strategy.MapPrep;
        if (string.IsNullOrWhiteSpace(prep.AtlasNodeName))
        {
            result.Error("mapPrep.atlasNodeName must be non-empty");
            return;
        }
        if (strategy.Supply.Map.Source == MapSource.AtlasStorage
            && !AtlasNodeCatalog.IsSupported(prep.AtlasNodeName))
            result.Warn($"atlas node '{prep.AtlasNodeName}' is not in this build's catalog; the device flow will fail closed rather than select it");
        if (!prep.AtlasNodeName.Trim().Equals(strategy.Supply.Map.TargetMapName.Trim(), StringComparison.OrdinalIgnoreCase))
            result.Warn($"mapPrep.atlasNodeName '{prep.AtlasNodeName}' differs from supply.map.targetMapName '{strategy.Supply.Map.TargetMapName}'");
        if (prep.Rolling.MaxAttempts is < 1 or > 100)
            result.Error("mapPrep.rolling.maxAttempts must be 1..100");
        if (prep.Rolling.RejectedStatIds.Any(id => id <= 0))
            result.Error("mapPrep.rolling.rejectedStatIds must contain only positive Stats.dat ids");
        if (prep.Rolling.RejectedStatIds.Count != prep.Rolling.RejectedStatIds.Distinct().Count())
            result.Error("mapPrep.rolling.rejectedStatIds must not contain duplicates");
        if (prep.Rolling.Mode == MapRollingMode.Scoured
            && strategy.Supply.Map.Source != MapSource.PlayerInventory)
            result.Error("mapPrep.rolling.mode 'scoured' requires playerInventory so Normal rarity can be positively verified");
        if (prep.Rolling.Mode is MapRollingMode.Rare or MapRollingMode.RareCorrupted)
            result.Error($"mapPrep.rolling.mode '{prep.Rolling.Mode}' is configured but automatic currency rolling is not executable in this build");
        if (strategy.Completion.RequireBossKill
            && !BubblesBot.Core.Knowledge.MapBossCatalog.HasEntry(strategy.Supply.Map.TargetMapName))
            result.Error($"completion.requireBossKill has no boss roster for map '{strategy.Supply.Map.TargetMapName}'");
    }

    private static void ValidateMechanics(FarmingStrategy strategy, StrategyValidationResult result)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in strategy.Mechanics)
        {
            if (!seen.Add(block.MechanicId))
                result.Error($"duplicate mechanic block '{block.MechanicId}'");
            if (block.Enabled && !SupportedMechanics.Contains(block.MechanicId))
                result.Error($"mechanic '{block.MechanicId}' has no adapter in this build; disable it or upgrade");
            if (block.SweepBias is < -100 or > 100)
                result.Error($"mechanic '{block.MechanicId}' sweepBias must be -100..100");

            switch (block)
            {
                case EldritchAltarsBlock altars: ValidateAltars(altars, result); break;
                case RitualBlock ritual: ValidateRitual(ritual, result); break;
                case DeliriumBlock delirium: ValidateDelirium(delirium, result); break;
            }
        }
    }

    private static void ValidateDelirium(DeliriumBlock delirium, StrategyValidationResult result)
    {
        if (delirium.InitialFogLeadGrid is < 10 or > 250)
            result.Error("delirium.initialFogLeadGrid must be 10..250");
        if (delirium.MaxForwardGridPerSecond is < 1 or > 100)
            result.Error("delirium.maxForwardGridPerSecond must be 1..100");
        if (delirium.MaximumPackDwellSeconds is < 1 or > 15)
            result.Error("delirium.maximumPackDwellSeconds must be 1..15");
        if (delirium.EndButtonTimeoutSeconds is < 5 or > 60)
            result.Error("delirium.endButtonTimeoutSeconds must be 5..60");
        if (delirium.MinimumRewardWaitSeconds is < 5 or > 120)
            result.Error("delirium.minimumRewardWaitSeconds must be 5..120");
        if (delirium.RewardQuietSeconds is < 1 or > 15)
            result.Error("delirium.rewardQuietSeconds must be 1..15");
        if (delirium.MaximumRewardWaitSeconds is < 30 or > 180
            || delirium.MaximumRewardWaitSeconds < delirium.MinimumRewardWaitSeconds)
            result.Error("delirium.maximumRewardWaitSeconds must be 30..180 and >= minimumRewardWaitSeconds");
    }

    private static void ValidateAltars(EldritchAltarsBlock altars, StrategyValidationResult result)
    {
        if (altars.Enabled && altars.Policy == AltarChoicePolicy.Skip)
            result.Warn("eldritchAltars is enabled with policy 'skip'; no altar will ever be taken");
        if (altars.DeferChoicesUntilBossDead)
            result.Error("eldritchAltars.deferChoicesUntilBossDead is not wired into altar scheduling yet");
        foreach (var (key, weight) in altars.WeightOverrides)
        {
            if (string.IsNullOrEmpty(key) || key != EldritchAltarScoring.Normalize(key))
                result.Error($"weightOverrides key '{key}' must be a normalized mod key (letters only, tags stripped)");
            if (weight is < -1000 or > 1000)
                result.Error($"weightOverrides['{key}'] must be -1000..1000");
        }
    }

    private static void ValidateRitual(RitualBlock ritual, StrategyValidationResult result)
    {
        if (ritual.CorpseRadiusGrid is < 5 or > 120)
            result.Error("ritual.corpseRadiusGrid must be 5..120");
        if (ritual.DensityWeight is < 0 or > 100)
            result.Error("ritual.densityWeight must be 0..100");
        if (ritual.ChainOrdering == RitualChainOrdering.CloisterCorpses
            && string.IsNullOrWhiteSpace(ritual.CorpseMonsterPathFragment))
            result.Error("ritual.chainOrdering 'cloisterCorpses' requires corpseMonsterPathFragment");
        var shop = ritual.Shop;
        if (shop.RerollThresholdChaos is < 0 or > 1000)
            result.Error("ritual.shop.rerollThresholdChaos must be 0..1000");
        if (shop.FinalBuyMinChaos is < 0 or > 1000)
            result.Error("ritual.shop.finalBuyMinChaos must be 0..1000");
        if (shop.MaxRerolls is < 0 or > 20)
            result.Error("ritual.shop.maxRerolls must be 0..20");
    }

    private static void ValidateLoot(LootStrategySection loot, StrategyValidationResult result)
    {
        if (loot.BacktrackMinChaosOverride is < 0 or > 1000)
            result.Error("loot.backtrackMinChaosOverride must be 0..1000 (or omitted to inherit the profile value)");
    }

    private static void ValidateCompletion(CompletionSection completion, StrategyValidationResult result)
    {
        if (completion.TargetMaps is < 1 or > 500)
            result.Error("completion.targetMaps must be 1..500");
        if (completion.ExplorationDonePercent is < 50 or > 100)
            result.Error("completion.explorationDonePercent must be 50..100");
    }

    private static void ValidateCampaign(CampaignSection campaign, StrategyValidationResult result)
    {
        if (campaign.Mode != CampaignMode.None)
            result.Error($"campaign.mode '{campaign.Mode}' is not executable in this build");
    }

    private static void ValidateLimits(LimitsSection limits, StrategyValidationResult result)
    {
        if (limits.MaxMapMinutes is < 0 or > 60)
            result.Error("limits.maxMapMinutes must be 0..60");
        if (limits.MaxZoneMinutes is < 0 or > 60)
            result.Error("limits.maxZoneMinutes must be 0..60 (or omitted to inherit the profile value)");
        if (limits.MaxMechanicStallsPerMap is < 0 or > 20)
            result.Error("limits.maxMechanicStallsPerMap must be 0..20");
    }
}
