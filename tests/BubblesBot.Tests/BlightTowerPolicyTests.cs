using BubblesBot.Bot.Modes;
using BubblesBot.Core.Game;

namespace BubblesBot.Tests;

public sealed class BlightTowerPolicyTests
{
    [Fact]
    public void Foundation_navigation_is_capped_by_pump_defense_radius()
    {
        Assert.Equal(65, BlightTowerController.EffectiveBuildRadius(100, 40, 25));
        Assert.Equal(55, BlightTowerController.EffectiveBuildRadius(55, 40, 25));
    }

    [Fact]
    public void Unguarded_intersection_outranks_single_guarded_path()
    {
        var intersection = BlightTowerController.CoveragePriority(
            Pos(12, 0), Pos(0, 0),
            [Pos(30, 0), Pos(28, 8), Pos(0, 30), Pos(8, 28)], []);
        var guardedSingle = BlightTowerController.CoveragePriority(
            Pos(30, 0), Pos(0, 0),
            [Pos(30, 0), Pos(40, 0)], [Pos(35, 0)]);

        Assert.True(intersection.UnguardedSectors >= 2);
        Assert.Equal(0, guardedSingle.UnguardedSectors);
        Assert.True(intersection.Score > guardedSingle.Score);
    }

    [Fact]
    public void Prefers_affordable_crowd_control_over_damage()
    {
        var choice = BlightTowerPolicy.Choose(Pos(0, 0), 40, 100,
        [
            new(1, Pos(25, 0), false, [new(BlightTowerKind.Damage, 50)]),
            new(2, Pos(25, 5), false, [new(BlightTowerKind.Chilling, 80)]),
        ], [Pos(60, 0)]);

        Assert.Equal((uint)2, choice?.EntityId);
        Assert.Equal(BlightTowerKind.Chilling, choice?.Option.Kind);
    }

    [Fact]
    public void Skips_occupied_unaffordable_and_remote_pads()
    {
        var choice = BlightTowerPolicy.Choose(Pos(0, 0), 30, 40,
        [
            new(1, Pos(20, 0), true, [new(BlightTowerKind.Chilling, 20)]),
            new(2, Pos(20, 0), false, [new(BlightTowerKind.Seismic, 50)]),
            new(3, Pos(100, 0), false, [new(BlightTowerKind.Chilling, 20)]),
        ], []);

        Assert.Null(choice);
    }

    [Fact]
    public void Lane_pressure_breaks_equal_control_tower_ties()
    {
        var choice = BlightTowerPolicy.Choose(Pos(0, 0), 40, 100,
        [
            new(1, Pos(-25, 0), false, [new(BlightTowerKind.Seismic, 50)]),
            new(2, Pos(25, 0), false, [new(BlightTowerKind.Seismic, 50)]),
        ], [Pos(60, 0)]);

        Assert.Equal((uint)2, choice?.EntityId);
    }

    [Theory]
    [InlineData("Metadata/Monsters/LeagueBlight/BlightTowerChillingRank3@85", BlightTowerKind.Chilling, 3)]
    [InlineData("Metadata/Monsters/LeagueBlight/BlightTowerStunningRank3_@85", BlightTowerKind.Seismic, 3)]
    [InlineData("Metadata/Monsters/LeagueBlight/BlightTowerBuffRank1@85", BlightTowerKind.Empowering, 1)]
    [InlineData("Metadata/Monsters/LeagueBlight/BlightTowerFlameRank3@85", BlightTowerKind.Meteor, 3)]
    [InlineData("Metadata/Monsters/LeagueBlight/BlightTowerMeteor@85", BlightTowerKind.Meteor, 4)]
    [InlineData("Metadata/Monsters/LeagueBlight/BlightTowerFlamethrower@85", BlightTowerKind.Damage, 4)]
    public void Live_tower_paths_classify_kind_and_tier(string path, BlightTowerKind kind, int tier)
    {
        Assert.Equal(kind, BlightTowerController.ClassifyPath(path));
        Assert.Equal(tier, BlightTowerController.ParseTier(path));
    }

    [Theory]
    [InlineData(BlightTowerKind.Chilling, 0)]
    [InlineData(BlightTowerKind.Empowering, 2)]
    [InlineData(BlightTowerKind.Seismic, 3)]
    [InlineData(BlightTowerKind.Meteor, 5)]
    public void Build_menu_indices_match_live_radial_order(BlightTowerKind kind, int expected)
        => Assert.Equal(expected, BlightTowerController.BuildMenuIndex(kind));

    [Theory]
    [InlineData(BlightTowerKind.Chilling, 3)]
    [InlineData(BlightTowerKind.Seismic, 3)]
    [InlineData(BlightTowerKind.Empowering, 3)]
    [InlineData(BlightTowerKind.Meteor, 4)]
    public void Target_tiers_match_defensive_build_plan(BlightTowerKind kind, int expected)
        => Assert.Equal(expected, BlightTowerController.TargetTier(kind));

    [Theory]
    [InlineData(BlightTowerKind.Meteor, 3, 2, 1)]
    [InlineData(BlightTowerKind.Meteor, 3, 1, -1)]
    [InlineData(BlightTowerKind.Chilling, 2, 1, 0)]
    public void Upgrade_menu_indices_fail_closed_for_unverified_meteor_branch(
        BlightTowerKind kind, int tier, int buttonCount, int expected)
        => Assert.Equal(expected, BlightTowerController.UpgradeMenuIndex(kind, tier, buttonCount));

    [Theory]
    [InlineData(0, 150)]
    [InlineData(1, 300)]
    [InlineData(2, 450)]
    [InlineData(3, 500)]
    public void Tower_step_costs_match_live_contract(int currentTier, int expected)
        => Assert.Equal(expected, BlightTowerController.NextStepCost(currentTier));

    [Theory]
    [InlineData(BlightTowerKind.Chilling, 0, 150, 900, true)]
    [InlineData(BlightTowerKind.Chilling, 0, 149, 900, false)]
    [InlineData(BlightTowerKind.Meteor, 0, 1050, 900, true)]
    [InlineData(BlightTowerKind.Meteor, 0, 1049, 900, false)]
    [InlineData(BlightTowerKind.Meteor, 3, 1400, 900, true)]
    public void Meteor_spending_preserves_real_control_currency_reserve(
        BlightTowerKind kind, int tier, int currency, int reserve, bool expected)
        => Assert.Equal(expected,
            BlightTowerController.CanAffordNextStep(kind, tier, currency, reserve));

    [Theory]
    [InlineData(BlightTowerKind.Chilling, true)]
    [InlineData(BlightTowerKind.Seismic, true)]
    [InlineData(BlightTowerKind.Empowering, true)]
    [InlineData(BlightTowerKind.Meteor, false)]
    public void Unknown_currency_allows_verified_controls_but_blocks_meteor(
        BlightTowerKind kind, bool expected)
        => Assert.Equal(expected,
            BlightTowerController.CanAffordNextStep(kind, 0, null, 900));

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(100, 0, false)]
    [InlineData(11, 12, false)]
    [InlineData(12, 12, true)]
    [InlineData(13, 12, true)]
    public void Zero_tower_limit_is_unlimited_and_positive_values_are_optional_caps(
        int towerCount, int configuredLimit, bool expected)
        => Assert.Equal(expected,
            BlightTowerController.HasReachedTowerLimit(towerCount, configuredLimit));

    private static Vector2i Pos(int x, int y) => new() { X = x, Y = y };
}
