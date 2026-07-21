using BubblesBot.Core;
using BubblesBot.Core.Game;
using BubblesBot.Research.Probing;
using BubblesBot.Research.Probing.Toolkit;

namespace BubblesBot.Research.Probes.Item;

/// <summary>
/// Decodes the ELEMENTS of ModsComponent.ItemStats — the per-item flattened stat list (map mods, gear
/// affixes). Decoded live 2026-07-14 against a T16 rare map's tooltip: each record is 8 bytes,
/// <c>(int32 Id, int32 Value)</c>, sorted ascending by Id (see KnownOffsets.ItemStatRecord).
/// Tooltip magnitudes appear verbatim as Values; "reduced X" lines store negative values.
///
/// <para>Validate enforces the structural invariants (8-byte divisibility, strictly ascending
/// ids, bounded values) across every inventory item with a Mods component, and echoes a map
/// item's decoded pairs for eyeball comparison against its tooltip. Discover prints the full
/// (id, value) table per item, cross-checks id→RawName via the oracle when POEMCP is up
/// (<c>--poemcp</c>), and dumps the Map/MapKey component with tier candidates (baseline fact
/// <c>item.map.tier</c> highlights the match). SKIPs when the inventory is closed.</para>
/// </summary>
public sealed class ItemModsProbe : IProbe
{
    public string Name => "item.mods";
    public string Group => "item";
    public string Description => "ItemStats records decode as sorted (int32 id, int32 value) pairs.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var items = ResolveItems(ctx, out var why);
        if (items is null) return ProbeResult.Skip(why);

        int arrays = 0, records = 0;
        var problems = new List<string>();
        var sample = "";
        foreach (var it in items)
        {
            var pairs = ReadRecords(ctx, it.Comps, out var problem);
            if (problem is not null) { problems.Add($"{ShortName(it.Path)}: {problem}"); continue; }
            if (pairs is null) continue; // no Mods component / empty array — nothing to check
            arrays++;
            records += pairs.Count;
            if (sample.Length == 0 && IsMapItem(it.Path, it.Comps))
                sample = $"; {ShortName(it.Path)}: {string.Join(" ", pairs.Select(p => $"{p.Id}={p.Value}"))}";
        }

        if (problems.Count > 0)
            return ProbeResult.Fail($"{problems.Count} malformed ItemStats array(s) — layout drifted? {string.Join(" | ", problems.Take(4))}");
        if (arrays == 0)
            return ProbeResult.Skip("no inventory item carried a non-empty ItemStats array");
        return ProbeResult.Pass($"{records} records across {arrays} item(s), all sorted+bounded{sample}");
    }

    public ProbeResult Discover(ProbeContext ctx)
    {
        var items = ResolveItems(ctx, out var why);
        if (items is null) return ProbeResult.Skip(why);

        foreach (var it in items.Take(6))
        {
            Console.WriteLine($"\n== item 0x{(long)it.Entity:X}  {it.Path}");
            Console.WriteLine($"   components: {string.Join(",", it.Comps.Keys.OrderBy(k => k, StringComparer.Ordinal))}");

            var pairs = ReadRecords(ctx, it.Comps, out var problem);
            if (problem is not null) Console.WriteLine($"   MALFORMED: {problem}");
            if (pairs is not null)
            {
                foreach (var p in pairs)
                    Console.WriteLine($"   id={p.Id,-6} value={p.Value}");
            }
            if (it.Comps.ContainsKey("Mods")) CrossCheckOracle(ctx, it.Entity);

            foreach (var key in new[] { "Map", "MapKey" })
                if (it.Comps.TryGetValue(key, out var mapAddr))
                    DumpMapComponent(ctx, key, mapAddr);
        }

        // Layout is committed constants now; discovery output is for the id→meaning catalog.
        return ProbeResult.Found("item.mods", []);
    }

    // ---- record decode ----

    private readonly record struct Record(int Id, int Value);

    /// <summary>
    /// Null when the item has no Mods component or an empty array. <paramref name="problem"/>
    /// is set when the array exists but violates the committed layout's invariants.
    /// </summary>
    private static List<Record>? ReadRecords(ProbeContext ctx, Dictionary<string, nint> comps, out string? problem)
    {
        problem = null;
        if (!comps.TryGetValue("Mods", out var modsAddr)) return null;
        if (!ctx.Reader.TryReadStruct<ModsComponent>(modsAddr, out var mc)) return null;
        var bytes = (long)mc.ItemStats.Last - (long)mc.ItemStats.First;
        if (bytes == 0) return null;
        if (bytes < 0 || bytes > 64 * KnownOffsets.ItemStatRecord.Stride || bytes % KnownOffsets.ItemStatRecord.Stride != 0)
        { problem = $"array size 0x{bytes:X} not a sane multiple of stride 0x{KnownOffsets.ItemStatRecord.Stride:X}"; return null; }

        var n = (int)(bytes / KnownOffsets.ItemStatRecord.Stride);
        var list = new List<Record>(n);
        var prevId = int.MinValue;
        for (var i = 0; i < n; i++)
        {
            var rec = mc.ItemStats.First + i * KnownOffsets.ItemStatRecord.Stride;
            if (!ctx.Reader.TryReadStruct<int>(rec + KnownOffsets.ItemStatRecord.Id, out var id)
                || !ctx.Reader.TryReadStruct<int>(rec + KnownOffsets.ItemStatRecord.Value, out var value))
            { problem = $"record {i} unreadable"; return null; }
            if (id <= prevId) { problem = $"ids not strictly ascending at record {i} ({prevId} -> {id})"; return null; }
            if (id is <= 0 or > 1_000_000) { problem = $"record {i} id {id} out of range"; return null; }
            if (value is < -10_000 or > 10_000) { problem = $"record {i} value {value} out of range"; return null; }
            prevId = id;
            list.Add(new Record(id, value));
        }
        return list;
    }

    /// <summary>Ask ExileCore for the same item's mod RawNames — maps our numeric ids to names.</summary>
    private static void CrossCheckOracle(ProbeContext ctx, nint entity)
    {
        if (!ctx.Oracle.IsAvailable) return;
        var expr =
            "var inv = IngameState.IngameUi.InventoryPanel[ExileCore.Shared.Enums.InventoryIndex.PlayerInventory];\n" +
            "var it = inv.VisibleInventoryItems.Select(i => i.Item).FirstOrDefault(e => e.Address == 0x" + ((long)entity).ToString("X") + ");\n" +
            "var m = it == null ? null : it.GetComponent<ExileCore.PoEMemory.Components.Mods>();\n" +
            "var ls = it == null ? null : it.GetComponent<ExileCore.PoEMemory.Components.LocalStats>();\n" +
            "m == null ? \"notfound\" : string.Join(\"|\", m.ItemMods.Select(x => x.RawName + \"=\" + x.Value1 + \",\" + x.Value2))" +
            " + \"  ## human=\" + string.Join(\";\", m.HumanStats)" +
            " + \"  ## enchant=\" + string.Join(\";\", m.EnchantedStats)" +
            " + \"  ## local=\" + (ls == null ? \"\" : string.Join(\";\", ls.StatDictionary.Select(x => x.Key + \"=\" + x.Value)))";
        Console.WriteLine(ctx.Oracle.TryEval(expr, out var truth)
            ? $"   oracle ItemMods: {truth}"
            : "   oracle eval failed (PoE unfocused pauses POEMCP)");
    }

    private static void DumpMapComponent(ProbeContext ctx, string compName, nint mapAddr)
    {
        var r = ctx.Reader;
        var hasTierFact = ctx.Facts.TryGetInt("item.map.tier", out var tierFact);
        Console.WriteLine($"   {compName} component @ 0x{(long)mapAddr:X} — int candidates in tier range [1..17]:");
        for (var off = 0x8; off + 4 <= 0x100; off += 4)
        {
            if (!r.TryReadStruct<int>(mapAddr + off, out var v) || v is < 1 or > 17) continue;
            Console.WriteLine($"      +0x{off:X}={v}{(hasTierFact && v == tierFact ? "   <-- matches item.map.tier fact" : "")}");
        }
        // Tier may live behind an inner pointer (ExileCore walks Map -> inner -> area row).
        for (var qoff = 0x8; qoff + 8 <= 0x40; qoff += 8)
        {
            if (!r.TryReadStruct<nint>(mapAddr + qoff, out var inner) || !Plausible(inner)) continue;
            for (var off = 0; off + 4 <= 0x48; off += 4)
            {
                if (!r.TryReadStruct<int>(inner + off, out var v) || v is < 1 or > 17) continue;
                Console.WriteLine($"      [+0x{qoff:X}]->+0x{off:X}={v}{(hasTierFact && v == tierFact ? "   <-- matches item.map.tier fact" : "")}");
            }
        }
        Console.WriteLine(MemDump.Window(r, mapAddr, 0x100));
    }

    private static bool IsMapItem(string path, Dictionary<string, nint> comps)
        => comps.ContainsKey("Map") || comps.ContainsKey("MapKey")
           || path.Contains("/Maps/", StringComparison.Ordinal);

    private static string ShortName(string path)
    {
        var i = path.LastIndexOf('/');
        return i >= 0 ? path[(i + 1)..] : path;
    }

    // ---- inventory plumbing ----

    private static List<(nint Entity, string Path, Dictionary<string, nint> Comps)>? ResolveItems(
        ProbeContext ctx, out string why)
    {
        why = "";
        var ui = ctx.Chain.IngameUi;
        if (ui == 0) { why = "IngameUi null"; return null; }
        if (!ctx.Reader.TryReadStruct<nint>(ui + KnownOffsets.IngameUiElements.InventoryPanel, out var panel) || panel == 0)
        { why = "inventory closed"; return null; }
        if (!InventoryReader.TryGetPlayerInventory(ctx.Reader, panel, out var inv))
        { why = "PlayerInventory unresolved"; return null; }
        var snap = InventoryReader.TryReadInventory(ctx.Reader, inv);
        if (snap is null || snap.VisibleItems.Count == 0) { why = "no items in inventory"; return null; }

        var all = snap.VisibleItems
            .Select(i => (Entity: i.ItemEntity,
                          Path: EntityListReader.ReadEntityPath(ctx.Reader, i.ItemEntity),
                          Comps: EntityComponents.ReadComponentMap(ctx.Reader, i.ItemEntity)))
            .Where(x => x.Comps.Count > 0)
            .OrderByDescending(x => IsMapItem(x.Path, x.Comps))
            .ThenByDescending(x => x.Comps.ContainsKey("Mods"))
            .ToList();
        if (all.Count == 0) { why = "no inventory item resolved a component map"; return null; }
        return all;
    }

    private static bool Plausible(nint p)
    {
        var v = (long)p;
        return v > 0x10000 && v < 0x7FFF_FFFF_FFFF;
    }
}
