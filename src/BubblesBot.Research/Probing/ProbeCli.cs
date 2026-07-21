using System.Globalization;
using System.Text;
using System.Diagnostics;
using BubblesBot.Core;
using BubblesBot.Core.Pathfinding;
using BubblesBot.Core.Snapshot;
using BubblesBot.Research.Probing.Oracle;
using BubblesBot.Research.Probing.Toolkit;

namespace BubblesBot.Research.Probing;

/// <summary>
/// Entry point for the probe suite. Dispatched from <c>Program.cs</c> for
/// <c>--list / --probe / --sweep / --baseline / --dump</c>. Resolves the chain once (no ExileAPI
/// required), loads the gitignored baseline, optionally connects the oracle (<c>--poemcp</c>), then
/// hands a shared <see cref="ProbeContext"/> to <see cref="ProbeRunner"/>.
/// </summary>
public static class ProbeCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        await Task.CompletedTask; // async signature kept for future oracle/IO work

        // These need neither the live game nor the resolved chain.
        if (HasFlag(args, "--list")) return List();
        if (HasFlag(args, "--baseline")) return RunBaseline(args);

        using var process = ProcessHandle.AttachToPoE();
        if (process is null) { Console.Error.WriteLine("PoE is not running."); return 1; }
        var reader = new MemoryReader(process);
        Console.WriteLine($"Attached to {process.ProcessName} (PID {process.ProcessId})");

        if (TryGetHex(args, "--dump", out var dumpAddr))
        {
            Console.WriteLine(MemDump.Window(reader, dumpAddr, GetInt(args, "--dump-len") ?? 0x100));
            return 0;
        }

        if (HasFlag(args, "--terrain"))
            return RunTerrain(process, reader, args);

        if (HasFlag(args, "--path"))
            return RunPath(process, reader, args);

        // --- probe / sweep: need baseline + chain + oracle ---
        var facts = Baseline.Load();
        Console.WriteLine($"baseline: {facts.Path} ({facts.Facts.Count} facts" +
                          $"{(facts.CapturedUtc is null ? "" : $", captured {facts.CapturedUtc}")})");

        IGameOracle oracle = BuildOracle(args);
        try
        {
            Console.WriteLine($"oracle:   {(oracle.IsAvailable ? "POEMCP connected (accelerate + cross-check)" : "none (independent mode)")}");

            var chain = ChainResolver.Resolve(process, reader, args);
            if (chain is null || !chain.IsValid)
            {
                Console.Error.WriteLine("Could not resolve the pointer chain. Pass --hp <currentHp> to value-scan.");
                return 1;
            }
            Console.WriteLine($"chain via {chain.ResolvedVia}: IngameState=0x{chain.IngameState:X} " +
                              $"IngameData=0x{chain.IngameData:X} Player=0x{chain.Player:X}");

            var gameBuild = FileVersionInfo.GetVersionInfo(process.ModulePath).FileVersion ?? "unknown";
            var ctx = new ProbeContext
            {
                Reader = reader,
                Chain = chain,
                Facts = facts,
                Oracle = oracle,
                GameBuild = gameBuild,
                Arguments = args,
            };

            if (TryGetStr(args, "--probe", out var name))
            {
                var probe = ProbeRegistry.ByName(name);
                if (probe is null)
                {
                    Console.Error.WriteLine($"No probe '{name}'. Known: {string.Join(", ", ProbeRegistry.All.Select(p => p.Name))}");
                    return 1;
                }
                var st = ProbeRunner.RunOne(probe, ctx, discover: HasFlag(args, "--discover"));
                return st is ProbeStatus.Fail or ProbeStatus.Conflict ? 2 : 0;
            }

            if (HasFlag(args, "--sweep"))
            {
                var group = GetStr(args, "--group");
                var probes = group is null ? ProbeRegistry.All : ProbeRegistry.ByGroup(group).ToList();
                if (probes.Count == 0) { Console.Error.WriteLine($"No probes{(group is null ? "" : $" in group '{group}'")}."); return 1; }
                return ProbeRunner.Sweep(probes, ctx).ExitCode;
            }

            Usage();
            return 0;
        }
        finally
        {
            (oracle as IDisposable)?.Dispose();
        }
    }

    // ---- subcommands ----

    private static int List()
    {
        Console.WriteLine();
        Console.WriteLine($"{ProbeRegistry.All.Count} probe(s):");
        foreach (var g in ProbeRegistry.All.GroupBy(p => p.Group))
        {
            Console.WriteLine($"  [{g.Key}]");
            foreach (var p in g)
            {
                var facts = p.RequiredFacts.Count == 0 ? "" : $"  (facts: {string.Join(", ", p.RequiredFacts)})";
                Console.WriteLine($"    {p.Name,-22} {p.Description}{facts}");
            }
        }
        return 0;
    }

    private static int RunBaseline(string[] args)
    {
        var sub = SubcommandAfter(args, "--baseline") ?? "show";
        var baseline = Baseline.Load();

        switch (sub)
        {
            case "show":
                Console.WriteLine($"baseline: {baseline.Path}");
                Console.WriteLine($"  capturedUtc: {baseline.CapturedUtc ?? "(never)"}");
                if (baseline.Facts.Count == 0) Console.WriteLine("  (no facts)");
                foreach (var (k, v) in baseline.Facts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    Console.WriteLine($"  {k} = {v}");
                return 0;

            case "set":
                var pairs = args.Where(a => a.Contains('=')).ToList();
                if (pairs.Count == 0) { Console.Error.WriteLine("Usage: --baseline set key=value [key=value ...]"); return 1; }
                foreach (var p in pairs)
                {
                    var i = p.IndexOf('=');
                    baseline.Set(p[..i], p[(i + 1)..]);
                    Console.WriteLine($"  set {p[..i]} = {p[(i + 1)..]}");
                }
                baseline.CapturedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                baseline.Save();
                return 0;

            case "capture":
                return HasFlag(args, "--from-poemcp")
                    ? CaptureFromOracle(baseline)
                    : CaptureInteractive(baseline);

            default:
                Console.Error.WriteLine($"Unknown baseline subcommand '{sub}'. Use show | set | capture.");
                return 1;
        }
    }

    private static int CaptureFromOracle(Baseline baseline)
    {
        var oracle = PoemcpOracle.TryConnect();
        if (oracle is null) { Console.Error.WriteLine("POEMCP not reachable; cannot --from-poemcp."); return 1; }
        try
        {
            var n = 0;
            foreach (var key in OracleKeys.Stable)
            {
                if (!oracle.TryGetValue(key, out var v) || v.Length == 0) continue;
                baseline.Set(key, v);
                Console.WriteLine($"  {key} = {v}");
                n++;
            }
            baseline.CapturedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            baseline.Note = "captured from POEMCP";
            baseline.Save();
            Console.WriteLine($"Captured {n} fact(s) -> {baseline.Path}");
            return 0;
        }
        finally { oracle.Dispose(); }
    }

    private static int CaptureInteractive(Baseline baseline)
    {
        Console.WriteLine("Enter ground-truth facts (blank = skip). Keys map to probe checks:");
        foreach (var key in OracleKeys.Stable)
        {
            Console.Write($"  {key}{(baseline.Facts.TryGetValue(key, out var cur) ? $" [{cur}]" : "")}: ");
            var line = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(line)) baseline.Set(key, line.Trim());
        }
        baseline.CapturedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        baseline.Note = "captured interactively";
        baseline.Save();
        Console.WriteLine($"Saved -> {baseline.Path}");
        return 0;
    }

    /// <summary>
    /// ASCII-render the walkable + targeting terrain layers around a grid cell, so we can see
    /// whether a spot the bot got stuck on reads as walkable (digit), a jumpable gap ('+', pf=0
    /// tgt&gt;0 — should produce a Blink), or a solid wall ('#', pf=0 tgt=0). Usage:
    /// <c>--terrain --cx N --cy N [--radius N]</c>.
    /// </summary>
    private static int RunTerrain(ProcessHandle process, MemoryReader reader, string[] args)
    {
        var chain = ChainResolver.Resolve(process, reader, args);
        if (chain is null || !chain.IsValid) { Console.Error.WriteLine("could not resolve chain (pass --hp to value-scan)"); return 1; }
        var snap = new GameSnapshot(reader, chain.IngameData, chain.IngameState, new WindowInfo(0, 0, 1920, 1080));
        var nav = snap.Nav;
        if (!nav.IsAvailable) { Console.Error.WriteLine("terrain not loaded (in a loading screen?)"); return 1; }

        var cx = GetInt(args, "--cx");
        var cy = GetInt(args, "--cy");
        if (cx is null || cy is null) { Console.Error.WriteLine("usage: --terrain --cx N --cy N [--radius N]"); return 1; }
        var r = Math.Clamp(GetInt(args, "--radius") ?? 18, 4, 60);

        Console.WriteLine($"terrain grid {nav.Width}x{nav.Height}; window center=({cx},{cy}) radius={r}");
        Console.WriteLine("legend: 1-5 walkable (higher=cheaper)  '+' jumpable gap (pf=0,tgt>0)  '#' wall (pf=0,tgt=0)  ' ' out-of-bounds  'P' center");
        var sb = new StringBuilder();
        for (var y = cy.Value - r; y <= cy.Value + r; y++)
        {
            sb.Clear();
            for (var x = cx.Value - r; x <= cx.Value + r; x++)
            {
                if (x < 0 || y < 0 || x >= nav.Width || y >= nav.Height) { sb.Append(' '); continue; }
                if (x == cx.Value && y == cy.Value) { sb.Append('P'); continue; }
                var pf = nav.Walkable(x, y);
                if (pf > 0) sb.Append((char)('0' + Math.Clamp(pf, 0, 9)));
                else sb.Append(nav.Targeting(x, y) > 0 ? '+' : '#');
            }
            Console.WriteLine(sb.ToString());
        }
        return 0;
    }

    /// <summary>
    /// Run the real A* pathfinder (with the gap plan) offline against live static terrain and print
    /// the resulting steps + blink count. Deterministic nav diagnosis with no input/foreground.
    /// Usage: <c>--path --cx N --cy N --gx N --gy N [--range N] [--no-gap]</c>.
    /// </summary>
    private static int RunPath(ProcessHandle process, MemoryReader reader, string[] args)
    {
        var chain = ChainResolver.Resolve(process, reader, args);
        if (chain is null || !chain.IsValid) { Console.Error.WriteLine("could not resolve chain"); return 1; }
        var snap = new GameSnapshot(reader, chain.IngameData, chain.IngameState, new WindowInfo(0, 0, 1920, 1080));
        var nav = snap.Nav;
        if (!nav.IsAvailable || nav.PathReader is not { } pf) { Console.Error.WriteLine("terrain not loaded"); return 1; }

        var cx = GetInt(args, "--cx"); var cy = GetInt(args, "--cy");
        var gx = GetInt(args, "--gx"); var gy = GetInt(args, "--gy");
        if (cx is null || cy is null || gx is null || gy is null)
        { Console.Error.WriteLine("usage: --path --cx N --cy N --gx N --gy N [--range N] [--no-gap]"); return 1; }
        var range = GetInt(args, "--range") ?? 45;

        Console.WriteLine($"grid {nav.Width}x{nav.Height}  start=({cx},{cy}) pf={nav.Walkable(cx.Value, cy.Value)}  goal=({gx},{gy}) pf={nav.Walkable(gx.Value, gy.Value)}");
        var tgt = nav.TargetingReader;
        Console.WriteLine($"targetingReader={(tgt is null ? "NULL (no blinks possible!)" : "ok")}  blinkRange={range}  gapEnabled={!HasFlag(args, "--no-gap")}");

        var astar = new AStar(nav.Width, nav.Height);
        GapPlan? gap = HasFlag(args, "--no-gap") ? null
            : new GapPlan { BlinkRange = range, BlinkPenalty = 6f, LandingBuffer = 3, Enabled = true };
        var raw = astar.FindPath(pf, new PathCell(cx.Value, cy.Value), new PathCell(gx.Value, gy.Value),
            maxNodes: 1_000_000, gap: gap, targeting: tgt);

        if (!raw.Found) { Console.WriteLine("RESULT: NO PATH FOUND"); return 0; }

        var blinks = raw.Cells.Count(c => c.Action == StepAction.Blink);
        Console.WriteLine($"RESULT: path found, {raw.Cells.Count} raw cells, cost={raw.Cost:F0}, BLINKS={blinks}");
        var smooth = PathSmoother.Smooth(pf, raw.Cells);
        var sblinks = smooth.Count(c => c.Action == StepAction.Blink);
        Console.WriteLine($"smoothed: {smooth.Count} cells, BLINKS={sblinks}");
        Console.WriteLine("smoothed steps (W=walk B=blink):");
        foreach (var c in smooth)
            Console.WriteLine($"  {(c.Action == StepAction.Blink ? "B" : "W")} ({c.X},{c.Y}) pf={nav.Walkable(c.X, c.Y)}");
        return 0;
    }

    private static IGameOracle BuildOracle(string[] args)
    {
        if (!HasFlag(args, "--poemcp")) return NullOracle.Instance;
        var o = PoemcpOracle.TryConnect();
        if (o is null) Console.WriteLine("  (--poemcp requested but POEMCP unreachable; continuing independent)");
        return (IGameOracle?)o ?? NullOracle.Instance;
    }

    private static void Usage()
    {
        Console.WriteLine();
        Console.WriteLine("Probe suite usage:");
        Console.WriteLine("  --list                          list probes");
        Console.WriteLine("  --probe <name> [--discover]     run one probe (validate, or hunt the offset)");
        Console.WriteLine("  --sweep [--group <g>]           validate all probes (or one group)");
        Console.WriteLine("  --baseline show|set|capture     manage gitignored ground-truth facts");
        Console.WriteLine("       set k=v ...                 set facts explicitly");
        Console.WriteLine("       capture [--from-poemcp]     interactive, or auto-fill from ExileAPI");
        Console.WriteLine("  --dump <hexAddr> [--dump-len N] hex-dump a window for manual inspection");
        Console.WriteLine("  Modifiers: --poemcp (use ExileAPI as oracle), --hp/--mana/--es-max (value-scan anchor)");
    }

    // ---- arg helpers ----

    private static bool HasFlag(string[] args, string flag) => args.Contains(flag);

    private static string? GetStr(string[] args, string flag)
    {
        var i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[i + 1] : null;
    }

    private static bool TryGetStr(string[] args, string flag, out string value)
    {
        value = GetStr(args, flag) ?? "";
        return value.Length > 0;
    }

    private static int? GetInt(string[] args, string flag)
        => int.TryParse(GetStr(args, flag), out var v) ? v : null;

    private static bool TryGetHex(string[] args, string flag, out nint addr)
    {
        addr = 0;
        var s = GetStr(args, flag);
        if (s is null) return false;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (!long.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        addr = (nint)v;
        return true;
    }

    /// <summary>The non-flag token immediately following <paramref name="flag"/> (the subcommand).</summary>
    private static string? SubcommandAfter(string[] args, string flag)
    {
        var i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[i + 1] : null;
    }
}
