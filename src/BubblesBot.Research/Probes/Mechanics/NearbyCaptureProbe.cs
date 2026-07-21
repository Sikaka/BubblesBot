using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;
using BubblesBot.Research.Probing;

namespace BubblesBot.Research.Probes.Mechanics;

/// <summary>
/// General-purpose "what is the user standing next to" capture: every ground label and
/// every non-monster entity within 60 grid of the player, with paths, components, and
/// state-machine values. Run this whenever a new mechanic or item class needs identifying —
/// the user parks the character next to it and we read everything in one shot.
/// </summary>
public sealed class NearbyCaptureProbe : IProbe
{
    private const int RadiusGrid = 60;

    public string Name => "capture.nearby";
    public string Group => "capture";
    public string Description => "Dump ground labels + non-monster entities within 60 grid of the player (paths, components, states) to identify new mechanics/items.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var player = EntityListReader.TryReadSnapshot(ctx.Reader, ctx.Chain.Player);
        if (player?.GridPosition is not { } p)
            return ProbeResult.Fail("cannot read player grid position");

        var lines = new List<string> { $"player grid=({p.X},{p.Y})" };

        var snapshot = new GameSnapshot(
            ctx.Reader, ctx.Chain.IngameData, ctx.Chain.IngameState,
            new WindowInfo(0, 0, 1920, 1080));
        lines.Add($"-- ground labels within {RadiusGrid} grid --");
        foreach (var l in snapshot.GroundLabels)
        {
            if (l.DistanceToPlayer > RadiusGrid) continue;
            lines.Add($"label d={l.DistanceToPlayer:F0} item={l.IsItem} rarity={l.ItemRarity} " +
                      $"name='{l.ItemName}' visible={l.IsLabelVisible} outer='{l.Path}' inner='{l.InnerItemPath}'");
        }

        lines.Add($"-- non-monster entities within {RadiusGrid} grid --");
        var evidence = MechanicStateCapture.Capture(ctx, "nearby", s =>
            s.GridPosition is { } g
            && !s.Path.StartsWith("Metadata/Monsters/", StringComparison.OrdinalIgnoreCase)
            && Dist2(g, p) <= (long)RadiusGrid * RadiusGrid);
        lines.Add(evidence.Message);
        return ProbeResult.Pass(string.Join(Environment.NewLine, lines));
    }

    public ProbeResult Discover(ProbeContext ctx) => Validate(ctx);

    private static long Dist2(Vector2i a, Vector2i b)
    {
        long dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }
}
