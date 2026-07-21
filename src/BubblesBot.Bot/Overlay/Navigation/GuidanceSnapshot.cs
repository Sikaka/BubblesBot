using BubblesBot.Core.Campaign;
using BubblesBot.Core.Game;
using BubblesBot.Core.Pathfinding;

namespace BubblesBot.Bot.Overlay.Navigation;

/// <summary>
/// A resolved guidance target for the current area, with a reverse-Dijkstra flow field built <b>to</b>
/// it. Because the field covers every start cell, the render/tick thread re-walks it from the live
/// player position each frame — O(path) and smooth — with no rebuild as the player moves. Built once
/// per area by the guidance worker.
/// </summary>
public sealed record GuidanceTarget(
    string Label,
    RouteTokenType Kind,
    DistanceField Field,
    Vector2i Target);

/// <summary>One drawn route (player → target) for a frame: decimated grid cells + label + target.</summary>
public sealed record GuidanceRoute(
    string Label,
    RouteTokenType Kind,
    IReadOnlyList<PathCell> Cells,
    Vector2i Target);

/// <summary>
/// Immutable per-area guidance published by the worker, read lock-free by the render/tick thread.
/// Carries a flow-field target for each key objective (waypoint, each area transition).
/// </summary>
public sealed record GuidanceSnapshot(
    uint AreaHash,
    string AreaId,
    IReadOnlyList<GuidanceTarget> Targets,
    string? Diagnostic)
{
    public static readonly GuidanceSnapshot Empty =
        new(0, string.Empty, Array.Empty<GuidanceTarget>(), "no guidance yet");
}

/// <summary>
/// Lightweight per-world-tick cursor the main thread publishes for the guidance worker: the live
/// IngameData address, current area hash, and player grid cell.
/// </summary>
public readonly record struct WorldCursor(nint IngameData, uint AreaHash, Vector2i PlayerGrid);
