using BubblesBot.Core;
using BubblesBot.Core.Game;
using BubblesBot.Research.Probing.Toolkit;

namespace BubblesBot.Research.Probing;

/// <summary>
/// <c>--probe-kosis</c> — dumps the raw memory reads BubblesBot uses to classify the Simulacrum
/// Kosis boss (<c>AfflictionDemonBoss*</c>), plus a normal hostile monster for comparison, so the
/// divergence from the POEMCP oracle (which reads the boss hostile/targetable/alive) can be traced
/// to a concrete byte/offset.
///
/// <para>Reads directly from a FRESH entity-list walk — the same traversal EntityCache uses — so it
/// exercises the live address, not a cached one. For each boss entity it prints: entity Address,
/// Entity.Flags (+0x8C) with bit decode, component addresses, the raw Positioned.Reaction byte
/// (+0x1E0) with a surrounding window, the raw Targetable bytes (+0x30/+0x32) with a window, and the
/// Life Health VitalStruct (cur/max). It then applies the exact EntityCache interpretation
/// (IsAllied = reaction != 0, IsTargetable = byte != 0, IsAlive = cur>0 && max>0) so the bug is
/// visible in-line.</para>
/// </summary>
public static class KosisBossProbe
{
    private const string BossFragment = "AfflictionDemonBoss";

    public static int Run(string[] args)
    {
        Console.WriteLine();
        Console.WriteLine("Kosis boss read probe — raw reaction/targetable/life bytes vs oracle semantics.");
        Console.WriteLine();

        using var process = ProcessHandle.AttachToPoE();
        if (process is null) { Console.Error.WriteLine("No PoE process found."); return 1; }
        Console.WriteLine($"Attached to {process.ProcessName} (PID {process.ProcessId})");

        var reader = new MemoryReader(process);
        var chain = ChainResolver.Resolve(process, reader, args);
        if (chain is null || !chain.IsValid) { Console.Error.WriteLine("Could not resolve IngameState."); return 1; }
        Console.WriteLine($"IngameState 0x{(long)chain.IngameState:X} IngameData 0x{(long)chain.IngameData:X} (via {chain.ResolvedVia})");

        var listAddr = chain.EntityList;
        if (listAddr == 0) { Console.Error.WriteLine("EntityList null."); return 1; }

        var traversal = EntityListReader.EnumerateEntityAddresses(reader, listAddr);
        Console.WriteLine($"Walked {traversal.EntityAddresses.Count} entity addresses ({traversal.BadReads} bad reads, safetyLimit={traversal.HitSafetyLimit}).");
        Console.WriteLine();

        var bosses = new List<(nint addr, uint id, string path)>();
        var normals = new List<(nint addr, uint id, string path)>();

        foreach (var addr in traversal.EntityAddresses)
        {
            if (!reader.TryReadStruct<uint>(addr + KnownOffsets.Entity.Id, out var id) || id == 0) continue;
            var path = EntityListReader.ReadEntityPath(reader, addr);
            if (string.IsNullOrEmpty(path)) continue;

            if (path.Contains(BossFragment, StringComparison.OrdinalIgnoreCase))
                bosses.Add((addr, id, path));
            else if (path.StartsWith("Metadata/Monsters/", StringComparison.OrdinalIgnoreCase)
                     && !path.Contains("/Daemon/", StringComparison.OrdinalIgnoreCase))
                normals.Add((addr, id, path));
        }

        Console.WriteLine($"==== BOSS ENTITIES (path contains \"{BossFragment}\"): {bosses.Count} ====");
        if (bosses.Count == 0)
            Console.WriteLine("  NONE found in live walk. If the oracle sees id 20778/20779, the live-list walk is diverging.");
        foreach (var (addr, id, path) in bosses)
            DumpEntity(reader, addr, id, path);

        Console.WriteLine();
        Console.WriteLine($"==== COMPARISON: up to 6 normal Monster-kind entities with valid Life ====");
        var shown = 0;
        foreach (var (addr, id, path) in normals)
        {
            // Prefer ones that read as a real live monster so the comparison is apples-to-apples.
            if (!reader.TryReadStruct<VitalStruct>(ComponentAddr(reader, addr, "Life") + KnownOffsets.LifeComponent.Health, out var hp) || !hp.LooksValid())
                continue;
            DumpEntity(reader, addr, id, path);
            if (++shown >= 6) break;
        }
        if (shown == 0)
            foreach (var (addr, id, path) in normals.Take(4))
                DumpEntity(reader, addr, id, path);

        return 0;
    }

    private static nint ComponentAddr(MemoryReader reader, nint entityAddr, string name)
    {
        var map = EntityComponents.ReadComponentMap(reader, entityAddr);
        return map.TryGetValue(name, out var a) ? a : 0;
    }

    private static void DumpEntity(MemoryReader reader, nint addr, uint id, string path)
    {
        Console.WriteLine();
        Console.WriteLine($"  ── id={id} @ 0x{(long)addr:X} ── {path}");

        // Entity.Flags (+0x8C) with the KnownOffsets.EntityFlags bit decode.
        if (reader.TryReadStruct<uint>(addr + KnownOffsets.Entity.Flags, out var flags))
        {
            Console.WriteLine($"     Entity.Flags(+0x8C) = 0x{flags:X8}  "
                + $"[Hostile(0x1)={(flags & KnownOffsets.EntityFlags.IsHostile) != 0} "
                + $"Targetable(0x2)={(flags & KnownOffsets.EntityFlags.IsTargetable) != 0} "
                + $"Alive(0x4)={(flags & KnownOffsets.EntityFlags.IsAlive) != 0} "
                + $"Valid(0x8)={(flags & KnownOffsets.EntityFlags.IsValid) != 0} "
                + $"Shown(0x400)={(flags & KnownOffsets.Entity.ShownFlagBit) != 0}]");
        }
        else Console.WriteLine("     Entity.Flags read FAILED");

        var map = EntityComponents.ReadComponentMap(reader, addr);
        map.TryGetValue("Positioned", out var pos);
        map.TryGetValue("Life", out var life);
        map.TryGetValue("Targetable", out var tgt);
        Console.WriteLine($"     components({map.Count}): Positioned=0x{(long)pos:X} Life=0x{(long)life:X} Targetable=0x{(long)tgt:X}");

        // Positioned.Reaction (+0x1E0) with window.
        if (pos != 0)
        {
            if (reader.TryReadStruct<byte>(pos + KnownOffsets.PositionedComponent.Reaction, out var reaction))
                Console.WriteLine($"     Positioned.Reaction(+0x1E0) = 0x{reaction:X2} ({reaction})  => bot IsAllied(reaction!=0) = {reaction != 0}");
            else Console.WriteLine("     Positioned.Reaction read FAILED");
            DumpWindow(reader, pos, 0x1D8, 0x24, "Positioned[+0x1D8..+0x1FC]");
        }

        // Targetable.IsTargetable (+0x30) / IsTargeted (+0x32) with window.
        if (tgt != 0)
        {
            reader.TryReadStruct<byte>(tgt + KnownOffsets.TargetableComponent.IsTargetable, out var t30);
            reader.TryReadStruct<byte>(tgt + KnownOffsets.TargetableComponent.IsTargeted, out var t32);
            Console.WriteLine($"     Targetable +0x30 = 0x{t30:X2} ({t30})  +0x32 = 0x{t32:X2} ({t32})  => bot IsTargetable(+0x30 != 0) = {t30 != 0}");
            DumpWindow(reader, tgt, 0x28, 0x20, "Targetable[+0x28..+0x48]");
        }
        else Console.WriteLine("     NO Targetable component");

        // Life Health VitalStruct.
        if (life != 0)
        {
            if (reader.TryReadStruct<VitalStruct>(life + KnownOffsets.LifeComponent.Health, out var hp))
                Console.WriteLine($"     Life.Health cur={hp.Current} max={hp.Max} looksValid={hp.LooksValid()}  => bot IsAlive(cur>0 && max>0) = {hp.Current > 0 && hp.Max > 0}");
            else Console.WriteLine("     Life.Health read FAILED");
        }
        else Console.WriteLine("     NO Life component");
    }

    private static void DumpWindow(MemoryReader reader, nint baseAddr, int start, int len, string label)
    {
        Span<byte> buf = stackalloc byte[len];
        if (reader.TryReadBytes(baseAddr + start, buf) != len) { Console.WriteLine($"       {label}: read failed"); return; }
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < len; i++)
        {
            if (i % 8 == 0) sb.Append($"\n       +0x{start + i:X3}: ");
            sb.Append($"{buf[i]:X2} ");
        }
        Console.WriteLine($"       {label}:{sb}");
    }
}
