using BubblesBot.Core.Game;

namespace BubblesBot.Core.Campaign;

/// <summary>
/// A hint for locating an objective in the world, resolved to coordinates in Phase B. Discriminated:
/// a specific named target (from the catalog), a landmark category, or a tile-path prefix.
/// </summary>
public abstract record TargetHint
{
    /// <summary>A named target/description to resolve via tile detail-name / path / entity match.</summary>
    public sealed record Named(string Name, int ExpectedCount, string Label, TargetKind Kind) : TargetHint;

    /// <summary>Resolve the nearest tile of a landmark category (waypoint, boss arena, …).</summary>
    public sealed record Landmark(LandmarkCatalog.Kind Kind) : TargetHint;

    /// <summary>Resolve tiles whose <c>.tdt</c> path starts with this prefix (per-area transitions).</summary>
    public sealed record PathPrefix(string Prefix) : TargetHint;
}

/// <summary>
/// A single thing the player should do in the current area, with hints for where it is. Lower
/// <see cref="Priority"/> sorts first (the exit toward the next zone is the primary objective).
/// </summary>
public sealed record CampaignObjective(
    RouteTokenType Kind,
    string Label,
    int Priority,
    IReadOnlyList<TargetHint> Hints,
    string? Note = null);

/// <summary>
/// The guidance the overlay should show for the current area: whether the route/targets cover it,
/// the ordered objectives, the raw POI list for markers, and the next area id.
/// </summary>
public sealed record GuidancePlan(
    string AreaId,
    bool HasRoute,
    bool HasTargets,
    IReadOnlyList<CampaignObjective> Objectives,
    IReadOnlyList<TargetDescription> AreaPois,
    string? NextAreaId,
    string? Diagnostic = null)
{
    public static GuidancePlan None(string areaId, string diagnostic) =>
        new(areaId, false, false, Array.Empty<CampaignObjective>(), Array.Empty<TargetDescription>(), null, diagnostic);
}
