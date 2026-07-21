using System.Linq;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;
using BubblesBot.Research.Probing;

namespace BubblesBot.Research.Probes.Mechanics;

/// <summary>
/// Loot-label occlusion capture. For every in-range item label this mirrors the bot's
/// <c>LootClosestVisible.BuildOcclusionRects</c> + <c>InteractSystem.FindUncoveredPoint</c>
/// (re-implemented here because Research cannot reference Bot) and reports whether an
/// unoccluded click point survives, plus every label rect that overlaps the target.
///
/// <para>Purpose: settle the "essence label covers the loot label" deadlock empirically. Park
/// the character at the overlap (e.g. a Glassblower's Bauble behind a "Warped Regurgitator"
/// essence monster label) and run <c>--probe capture.loot-occlusion</c>. The output answers:
/// (1) does the essence/monster nameplate appear in GroundLabels at all (i.e. can the
/// occlusion system even see it)? (2) does it overlap the loot rect? (3) does the clicker
/// find a clickable point or refuse? Read-only.</para>
/// </summary>
public sealed class LootOcclusionProbe : IProbe
{
    private const int RangeGrid = 30;

    // Mirror of InteractSystem.ClickFractions (Bot). Center first, then vertical offsets, then
    // horizontal/corners. Keep in sync with the Bot copy.
    private static readonly (float FX, float FY)[] ClickFractions =
    {
        (0.5f, 0.5f), (0.5f, 0.28f), (0.5f, 0.72f),
        (0.28f, 0.5f), (0.72f, 0.5f),
        (0.28f, 0.28f), (0.72f, 0.28f), (0.28f, 0.72f), (0.72f, 0.72f),
        (0.12f, 0.5f), (0.88f, 0.5f),
    };

    public string Name => "capture.loot-occlusion";
    public string Group => "capture";
    public string Description => "For each in-range item label, mirror the bot's occlusion test: overlapping label rects + whether an unoccluded click point survives.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var snapshot = new GameSnapshot(
            ctx.Reader, ctx.Chain.IngameData, ctx.Chain.IngameState,
            new WindowInfo(0, 0, 1920, 1080));

        var labels = snapshot.GroundLabels;
        var lines = new List<string> { $"total ground labels={labels.Count}" };

        // Dump every nearby label with its rect — a monster/essence nameplate shows up here
        // (outer='Metadata/Monsters/...') iff PoE inserted it into the ItemsOnGround list.
        lines.Add("-- nearby labels (rect = X,Y WxH) --");
        foreach (var l in labels)
        {
            if (l.DistanceToPlayer > RangeGrid + 20) continue;
            var rectStr = l.LabelRect is { } rr ? $"[{rr.X:F0},{rr.Y:F0} {rr.Width:F0}x{rr.Height:F0}]" : "none";
            lines.Add($"d={l.DistanceToPlayer:F1} item={l.IsItem} vis={l.IsLabelVisible} onScreen={l.IsRectOnScreen} " +
                      $"name='{l.ItemName}' rect={rectStr} outer='{l.Path}'");
        }

        // Run the clicker's occlusion test against every visible in-range item label.
        lines.Add($"-- occlusion test for item labels within {RangeGrid} grid --");
        var itemsInRange = labels
            .Where(l => l.IsItem && l.IsLabelVisible && l.DistanceToPlayer <= RangeGrid)
            .OrderBy(l => l.DistanceToPlayer)
            .ToArray();
        if (itemsInRange.Length == 0)
            lines.Add("(no visible item labels in range)");
        foreach (var target in itemsInRange)
        {
            if (target.LabelRect is not { } t)
            {
                lines.Add($"'{target.ItemName}' d={target.DistanceToPlayer:F1}: NO RECT");
                continue;
            }
            var occ = BuildOcclusionRects(snapshot, target);
            var point = FindUncoveredPoint(t, occ);
            var verdict = point is { } pt
                ? $"CLICKABLE at ({pt.X:F0},{pt.Y:F0})"
                : "FULLY COVERED — clicker refuses";
            lines.Add($"'{target.ItemName}' d={target.DistanceToPlayer:F1} " +
                      $"rect=[{t.X:F0},{t.Y:F0} {t.Width:F0}x{t.Height:F0}] occluders={occ.Count} -> {verdict}");
            foreach (var (o, name) in occ)
                lines.Add($"      covered by '{name}' [{o.X:F0},{o.Y:F0} {o.Width:F0}x{o.Height:F0}]");
        }

        return ProbeResult.Pass(string.Join(Environment.NewLine, lines));
    }

    public ProbeResult Discover(ProbeContext ctx) => Validate(ctx);

    // Mirror of LootClosestVisible.BuildOcclusionRects (Bot), but also returns each occluder's
    // display name so the capture shows WHAT is covering the loot. The Bot version additionally
    // includes the on-ground Ritual Rewards button; that is not needed for the essence case and
    // isn't reachable from Core, so it is omitted here.
    private static List<(ElementGeometry.Rect Rect, string Name)> BuildOcclusionRects(
        GameSnapshot snap, GroundLabelView target)
    {
        var rects = new List<(ElementGeometry.Rect, string)>();
        if (target.LabelRect is not { } t) return rects;
        foreach (var l in snap.GroundLabels)
        {
            if (l.LabelAddress == target.LabelAddress) continue;
            if (!l.IsLabelVisible) continue;
            if (l.LabelRect is not { } r || !t.Overlaps(r)) continue;
            var nm = !string.IsNullOrEmpty(l.ItemName) ? l.ItemName
                : !string.IsNullOrEmpty(l.DisplayName) ? l.DisplayName
                : l.Path;
            rects.Add((r, nm));
            if (rects.Count >= 24) break;
        }
        return rects;
    }

    private static (float X, float Y)? FindUncoveredPoint(
        ElementGeometry.Rect target, List<(ElementGeometry.Rect Rect, string Name)> occluders)
    {
        if (occluders.Count == 0) return (target.CenterX, target.CenterY);
        foreach (var (fx, fy) in ClickFractions)
        {
            var x = target.X + target.Width * fx;
            var y = target.Y + target.Height * fy;
            var covered = false;
            foreach (var (o, _) in occluders)
                if (o.Contains(x, y)) { covered = true; break; }
            if (!covered) return (x, y);
        }
        return null;
    }
}
