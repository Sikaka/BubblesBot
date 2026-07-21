namespace BubblesBot.Bot.Modes;

/// <summary>
/// Decides whether a shrine is blocking the combat the bot is fighting right now and may
/// therefore jump ahead of the normal interaction sweep. This is deliberately much narrower
/// than "an available shrine exists": streamed shrines can be hundreds of grid units away.
/// </summary>
public static class ShrinePriorityPolicy
{
    /// <summary>A shrine farther away than this is routing, not immediate combat recovery.</summary>
    public const float MaxPlayerDistanceGrid = 80f;

    /// <summary>The engaged monster must be inside the shrine pack's local footprint.</summary>
    public const float MaxTargetDistanceGrid = 45f;

    public static bool ShouldPreempt(
        bool exclusiveMechanicOwnsControl,
        bool hasEngagedTarget,
        float shrineDistanceToPlayer,
        float shrineDistanceToTarget)
        => !exclusiveMechanicOwnsControl
            && hasEngagedTarget
            && shrineDistanceToPlayer <= MaxPlayerDistanceGrid
            && shrineDistanceToTarget <= MaxTargetDistanceGrid;
}
