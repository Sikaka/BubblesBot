using BubblesBot.Bot.Modes;
using BubblesBot.Bot.Behaviors.Interact;
using BubblesBot.Bot.Behaviors.Movement;
using BubblesBot.Core.Knowledge;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Tests;

public sealed class MapZoneCompletionPolicyTests
{
    [Theory]
    [InlineData("Arena", true)]
    [InlineData("ARENA", true)]
    [InlineData("Grand Arena", false)]
    [InlineData("Portal", false)]
    public void TerminalArenaFallbackRequiresExactArenaLabel(string text, bool expected)
        => Assert.Equal(expected, PushCombatMode.IsArenaLabelText(text));

    [Theory]
    [InlineData(true, false, true, false, false, false, true)]
    [InlineData(true, false, true, false, true, false, false)]
    [InlineData(true, false, true, false, false, true, false)]
    [InlineData(true, true, true, false, false, false, false)]
    [InlineData(false, false, true, false, false, false, false)]
    public void RequiredSeparateArenaContinuesCoverageUntilDoorOrTrueExhaustion(
        bool required, bool complete, bool separate, bool entered, bool exhausted,
        bool transitionVisible, bool expected)
        => Assert.Equal(expected, MapZoneCompletionPolicy.ShouldContinueBossDiscovery(
            required, complete, separate, entered, exhausted, transitionVisible));

    [Fact]
    public void ExhaustedMainZoneCanAdvanceBeforeRequiredBossIsDead()
        => Assert.True(MapZoneCompletionPolicy.CanAdvanceToAnotherZone(explorationDone: true));

    [Fact]
    public void WholeMapStillRequiresBossAndDeliriumSettlement()
    {
        Assert.False(MapZoneCompletionPolicy.CanCompleteMap(true, true, false, true));
        Assert.False(MapZoneCompletionPolicy.CanCompleteMap(true, true, true, false));
        Assert.True(MapZoneCompletionPolicy.CanCompleteMap(true, true, true, true));
    }

    [Fact]
    public void StrandIsCataloguedAsSeparateBossArena()
    {
        Assert.True(MapBossCatalog.HasSeparateBossArena("Strand"));
        Assert.False(MapBossCatalog.HasSeparateBossArena("Dunes"));
    }

    [Fact]
    public void StandardStrandRequiresBothObservedArenaBosses()
    {
        var fragments = MapBossCatalog.BossFragments("Strand");
        Assert.Equal(2, fragments.Count);
        Assert.Contains(fragments, f => f.Contains("MapBanditBossHeavyStrike"));
        Assert.Contains(fragments, f => f.Contains("MapBanditLeaderKraityn"));
    }

    [Fact]
    public void StrandBossRosterCompletesTerminalTraversalWithoutBacktracking()
    {
        Assert.True(MapBossCatalog.BossCompletesTraversal("Strand"));
        Assert.False(MapBossCatalog.BossCompletesTraversal("Dunes"));
        Assert.True(MapZoneCompletionPolicy.CanCompleteMap(
            explorationDone: false, bossRequired: true, bossComplete: true,
            deliriumSettled: true, bossCompletesTraversal: true));
        Assert.False(MapZoneCompletionPolicy.CanCompleteMap(
            explorationDone: false, bossRequired: true, bossComplete: false,
            deliriumSettled: true, bossCompletesTraversal: true));
    }

    [Theory]
    [InlineData("Forge of the Phoenix", "PhoenixBoss")]
    [InlineData("Maze of the Minotaur", "MinotaurBoss")]
    [InlineData("Pit of the Chimera", "ChimeraBoss")]
    [InlineData("Lair of the Hydra", "HydraBoss")]
    public void GuardianBossesAreCataloguedAsTerminal(string map, string fragment)
    {
        Assert.Contains(MapBossCatalog.BossFragments(map), x => x.Contains(fragment));
        Assert.True(MapBossCatalog.BossCompletesTraversal(map));
        Assert.False(MapBossCatalog.HasSeparateBossArena(map));
    }

    [Fact]
    public void CompletedSeparateBossArenaExitsWithoutExploringArena()
    {
        Assert.True(MapZoneCompletionPolicy.ShouldExitCompletedBossArena(true, true, true, true));
        Assert.False(MapZoneCompletionPolicy.ShouldExitCompletedBossArena(true, false, true, true));
        Assert.False(MapZoneCompletionPolicy.ShouldExitCompletedBossArena(true, true, false, true));
        Assert.False(MapZoneCompletionPolicy.ShouldExitCompletedBossArena(true, true, true, false));
    }

    [Fact]
    public void FullyDeadRosterWaitsForBossDeathQuietGate()
    {
        Assert.False(MapZoneCompletionPolicy.ShouldSearchArenaForMissingBoss(
            bossComplete: false, bossesDead: 1, expectedBosses: 1));
        Assert.True(MapZoneCompletionPolicy.ShouldSearchArenaForMissingBoss(
            bossComplete: false, bossesDead: 1, expectedBosses: 2));
        Assert.False(MapZoneCompletionPolicy.ShouldSearchArenaForMissingBoss(
            bossComplete: true, bossesDead: 1, expectedBosses: 1));
    }

    [Fact]
    public void StableObjectivesSkipFrontierDebounceOnlyAfterArenaExit()
    {
        Assert.False(MapZoneCompletionPolicy.ShouldCompleteImmediately(
            canCompleteMap: true, hasSeparateBossArena: true, arenaEntered: true));
        Assert.True(MapZoneCompletionPolicy.ShouldCompleteImmediately(
            canCompleteMap: true, hasSeparateBossArena: true, arenaEntered: false));
        Assert.True(MapZoneCompletionPolicy.ShouldCompleteImmediately(
            canCompleteMap: true, hasSeparateBossArena: false, arenaEntered: false));
        Assert.False(MapZoneCompletionPolicy.ShouldCompleteImmediately(
            canCompleteMap: false, hasSeparateBossArena: false, arenaEntered: false));
    }

    [Fact]
    public void IncompleteSameHashArenaCannotUseGenericExit()
    {
        Assert.False(MapZoneCompletionPolicy.MayUseGenericTransition(
            arenaEntered: true, bossComplete: false));
        Assert.True(MapZoneCompletionPolicy.MayUseGenericTransition(
            arenaEntered: true, bossComplete: true));
        Assert.True(MapZoneCompletionPolicy.MayUseGenericTransition(
            arenaEntered: false, bossComplete: false));
    }

    [Theory]
    [InlineData(AreaTransitionType.Normal)]
    [InlineData(AreaTransitionType.Local)]
    public void OrdinaryAndLocalDoorsRemainTraversalCandidates(AreaTransitionType type)
    {
        var entry = Transition(type, readable: true);
        Assert.True(MapTransitionPolicy.IsTraversalCandidate(entry));
        Assert.False(MapTransitionPolicy.IsVaalSideArea(entry));
    }

    [Theory]
    [InlineData(AreaTransitionType.NormalToCorrupted)]
    [InlineData(AreaTransitionType.CorruptedToNormal)]
    public void VaalSideAreaTransitionsCannotBecomeBossOrNextZoneGoals(AreaTransitionType type)
    {
        var entry = Transition(type, readable: true);
        Assert.False(MapTransitionPolicy.IsTraversalCandidate(entry));
        Assert.True(MapTransitionPolicy.IsVaalSideArea(entry));
    }

    [Fact]
    public void UnreadableTransitionIdentityFailsClosed()
        => Assert.False(MapTransitionPolicy.IsTraversalCandidate(
            Transition(AreaTransitionType.Normal, readable: false)));

    [Fact]
    public void ExhaustedIncompleteArenaSearchRequestsMapAbandon()
    {
        Assert.False(MapZoneCompletionPolicy.ShouldAbandonExhaustedArenaSearch(7, 8, false));
        Assert.True(MapZoneCompletionPolicy.ShouldAbandonExhaustedArenaSearch(8, 8, false));
        Assert.False(MapZoneCompletionPolicy.ShouldAbandonExhaustedArenaSearch(8, 8, true));
    }

    [Fact]
    public void LocalTransitionClickWaitsForReliableDoorRange()
    {
        var door = new Vector2i { X = 100, Y = 100 };
        Assert.False(EnterAreaTransition.IsWithinReliableClickRange(
            new Vector2i { X = 118, Y = 100 }, door, configuredRange: 30));
        Assert.True(EnterAreaTransition.IsWithinReliableClickRange(
            new Vector2i { X = 108, Y = 100 }, door, configuredRange: 30));
    }

    [Fact]
    public void ArenaStagingStartsAwayFromExit()
    {
        var entry = new Vector2i { X = 13, Y = -12 };
        var exit = new Vector2i { X = 0, Y = 0 };
        var goal = PushCombatMode.BossArenaInwardGoal(entry, exit, attempt: 0);
        var awayX = entry.X - exit.X;
        var awayY = entry.Y - exit.Y;
        var moveX = goal.X - entry.X;
        var moveY = goal.Y - entry.Y;
        Assert.True(awayX * moveX + awayY * moveY > 0);
    }

    [Fact]
    public void ChimeraSmokeSearchCrossesBossThenSweepsTwoRings()
    {
        var anchor = new Vector2i { X = 1182, Y = 309 };
        Assert.Equal(anchor, PushCombatMode.ChimeraRevealGoal(anchor, attempt: 0));

        var inner = PushCombatMode.ChimeraRevealGoal(anchor, attempt: 1);
        var outer = PushCombatMode.ChimeraRevealGoal(anchor, attempt: 9);
        Assert.Equal(75, inner.X - anchor.X);
        Assert.Equal(130, outer.X - anchor.X);
        Assert.Equal(anchor.Y, inner.Y);
        Assert.Equal(anchor.Y, outer.Y);
    }

    [Fact]
    public void BlockedWalkOnlyArenaRouteFailsForGoalRotation()
    {
        Assert.True(FollowPath.ShouldFailWalkOnlyPath(
            allowGapCrossing: false, stuck: true));
        Assert.False(FollowPath.ShouldFailWalkOnlyPath(
            allowGapCrossing: true, stuck: true));
        Assert.False(FollowPath.ShouldFailWalkOnlyPath(
            allowGapCrossing: false, stuck: false));
    }

    [Fact]
    public void SameHashBossDoorRequiresClickAndLargeDisplacement()
    {
        var origin = new Vector2i { X = 2490, Y = 430 };
        Assert.False(EnterAreaTransition.IsConfirmedSameAreaTeleport(
            origin, new Vector2i { X = 323, Y = 2706 }, clickDispatched: false));
        Assert.False(EnterAreaTransition.IsConfirmedSameAreaTeleport(
            origin, new Vector2i { X = 2500, Y = 435 }, clickDispatched: true));
        Assert.True(EnterAreaTransition.IsConfirmedSameAreaTeleport(
            origin, new Vector2i { X = 323, Y = 2706 }, clickDispatched: true));
    }

    [Fact]
    public void SeparateArenaBecomesPriorityAtSeventyFivePercent()
    {
        Assert.False(MapZoneCompletionPolicy.ShouldPrioritizeBossArena(true, false, true, false, 74.9));
        Assert.True(MapZoneCompletionPolicy.ShouldPrioritizeBossArena(true, false, true, false, 75));
        Assert.False(MapZoneCompletionPolicy.ShouldPrioritizeBossArena(true, true, true, false, 90));
        Assert.False(MapZoneCompletionPolicy.ShouldPrioritizeBossArena(true, false, true, true, 90));
    }

    private static EntityCache.Entry Transition(
        AreaTransitionType type, bool readable) => new()
    {
        Kind = EntityListReader.EntityKind.AreaTransition,
        Path = "Metadata/MiscellaneousObjects/AreaTransition",
        Metadata = "Metadata/MiscellaneousObjects/AreaTransition",
        AreaTransitionIdentityReadable = readable,
        AreaTransitionType = type,
    };

}
