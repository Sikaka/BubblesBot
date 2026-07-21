using BubblesBot.Bot.Modes;
using BubblesBot.Bot.Behaviors.Interact;
using BubblesBot.Bot.Behaviors.Movement;
using BubblesBot.Core.Knowledge;
using BubblesBot.Core.Game;

namespace BubblesBot.Tests;

public sealed class MapZoneCompletionPolicyTests
{
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

    [Fact]
    public void CompletedSeparateBossArenaExitsWithoutExploringArena()
    {
        Assert.True(MapZoneCompletionPolicy.ShouldExitCompletedBossArena(true, true, true, true));
        Assert.False(MapZoneCompletionPolicy.ShouldExitCompletedBossArena(true, false, true, true));
        Assert.False(MapZoneCompletionPolicy.ShouldExitCompletedBossArena(true, true, false, true));
        Assert.False(MapZoneCompletionPolicy.ShouldExitCompletedBossArena(true, true, true, false));
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

}
