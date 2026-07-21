using BubblesBot.Core;
using BubblesBot.Core.Game;
using BubblesBot.Research.Probing.Toolkit;

namespace BubblesBot.Research.Probing;

/// <summary>
/// <c>--dump-entity-buffs</c> — dumps the named buff list of every Monster-kind entity near the
/// player.
///
/// <para>WHY: dormant/unspawned monsters (Vaal constructs, submerged water elementals) and
/// essence-imprisoned monsters read as alive + targetable in memory but cannot be fought until
/// they activate. ExileCore reads these states as named buffs on the entity — expected ids are
/// "hidden_monster" (dormant) and "frozen_in_time" (essence) — but we want the EMPIRICAL list for
/// the current patch. These states are temporary (essence mobs become valid on release), so the
/// bot must read them live per tick, never cache or blacklist; this tool proves the read works
/// before Core grows an entity-side buff surface.</para>
///
/// <para>The player-side reader (<c>BuffsView</c>) is validated but player-plumbed — its ctor is
/// internal, reachable only through <c>PlayerView</c> — so its offset walk is replicated here
/// verbatim against any entity's "Buffs" component (same offsets, same sanity bounds). If
/// <c>BuffsView</c> changes, mirror it here. If the layout does NOT transfer to monsters, the
/// summary says so loudly and hex-dumps a few Buffs components for manual inspection.</para>
/// </summary>
public static class EntityBuffsDump
{
    // "Near the player" — generous slice of the network bubble; far entities despawn/pause and
    // their buff vectors go stale anyway.
    private const int MaxGridDistance = 120;

    // Substrings that mark a buff as a candidate target-validity state — flagged in the output
    // so they jump out of the noise (auras, charges, map-mod buffs, …).
    private static readonly string[] StateFragments = ["hidden", "frozen", "invuln", "dormant"];

    private sealed record BuffRead(string Name, int Charges, float Timer, float MaxTime);

    public static int Run(string[] args)
    {
        Console.WriteLine();
        Console.WriteLine("Entity buff dump — reads the Buffs component of every Monster-kind entity near the player.");
        Console.WriteLine("Best run with dormant/essence monsters on screen (Vaal ruins, essence packs).");
        Console.WriteLine();

        using var process = ProcessHandle.AttachToPoE();
        if (process is null) { Console.Error.WriteLine("No PoE process found."); return 1; }
        Console.WriteLine($"Attached to {process.ProcessName} (PID {process.ProcessId})");

        var reader = new MemoryReader(process);
        var chain = ChainResolver.Resolve(process, reader, args);
        if (chain is null || !chain.IsValid) { Console.Error.WriteLine("Could not resolve IngameState."); return 1; }
        Console.WriteLine($"IngameState: 0x{(long)chain.IngameState:X16} (via {chain.ResolvedVia})");

        var playerSnap = chain.Player != 0 ? EntityListReader.TryReadSnapshot(reader, chain.Player) : null;
        var playerGrid = playerSnap?.GridPosition;
        Console.WriteLine(playerGrid is { } pg
            ? $"Player grid: {pg.X},{pg.Y} (filtering to {MaxGridDistance} grid)"
            : "Player grid unreadable — distance filter disabled, dumping ALL monsters.");

        var listAddr = chain.EntityList;
        if (listAddr == 0) { Console.Error.WriteLine("EntityList pointer null — on a loading screen?"); return 1; }

        var traversal = EntityListReader.EnumerateEntityAddresses(reader, listAddr);
        Console.WriteLine($"{traversal.EntityAddresses.Count} entities ({traversal.BadReads} bad reads)");
        Console.WriteLine();

        var rows = new List<(double Dist, string Text)>();
        var noBuffsComp = new List<string>();
        var buffsCompAddrs = new List<nint>();   // for the layout-mismatch hex dumps
        int monstersInRange = 0, totalNamed = 0, validNames = 0, garbageNames = 0, unreadableNames = 0;

        foreach (var addr in traversal.EntityAddresses)
        {
            var snap = EntityListReader.TryReadSnapshot(reader, addr);
            if (snap is null || snap.Kind != EntityListReader.EntityKind.Monster) continue;

            double dist = double.NaN;
            if (playerGrid is { } p)
            {
                if (snap.GridPosition is not { } g) continue; // no Positioned grid — can't range-gate it
                dist = Math.Sqrt((double)(g.X - p.X) * (g.X - p.X) + (double)(g.Y - p.Y) * (g.Y - p.Y));
                if (dist > MaxGridDistance) continue;
            }
            monstersInRange++;

            var hp = snap.Health is { } h ? $"{h.Current}/{h.Max}" : "?";
            var targetable = snap.IsTargetable is { } t ? (t ? "yes" : "no") : "?";
            var distText = double.IsNaN(dist) ? "  ?" : $"{dist,3:F0}";
            var header = $"id={snap.Id,-8} alive={(snap.IsAlive ? "yes" : "no "),-3} hp={hp,-11} targetable={targetable,-3} dist={distText}  {PathTail(snap.Metadata)}";

            if (!snap.Components.TryGetValue("Buffs", out var buffsComp))
            {
                noBuffsComp.Add(header);
                continue;
            }
            buffsCompAddrs.Add(buffsComp);

            var (buffs, byteLen, error) = ReadBuffList(reader, buffsComp);
            string body;
            if (error is not null)
            {
                body = $"    buffs: READ FAIL ({error})";
            }
            else if (buffs!.Count == 0)
            {
                body = $"    buffs(0, {byteLen}B): (none)";
            }
            else
            {
                var parts = new List<string>(buffs.Count);
                foreach (var b in buffs)
                {
                    totalNamed++;
                    if (b.Name == "?") unreadableNames++;
                    else if (LooksLikeBuffId(b.Name)) validNames++;
                    else garbageNames++;

                    var flagged = StateFragments.Any(f => b.Name.Contains(f, StringComparison.OrdinalIgnoreCase));
                    parts.Add(flagged
                        ? $"[!STATE] {b.Name} (t={b.Timer:F1}/{b.MaxTime:F1} c={b.Charges})"
                        : b.Name);
                }
                body = $"    buffs({buffs.Count}, {byteLen}B): {string.Join(", ", parts)}";
            }
            rows.Add((double.IsNaN(dist) ? double.MaxValue : dist, header + Environment.NewLine + body));
        }

        foreach (var (_, text) in rows.OrderBy(r => r.Dist))
            Console.WriteLine(text);

        Console.WriteLine();
        Console.WriteLine($"Monsters without a Buffs component ({noBuffsComp.Count}):");
        if (noBuffsComp.Count == 0) Console.WriteLine("  none");
        foreach (var line in noBuffsComp) Console.WriteLine($"  {line}");

        Console.WriteLine();
        Console.WriteLine($"Summary: {monstersInRange} monsters in range, {rows.Count} with Buffs component; " +
                          $"{totalNamed} buff entries — {validNames} valid names, {garbageNames} garbage, {unreadableNames} unreadable.");

        // Layout-transfer verdict. Empty vectors prove nothing; judge only entities that produced
        // entries. Garbage-dominated names mean the player-validated offsets do NOT hold on
        // monsters — dump raw component heads so the real vector offset can be eyeballed.
        if (totalNamed > 0 && validNames * 2 < totalNamed)
        {
            Console.WriteLine();
            Console.WriteLine("!!! LAYOUT MISMATCH: most buff names read as garbage — BuffsComponent.Buffs (+0x" +
                              $"{KnownOffsets.BuffsComponent.Buffs:X}) / Buff.* offsets likely do NOT transfer to monsters.");
            Console.WriteLine("!!! Raw Buffs component heads for manual inspection:");
            foreach (var comp in buffsCompAddrs.Take(3))
            {
                Console.WriteLine();
                Console.Write(MemDump.Window(reader, comp, 0x40));
            }
            return 2;
        }

        if (monstersInRange == 0)
            Console.WriteLine("No monsters in range — stand near a pack (ideally with dormant/essence mobs) and rerun.");
        else if (totalNamed == 0)
            Console.WriteLine("All buff vectors empty — plausible for calm monsters, but rerun next to essence/Vaal mobs to be sure.");

        return 0;
    }

    /// <summary>
    /// The exact walk <c>BuffsView</c> performs (NativePtrArray of buff pointers at
    /// BuffsComponent+0x160; per-buff name via BuffDatPtr → +0x0 → UTF-16 id), with its sanity
    /// bounds. Kept as a local copy because the Core ctor is internal/player-plumbed.
    /// </summary>
    private static (List<BuffRead>? Buffs, long ByteLen, string? Error) ReadBuffList(MemoryReader reader, nint buffsComp)
    {
        if (!reader.TryReadStruct<nint>(buffsComp + KnownOffsets.BuffsComponent.Buffs, out var begin) || begin == 0)
            return (null, 0, "vector begin null/unreadable");
        if (!reader.TryReadStruct<nint>(buffsComp + KnownOffsets.BuffsComponent.Buffs + 8, out var end))
            return (null, 0, "vector end unreadable");

        var byteLen = (long)end - (long)begin;
        if (byteLen < 0 || byteLen > 128 * 8)
            return (null, byteLen, $"implausible vector length {byteLen}B");

        var count = (int)(byteLen / 8);
        var list = new List<BuffRead>(count);
        for (var i = 0; i < count; i++)
        {
            if (!reader.TryReadStruct<nint>(begin + i * 8, out var buffPtr) || buffPtr == 0) continue;

            reader.TryReadStruct<nint>(buffPtr + KnownOffsets.Buff.BuffDatPtr, out var datPtr);
            reader.TryReadStruct<float>(buffPtr + KnownOffsets.Buff.MaxTime, out var maxTime);
            reader.TryReadStruct<float>(buffPtr + KnownOffsets.Buff.Timer, out var timer);
            reader.TryReadStruct<ushort>(buffPtr + KnownOffsets.Buff.Charges, out var charges);

            var name = "?";
            if (datPtr != 0 && reader.TryReadStruct<nint>(datPtr, out var namePtr) && namePtr != 0)
                try { name = reader.ReadStringUtf16(namePtr, maxChars: 96); }
                catch { name = "?"; }

            list.Add(new BuffRead(name, charges, timer, maxTime));
        }
        return (list, byteLen, null);
    }

    /// <summary>PoE buff ids are snake_case ASCII — anything else means we walked the wrong pointer.</summary>
    private static bool LooksLikeBuffId(string name)
    {
        if (name.Length is < 2 or > 96) return false;
        foreach (var c in name)
            if (!char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-' && c != ' ') return false;
        return true;
    }

    private static string PathTail(string metadata)
        => metadata.Length <= 44 ? metadata : "…" + metadata[^43..];
}
