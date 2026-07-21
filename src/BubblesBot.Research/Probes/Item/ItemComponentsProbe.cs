using BubblesBot.Core;
using BubblesBot.Core.Game;
using BubblesBot.Research.Probing;

namespace BubblesBot.Research.Probes.Item;

/// <summary>
/// Validates item-component struct layouts (Base / Sockets / Mods / Stack / Quality) against a REAL
/// item taken from the open player inventory -- no oracle hunt needed. Migrated from
/// ItemComponentTests + InventoryItemComponentsOracleTest. SKIPs if the inventory is closed/empty.
/// </summary>
public sealed class ItemComponentsProbe : IProbe
{
    public string Name => "item.components";
    public string Group => "item";
    public string Description => "Base/Sockets/Mods/Stack/Quality fields sane on an inventory item.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var ui = ctx.Chain.IngameUi;
        if (ui == 0 || !ctx.Reader.TryReadStruct<nint>(ui + KnownOffsets.IngameUiElements.InventoryPanel, out var panel) || panel == 0)
            return ProbeResult.Skip("inventory closed");
        if (!InventoryReader.TryGetPlayerInventory(ctx.Reader, panel, out var inv))
            return ProbeResult.Skip("PlayerInventory unresolved");
        var snap = InventoryReader.TryReadInventory(ctx.Reader, inv);
        if (snap is null || snap.VisibleItems.Count == 0)
            return ProbeResult.Skip("no items in inventory to inspect");

        // Pick the inventory item whose entity resolves the most components (most informative).
        var best = snap.VisibleItems
            .Select(i => (i.ItemEntity, Map: EntityComponents.ReadComponentMap(ctx.Reader, i.ItemEntity)))
            .Where(x => x.Map.Count > 0)
            .OrderByDescending(x => x.Map.Count)
            .FirstOrDefault();
        if (best.Map is null || best.Map.Count == 0)
            return ProbeResult.Fail("no inventory item resolved a component map (Entity.ComponentList drifted?)");

        var checks = new List<ProbeResult>();
        var map = best.Map;

        var baseNote = "";
        if (map.TryGetValue("Base", out var baseA) && ctx.Reader.TryReadStruct<BaseComponent>(baseA, out var bc))
        {
            var baseName = bc.ItemInfo == 0
                ? string.Empty
                : NativeString.Read(ctx.Reader, bc.ItemInfo + KnownOffsets.ItemInfo.BaseName);
            checks.Add(bc.ItemInfo != 0 && !string.IsNullOrWhiteSpace(baseName)
                ? ProbeResult.Pass($"Base: itemInfo=0x{(long)bc.ItemInfo:X} name='{baseName}'")
                : ProbeResult.Fail("Base.ItemInfo/BaseName did not resolve"));
            baseNote = $"  [Base Influence/Corrupted remain unverified raw bytes: {bc.Influence}/{bc.Corrupted}]";
        }

        var socketsNote = "";
        if (map.TryGetValue("Sockets", out var sockA))
        {
            socketsNote = ItemSocketsReader.TryReadComponent(ctx.Reader, sockA, out var sockets)
                ? $"  [Sockets validated: {sockets.Canonical}]"
                : "  [Sockets INVALID: strict current-build reader rejected component]";
        }

        if (map.TryGetValue("Mods", out var modsA) && ctx.Reader.TryReadStruct<ModsComponent>(modsA, out var mc))
            checks.Add(mc.ItemRarity is >= 0 and <= 9 && mc.Identified <= 1 && mc.ItemLevel is >= 0 and <= 100
                ? ProbeResult.Pass($"Mods: rarity={mc.ItemRarity} ident={mc.Identified} ilvl={mc.ItemLevel}")
                : ProbeResult.Fail($"Mods fields out of range (rarity={mc.ItemRarity} ident={mc.Identified} ilvl={mc.ItemLevel})"));

        if (map.TryGetValue("Stack", out var stackA) && ctx.Reader.TryReadStruct<StackComponent>(stackA, out var stc))
            checks.Add(stc.CurrentCount is >= 0 and < 100_000
                ? ProbeResult.Pass($"Stack: count={stc.CurrentCount}")
                : ProbeResult.Fail($"Stack.CurrentCount implausible ({stc.CurrentCount})"));

        if (map.TryGetValue("Quality", out var qualA) && ctx.Reader.TryReadStruct<QualityComponent>(qualA, out var qc))
            checks.Add(qc.CurrentQuality is >= 0 and <= 100
                ? ProbeResult.Pass($"Quality: {qc.CurrentQuality}%")
                : ProbeResult.Fail($"Quality out of range ({qc.CurrentQuality})"));

        if (checks.Count == 0)
            return ProbeResult.Pass($"item 0x{(long)best.ItemEntity:X} has no validatable components ({map.Count} total){baseNote}{socketsNote}");

        var combined = ProbeResult.Combine(checks.ToArray());
        return combined with { Message = combined.Message + baseNote + socketsNote };
    }

    public ProbeResult Discover(ProbeContext ctx)
        // Item component offsets are best rediscovered with --dump on a known item's component addr.
        => ProbeResult.Found("item.components", []);
}
