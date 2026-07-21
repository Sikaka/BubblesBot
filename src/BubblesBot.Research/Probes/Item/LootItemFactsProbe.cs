using BubblesBot.Core;
using BubblesBot.Core.Snapshot;
using BubblesBot.Research.Probing;

namespace BubblesBot.Research.Probes.Item;

/// <summary>
/// Read-only capture of the exact fields consumed by value filtering. It is deliberately
/// useful without POEMCP: every observed ground item is recorded with raw metadata/art and
/// decoded name/quality/level/footprint details for later incident analysis.
/// </summary>
public sealed class LootItemFactsProbe : IProbe
{
    public string Name => "item.loot-facts";
    public string Group => "item";
    public string Description => "Ground-item names, art identity, gem/cluster fields, and inventory footprint.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var snapshot = new GameSnapshot(ctx.Reader, ctx.Chain.IngameData, ctx.Chain.IngameState,
            new WindowInfo(0, 0, 1920, 1080));
        var items = snapshot.GroundLabels.Where(x => x.IsItem).Take(100).ToArray();
        if (items.Length == 0) return ProbeResult.Skip("no ground-item labels currently in memory");

        var rows = items.Select(x =>
            $"id={x.EntityId} visible={x.IsLabelVisible} name='{x.ItemName}' base='{x.BaseName}' "
            + $"rarity={x.ItemRarity} identified={x.IsIdentified} ilvl={x.ItemLevel} "
            + $"gem={x.GemLevel} q={x.Quality} corrupted={x.IsCorrupted} stack={x.StackCount} cells={x.InventorySlots} "
            + $"grid={x.EntityGridPosition} dist={x.DistanceToPlayer:F1} onScreen={x.IsRectOnScreen} "
            + $"clusterPassives={x.ClusterPassiveCount} art='{x.ResourcePath}' path='{x.InnerItemPath}'");
        return ProbeResult.Pass($"{items.Length} item(s): " + string.Join(" || ", rows));
    }

    public ProbeResult Discover(ProbeContext ctx) => Validate(ctx);
}
