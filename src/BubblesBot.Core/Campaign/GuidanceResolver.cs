using BubblesBot.Core.Game;
using BubblesBot.Core.Pathfinding;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Core.Campaign;

/// <summary>An objective resolved to concrete, clustered, walkable grid markers.</summary>
public sealed record ResolvedObjective(CampaignObjective Objective, IReadOnlyList<Vector2i> Markers);

/// <summary>
/// The full guidance for the current area with every objective resolved to world cells. The primary
/// target (what a single guidance route should point at) is the first marker of the first objective.
/// </summary>
public sealed record ResolvedGuidance(GuidancePlan Plan, IReadOnlyList<ResolvedObjective> Objectives)
{
    public Vector2i? PrimaryTarget
    {
        get
        {
            foreach (var obj in Objectives)
                if (obj.Markers.Count > 0)
                    return obj.Markers[0];
            return null;
        }
    }

    public static readonly ResolvedGuidance Empty =
        new(GuidancePlan.None("", "no guidance"), Array.Empty<ResolvedObjective>());
}

/// <summary>
/// Resolves a <see cref="GuidancePlan"/>'s objective hints into clustered, walkable grid markers
/// using the area's <see cref="TileMapView"/>. Tile/landmark/path-prefix hints resolve here; entity
/// hints are deferred to the live entity layer (Phase D) and skipped for tile resolution.
/// </summary>
public static class GuidanceResolver
{
    private const int DefaultLandmarkCount = 3;

    public static ResolvedGuidance Resolve(GuidancePlan plan, TileMapView tiles, ICellReader? grid)
    {
        var resolved = new List<ResolvedObjective>(plan.Objectives.Count);
        foreach (var obj in plan.Objectives)
        {
            var points = new List<Vector2i>();
            var expected = 1;
            foreach (var hint in obj.Hints)
            {
                switch (hint)
                {
                    case TargetHint.Named named:
                        if (named.Kind == TargetKind.Entity) break; // resolved from live entities later
                        expected = Math.Max(expected, named.ExpectedCount);
                        points.AddRange(GatherNamed(tiles, named.Name));
                        break;
                    case TargetHint.Landmark lm:
                        expected = Math.Max(expected, DefaultLandmarkCount);
                        points.AddRange(GatherLandmark(tiles, lm.Kind));
                        break;
                    case TargetHint.PathPrefix pp:
                        expected = Math.Max(expected, DefaultLandmarkCount);
                        foreach (var (_, positions) in tiles.FindByPathPrefix(pp.Prefix))
                            points.AddRange(positions);
                        break;
                }
            }

            var markers = points.Count == 0
                ? (IReadOnlyList<Vector2i>)Array.Empty<Vector2i>()
                : TargetClusterer.Cluster(Dedupe(points), expected, grid);
            resolved.Add(new ResolvedObjective(obj, markers));
        }

        return new ResolvedGuidance(plan, resolved);
    }

    private static IReadOnlyList<Vector2i> GatherNamed(TileMapView tiles, string name)
    {
        var exact = tiles.Find(name);
        if (exact.Count > 0) return exact;
        return tiles.FindByKeyContains(name);
    }

    private static IReadOnlyList<Vector2i> GatherLandmark(TileMapView tiles, LandmarkCatalog.Kind kind)
    {
        var hits = new List<Vector2i>();
        foreach (var entry in LandmarkCatalog.All)
            if (entry.Kind == kind)
                hits.AddRange(tiles.Find(entry.DetailName));
        return hits;
    }

    private static IReadOnlyList<Vector2i> Dedupe(List<Vector2i> points)
    {
        var seen = new HashSet<long>(points.Count);
        var result = new List<Vector2i>(points.Count);
        foreach (var p in points)
        {
            var key = ((long)p.X << 32) ^ (uint)p.Y;
            if (seen.Add(key)) result.Add(p);
        }
        return result;
    }
}
