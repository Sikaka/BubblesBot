using BubblesBot.Core;
using BubblesBot.Core.Snapshot;
using BubblesBot.Research.Probing;

namespace BubblesBot.Research.Probes.Item;

/// <summary>Read-only proof for the exact occupied-cell signal used between Simulacrum waves.</summary>
public sealed class InventoryOccupancyProbe : IProbe
{
    public string Name => "item.inventory-occupancy";
    public string Group => "item";
    public string Description => "Visible player inventory item footprints and occupied-cell total.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var snapshot = new GameSnapshot(ctx.Reader, ctx.Chain.IngameData, ctx.Chain.IngameState,
            new WindowInfo(0, 0, 1920, 1080));
        var inventory = snapshot.Inventory;
        if (!inventory.IsOpen) return ProbeResult.Skip("inventory panel is closed");
        if (inventory.OccupiedCells is < 0 or > 60)
            return ProbeResult.Fail($"impossible occupied-cell total {inventory.OccupiedCells}/60");

        var rows = inventory.Items.Select(x =>
            $"{ShortName(x.Path)} stack={x.StackSize} size={x.Width}x{x.Height} cells={x.OccupiedCells}");
        return ProbeResult.Pass($"{inventory.Items.Count} item(s), {inventory.OccupiedCells}/60 cells: "
            + string.Join(" | ", rows));
    }

    public ProbeResult Discover(ProbeContext ctx) => Validate(ctx);

    private static string ShortName(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }
}
