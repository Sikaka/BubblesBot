using BubblesBot.Core;
using BubblesBot.Core.Game;
using BubblesBot.Research.Probing.Toolkit;

namespace BubblesBot.Research.Probing;

/// <summary>
/// <c>--discover-reaction</c> — finds the hostility ("Reaction") byte on the Positioned component
/// by diffing live memory between known-allied and known-hostile monsters.
///
/// <para>WHY: Monster-kind entities can be allies — the player's Blink/Mirror Arrow clones,
/// shrine-summoned skeletons, Innocence/Sin in the Kitava fight. <c>EntityCache</c> currently
/// guesses hostility from metadata paths (<c>EntityDispositionCatalog</c>), which cannot work in
/// general: a shrine-summoned skeleton shares the exact metadata path of a hostile one. ExileCore
/// derives <c>Entity.IsHostile</c> from a byte on Positioned; its offset for the current patch is
/// unknown to us, so we discover it empirically. Ground truth needs no oracle:
/// <c>/Monsters/Clone/</c> entities are definitionally the player's own, and everything else
/// Monster-kind (minus known-ambiguous species) is hostile.</para>
///
/// <para>Run it standing next to a live pack with Blink Arrow clones up. Optionally widen the ally
/// group with <c>--ally-fragment RaisedSkeletons</c> when the on-screen skeletons are KNOWN to be
/// allied (shrine-summoned). When the ally's path is unknown (persistent minion, spectre, …), run
/// <c>--discover-reaction --list</c> first: it dumps the distinct Monster-kind metadata paths near
/// the player so the operator can pick the fragment, then rerun with it.</para>
/// </summary>
public static class ReactionDiscovery
{
    // Positioned window to diff. GridPosition sits at +0x294 — the reaction byte has always lived
    // well below it (community tables put it near 0x1E0), so [0, 0x280) covers it without dragging
    // in the constantly-moving position fields that would poison the group-agreement stats.
    private const int Window = 0x280;

    // Below these counts a byte can separate the groups by coincidence too easily to be worth
    // printing; the operator instructions below tell them how to grow each group.
    private const int MinAllies = 2;
    private const int MinHostiles = 5;

    // Monster-kind species that are neither clean allies nor clean hostiles (invisible effect
    // daemons, player mirages, invulnerable observers, wildlife, friendly masters, …). Excluded
    // from the HOSTILE group so one mislabeled sample can't erase the real candidate.
    private static readonly string[] IgnoreFragments =
    [
        "/Monsters/Daemon/",
        "/Monsters/Mirage/",
        "/MavenBoss/",
        "Critter",
        "/Masters/",
        "/AnimatedItem/",
        "/Monsters/VolatileCore/",
        "/KitavaBoss/InnocenceSin/",
    ];

    private sealed record Sample(uint Id, string Metadata, byte[] Bytes);

    public static int Run(string[] args)
    {
        Console.WriteLine();
        Console.WriteLine("Reaction/hostility discovery — diffs Positioned bytes between allied and hostile monsters.");
        Console.WriteLine("No POEMCP needed. Summon Blink Arrow clones near a live pack before running.");
        Console.WriteLine();

        using var process = ProcessHandle.AttachToPoE();
        if (process is null) { Console.Error.WriteLine("No PoE process found."); return 1; }
        Console.WriteLine($"Attached to {process.ProcessName} (PID {process.ProcessId})");

        var reader = new MemoryReader(process);
        var chain = ChainResolver.Resolve(process, reader, args);
        if (chain is null || !chain.IsValid) { Console.Error.WriteLine("Could not resolve IngameState."); return 1; }
        Console.WriteLine($"IngameState: 0x{(long)chain.IngameState:X16} (via {chain.ResolvedVia})");

        // Blink/Mirror Arrow clones are always the player's own; extra fragments let the operator
        // vouch for entities only they can classify (e.g. shrine-summoned skeletons on screen NOW).
        var allyFragments = new List<string> { "/Monsters/Clone/" };
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--ally-fragment")
                allyFragments.Add(args[i + 1]);
        Console.WriteLine($"Ally fragments: {string.Join(", ", allyFragments)}");

        var listAddr = chain.EntityList;
        if (listAddr == 0) { Console.Error.WriteLine("EntityList pointer null — on a loading screen?"); return 1; }

        var traversal = EntityListReader.EnumerateEntityAddresses(reader, listAddr);
        Console.WriteLine($"{traversal.EntityAddresses.Count} entities ({traversal.BadReads} bad reads)");

        if (args.Contains("--list"))
            return ListMonsterPaths(reader, chain, traversal.EntityAddresses, allyFragments);

        var allies = new List<Sample>();
        var hostiles = new List<Sample>();
        var ignoredCount = 0;

        foreach (var addr in traversal.EntityAddresses)
        {
            var snap = EntityListReader.TryReadSnapshot(reader, addr);
            if (snap is null) continue;
            if (!snap.Components.TryGetValue("Positioned", out var positioned)) continue;

            var isAlly = allyFragments.Any(f => snap.Metadata.Contains(f, StringComparison.OrdinalIgnoreCase));
            if (!isAlly)
            {
                if (snap.Kind != EntityListReader.EntityKind.Monster) continue;
                if (IgnoreFragments.Any(f => snap.Metadata.Contains(f, StringComparison.OrdinalIgnoreCase)))
                {
                    ignoredCount++;
                    continue;
                }
                // Require alive when HP is readable — a corpse's reaction is not evidence.
                if (snap.Health is { Current: <= 0 }) continue;
            }

            var bytes = new byte[Window];
            if (reader.TryReadBytes(positioned, bytes.AsSpan()) != Window) continue;
            (isAlly ? allies : hostiles).Add(new Sample(snap.Id, snap.Metadata, bytes));
        }

        Console.WriteLine();
        Console.WriteLine($"ALLY group ({allies.Count}):");
        foreach (var s in allies)
            Console.WriteLine($"  id={s.Id,-8} {PathTail(s.Metadata)}");
        Console.WriteLine($"HOSTILE group ({hostiles.Count}), {ignoredCount} ambiguous species skipped:");
        foreach (var s in hostiles.Take(12))
            Console.WriteLine($"  id={s.Id,-8} {PathTail(s.Metadata)}");
        if (hostiles.Count > 12) Console.WriteLine($"  … and {hostiles.Count - 12} more");

        if (allies.Count < MinAllies || hostiles.Count < MinHostiles)
        {
            Console.WriteLine();
            Console.WriteLine($"Not enough samples: {allies.Count} ally / {hostiles.Count} hostile (need >= {MinAllies} / >= {MinHostiles}).");
            Console.WriteLine("Summon Blink Arrow clones near a pack of monsters, then rerun. Clones despawn after a few");
            Console.WriteLine("seconds — recast right before launching. If shrine-summoned skeletons you KNOW are allied");
            Console.WriteLine("are on screen, include them:  --discover-reaction --ally-fragment RaisedSkeletons");
            return 1;
        }

        // Group-separation sweep: a byte is the reaction flag if every ally reads one value and
        // every hostile another. Bytes that merely correlate in this one scene will show up too —
        // hence the per-candidate samples and the rerun advice at the end.
        var perfect = new List<(int Off, byte AllyVal, byte HostileVal)>();
        var strong = new List<(int Off, byte AllyVal, int AllyAgree, byte HostileVal, int HostileAgree)>();

        for (var off = 0; off < Window; off++)
        {
            var (allyModal, allyAgree) = Modal(allies, off);
            var (hostModal, hostAgree) = Modal(hostiles, off);
            if (allyModal == hostModal) continue;

            if (allyAgree == allies.Count && hostAgree == hostiles.Count)
                perfect.Add((off, allyModal, hostModal));
            else if (allyAgree * 10 >= allies.Count * 9 && hostAgree * 10 >= hostiles.Count * 9)
                strong.Add((off, allyModal, allyAgree, hostModal, hostAgree));
        }

        Console.WriteLine();
        Console.WriteLine($"PERFECT candidates ({perfect.Count}) — every ally shares one value, every hostile another:");
        if (perfect.Count == 0)
            Console.WriteLine("  none — check the group listing above for a mislabeled entity, or grow both groups.");
        foreach (var (off, allyVal, hostVal) in perfect)
        {
            Console.WriteLine($"  +0x{off:X3}: ally=0x{allyVal:X2} hostile=0x{hostVal:X2}");
            PrintSamples(allies, hostiles, off);
        }

        Console.WriteLine();
        Console.WriteLine($"STRONG candidates ({strong.Count}) — >= 90% agreement within each group, modal values differ:");
        if (strong.Count == 0) Console.WriteLine("  none");
        foreach (var (off, allyVal, allyAgree, hostVal, hostAgree) in strong.Take(30))
        {
            Console.WriteLine($"  +0x{off:X3}: ally=0x{allyVal:X2} ({allyAgree}/{allies.Count}) hostile=0x{hostVal:X2} ({hostAgree}/{hostiles.Count})");
            PrintSamples(allies, hostiles, off);
        }
        if (strong.Count > 30) Console.WriteLine($"  … and {strong.Count - 30} more");

        Console.WriteLine();
        Console.WriteLine("Commit the winning offset in src/BubblesBot.Core/Game/KnownOffsets.cs as PositionedComponent.Reaction");
        Console.WriteLine($"(currently 0x{KnownOffsets.PositionedComponent.Reaction:X}, unverified). Prefer a PERFECT candidate that survives a rerun");
        Console.WriteLine("in a different area — bytes that separate by coincidence won't reproduce.");
        return perfect.Count + strong.Count > 0 ? 0 : 2;
    }

    /// <summary>
    /// <c>--list</c> mode: dump the distinct metadata paths of Monster-kind entities with ids and
    /// grid distance to the player. For identifying an ally whose path isn't known in advance
    /// (persistent minion, spectre) — find it here, then rerun with <c>--ally-fragment</c>.
    /// </summary>
    private static int ListMonsterPaths(MemoryReader reader, ResolvedChain chain, IReadOnlyList<nint> addresses, List<string> allyFragments)
    {
        var playerSnap = chain.Player != 0 ? EntityListReader.TryReadSnapshot(reader, chain.Player) : null;
        var playerGrid = playerSnap?.GridPosition;
        Console.WriteLine(playerGrid is { } pg
            ? $"Player grid: {pg.X},{pg.Y}"
            : "Player grid unknown — distances omitted.");

        var byPath = new Dictionary<string, (int Count, int Alive, double NearestDist, uint NearestId)>(StringComparer.OrdinalIgnoreCase);
        foreach (var addr in addresses)
        {
            var snap = EntityListReader.TryReadSnapshot(reader, addr);
            if (snap is null || snap.Kind != EntityListReader.EntityKind.Monster) continue;

            var dist = double.MaxValue;
            if (playerGrid is { } p && snap.GridPosition is { } g)
                dist = Math.Sqrt((double)(g.X - p.X) * (g.X - p.X) + (double)(g.Y - p.Y) * (g.Y - p.Y));

            if (!byPath.TryGetValue(snap.Metadata, out var e))
                e = (0, 0, double.MaxValue, 0);
            var nearest = dist < e.NearestDist ? (dist, snap.Id) : (e.NearestDist, e.NearestId);
            byPath[snap.Metadata] = (e.Count + 1, e.Alive + (snap.IsAlive ? 1 : 0), nearest.Item1, nearest.Item2);
        }

        Console.WriteLine();
        Console.WriteLine($"{byPath.Count} distinct Monster-kind paths (nearest first):");
        foreach (var (path, e) in byPath.OrderBy(kv => kv.Value.NearestDist))
        {
            var tag = allyFragments.Any(f => path.Contains(f, StringComparison.OrdinalIgnoreCase)) ? "ALLY   "
                : IgnoreFragments.Any(f => path.Contains(f, StringComparison.OrdinalIgnoreCase)) ? "IGNORED"
                : "HOSTILE";
            var dist = e.NearestDist == double.MaxValue ? "   ?" : $"{e.NearestDist,4:F0}";
            Console.WriteLine($"  [{tag}] n={e.Count,-3} alive={e.Alive,-3} dist={dist} id={e.NearestId,-8} {path}");
        }

        Console.WriteLine();
        Console.WriteLine("Identify your summon's path above, then rerun with a distinguishing substring:");
        Console.WriteLine("  --discover-reaction --ally-fragment <fragment>");
        return 0;
    }

    /// <summary>Most common byte value at <paramref name="off"/> within a group, plus how many agree.</summary>
    private static (byte Value, int Count) Modal(List<Sample> group, int off)
    {
        Span<int> counts = stackalloc int[256];
        foreach (var s in group) counts[s.Bytes[off]]++;
        var best = 0;
        for (var v = 1; v < 256; v++)
            if (counts[v] > counts[best]) best = v;
        return ((byte)best, counts[best]);
    }

    private static void PrintSamples(List<Sample> allies, List<Sample> hostiles, int off)
    {
        foreach (var s in allies.Take(3))
            Console.WriteLine($"      ally    id={s.Id,-8} val=0x{s.Bytes[off]:X2}  {PathTail(s.Metadata)}");
        foreach (var s in hostiles.Take(3))
            Console.WriteLine($"      hostile id={s.Id,-8} val=0x{s.Bytes[off]:X2}  {PathTail(s.Metadata)}");
    }

    private static string PathTail(string metadata)
        => metadata.Length <= 44 ? metadata : "…" + metadata[^43..];
}
