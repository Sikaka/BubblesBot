using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Modes;

/// <summary>
/// Defines which in-map transitions may participate in map traversal. This is deliberately
/// semantic rather than path-based: ordinary doors and Vaal side-area entrances share the
/// same generic metadata path.
/// </summary>
public static class MapTransitionPolicy
{
    public static bool IsTraversalCandidate(EntityCache.Entry entry)
        => entry.Kind == EntityListReader.EntityKind.AreaTransition
           && IsTraversalType(
               entry.AreaTransitionIdentityReadable,
               entry.AreaTransitionType);

    /// <summary>
    /// Only ordinary cross-zone and same-area/local doors are map progression candidates.
    /// Unknown identity fails closed; corrupted-side-area and Labyrinth transitions are
    /// optional destinations and cannot be mistaken for a boss room or next map zone.
    /// </summary>
    public static bool IsTraversalType(bool identityReadable, AreaTransitionType type)
        => identityReadable
           && type is AreaTransitionType.Normal or AreaTransitionType.Local;

    public static bool IsVaalSideArea(EntityCache.Entry entry)
        => entry.Kind == EntityListReader.EntityKind.AreaTransition
           && entry.AreaTransitionIdentityReadable
           && entry.AreaTransitionType is AreaTransitionType.NormalToCorrupted
               or AreaTransitionType.CorruptedToNormal;
}
