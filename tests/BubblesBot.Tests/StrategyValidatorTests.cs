using BubblesBot.Bot.Strategies;

namespace BubblesBot.Tests;

public sealed class StrategyValidatorTests
{
    private static FarmingStrategy Valid() => LegacySettingsMigration.CloisterStackedDecks(new LegacyFarmSettings());

    [Fact]
    public void SeededStrategyValidates()
    {
        var result = StrategyValidator.Validate(Valid());
        Assert.True(result.Ok, string.Join("; ", result.Errors));
    }

    [Fact]
    public void ScarabRecipeOverFiveSlotsIsRejected()
    {
        var strategy = Valid();
        strategy.Supply.Scarabs.Add(new ScarabLine { PathFragment = "ScarabSomethingElse", CountPerMap = 3 });
        var result = StrategyValidator.Validate(strategy);
        Assert.Contains(result.Errors, error => error.Contains("5 scarab slots"));
    }

    [Fact]
    public void EmptyScarabPathFragmentIsRejected()
    {
        var strategy = Valid();
        strategy.Supply.Scarabs[0].PathFragment = " ";
        Assert.False(StrategyValidator.Validate(strategy).Ok);
    }

    [Fact]
    public void EnabledMechanicWithoutAdapterIsRejected()
    {
        var strategy = Valid();
        strategy.Mechanics.Add(new StrongboxesBlock { Enabled = true });
        var result = StrategyValidator.Validate(strategy);
        Assert.Contains(result.Errors, error => error.Contains("strongboxes") && error.Contains("no adapter"));
    }

    [Fact]
    public void DisabledMechanicWithoutAdapterIsAllowed()
    {
        var strategy = Valid();
        strategy.Mechanics.Add(new StrongboxesBlock { Enabled = false });
        Assert.True(StrategyValidator.Validate(strategy).Ok);
    }

    [Fact]
    public void CampaignModeIsRejectedUntilExecutable()
    {
        var strategy = Valid();
        strategy.Campaign.Mode = CampaignMode.GuardianRotation;
        Assert.False(StrategyValidator.Validate(strategy).Ok);
    }

    [Fact]
    public void RequireBossKillIsAcceptedForCataloguedMap()
    {
        var strategy = Valid();
        strategy.Supply.Map.TargetMapName = "Strand";
        strategy.MapPrep.AtlasNodeName = "Strand";
        strategy.Completion.RequireBossKill = true;
        Assert.True(StrategyValidator.Validate(strategy).Ok);
    }

    [Fact]
    public void SeparateArenaWarnsWhenPortalReserveCannotCoverCheckpointAndExit()
    {
        var strategy = Valid();
        strategy.Supply.Map.TargetMapName = "Strand";
        strategy.MapPrep.AtlasNodeName = "Strand";
        strategy.Completion.RequireBossKill = true;
        strategy.Supply.CurrencyReserves.Single(reserve =>
            reserve.Item.Equals("PortalScroll", StringComparison.OrdinalIgnoreCase)).MinCount = 1;

        var result = StrategyValidator.Validate(strategy);
        Assert.True(result.Ok);
        Assert.Contains(result.Warnings, warning => warning.Contains("at least 2"));
    }

    [Fact]
    public void RequireBossKillIsRejectedWithoutCataloguedRoster()
    {
        var strategy = Valid();
        strategy.Supply.Map.TargetMapName = "Dunes";
        strategy.MapPrep.AtlasNodeName = "Dunes";
        strategy.Completion.RequireBossKill = true;
        Assert.False(StrategyValidator.Validate(strategy).Ok);
    }

    [Fact]
    public void DeferAltarChoicesUntilBossDeadIsRejectedUntilEvidenceShips()
    {
        var strategy = Valid();
        strategy.Block<EldritchAltarsBlock>()!.DeferChoicesUntilBossDead = true;
        Assert.False(StrategyValidator.Validate(strategy).Ok);
    }

    [Fact]
    public void WeightOverrideKeysMustBeNormalized()
    {
        var strategy = Valid();
        var altars = strategy.Block<EldritchAltarsBlock>()!;
        altars.WeightOverrides["#% increased Quantity"] = 50;   // raw mod text, not a normalized key
        var result = StrategyValidator.Validate(strategy);
        Assert.Contains(result.Errors, error => error.Contains("normalized"));
    }

    [Fact]
    public void NormalizedWeightOverrideKeyIsAccepted()
    {
        var strategy = Valid();
        var altars = strategy.Block<EldritchAltarsBlock>()!;
        altars.WeightOverrides["IncreasedQuantityofItemsfoundinthisArea"] = 120;
        Assert.True(StrategyValidator.Validate(strategy).Ok);
    }

    [Fact]
    public void UnknownAtlasNodeWarnsButValidates()
    {
        var strategy = Valid();
        strategy.MapPrep.AtlasNodeName = "Dunes";
        strategy.Supply.Map.TargetMapName = "Dunes";
        var result = StrategyValidator.Validate(strategy);
        Assert.True(result.Ok);
        Assert.Contains(result.Warnings, warning => warning.Contains("Dunes"));
    }

    [Fact]
    public void DeliriumRejectsRewardMaximumBelowMinimum()
    {
        var strategy = Valid();
        strategy.Mechanics.Add(new DeliriumBlock
        {
            MinimumRewardWaitSeconds = 90,
            MaximumRewardWaitSeconds = 60,
        });
        Assert.False(StrategyValidator.Validate(strategy).Ok);
    }

    [Fact]
    public void DuplicateMechanicBlocksAreRejected()
    {
        var strategy = Valid();
        strategy.Mechanics.Add(new ShrinesBlock());
        var result = StrategyValidator.Validate(strategy);
        Assert.Contains(result.Errors, error => error.Contains("duplicate"));
    }

    [Fact]
    public void CorpseOrderingRequiresMonsterFragment()
    {
        var strategy = Valid();
        strategy.Block<RitualBlock>()!.CorpseMonsterPathFragment = "";
        Assert.False(StrategyValidator.Validate(strategy).Ok);
    }

    [Fact]
    public void DepositRequiresDumpTab()
    {
        var strategy = Valid();
        strategy.Supply.DumpTabName = "";
        Assert.False(StrategyValidator.Validate(strategy).Ok);
    }

    [Fact]
    public void StashTabMapSourceIsRejectedUntilSupported()
    {
        var strategy = Valid();
        strategy.Supply.Map.Source = MapSource.StashTab;
        Assert.False(StrategyValidator.Validate(strategy).Ok);
    }

    [Fact]
    public void Scoured_policy_is_executable_for_verified_inventory_maps()
    {
        var strategy = Valid();
        strategy.Supply.Map.Source = MapSource.PlayerInventory;
        strategy.MapPrep.Rolling.Mode = MapRollingMode.Scoured;
        Assert.True(StrategyValidator.Validate(strategy).Ok);
    }

    [Theory]
    [InlineData(MapRollingMode.Rare)]
    [InlineData(MapRollingMode.RareCorrupted)]
    public void Currency_rolling_modes_fail_closed_until_executor_is_live_proven(MapRollingMode mode)
    {
        var strategy = Valid();
        strategy.Supply.Map.Source = MapSource.PlayerInventory;
        strategy.MapPrep.Rolling.Mode = mode;
        strategy.MapPrep.Rolling.RejectedStatIds.Add(1234);
        var result = StrategyValidator.Validate(strategy);
        Assert.Contains(result.Errors, error => error.Contains("not executable"));
    }

    [Fact]
    public void EmptyNameIsRejected()
    {
        var strategy = Valid();
        strategy.Identity.Name = "";
        Assert.False(StrategyValidator.Validate(strategy).Ok);
    }
}
