namespace BubblesBot.Core.Campaign;

/// <summary>Coverage status for one route area: does the target catalog resolve it, and does the
/// exit have any transition-like hint?</summary>
public sealed record AreaCoverage(string AreaId, bool InRoute, bool HasTargets, bool HasExitHint, string? NextAreaId);

/// <summary>
/// Audits the join between the campaign route and the target catalog (campaign-runner D-03/D-06):
/// which route areas have target coverage and a resolvable exit. Used by the probe and by a startup
/// diagnostic so unresolved areas fail visibly rather than silently mis-guiding.
/// </summary>
public static class AreaCoverageReport
{
    public static IReadOnlyList<AreaCoverage> Build(CampaignRoute route, CampaignTargets targets)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var report = new List<AreaCoverage>();
        foreach (var seg in route.Segments)
        {
            if (!seen.Add(seg.AreaId)) continue;
            var pois = targets.ForArea(seg.AreaId);
            var hasExitHint = !string.IsNullOrEmpty(seg.NextAreaId);
            report.Add(new AreaCoverage(
                seg.AreaId,
                InRoute: true,
                HasTargets: pois.Count > 0,
                HasExitHint: hasExitHint,
                seg.NextAreaId));
        }
        return report;
    }

    /// <summary>Route areas with no target coverage — the blocking list for D-06.</summary>
    public static IReadOnlyList<string> UncoveredAreas(CampaignRoute route, CampaignTargets targets)
        => Build(route, targets).Where(c => !c.HasTargets).Select(c => c.AreaId).ToList();
}
