using BubblesBot.Bot.Modes;

namespace BubblesBot.Tests;

public sealed class BlightSweepPolicyTests
{
    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(false, false, false, false)]
    public void Terminal_result_bypasses_cleanup_sweep(
        bool resolved, bool timedOut, bool quietHandoff, bool expected)
    {
        Assert.Equal(expected, BlightMode.ShouldEnterPostEncounterSweep(
            resolved, timedOut, quietHandoff));
    }

    [Fact]
    public void Terminal_latch_survives_later_unknown_reads()
    {
        var latched = BlightMode.NextTerminalLatch(
            currentlyLatched: false,
            successKnown: true, successValue: 1,
            failKnown: true, failValue: 0);

        latched = BlightMode.NextTerminalLatch(
            currentlyLatched: latched,
            successKnown: false, successValue: 0,
            failKnown: false, failValue: 0);

        Assert.True(latched);
    }

    [Theory]
    [InlineData(false, false, 10, true)]
    [InlineData(true, true, 10, true)]
    [InlineData(false, true, 2, true)]
    [InlineData(false, true, 2.01, false)]
    public void Exploration_or_combat_blocks_quiet_cleanup_exit(
        bool hasHostile,
        bool explorationExhausted,
        double secondsSinceLastEnemy,
        bool expected)
    {
        Assert.Equal(expected, BlightMode.ShouldContinueSweepAfterQuietWindow(
            hasHostile, explorationExhausted, secondsSinceLastEnemy));
    }

    [Theory]
    [InlineData(true, false, false, false, true)]
    [InlineData(true, true, true, true, false)]
    [InlineData(false, false, false, false, false)]
    [InlineData(false, false, false, true, true)]
    public void Active_cleanup_survives_a_moving_monster_resetting_the_quiet_window(
        bool cleanupStarted,
        bool resolved,
        bool timedOut,
        bool timerQuietHandoff,
        bool expected)
    {
        Assert.Equal(expected, BlightMode.ShouldKeepPostEncounterCleanup(
            cleanupStarted, resolved, timedOut, timerQuietHandoff));
    }

    [Theory]
    [InlineData(false, 2, 3, 100, 7, true)]
    [InlineData(false, 4, 3, 7, 7, true)]
    [InlineData(false, 4, 3, 7.01, 7, false)]
    [InlineData(true, 4, 3, 0, 7, false)]
    public void Cleanup_handoff_never_regresses_to_pump_defense(
        bool cleanupStarted,
        double sinceTimer,
        double postTimerDelay,
        double quietFor,
        double stuckQuietSeconds,
        bool expected)
    {
        Assert.Equal(expected, BlightMode.ShouldRemainInDefendAfterTimer(
            cleanupStarted, sinceTimer, postTimerDelay, quietFor, stuckQuietSeconds));
    }

    [Theory]
    [InlineData("Metadata/Chests/BlightDivinationCardsStackedDeck", true)]
    [InlineData("metadata/chests/blightcurrencylow", true)]
    [InlineData("Metadata/Chests/StrongBoxes/StrongboxDivination", false)]
    [InlineData("Metadata/MiscellaneousObjects/WorldItem", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Reward_label_path_identifies_only_blight_chests(string? path, bool expected)
    {
        Assert.Equal(expected, BlightMode.IsBlightChestPath(path));
    }
}
