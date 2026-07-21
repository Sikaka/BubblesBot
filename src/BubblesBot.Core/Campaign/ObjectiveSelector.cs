using BubblesBot.Core.Game;

namespace BubblesBot.Core.Campaign;

/// <summary>
/// Turns "which area am I in" into an ordered <see cref="GuidancePlan"/>: the exit toward the next
/// zone (primary), plus the in-area kill/quest/waypoint objectives, each carrying hints for where to
/// resolve it. Route and target data are joined on the shared area id (route <c>areaId</c> ≡
/// <c>targets.json</c> key ≡ live <c>RawName</c>); the coordinate resolution itself happens in
/// Phase B against the live tile map.
/// </summary>
public static class ObjectiveSelector
{
    // Priorities: the exit is what "get through the campaign quickly" is about, so it sorts first;
    // bosses/quests come next; waypoint pickup and prose objectives after.
    private const int PriExit = 0;
    private const int PriBoss = 10;
    private const int PriQuest = 20;
    private const int PriWaypoint = 30;
    private const int PriOther = 40;

    public static GuidancePlan Select(string liveRawName, CampaignRoute? route, CampaignTargets targets)
    {
        if (string.IsNullOrEmpty(liveRawName))
            return GuidancePlan.None(liveRawName, "no area name");

        var pois = targets.ForArea(liveRawName);
        var hasTargets = pois.Count > 0;

        RouteSegment? seg = null;
        if (route is not null)
        {
            var segs = route.SegmentsForArea(liveRawName);
            if (segs.Count > 0) seg = segs[0]; // first visit; refined by quest state later
        }

        if (seg is null)
        {
            // No route coverage. Still surface any POIs so the overlay shows markers, but fail
            // loudly on guidance (per N-07: no arbitrary "walk to nearest exit" fallback).
            return new GuidancePlan(liveRawName, HasRoute: false, hasTargets,
                Array.Empty<CampaignObjective>(), pois, NextAreaId: null,
                Diagnostic: hasTargets ? "targets but no route step for area" : "no route or targets for area");
        }

        var objectives = new List<CampaignObjective>();

        // Primary: the exit toward the next area.
        if (!string.IsNullOrEmpty(seg.NextAreaId))
        {
            objectives.Add(new CampaignObjective(
                RouteTokenType.Enter,
                $"Exit → {seg.NextAreaId}",
                PriExit,
                ExitHints(pois, seg.NextAreaId!)));
        }

        // In-area objectives.
        foreach (var tok in seg.Objectives)
        {
            switch (tok.Type)
            {
                case RouteTokenType.Kill:
                    objectives.Add(new CampaignObjective(tok.Type, $"Kill {tok.Text}", PriBoss,
                        MatchOrBoss(pois, tok.Text)));
                    break;
                case RouteTokenType.Arena:
                    objectives.Add(new CampaignObjective(tok.Type, $"Arena: {tok.Text}", PriBoss,
                        MatchOrBoss(pois, tok.Text)));
                    break;
                case RouteTokenType.QuestText:
                    objectives.Add(new CampaignObjective(tok.Type, $"Quest: {tok.Text}", PriQuest,
                        MatchHints(pois, tok.Text)));
                    break;
                case RouteTokenType.WaypointGet:
                    objectives.Add(new CampaignObjective(tok.Type, "Get waypoint", PriWaypoint,
                        Landmark(LandmarkCatalog.Kind.Waypoint)));
                    break;
                case RouteTokenType.Trial:
                    objectives.Add(new CampaignObjective(tok.Type, "Labyrinth trial", PriOther,
                        MatchHints(pois, "trial")));
                    break;
                default:
                    objectives.Add(new CampaignObjective(tok.Type, tok.Text ?? tok.Type.ToString(), PriOther,
                        MatchHints(pois, tok.Text)));
                    break;
            }
        }

        objectives.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        return new GuidancePlan(liveRawName, HasRoute: true, hasTargets, objectives, pois, seg.NextAreaId);
    }

    private static IReadOnlyList<TargetHint> Landmark(LandmarkCatalog.Kind kind)
        => new TargetHint[] { new TargetHint.Landmark(kind) };

    /// <summary>POI hints matching the text, or the boss-arena landmark when nothing matches.</summary>
    private static IReadOnlyList<TargetHint> MatchOrBoss(IReadOnlyList<TargetDescription> pois, string? value)
    {
        var matched = MatchHints(pois, value);
        return matched.Count > 0 ? matched : Landmark(LandmarkCatalog.Kind.BossArena);
    }

    /// <summary>Hints for reaching the exit: the transition-like POIs from the catalog first, then a
    /// fallback that matches terrain tiles whose detail-name/path contains "AreaTransition". We do
    /// NOT add a broad "Metadata/Terrain" prefix — every terrain tile matches it, which would flood
    /// the clusterer and average the exit into a meaningless map-center centroid.</summary>
    private static IReadOnlyList<TargetHint> ExitHints(IReadOnlyList<TargetDescription> pois, string nextAreaId)
    {
        var hints = new List<TargetHint>();
        foreach (var t in pois)
            if (LooksLikeTransition(t))
                hints.Add(new TargetHint.Named(t.Name, t.ExpectedCount, t.Label, t.TargetType));
        // Fallback (resolved via TileMapView.FindByKeyContains): actual transition tiles are named
        // ".../AreaTransition_To_X.tdt". Bounded ExpectedCount keeps it to a few representatives.
        hints.Add(new TargetHint.Named("AreaTransition", 3, $"Exit → {nextAreaId}", TargetKind.Tile));
        return hints;
    }

    private static bool LooksLikeTransition(TargetDescription t)
    {
        var s = (t.Name + " " + (t.DisplayName ?? "")).ToLowerInvariant();
        return s.Contains("transition") || s.Contains("entrance") || s.Contains("exit")
            || s.Contains("passage") || s.Contains("gate") || s.Contains("stair");
    }

    /// <summary>Named hints for every POI whose name/label loosely contains the objective text.</summary>
    private static IReadOnlyList<TargetHint> MatchHints(IReadOnlyList<TargetDescription> pois, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<TargetHint>();
        var needle = value.Trim().ToLowerInvariant();
        var hints = new List<TargetHint>();
        foreach (var t in pois)
        {
            var hay = (t.Name + " " + (t.DisplayName ?? "")).ToLowerInvariant();
            if (hay.Contains(needle))
                hints.Add(new TargetHint.Named(t.Name, t.ExpectedCount, t.Label, t.TargetType));
        }
        return hints;
    }
}
