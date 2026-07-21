using BubblesBot.Core.Game;
using BubblesBot.Research.Probing;

namespace BubblesBot.Research.Probes.Inventory;

/// <summary>
/// Player inventory panel: resolves the InventoryPanel -> PlayerInventory -> snapshot, and validates
/// address + item count (oracle-exact when present) plus per-item geometry. Migrated from
/// PlayerInventoryOracleTest. SKIPs when the inventory is closed.
/// </summary>
public sealed class PlayerInventoryProbe : IProbe
{
    public string Name => "inventory.player";
    public string Group => "inventory";
    public string Description => "Player inventory resolves; item count + item geometry sane.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var ui = ctx.Chain.IngameUi;
        if (ui == 0) return ProbeResult.Fail("IngameUi null");
        if (!ctx.Reader.TryReadStruct<nint>(ui + KnownOffsets.IngameUiElements.InventoryPanel, out var panel) || panel == 0)
            return ProbeResult.Skip("InventoryPanel null (inventory closed?)");
        if (!InventoryReader.TryGetPlayerInventory(ctx.Reader, panel, out var inv))
            return ProbeResult.Fail("could not resolve PlayerInventory from InventoryPanel");

        var snap = InventoryReader.TryReadInventory(ctx.Reader, inv);
        if (snap is null) return ProbeResult.Fail($"could not read inventory at 0x{(long)inv:X}");

        var addr = Check.Address(ctx, "inventory.player", snap.Address, "PlayerInventory", requireNonNull: true, a => Reads.Readable(ctx.Reader, a));
        var count = Check.Live(ctx, "inventory.itemcount", snap.ItemCount, "Inventory.ItemCount", 0, 500);

        var badGeom = snap.VisibleItems.Count(i => i.Width is < 1 or > 4 || i.Height is < 1 or > 4 || !Reads.Readable(ctx.Reader, i.ItemEntity));
        var items = badGeom == 0
            ? ProbeResult.Pass($"{snap.VisibleItems.Count} visible items, grid {snap.Size.X}x{snap.Size.Y}")
            : ProbeResult.Fail($"{badGeom}/{snap.VisibleItems.Count} items have bad geometry or unreadable entity");

        return ProbeResult.Combine(addr, count, items);
    }

    public ProbeResult Discover(ProbeContext ctx)
    {
        var ui = ctx.Chain.IngameUi;
        if (ui == 0 || !ctx.Reader.TryReadStruct<nint>(ui + KnownOffsets.IngameUiElements.InventoryPanel, out var panel) || panel == 0)
            return ProbeResult.Found("PlayerInventory", []);
        return Discovery.Pointer(ctx, panel, "inventory.player", 0x1000, "PlayerInventory");
    }
}
