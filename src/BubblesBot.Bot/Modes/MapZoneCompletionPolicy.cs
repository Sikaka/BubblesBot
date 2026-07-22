namespace BubblesBot.Bot.Modes;

/// <summary>Separates per-zone traversal from whole-map completion for boss sub-areas.</summary>
public static class MapZoneCompletionPolicy
{
    public const double BossHuntRevealPercent = 75.0;

    public static bool CanAdvanceToAnotherZone(bool explorationDone) => explorationDone;

    public static bool CanCompleteMap(
        bool explorationDone, bool bossRequired, bool bossComplete, bool deliriumSettled,
        bool bossCompletesTraversal = false)
        => (explorationDone || (bossRequired && bossComplete && bossCompletesTraversal))
           && (!bossRequired || bossComplete)
           && deliriumSettled;

    /// <summary>Stable whole-map objectives do not need the generic five-second frontier
    /// debounce. A separate boss arena is the sole exception: finish its local loot and take
    /// its exit first, then complete immediately in the parent map.</summary>
    public static bool ShouldCompleteImmediately(
        bool canCompleteMap, bool hasSeparateBossArena, bool arenaEntered)
        => canCompleteMap && (!hasSeparateBossArena || !arenaEntered);

    /// <summary>Once most of a separate-arena map has been swept, remaining coverage is no
    /// longer the objective: preserve outward progress until the boss door is discovered.</summary>
    public static bool ShouldPrioritizeBossArena(
        bool bossRequired, bool bossComplete, bool hasSeparateArena, bool arenaEntered,
        double revealPercent)
        => bossRequired
           && !bossComplete
           && hasSeparateArena
           && !arenaEntered
           && revealPercent >= BossHuntRevealPercent;

    /// <summary>The configured reveal cutoff may finish ordinary coverage, but it must not
    /// strand a required separate-arena boss behind an undiscovered door. Keep exploring until
    /// a valid door is visible or the connected terrain is genuinely exhausted.</summary>
    public static bool ShouldContinueBossDiscovery(
        bool bossRequired,
        bool bossComplete,
        bool hasSeparateArena,
        bool arenaEntered,
        bool explorationExhausted,
        bool eligibleTransitionVisible)
        => bossRequired
           && !bossComplete
           && hasSeparateArena
           && !arenaEntered
           && !explorationExhausted
           && !eligibleTransitionVisible;

    /// <summary>Once every required boss in a separate arena is dead, return to the parent
    /// map immediately. Local loot/mechanic branches still run before this policy branch.</summary>
    public static bool ShouldExitCompletedBossArena(
        bool bossRequired, bool bossComplete, bool hasSeparateArena, bool arenaEntered)
        => bossRequired && bossComplete && hasSeparateArena && arenaEntered;

    /// <summary>A generic zone transition must not consume a same-hash arena exit while
    /// its required boss roster is incomplete. Egress belongs to the dedicated branch.</summary>
    public static bool MayUseGenericTransition(bool arenaEntered, bool bossComplete)
        => !arenaEntered || bossComplete;

    public static bool ShouldAbandonExhaustedArenaSearch(
        int attempts, int maxAttempts, bool bossComplete)
        => !bossComplete && maxAttempts > 0 && attempts >= maxAttempts;

    /// <summary>Boss death evidence intentionally settles for several seconds. During that
    /// quiet gate, a fully observed dead roster must wait rather than launching the missing-boss
    /// arena search and exhausting its bounded route attempts before certification completes.</summary>
    public static bool ShouldSearchArenaForMissingBoss(
        bool bossComplete, int bossesDead, int expectedBosses)
        => !bossComplete && (expectedBosses <= 0 || bossesDead < expectedBosses);

}
