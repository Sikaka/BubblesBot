using System.Diagnostics;
using BubblesBot.Core;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;
using BubblesBot.Research.Validation;
using BubblesBot.Research.Validation.Tests;

Console.WriteLine("BubblesBot.Research â€” V1 plumbing smoke test");
Console.WriteLine("==========================================");

// Before the probe-CLI dispatch: --discover-reaction takes --list as its own modifier
// (dump nearby Monster-kind paths), which would otherwise route to ProbeCli.
if (args.Contains("--discover-reaction"))
    return BubblesBot.Research.Probing.ReactionDiscovery.Run(args);

if (args.Contains("--probe-kosis"))
    return BubblesBot.Research.Probing.KosisBossProbe.Run(args);

if (args.Contains("--dump-entity-buffs"))
    return BubblesBot.Research.Probing.EntityBuffsDump.Run(args);

// Probe suite (oracle-free RE apparatus). Independent by default; --poemcp opts into the
// ExileAPI cross-check/accelerator. See src/BubblesBot.Research/Probing/README.md.
if (args.Contains("--list") || args.Contains("--probe") || args.Contains("--sweep")
    || args.Contains("--baseline") || args.Contains("--dump") || args.Contains("--terrain")
    || args.Contains("--path"))
    return await BubblesBot.Research.Probing.ProbeCli.RunAsync(args);

if (args.Contains("--validate-all"))
    return await RunValidateAll(args);

if (args.Contains("--validate-core-snapshot"))
    return await RunValidateCoreSnapshot();

if (args.Contains("--inspect-inventory-view"))
    return RunInspectInventoryView(args);

if (args.Contains("--inspect-stash-tabs"))
    return RunInspectStashTabs(args);

if (args.Contains("--inspect-stash-tree"))
    return await RunInspectStashTree(args);

if (args.Contains("--inspect-nav-local"))
    return RunInspectNavLocal(args);

if (args.Contains("--inspect-path-layout"))
    return await RunInspectPathLayout();

if (args.Contains("--inspect-entity-flags"))
    return await RunInspectEntityFlags();

if (args.Contains("--inspect-camera-layout"))
    return await RunInspectCameraLayout();

if (args.Contains("--inspect-monster-pathfinding-layout"))
    return await RunInspectMonsterPathfindingLayout();

if (args.Contains("--watch-monster-pathfinding-layout"))
    return await RunWatchMonsterPathfindingLayout(args);

if (args.Contains("--inspect-terrain-layout"))
    return await RunInspectTerrainLayout();

if (TryGetIntArg(args, "--validate-life", out var validateHp))
{
    var manaArg = TryGetIntArg(args, "--mana-current", out var mc) ? mc : (int?)null;
    var esMaxArg = TryGetIntArg(args, "--es-max", out var em) ? em : (int?)null;
    return RunValidateLife(validateHp, manaArg, esMaxArg);
}

if (args.Contains("--find-serverdata-offsets"))
    return RunFindServerDataOffsets(args);

if (args.Contains("--discover-aob"))
    return await RunDiscoverAob();

if (args.Contains("--discover-thegame"))
    return RunDiscoverTheGame(args);

if (args.Contains("--watch-gamestate"))
    return RunWatchGameState(args);

if (args.Contains("--inspect-resurrect"))
    return RunInspectResurrect(args);

if (args.Contains("--find-hidden-flag"))
    return await RunFindHiddenFlag(args);

if (args.Contains("--find-hidden-diff"))
    return await RunFindHiddenDiff(args);

if (args.Contains("--watch-thegame-diff"))
    return await RunWatchTheGameDiff(args);

if (args.Contains("--watch-ui-panels"))
    return await RunWatchUiPanels(args);

if (args.Contains("--inspect-panel-flags"))
    return await RunInspectPanelFlags(args);

if (args.Contains("--snap-panel-flags"))
    return await RunSnapPanelFlags(args);

if (args.Contains("--sweep-offsets"))
    return await RunSweepOffsets();

if (args.Contains("--discover-field-aob"))
    return RunDiscoverFieldAob(args);

if (args.Contains("--discover-ui-paths"))
    return RunDiscoverUiPaths();

return RunDefault(args);

// ---

static int RunInspectInventoryView(string[] args)
{
    using var process = ProcessHandle.AttachToPoE();
    if (process is null) { Console.Error.WriteLine("No PoE process found."); return 1; }
    var reader = new MemoryReader(process);
    var chain = BubblesBot.Research.Probing.Toolkit.ChainResolver.Resolve(process, reader, args);
    if (chain is null || !chain.IsValid) { Console.Error.WriteLine("Could not resolve IngameState."); return 1; }
    var inventory = InventoryView.FromIngameUi(reader, chain.IngameState);
    Console.WriteLine($"inventory open={inventory.IsOpen} items={inventory.Items.Count}");
    foreach (var item in inventory.Items.Where(item => InventoryView.IsMap(item)))
    {
        var uberStat = item.Stats?.FirstOrDefault(stat => stat.Id == InventoryView.UberBlightedMapStatId);
        var allStats = item.Stats is null ? "null"
            : string.Join(",", item.Stats.Select(s => $"{s.Id}={s.Value}"));
        Console.WriteLine($"map base='{item.BaseName}' rarity={item.Rarity} quality={item.Quality} " +
            $"uber={uberStat?.Value.ToString() ?? "none"} stats=[{allStats}]");
    }
    return 0;
}

static int RunInspectStashTabs(string[] args)
{
    using var process = ProcessHandle.AttachToPoE();
    if (process is null) { Console.Error.WriteLine("No PoE process found."); return 1; }
    var reader = new MemoryReader(process);
    var chain = BubblesBot.Research.Probing.Toolkit.ChainResolver.Resolve(process, reader, args);
    if (chain is null || !chain.IsValid) { Console.Error.WriteLine("Could not resolve IngameState."); return 1; }

    var tabs = StashTabsView.FromIngameData(reader, chain.IngameData).Tabs;
    Console.WriteLine($"stash tabs={tabs.Count}");
    foreach (var tab in tabs.OrderBy(tab => tab.DisplayIndex))
        Console.WriteLine($"index={tab.DisplayIndex,3} type={tab.Type,2} name='{tab.Name}'");
    return tabs.Count > 0 ? 0 : 1;
}

static int RunInspectNavLocal(string[] args)
{
    using var process = ProcessHandle.AttachToPoE();
    if (process is null) { Console.Error.WriteLine("No PoE process found."); return 1; }
    var reader = new MemoryReader(process);
    var chain = BubblesBot.Research.Probing.Toolkit.ChainResolver.Resolve(process, reader, args);
    if (chain is null || !chain.IsValid) { Console.Error.WriteLine("Could not resolve IngameState."); return 1; }
    var snapshot = new GameSnapshot(reader, chain.IngameData, chain.IngameState, new WindowInfo(0, 0, 1920, 1080));
    var player = snapshot.Player?.GridPosition ?? default;
    var nav = snapshot.Nav;
    var pf = nav.PathReader;
    if (!nav.IsAvailable || pf is null) { Console.Error.WriteLine("Terrain unavailable."); return 1; }
    Console.WriteLine($"nav={nav.Width}x{nav.Height} player=({player.X},{player.Y}) path={pf.Read(player.X, player.Y)}");

    (int X, int Y, int D)? nearest = null;
    for (var d = 0; d <= 80 && nearest is null; d++)
        for (var dy = -d; dy <= d && nearest is null; dy++)
            foreach (var dx in new[] { -d, d })
                if (pf.Read(player.X + dx, player.Y + dy) > 0)
                { nearest = (player.X + dx, player.Y + dy, d); break; }
    Console.WriteLine(nearest is { } n
        ? $"nearest walkable=({n.X},{n.Y}) shell={n.D}"
        : "no walkable cell within 80");

    var start = nearest is { } s ? new Vector2i { X = s.X, Y = s.Y } : player;
    var queue = new Queue<Vector2i>();
    var seen = new HashSet<long>();
    queue.Enqueue(start);
    seen.Add(((long)start.X << 32) | (uint)start.Y);
    var minX = start.X; var maxX = start.X; var minY = start.Y; var maxY = start.Y;
    ReadOnlySpan<int> dxs = [1, -1, 0, 0, 1, 1, -1, -1];
    ReadOnlySpan<int> dys = [0, 0, 1, -1, 1, -1, 1, -1];
    while (queue.Count > 0 && seen.Count < 500_000)
    {
        var p = queue.Dequeue();
        minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
        minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
        for (var i = 0; i < dxs.Length; i++)
        {
            var x = p.X + dxs[i]; var y = p.Y + dys[i];
            var key = ((long)x << 32) | (uint)y;
            if (!seen.Add(key) || pf.Read(x, y) <= 0) continue;
            queue.Enqueue(new Vector2i { X = x, Y = y });
        }
    }
    Console.WriteLine($"component={seen.Count} bounds=({minX},{minY})..({maxX},{maxY}) capped={seen.Count >= 500_000}");
    return 0;
}

static async Task<int> RunInspectStashTree(string[] args)
{
    using var process = ProcessHandle.AttachToPoE();
    if (process is null) { Console.Error.WriteLine("No PoE process found."); return 1; }
    var reader = new MemoryReader(process);
    var chain = BubblesBot.Research.Probing.Toolkit.ChainResolver.Resolve(process, reader, args);
    if (chain is null || !chain.IsValid) { Console.Error.WriteLine("Could not resolve IngameState."); return 1; }
    if (!reader.TryReadStruct<nint>(
            chain.IngameUi + KnownOffsets.IngameUiElements.StashElement, out var root)
        || root == 0)
    { Console.Error.WriteLine("StashElement unavailable."); return 1; }

    using var poemcp = new PoemcpClient();
    var oracle = await poemcp.EvalAsync(
        "IngameState.IngameUi.StashElement.VisibleStash.Address.ToString(\"X\")");
    if (!oracle.Success) { Console.Error.WriteLine($"POEMCP failed: {oracle.Error}"); return 1; }
    var target = oracle.AsAddress();

    var visibleText = VisibleUiTextView.ReadInGame(reader, chain.IngameState, 20_000, 32);
    foreach (var label in visibleText.Elements.Where(x =>
                 x.Text.Contains("Supplies", StringComparison.OrdinalIgnoreCase)
                 || x.Text.Trim().Equals("Dump", StringComparison.OrdinalIgnoreCase)))
        Console.WriteLine($"label text='{label.Text}' element=0x{label.Element:X} path={label.TreePath} rect={label.Rect?.ToString() ?? "null"} children={label.ChildCount}");

    var tabStrip = root;
    foreach (var index in new[] { 2, 0, 0, 1, 0 })
        if (!ElementReader.TryGetChild(reader, tabStrip, index, out tabStrip))
        { Console.Error.WriteLine("Could not resolve stash tab strip."); return 1; }
    var tabStripSnapshot = ElementReader.TryReadSnapshot(reader, tabStrip, 1_000);
    if (tabStripSnapshot is not null)
    {
        for (var i = 0; i < tabStripSnapshot.Children.Count; i++)
        {
            var child = tabStripSnapshot.Children[i];
            var texts = new List<string>();
            var childQueue = new Queue<(nint Address, int Depth)>();
            var childSeen = new HashSet<nint>();
            childQueue.Enqueue((child, 0));
            while (childQueue.Count > 0 && childSeen.Count < 100)
            {
                var (address, depth) = childQueue.Dequeue();
                if (!childSeen.Add(address)) continue;
                var snapshot = ElementReader.TryReadSnapshot(reader, address, 64);
                if (snapshot is null) continue;
                var text = NativeString.Read(reader, address + KnownOffsets.Element.TextNoTags);
                if (string.IsNullOrWhiteSpace(text))
                    text = NativeString.Read(reader, address + KnownOffsets.Element.Text);
                if (!string.IsNullOrWhiteSpace(text)) texts.Add(text.Trim());
                if (depth >= 4) continue;
                foreach (var descendant in snapshot.Children)
                    childQueue.Enqueue((descendant, depth + 1));
            }
            var visible = ElementReader.IsVisibleDeep(reader, child);
            if (i < 30 || visible || texts.Any(text => text.Contains("Supplies", StringComparison.OrdinalIgnoreCase)))
                Console.WriteLine($"tab-child={i,3} address=0x{child:X} visible={visible} rect={ElementGeometry.TryReadRect(reader, child)?.ToString() ?? "null"} text=[{string.Join(" | ", texts)}]");
        }
    }

    if (args.Contains("--all-text"))
    {
        var structureQueue = new Queue<(nint Address, string Path, int Depth)>();
        var structureSeen = new HashSet<nint>();
        structureQueue.Enqueue((target, "visible", 0));
        while (structureQueue.Count > 0 && structureSeen.Count < 500)
        {
            var (address, path, depth) = structureQueue.Dequeue();
            if (!structureSeen.Add(address)) continue;
            var snapshot = ElementReader.TryReadSnapshot(reader, address, 2_000);
            if (snapshot is null) continue;
            Console.WriteLine($"stash-structure path={path} address=0x{address:X} children={snapshot.Children.Count} visible={ElementReader.IsVisibleDeep(reader, address)} rect={ElementGeometry.TryReadRect(reader, address)?.ToString() ?? "null"}");
            if (depth >= 3) continue;
            for (var i = 0; i < Math.Min(snapshot.Children.Count, 120); i++)
                structureQueue.Enqueue((snapshot.Children[i], $"{path}/{i}", depth + 1));
        }

        var textQueue = new Queue<(nint Address, string Path, int Depth)>();
        var textSeen = new HashSet<nint>();
        textQueue.Enqueue((root, "stash", 0));
        while (textQueue.Count > 0 && textSeen.Count < 20_000)
        {
            var (address, path, depth) = textQueue.Dequeue();
            if (!textSeen.Add(address) || !ElementReader.IsVisibleDeep(reader, address)) continue;
            var snapshot = ElementReader.TryReadSnapshot(reader, address, 2_000);
            if (snapshot is null) continue;
            var text = NativeString.Read(reader, address + KnownOffsets.Element.TextNoTags);
            if (string.IsNullOrWhiteSpace(text))
                text = NativeString.Read(reader, address + KnownOffsets.Element.Text);
            if (!string.IsNullOrWhiteSpace(text))
                Console.WriteLine($"stash-text path={path} element=0x{address:X} rect={ElementGeometry.TryReadRect(reader, address)?.ToString() ?? "null"} text='{text.Trim()}'");
            if (depth >= 14) continue;
            for (var i = 0; i < snapshot.Children.Count; i++)
                textQueue.Enqueue((snapshot.Children[i], $"{path}/{i}", depth + 1));
        }
    }

    var queue = new Queue<(nint Address, int[] Path)>();
    var seen = new HashSet<nint>();
    var large = new List<string>();
    queue.Enqueue((root, []));
    while (queue.Count > 0 && seen.Count < 20_000)
    {
        var (address, path) = queue.Dequeue();
        if (!seen.Add(address)) continue;
        var element = ElementReader.TryReadSnapshot(reader, address, 2_000);
        if (element is null) continue;
        if (element.Children.Count is >= 200 and <= 1000)
            large.Add($"path=[{string.Join(',', path)}] address=0x{address:X} children={element.Children.Count} visible={element.IsVisibleLocal}");
        if (address == target)
        {
            Console.WriteLine($"visible stash target=0x{target:X} path=[{string.Join(',', path)}] depth={path.Length}");
            foreach (var candidate in large) Console.WriteLine($"large {candidate}");
            return 0;
        }
        if (path.Length >= 12) continue;
        for (var i = 0; i < element.Children.Count; i++)
            queue.Enqueue((element.Children[i], [.. path, i]));
    }

    Console.WriteLine($"target=0x{target:X} not found; visited={seen.Count}");
    foreach (var candidate in large) Console.WriteLine($"large {candidate}");
    return 1;
}

// ---

static int RunDiscoverUiPaths()
{
    using var process = ProcessHandle.AttachToPoE();
    if (process is null) { Console.Error.WriteLine("No PoE process found."); return 1; }
    var reader = new MemoryReader(process);

    // AOB-scan for IngameState (uses our committed pattern). No --hp fallback here —
    // discovery is a dev-time tool, AOB should always work.
    nint ingameState = 0;
    foreach (var pattern in BubblesBot.Core.Game.AobPatterns.IngameStateRefs)
    {
        var slotAddresses = BubblesBot.Core.Game.AobScanner.ScanForResolvedAddresses(process, reader, pattern);
        foreach (var slotAddr in slotAddresses)
        {
            if (!reader.TryReadStruct<nint>(slotAddr, out var candidateIs)) continue;
            if (!reader.TryReadStruct<nint>(candidateIs + BubblesBot.Core.Game.KnownOffsets.IngameState.Data, out var candidateData)) continue;
            if (!reader.TryReadStruct<nint>(candidateData + BubblesBot.Core.Game.KnownOffsets.IngameData.IngameStatePtr, out var roundtrip)) continue;
            if (roundtrip != candidateIs) continue;
            ingameState = candidateIs; break;
        }
        if (ingameState != 0) break;
    }
    if (ingameState == 0) { Console.Error.WriteLine("Could not locate IngameState via AOB."); return 1; }

    if (!reader.TryReadStruct<nint>(ingameState + BubblesBot.Core.Game.KnownOffsets.IngameState.UIRoot, out var uiRoot) || uiRoot == 0)
    { Console.Error.WriteLine("UIRoot pointer null — game might be on a loading screen."); return 1; }

    Console.WriteLine($"UIRoot @ 0x{(long)uiRoot:X}");
    Console.WriteLine($"Sweeping {BubblesBot.Core.Snapshot.UiPatterns.All.Count} patterns…\n");

    var stale = false;
    foreach (var pattern in BubblesBot.Core.Snapshot.UiPatterns.All)
    {
        var matches = BubblesBot.Core.Snapshot.UiPatternMatcher.Find(reader, uiRoot, pattern, maxDepth: 10);
        if (matches.Count == 0)
        {
            Console.WriteLine($"  ✗ {pattern.Name,-24} no match — pattern may be stale or panel not open");
            stale = true;
            continue;
        }
        var top = matches[0];
        var committed = GetCommittedPath(pattern.Name);
        string diff;
        if (committed is null) diff = "";
        else if (committed.Value.IsUnset) diff = "  (no committed path yet — paste the discovered one)";
        else if (!committed.Value.Equals(top.Path)) diff = $"  ⚠ committed={committed.Value} differs";
        else diff = "  (matches committed)";
        var marker = top.Confidence >= 0.99 ? "✓" : "?";
        Console.WriteLine($"  {marker} {pattern.Name,-24} path={top.Path,-12} conf={top.Confidence:F2}  {top.Notes}{diff}");
        if (matches.Count > 1)
        {
            Console.WriteLine($"      (also {matches.Count - 1} lower-confidence candidates; first 3:)");
            foreach (var m in matches.Skip(1).Take(3))
                Console.WriteLine($"        path={m.Path,-12} conf={m.Confidence:F2}  {m.Notes}");
        }
    }

    Console.WriteLine();
    Console.WriteLine("To commit a discovered path:");
    Console.WriteLine("  Edit src/BubblesBot.Core/Snapshot/UiIndexPaths.cs");
    Console.WriteLine("  Replace `new(...)` with `new(a, b, c)` matching the discovered path");
    return stale ? 2 : 0;
}

static BubblesBot.Core.Snapshot.UiIndexPath? GetCommittedPath(string patternName) => patternName switch
{
    "MapDeviceWindow" => BubblesBot.Core.Snapshot.UiIndexPaths.MapDeviceWindow,
    _ => null,
};

// ---

static int RunDiscoverFieldAob(string[] args)
{
    // Usage: --discover-field-aob --offset 0x218 --base-reg rdx
    // Scans PoE.exe .text for `mov reg64, [base+0x218]` instructions, prints each match's
    // surrounding bytes formatted as a candidate FieldOffsetPattern. Human picks the unique-
    // enough one and pastes into AobPatterns.FieldPatterns.
    int offset = 0;
    string baseReg = "rdx";
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "--offset")
        {
            var s = args[i + 1];
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                offset = int.Parse(s[2..], System.Globalization.NumberStyles.HexNumber);
            else int.TryParse(s, out offset);
        }
        if (args[i] == "--base-reg") baseReg = args[i + 1].ToLowerInvariant();
    }
    if (offset == 0) { Console.Error.WriteLine("Pass --offset 0xNNN"); return 1; }

    byte rmBits = baseReg switch
    {
        "rax" => 0, "rcx" => 1, "rdx" => 2, "rbx" => 3, "rsp" => 4, "rbp" => 5, "rsi" => 6, "rdi" => 7,
        _ => throw new ArgumentException($"unsupported base-reg {baseReg}"),
    };

    using var process = ProcessHandle.AttachToPoE();
    if (process is null) { Console.Error.WriteLine("No PoE process found."); return 1; }
    var reader = new MemoryReader(process);
    var hits = AobScanner.FindFieldAccessHits(process, reader, offset, rmBits, contextBytes: 16);
    Console.WriteLine($"Found {hits.Count} `mov r, [{baseReg}+0x{offset:X}]` instructions.");
    foreach (var h in hits.Take(8))
    {
        Console.WriteLine($"\n  RVA 0x{(long)h.SectionBase + h.MatchOffset:X}");
        Console.WriteLine("  " + h.FormatPattern("FIELD_NAME"));
    }
    if (hits.Count > 8) Console.WriteLine($"\n  …and {hits.Count - 8} more — narrow with longer context to find unique signatures.");
    return 0;
}

// ---

static async Task<int> RunSweepOffsets()
{
    using var process = ProcessHandle.AttachToPoE();
    if (process is null) { Console.Error.WriteLine("No PoE process found."); return 1; }
    using var poemcp = new PoemcpClient();
    if (!await poemcp.PingAsync()) { Console.Error.WriteLine("POEMCP unreachable."); return 1; }
    var reader = new MemoryReader(process);

    var sweep = new OffsetSweep(reader, poemcp);
    Console.WriteLine($"Resolving {OffsetProbeCatalog.BaseAddresses.Length} struct base addresses…");
    foreach (var (key, expr) in OffsetProbeCatalog.BaseAddresses)
    {
        var ok = await sweep.ResolveBaseAsync(key, expr);
        Console.WriteLine($"  {(ok ? "✓" : "✗")} {key,-22} 0x{(long)sweep.GetBase(key):X}");
    }

    Console.WriteLine();
    Console.WriteLine($"Sweeping {OffsetProbeCatalog.Probes.Length} field probes…");
    int pass = 0, fail = 0, error = 0;
    var failures = new List<SweepResult>();
    foreach (var probe in OffsetProbeCatalog.Probes)
    {
        var r = await sweep.RunAsync(probe);
        if (r.Error is not null) { error++; Console.WriteLine($"  ⚠ {probe.Category}.{probe.FieldName,-22} ERR: {r.Error}"); continue; }
        if (r.Match)
        {
            pass++;
            Console.WriteLine($"  ✓ {probe.Category}.{probe.FieldName,-22} ours={r.OursValue} truth={r.TruthValue}");
        }
        else
        {
            fail++;
            failures.Add(r);
            Console.WriteLine($"  ✗ {probe.Category}.{probe.FieldName,-22} ours={r.OursValue} truth={r.TruthValue} ({r.RescanProposal ?? "no rescan match"})");
        }
    }
    Console.WriteLine();
    Console.WriteLine($"  Pass: {pass}   Fail: {fail}   Error: {error}");
    if (failures.Count > 0)
    {
        Console.WriteLine("\nProposed offset fixes (paste into KnownOffsets.cs):");
        foreach (var r in failures)
            Console.WriteLine($"  {r.Category}.{r.Field}: was 0x{r.OurOffset:X}; {r.RescanProposal ?? "no candidate"}");
    }
    return failures.Count == 0 ? 0 : 2;
}

// ---

static async Task<int> RunValidateAll(string[] args)
{
    using var process = ProcessHandle.AttachToPoE();
    if (process is null)
    {
        Console.Error.WriteLine("No PoE process found.");
        return 1;
    }
    Console.WriteLine($"Attached to {process.ProcessName} (PID {process.ProcessId})");

    using var poemcp = new PoemcpClient();
    var poemcpAlive = await poemcp.PingAsync();
    if (!poemcpAlive) poemcp.DegradedMode = true; // skip future eval calls instead of waiting on timeouts
    Console.WriteLine(poemcpAlive
        ? $"POEMCP reachable at {poemcp.BaseAddress}"
        : $"POEMCP NOT reachable at {poemcp.BaseAddress} â€” running in degraded mode (POEMCP-dependent tests will skip, no per-call retries)");

    var ctx = new TestContext { Process = process, Reader = new MemoryReader(process), Poemcp = poemcp };

    var tests = new List<ValidationTest>();

    // POEMCP-dependent tests come first (when POEMCP works, these populate State for downstream tests).
    if (poemcpAlive)
    {
        tests.Add(new IngameStateAddressTest());
        tests.Add(new IngameStateDataOffsetTest());
        tests.Add(new IngameDataLocalPlayerTest());
        tests.Add(new IngameDataEntityListTest());
        tests.Add(new IngameDataServerDataTest());
        tests.Add(new IngameStateIngameUiTest());
    }

    // POEMCP-free fallback anchor â€” if POEMCP didn't populate LocalPlayer, this scans memory using
    // user-provided HP/Mana/ES values to find the player. No-op when POEMCP filled the state already.
    if (TryGetIntArg(args, "--hp", out var hpArg))
    {
        var manaArg = TryGetIntArg(args, "--mana", out var m) ? m : (int?)null;
        var esArg = TryGetIntArg(args, "--es-max", out var e) ? e : (int?)null;
        tests.Add(new FindLifeViaValueScanTest(hpArg, manaArg, esArg));

        // Once player Entity is known, back-walk to IngameData (so area + entity-list tests can run without POEMCP).
        tests.Add(new FindIngameDataFromPlayerTest());
    }

    // Component-lookup tests â€” work as long as State.LocalPlayer is set (either by POEMCP or value-scan).
    tests.Add(new PlayerComponentMapTest());
    if (poemcpAlive)
    {
        tests.Add(new PlayerComponentAddressMatchTest("Life"));
        tests.Add(new PlayerComponentAddressMatchTest("Render"));
        tests.Add(new PlayerComponentAddressMatchTest("Positioned"));
        tests.Add(new PlayerComponentAddressMatchTest("Player"));
        tests.Add(new PlayerComponentAddressMatchTest("Actor"));
        tests.Add(new LifeViaComponentLookupTest());
    }

    // Component-field tests â€” read specific fields, sanity-check, and exact-compare with POEMCP if available.
    // These work without POEMCP (sanity-check only mode).
    tests.Add(new PlayerLevelTest());
    tests.Add(new PlayerNameTest());
    tests.Add(new PositionedGridTest());
    tests.Add(new RenderWorldPosTest());
    tests.Add(new LifeCurHpTest());

    // Actor component tests
    tests.Add(new ActorActionIdTest());
    tests.Add(new ActorAnimationIdTest());
    if (poemcpAlive)
    {
        tests.Add(new PlayerComponentAddressMatchTest("Actor"));
        tests.Add(new ActorActionPtrTest());
        tests.Add(new ActorAnimationControllerPtrTest());
    }
    tests.Add(new AnimationProgressTest());

    // Buffs component tests
    tests.Add(new BuffsComponentMapTest());
    tests.Add(new BuffFieldsTest());
    tests.Add(new BuffDetailTest());

    // Camera tests
    tests.Add(new CameraAddressTest());
    tests.Add(new CameraSizeTest());
    tests.Add(new CameraWorldToScreenTest());
    tests.Add(new CameraZoomTest());

    // IngameData-rooted tests â€” work whenever IngameData is resolved (POEMCP path or back-walk fallback).
    tests.Add(new TerrainPackedGridOracleTest());
    tests.Add(new PlayerToNearbyCellPathfindTest());
    tests.Add(new GroundLabelRootOracleTest());
    tests.Add(new UiRootElementOracleTest());
    tests.Add(new IngameUiPanelRootsOracleTest());
    tests.Add(new ElementParentChainOracleTest());
    tests.Add(new InventoryPanelRectOracleTest());
    tests.Add(new StashElementRectOracleTest());
    tests.Add(new PlayerInventoryOracleTest());
    tests.Add(new VisibleStashRootOracleTest());
    tests.Add(new ServerInventoryLayoutDiscoveryTest());
    tests.Add(new MechanicPanelRootsOracleTest());
    tests.Add(new CurrentAreaLevelTest());
    tests.Add(new CurrentAreaHashTest());
    tests.Add(new EntityListBasicReadTest());
    tests.Add(new EntityListTraversalTest());
    tests.Add(new CoreEntitySnapshotBuildTest());
    if (poemcpAlive)
    {
        tests.Add(new PlayerSnapshotMatchesPoemcpTest());
        tests.Add(new EntitySemanticsOracleTest());
        tests.Add(new NearestHostileMonsterOracleTest());
        tests.Add(new NearestMonsterPathfindingOracleTest());
        tests.Add(new MonsterCombatSemanticsOracleTest());
        tests.Add(new MonsterRarityOffsetScanTest());
    }

    // ServerData-rooted tests
    tests.Add(new ServerDataLeagueTest());
    tests.Add(new ServerDataLatencyTest());
    tests.Add(new ServerDataTimeInGameTest());
    tests.Add(new ServerDataSkillBarIdsTest());
    tests.Add(new ServerDataInventoryVectorsTest());
    tests.Add(new ServerDataSimpleFieldsTest());
    tests.Add(new ServerDataDynamicVectorsTest());

    // Item component tests (POEMCP-dependent â€” need entity search)
    if (poemcpAlive)
    {
        tests.Add(new BaseComponentOnEntityTest());
        tests.Add(new SocketsComponentTest());
        tests.Add(new ChestComponentTest());
        tests.Add(new InventoryItemComponentsOracleTest());
    }

    return await TestRunner.RunAllAsync(tests, ctx);
}

// ---

static async Task<int> RunValidateCoreSnapshot()
{
    using var process = ProcessHandle.AttachToPoE();
    if (process is null)
    {
        Console.Error.WriteLine("No PoE process found.");
        return 1;
    }
    Console.WriteLine($"Attached to {process.ProcessName} (PID {process.ProcessId})");

    using var poemcp = new PoemcpClient();
    var poemcpAlive = await poemcp.PingAsync();
    if (!poemcpAlive)
    {
        Console.Error.WriteLine($"POEMCP NOT reachable at {poemcp.BaseAddress}. Start ExileAPI + POEMCP, then re-run.");
        return 2;
    }
    Console.WriteLine($"POEMCP reachable at {poemcp.BaseAddress}");

    var ctx = new TestContext { Process = process, Reader = new MemoryReader(process), Poemcp = poemcp };
    var tests = new ValidationTest[]
    {
        new IngameStateAddressTest(),
        new IngameStateDataOffsetTest(),
        new IngameDataLocalPlayerTest(),
        new IngameDataEntityListTest(),
        new EntityListBasicReadTest(),
        new EntityListTraversalTest(),
        new CoreEntitySnapshotBuildTest(),
        new PlayerSnapshotMatchesPoemcpTest(),
        new EntitySemanticsOracleTest(),
        new NearestHostileMonsterOracleTest(),
    };

    return await TestRunner.RunAllAsync(tests, ctx);
}

// ---

static async Task<int> RunInspectPathLayout()
{
    using var process = ProcessHandle.AttachToPoE();
    if (process is null)
    {
        Console.Error.WriteLine("No PoE process found.");
        return 1;
    }
    Console.WriteLine($"Attached to {process.ProcessName} (PID {process.ProcessId})");

    using var poemcp = new PoemcpClient();
    if (!await poemcp.PingAsync())
    {
        Console.Error.WriteLine($"POEMCP NOT reachable at {poemcp.BaseAddress}.");
        return 2;
    }

    var truth = await poemcp.EvalAsync(
        """
        var p = Player.GridPosNum;
        var e = EntityListWrapper.OnlyValidEntities
            .Where(x => !string.IsNullOrEmpty(x.Path))
            .OrderBy(x => x.Type == ExileCore.Shared.Enums.EntityType.Monster ? 0 : 1)
            .ThenBy(x => System.Numerics.Vector2.Distance(x.GridPosNum, p))
            .FirstOrDefault();
        e == null ? "none" : e.Address.ToString("X") + "|" + e.Id + "|" + e.Path
        """);

    if (!truth.Success)
    {
        Console.Error.WriteLine($"POEMCP error: {truth.Error}");
        return 3;
    }

    var text = truth.AsString();
    if (text == "none")
    {
        Console.Error.WriteLine("POEMCP found no entity with Path.");
        return 4;
    }

    var parts = text.Split('|', 3);
    var entity = (nint)long.Parse(parts[0], System.Globalization.NumberStyles.HexNumber);
    var id = parts[1];
    var expectedPath = parts[2];

    var reader = new MemoryReader(process);
    Console.WriteLine($"Entity 0x{entity:X16}, id={id}");
    Console.WriteLine($"POEMCP Path: {expectedPath}");
    Console.WriteLine();

    DumpQwords(reader, entity, "Entity", 0x0, 0xC0);

    if (reader.TryReadStruct<nint>(entity + KnownOffsets.Entity.EntityDetailsPtr, out var details))
    {
        Console.WriteLine();
        Console.WriteLine($"Entity+0x8 pointer/details: 0x{details:X16}");
        DumpQwords(reader, details, "Details?", 0x0, 0x80);
        DumpPathCandidates(reader, details, "Details?");

        if (reader.TryReadStruct<nint>(details + KnownOffsets.ObjectHeader.MainObject, out var mainObject))
        {
            Console.WriteLine();
            Console.WriteLine($"Details+0x0 pointer/mainObject: 0x{mainObject:X16}");
            DumpQwords(reader, mainObject, "MainObject?", 0x0, 0x80);
            DumpPathCandidates(reader, mainObject, "MainObject?");
        }
    }

    return 0;
}

static async Task<int> RunInspectEntityFlags()
{
    using var process = ProcessHandle.AttachToPoE();
    if (process is null)
    {
        Console.Error.WriteLine("No PoE process found.");
        return 1;
    }
    Console.WriteLine($"Attached to {process.ProcessName} (PID {process.ProcessId})");

    using var poemcp = new PoemcpClient();
    if (!await poemcp.PingAsync())
    {
        Console.Error.WriteLine($"POEMCP NOT reachable at {poemcp.BaseAddress}.");
        return 2;
    }

    var truth = await poemcp.EvalAsync(
        """
        string.Join("\n", EntityListWrapper.OnlyValidEntities
            .Where(e => e.Id > 0)
            .OrderBy(e =>
                e.Type == ExileCore.Shared.Enums.EntityType.Monster ? 0 :
                e.Type == ExileCore.Shared.Enums.EntityType.Chest ? 1 :
                e.Type == ExileCore.Shared.Enums.EntityType.WorldItem ? 2 :
                e.Type == ExileCore.Shared.Enums.EntityType.AreaTransition ? 3 :
                e.Type == ExileCore.Shared.Enums.EntityType.TownPortal ? 4 :
                e.Type == ExileCore.Shared.Enums.EntityType.Portal ? 5 :
                e.Type == ExileCore.Shared.Enums.EntityType.Stash ? 6 : 9)
            .ThenBy(e => e.Id)
            .Take(140)
            .Select(e => e.Address.ToString("X") + "|" + e.Id + "|" + e.Type + "|" + e.IsAlive + "|" + e.IsHostile + "|" + e.IsTargetable + "|" + e.Rarity + "|" + (e.Path ?? "")))
        """);

    if (!truth.Success)
    {
        Console.Error.WriteLine($"POEMCP error: {truth.Error}");
        return 3;
    }

    var reader = new MemoryReader(process);
    Console.WriteLine("addr|id|flags|type|alive|hostile|targetable|rarity|path");
    foreach (var line in truth.AsString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        var parts = line.Split('|', 8);
        if (parts.Length < 8) continue;
        var address = (nint)long.Parse(parts[0], System.Globalization.NumberStyles.HexNumber);
        _ = reader.TryReadStruct<uint>(address + KnownOffsets.Entity.Flags, out var flags);
        var components = EntityComponents.ReadComponentMap(reader, address);
        var interestingComponents = string.Join(",", components.Keys
            .Where(k => k is "Life" or "Stats" or "StateMachine" or "Targetable" or "Monster" or "Player" or "NPC" or "WorldItem" or "ObjectMagicProperties")
            .OrderBy(k => k));
        var rarityCandidates = "";
        if (components.TryGetValue("ObjectMagicProperties", out var omp))
        {
            var vals = new List<string>();
            for (var off = 0x60; off <= 0xA0; off += 4)
            {
                if (reader.TryReadStruct<int>(omp + off, out var v) && v >= 0 && v <= 4)
                    vals.Add($"+0x{off:X}={v}");
            }
            rarityCandidates = string.Join(",", vals);
        }
        Console.WriteLine($"0x{address:X}|{parts[1]}|0x{flags:X8}|{parts[2]}|{parts[3]}|{parts[4]}|{parts[5]}|{parts[6]}|{interestingComponents}|rarityCandidates[{rarityCandidates}]|{parts[7]}");
    }

    return 0;
}

static async Task<int> RunInspectCameraLayout()
{
    using var process = ProcessHandle.AttachToPoE();
    if (process is null)
    {
        Console.Error.WriteLine("No PoE process found.");
        return 1;
    }
    Console.WriteLine($"Attached to {process.ProcessName} (PID {process.ProcessId})");

    using var poemcp = new PoemcpClient();
    if (!await poemcp.PingAsync())
    {
        Console.Error.WriteLine($"POEMCP NOT reachable at {poemcp.BaseAddress}.");
        return 2;
    }

    var truth = await poemcp.EvalAsync(
        """
        IngameState.Camera.Address.ToString("X") + "|" +
        IngameState.Camera.Width + "|" +
        IngameState.Camera.Height + "|" +
        IngameState.Camera.Position.X.ToString("F3") + "|" +
        IngameState.Camera.Position.Y.ToString("F3") + "|" +
        IngameState.Camera.Position.Z.ToString("F3") + "|" +
        IngameState.Camera.ZFar.ToString("F6") + "|" +
        Player.GetComponent<Render>().Pos.X.ToString("F3") + "|" +
        Player.GetComponent<Render>().Pos.Y.ToString("F3") + "|" +
        Player.GetComponent<Render>().Pos.Z.ToString("F3") + "|" +
        IngameState.Camera.WorldToScreen(Player.GetComponent<Render>().Pos).X.ToString("F3") + "|" +
        IngameState.Camera.WorldToScreen(Player.GetComponent<Render>().Pos).Y.ToString("F3")
        """);
    if (!truth.Success)
    {
        Console.Error.WriteLine($"POEMCP error: {truth.Error}");
        return 3;
    }

    var parts = truth.AsString().Split('|');
    var camera = (nint)long.Parse(parts[0], System.Globalization.NumberStyles.HexNumber);
    var width = int.Parse(parts[1]);
    var height = int.Parse(parts[2]);
    var camX = float.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture);
    var camY = float.Parse(parts[4], System.Globalization.CultureInfo.InvariantCulture);
    var camZ = float.Parse(parts[5], System.Globalization.CultureInfo.InvariantCulture);
    var zFar = float.Parse(parts[6], System.Globalization.CultureInfo.InvariantCulture);
    var playerX = float.Parse(parts[7], System.Globalization.CultureInfo.InvariantCulture);
    var playerY = float.Parse(parts[8], System.Globalization.CultureInfo.InvariantCulture);
    var playerZ = float.Parse(parts[9], System.Globalization.CultureInfo.InvariantCulture);
    var screenX = float.Parse(parts[10], System.Globalization.CultureInfo.InvariantCulture);
    var screenY = float.Parse(parts[11], System.Globalization.CultureInfo.InvariantCulture);

    var reader = new MemoryReader(process);
    Console.WriteLine($"Camera 0x{camera:X16}");
    Console.WriteLine($"POEMCP: {width}x{height}, pos=({parts[3]},{parts[4]},{parts[5]}), zFar={parts[6]}");
    Console.WriteLine($"Player render pos=({parts[7]},{parts[8]},{parts[9]}), WorldToScreen=({parts[10]},{parts[11]})");
    Console.WriteLine();

    Console.WriteLine("Int32 candidates matching width/height:");
    for (var off = 0; off < 0x600; off += 4)
    {
        if (!reader.TryReadStruct<int>(camera + off, out var value)) continue;
        if (value == width || value == height)
            Console.WriteLine($"  +0x{off:X3}: {value}");
    }

    Console.WriteLine();
    Console.WriteLine("Float candidates matching camera position/ZFar:");
    for (var off = 0; off < 0x600; off += 4)
    {
        if (!reader.TryReadStruct<float>(camera + off, out var value)) continue;
        if (!float.IsFinite(value)) continue;
        if (Approximately(value, camX) || Approximately(value, camY) || Approximately(value, camZ) || Approximately(value, zFar))
            Console.WriteLine($"  +0x{off:X3}: {value:F3}");
    }

    Console.WriteLine();
    Console.WriteLine("Matrix candidates projecting player near POEMCP WorldToScreen:");
    Span<byte> matrixBytes = stackalloc byte[64];
    for (var off = 0; off <= 0x600 - 64; off += 4)
    {
        if (reader.TryReadBytes(camera + off, matrixBytes) != 64) continue;
        var projected = Project(matrixBytes, playerX, playerY, playerZ, width, height);
        if (projected is not { } p) continue;

        var dx = Math.Abs(p.X - screenX);
        var dy = Math.Abs(p.Y - screenY);
        if (dx <= 2 && dy <= 2)
            Console.WriteLine($"  +0x{off:X3}: ({p.X:F3},{p.Y:F3}) dx={dx:F3} dy={dy:F3}");
    }

    if (reader.TryReadStruct<nint>(camera + KnownOffsets.Camera.Inner, out var inner))
    {
        Console.WriteLine();
        Console.WriteLine($"Current KnownOffsets.Camera.Inner +0x{KnownOffsets.Camera.Inner:X}: 0x{inner:X16}");
        if (inner != 0)
        {
            Console.WriteLine("Inner first 0x40 qwords:");
            DumpQwords(reader, inner, "CameraInner?", 0, 0x40);
        }
    }

    return 0;
}

static async Task<int> RunInspectMonsterPathfindingLayout()
{
    using var process = ProcessHandle.AttachToPoE();
    if (process is null)
    {
        Console.Error.WriteLine("No PoE process found.");
        return 1;
    }
    Console.WriteLine($"Attached to {process.ProcessName} (PID {process.ProcessId})");

    using var poemcp = new PoemcpClient();
    if (!await poemcp.PingAsync())
    {
        Console.Error.WriteLine($"POEMCP NOT reachable at {poemcp.BaseAddress}.");
        return 2;
    }

    var truth = await poemcp.EvalAsync(
        """
        var playerPos = Player.GridPosNum;
        var e = EntityListWrapper.OnlyValidEntities
            .Where(x => x.Type == ExileCore.Shared.Enums.EntityType.Monster && x.IsAlive && x.IsHostile && x.GetComponent<Pathfinding>() != null)
            .OrderByDescending(x => x.GetComponent<Pathfinding>().IsMoving)
            .ThenBy(x => System.Numerics.Vector2.Distance(x.GridPosNum, playerPos))
            .FirstOrDefault();
        e == null ? "none" :
            e.Address.ToString("X") + "|" + e.Id + "|" + e.Path + "|" +
            e.GetComponent<Pathfinding>().Address.ToString("X") + "|" +
            e.GetComponent<Pathfinding>().TargetMovePos.X + "," + e.GetComponent<Pathfinding>().TargetMovePos.Y + "|" +
            e.GetComponent<Pathfinding>().PreviousMovePos.X + "," + e.GetComponent<Pathfinding>().PreviousMovePos.Y + "|" +
            e.GetComponent<Pathfinding>().WantMoveToPosition.X + "," + e.GetComponent<Pathfinding>().WantMoveToPosition.Y + "|" +
            e.GetComponent<Pathfinding>().IsMoving + "|" + e.GetComponent<Pathfinding>().StayTime.ToString("F3")
        """);

    if (!truth.Success)
    {
        Console.Error.WriteLine($"POEMCP error: {truth.Error}");
        return 3;
    }

    var text = truth.AsString();
    if (text == "none")
    {
        Console.Error.WriteLine("POEMCP found no live hostile monster with Pathfinding.");
        return 4;
    }

    var parts = text.Split('|', 9);
    var entity = (nint)long.Parse(parts[0], System.Globalization.NumberStyles.HexNumber);
    var pathfinding = (nint)long.Parse(parts[3], System.Globalization.NumberStyles.HexNumber);
    var stayTime = float.Parse(parts[8], System.Globalization.CultureInfo.InvariantCulture);
    var reader = new MemoryReader(process);

    Console.WriteLine($"Entity 0x{entity:X16}, id={parts[1]}");
    Console.WriteLine(parts[2]);
    Console.WriteLine($"Pathfinding 0x{pathfinding:X16}");
    Console.WriteLine($"POEMCP: target={parts[4]}, previous={parts[5]}, wanted={parts[6]}, moving={parts[7]}, stayTime={parts[8]}");
    Console.WriteLine();

    Console.WriteLine("Vector2i candidates in first 0x700 bytes:");
    for (var off = 0; off <= 0x700 - 8; off += 4)
    {
        if (!reader.TryReadStruct<Vector2i>(pathfinding + off, out var v)) continue;
        if (Math.Abs(v.X) < 20_000 && Math.Abs(v.Y) < 20_000)
            Console.WriteLine($"  +0x{off:X3}: {v.X},{v.Y}");
    }

    Console.WriteLine();
    Console.WriteLine("Float candidates near StayTime:");
    for (var off = 0; off <= 0x700 - 4; off += 4)
    {
        if (!reader.TryReadStruct<float>(pathfinding + off, out var value)) continue;
        if (!float.IsFinite(value)) continue;
        if (Math.Abs(value - stayTime) < 0.01f || (value >= 0 && value < 60_000))
            Console.WriteLine($"  +0x{off:X3}: {value:F3}");
    }

    return 0;
}

static async Task<int> RunWatchMonsterPathfindingLayout(string[] args)
{
    using var process = ProcessHandle.AttachToPoE();
    if (process is null)
    {
        Console.Error.WriteLine("No PoE process found.");
        return 1;
    }
    Console.WriteLine($"Attached to {process.ProcessName} (PID {process.ProcessId})");

    using var poemcp = new PoemcpClient();
    if (!await poemcp.PingAsync())
    {
        Console.Error.WriteLine($"POEMCP NOT reachable at {poemcp.BaseAddress}.");
        return 2;
    }

    var seconds = TryGetIntArg(args, "--seconds", out var s) ? Math.Clamp(s, 1, 120) : 20;
    var reader = new MemoryReader(process);
    var stopAt = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
    var seen = new HashSet<string>();

    Console.WriteLine($"Watching hostile monsters for {seconds}s. Move near a pack; useful frames print automatically.");
    Console.WriteLine("Looking for POEMCP IsMoving=true or non-zero pathing target vectors.");
    Console.WriteLine();

    while (DateTime.UtcNow < stopAt)
    {
            var truth = await poemcp.EvalAsync(
            """
            var p = Player.GridPosNum;
            string.Join("\n", EntityListWrapper.OnlyValidEntities
                .Where(e => e.Type == ExileCore.Shared.Enums.EntityType.Monster && e.IsAlive && e.IsHostile && e.GetComponent<Pathfinding>() != null)
                .OrderBy(e => System.Numerics.Vector2.Distance(e.GridPosNum, p))
                .Take(40)
                .Select(e =>
                {
                    var path = e.GetComponent<Pathfinding>();
                    return e.Address.ToString("X") + "|" + e.Id + "|" + e.GridPosNum.X.ToString("F0") + "," + e.GridPosNum.Y.ToString("F0") + "|" +
                        path.Address.ToString("X") + "|" +
                        path.TargetMovePos.X + "," + path.TargetMovePos.Y + "|" +
                        path.PreviousMovePos.X + "," + path.PreviousMovePos.Y + "|" +
                        path.WantMoveToPosition.X + "," + path.WantMoveToPosition.Y + "|" +
                        path.IsMoving + "|" + path.StayTime.ToString("F3") + "|" +
                        path.DestinationNodes + "|" +
                        string.Join(";", path.PathingNodes.Select(n => n.X + "," + n.Y)) + "|" +
                        e.Path;
                }))
            """);

        if (!truth.Success)
        {
            Console.WriteLine($"POEMCP error: {truth.Error}");
            await Task.Delay(250);
            continue;
        }

        foreach (var line in truth.AsString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|', 12);
            if (parts.Length != 12) continue;

            var target = ParseVector(parts[4]);
            var previous = ParseVector(parts[5]);
            var wanted = ParseVector(parts[6]);
            var moving = bool.TryParse(parts[7], out var mv) && mv;
            var destinationNodes = int.TryParse(parts[9], out var dn) ? dn : 0;
            var pathingNodes = parts[10].Split(';', StringSplitOptions.RemoveEmptyEntries).Select(ParseVector).Where(v => !IsZero(v)).ToArray();
            var hasNonZeroTarget = !IsZero(target) || !IsZero(previous) || !IsZero(wanted) || destinationNodes > 0 || pathingNodes.Length > 0;
            if (!moving && !hasNonZeroTarget) continue;

            var pathfinding = (nint)long.Parse(parts[3], System.Globalization.NumberStyles.HexNumber);
            var key = $"{parts[1]}|{parts[4]}|{parts[5]}|{parts[6]}|{parts[7]}|{parts[9]}|{parts[10]}";
            if (!seen.Add(key)) continue;

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] id={parts[1]} grid={parts[2]} moving={parts[7]} stay={parts[8]}");
            Console.WriteLine($"  {parts[11]}");
            Console.WriteLine($"  POEMCP target={parts[4]} previous={parts[5]} wanted={parts[6]} Pathfinding=0x{pathfinding:X16}");
            Console.WriteLine($"  POEMCP destinationNodes={parts[9]} pathingNodes=[{parts[10]}]");
            if (reader.TryReadStruct<byte>(pathfinding + KnownOffsets.PathfindingComponent.IsMoving, out var directMoving)
                && reader.TryReadStruct<Vector2i>(pathfinding + KnownOffsets.PathfindingComponent.WantMoveToPosition, out var directWanted)
                && reader.TryReadStruct<float>(pathfinding + KnownOffsets.PathfindingComponent.StayTime, out var directStay))
            {
                Console.WriteLine($"  direct +0x54C movingRaw={directMoving} +0x550 wanted={directWanted.X},{directWanted.Y} +0x55C stay={directStay:F3}");
            }
            PrintVectorMatches(reader, pathfinding, "target", target);
            PrintVectorMatches(reader, pathfinding, "previous", previous);
            PrintVectorMatches(reader, pathfinding, "wanted", wanted);
            PrintIntMatches(reader, pathfinding, "destinationNodes", destinationNodes);
            foreach (var (node, i) in pathingNodes.Select((node, i) => (node, i)))
                PrintVectorMatches(reader, pathfinding, $"pathingNode[{i}]", node);
            PrintBoolMatches(reader, pathfinding, moving, new[]
            {
                (KnownOffsets.PathfindingComponent.WantMoveToPosition, 8),
            });
            PrintPathfindingCluster(reader, pathfinding);
            Console.WriteLine();
        }

        await Task.Delay(100);
    }

    Console.WriteLine(seen.Count == 0
        ? "No moving/non-zero pathing frames captured. Try again while monsters are walking but before the bot attacks."
        : $"Captured {seen.Count} distinct moving/non-zero pathing frame(s).");
    return 0;
}

static async Task<int> RunInspectTerrainLayout()
{
    using var process = ProcessHandle.AttachToPoE();
    if (process is null)
    {
        Console.Error.WriteLine("No PoE process found.");
        return 1;
    }
    Console.WriteLine($"Attached to {process.ProcessName} (PID {process.ProcessId})");

    using var poemcp = new PoemcpClient();
    if (!await poemcp.PingAsync())
    {
        Console.Error.WriteLine($"POEMCP NOT reachable at {poemcp.BaseAddress}.");
        return 2;
    }

    var truth = await poemcp.EvalAsync(
        """
        var p = Player.GridPosNum;
        var rx = (int)p.X;
        var ry = (int)p.Y;
        IngameState.Data.Address.ToString("X") + "|" +
        IngameState.Data.RawPathfindingData.Length + "," + IngameState.Data.RawPathfindingData[0].Length + "|" +
        IngameState.Data.RawTerrainTargetingData.Length + "," + IngameState.Data.RawTerrainTargetingData[0].Length + "|" +
        IngameState.Data.RawFramePathfindingData.Length + "," + IngameState.Data.RawFramePathfindingData[0].Length + "|" +
        IngameState.Data.RawTerrainHeightData.Length + "," + IngameState.Data.RawTerrainHeightData[0].Length + "|" +
        rx + "," + ry + "|" +
        IngameState.Data.GetPathfindingValueAt(p) + "|" +
        IngameState.Data.GetTerrainTargetingValueAt(p) + "|" +
        IngameState.Data.GetTerrainHeightAt(p).ToString("F3") + "|" +
        IngameState.Data.ToWorldWithTerrainHeight(p).X.ToString("F3") + "," +
        IngameState.Data.ToWorldWithTerrainHeight(p).Y.ToString("F3") + "," +
        IngameState.Data.ToWorldWithTerrainHeight(p).Z.ToString("F3")
        """);

    if (!truth.Success)
    {
        Console.Error.WriteLine($"POEMCP error: {truth.Error}");
        return 3;
    }

    var parts = truth.AsString().Split('|');
    if (parts.Length != 10)
    {
        Console.Error.WriteLine($"Unexpected POEMCP result: {truth.AsString()}");
        return 4;
    }

    var ingameData = (nint)long.Parse(parts[0], System.Globalization.NumberStyles.HexNumber);
    var reader = new MemoryReader(process);

    Console.WriteLine($"IngameData 0x{ingameData:X16}");
    Console.WriteLine($"RawPathfindingData dims        {parts[1]}");
    Console.WriteLine($"RawTerrainTargetingData dims   {parts[2]}");
    Console.WriteLine($"RawFramePathfindingData dims   {parts[3]}");
    Console.WriteLine($"RawTerrainHeightData dims      {parts[4]}");
    Console.WriteLine($"Player grid={parts[5]}, path={parts[6]}, terrainTarget={parts[7]}, height={parts[8]}, world={parts[9]}");
    Console.WriteLine();

    Console.WriteLine("Scanning IngameData first 0x1800 bytes for dimension ints and plausible containers:");
    DumpIntCandidates(ctxReader: reader, baseAddress: ingameData, dims: parts[1], label: "IngameData path dims");
    DumpIntCandidates(ctxReader: reader, baseAddress: ingameData, dims: parts[4], label: "IngameData height dims");
    var grid = ParseVector(parts[5]);
    DumpNativeArrays(reader, ingameData, 0, 0x1800, parts[1], parts[4], grid);
    Console.WriteLine();

    if (reader.TryReadStruct<nint>(ingameData + KnownOffsets.IngameData.Terrain, out var terrain))
    {
        Console.WriteLine($"KnownOffsets.IngameData.Terrain +0x{KnownOffsets.IngameData.Terrain:X}: 0x{terrain:X16}");
        if (terrain != 0)
        {
            DumpQwords(reader, terrain, "Terrain", 0, 0x180);
            DumpIntCandidates(reader, terrain, parts[1], "path dims");
            DumpIntCandidates(reader, terrain, parts[2], "target dims");
        }
    }

    if (reader.TryReadStruct<NativePtrArray>(ingameData + KnownOffsets.IngameData.TgtArray, out var tgtArray))
    {
        Console.WriteLine();
        Console.WriteLine($"KnownOffsets.IngameData.TgtArray +0x{KnownOffsets.IngameData.TgtArray:X}: first=0x{tgtArray.First:X16} last=0x{tgtArray.Last:X16} count={tgtArray.Count}");
        if (tgtArray.Count is > 0 and < 100_000)
            DumpQwords(reader, tgtArray.First, "TgtArray first entries", 0, (int)Math.Min(0x80, tgtArray.Count * 8));
    }

    return 0;
}

static void DumpIntCandidates(MemoryReader ctxReader, nint baseAddress, string dims, string label)
{
    var parts = dims.Split(',', 2);
    if (parts.Length != 2 || !int.TryParse(parts[0], out var rows) || !int.TryParse(parts[1], out var cols))
        return;

    Console.WriteLine($"{label} dimension int32 candidates:");
    for (var off = 0; off < 0x300; off += 4)
    {
        if (ctxReader.TryReadStruct<int>(baseAddress + off, out var value) && (value == rows || value == cols))
            Console.WriteLine($"  +0x{off:X3}: {value}");
    }
}

static void DumpNativeArrays(MemoryReader reader, nint baseAddress, int start, int endExclusive, string pathDims, string heightDims, Vector2i sampleGrid)
{
    _ = TryParseDims(pathDims, out var pathRows, out var pathCols);
    _ = TryParseDims(heightDims, out var heightRows, out var heightCols);
    Console.WriteLine("NativePtrArray/StdVector-like candidates:");
    for (var off = start; off <= endExclusive - 24; off += 8)
    {
        if (!reader.TryReadStruct<NativePtrArray>(baseAddress + off, out var arr)) continue;
        var first = (long)arr.First;
        var last = (long)arr.Last;
        var end = (long)arr.End;
        if (first <= 0x10000 || first >= 0x7FFF_FFFF_FFFF) continue;
        if (last < first || end < last) continue;
        var bytes = last - first;
        if (bytes <= 0 || bytes > 100_000_000) continue;
        var sample = "";
        if (pathRows > 0 && pathCols > 0 && bytes == pathRows * pathCols)
        {
            var idxYx = sampleGrid.Y * pathCols + sampleGrid.X;
            var idxXy = sampleGrid.X * pathRows + sampleGrid.Y;
            if (idxYx >= 0 && idxYx < bytes && reader.TryReadStruct<byte>(arr.First + idxYx, out var value))
                sample += $" byte[y*x]={value}";
            if (idxXy >= 0 && idxXy < bytes && reader.TryReadStruct<byte>(arr.First + idxXy, out value))
                sample += $" byte[x*y]={value}";
        }
        if (pathRows > 0 && pathCols > 0 && bytes == (pathRows * pathCols + 1L) / 2L)
        {
            var idxYx = sampleGrid.Y * pathCols + sampleGrid.X;
            var idxXy = sampleGrid.X * pathRows + sampleGrid.Y;
            sample += ReadPackedNibble(reader, arr.First, bytes, idxYx, "nib[y*x]");
            sample += ReadPackedNibble(reader, arr.First, bytes, idxXy, "nib[x*y]");
        }
        if (heightRows > 0 && heightCols > 0 && bytes == heightRows * heightCols * 4L)
        {
            var idx = (sampleGrid.Y * heightCols + sampleGrid.X) * 4;
            if (idx >= 0 && idx < bytes && reader.TryReadStruct<float>(arr.First + idx, out var value))
                sample += $" float[{sampleGrid.X},{sampleGrid.Y}]={value:F3}";
        }
        Console.WriteLine($"  +0x{off:X4}: first=0x{arr.First:X16} last=0x{arr.Last:X16} end=0x{arr.End:X16} bytes={bytes:N0} ptrCount={arr.Count:N0}{sample}");
    }
}

static string ReadPackedNibble(MemoryReader reader, nint first, long bytes, int cellIndex, string label)
{
    var byteIndex = cellIndex / 2;
    if (byteIndex < 0 || byteIndex >= bytes || !reader.TryReadStruct<byte>(first + byteIndex, out var packed))
        return "";
    var low = packed & 0x0F;
    var high = (packed >> 4) & 0x0F;
    var value = (cellIndex & 1) == 0 ? low : high;
    return $" {label}={value}";
}

static bool TryParseDims(string dims, out int rows, out int cols)
{
    rows = 0;
    cols = 0;
    var parts = dims.Split(',', 2);
    return parts.Length == 2 && int.TryParse(parts[0], out rows) && int.TryParse(parts[1], out cols);
}

static Vector2i ParseVector(string value)
{
    var parts = value.Split(',', 2);
    if (parts.Length != 2) return default;
    _ = int.TryParse(parts[0], out var x);
    _ = int.TryParse(parts[1], out var y);
    return new Vector2i { X = x, Y = y };
}

static bool IsZero(Vector2i v) => v.X == 0 && v.Y == 0;

static void PrintVectorMatches(MemoryReader reader, nint baseAddress, string label, Vector2i expected)
{
    if (IsZero(expected)) return;

    var matches = new List<string>();
    for (var off = 0; off <= 0x800 - 8; off += 4)
    {
        if (reader.TryReadStruct<Vector2i>(baseAddress + off, out var v) && v.X == expected.X && v.Y == expected.Y)
            matches.Add($"+0x{off:X3}");
    }

    Console.WriteLine($"  {label} matches: {(matches.Count == 0 ? "(none)" : string.Join(", ", matches))}");
}

static void PrintIntMatches(MemoryReader reader, nint baseAddress, string label, int expected)
{
    if (expected == 0) return;

    var matches = new List<string>();
    for (var off = 0; off <= 0x800 - 4; off += 4)
    {
        if (reader.TryReadStruct<int>(baseAddress + off, out var v) && v == expected)
            matches.Add($"+0x{off:X3}");
    }

    Console.WriteLine($"  {label} int32 matches: {(matches.Count == 0 ? "(none)" : string.Join(", ", matches))}");
}

static void PrintBoolMatches(MemoryReader reader, nint baseAddress, bool expectedMoving, IReadOnlyList<(int Start, int Length)> excludedRanges)
{
    if (!expectedMoving) return;

    var matches = new List<string>();
    for (var off = 0; off <= 0x800 - 1; off++)
    {
        if (excludedRanges.Any(r => off >= r.Start && off < r.Start + r.Length)) continue;
        if (reader.TryReadStruct<byte>(baseAddress + off, out var b) && b == 2)
            matches.Add($"+0x{off:X3}");
    }

    Console.WriteLine($"  IsMoving raw-byte=2 candidates: {(matches.Count == 0 ? "(none)" : string.Join(", ", matches.Take(40)))}");
}

static void PrintPathfindingCluster(MemoryReader reader, nint baseAddress)
{
    Console.Write("  cluster +0x548..+0x563 bytes:");
    for (var off = 0x548; off <= 0x563; off++)
    {
        if (reader.TryReadStruct<byte>(baseAddress + off, out var b))
            Console.Write($" {off:X3}:{b:X2}");
    }
    Console.WriteLine();
}

static bool Approximately(float actual, float expected, float epsilon = 0.01f)
    => Math.Abs(actual - expected) <= epsilon;

static (float X, float Y)? Project(ReadOnlySpan<byte> bytes, float x, float y, float z, int width, int height)
{
    Span<float> m = stackalloc float[16];
    for (var i = 0; i < 16; i++)
    {
        m[i] = BitConverter.ToSingle(bytes[(i * 4)..(i * 4 + 4)]);
        if (!float.IsFinite(m[i]) || Math.Abs(m[i]) > 1_000_000) return null;
    }

    // SharpDX Vector4.Transform(vector, matrix) treats matrix fields as M11..M44:
    // x' = x*M11 + y*M21 + z*M31 + w*M41, and so on.
    var clipX = x * m[0] + y * m[4] + z * m[8]  + m[12];
    var clipY = x * m[1] + y * m[5] + z * m[9]  + m[13];
    var clipW = x * m[3] + y * m[7] + z * m[11] + m[15];
    if (!float.IsFinite(clipW) || Math.Abs(clipW) < 0.0001f) return null;

    var ndcX = clipX / clipW;
    var ndcY = clipY / clipW;
    if (!float.IsFinite(ndcX) || !float.IsFinite(ndcY)) return null;
    return ((ndcX + 1.0f) * width * 0.5f, (1.0f - ndcY) * height * 0.5f);
}

static void DumpQwords(MemoryReader reader, nint baseAddress, string label, int start, int endExclusive)
{
    Console.WriteLine($"{label} qwords:");
    for (var off = start; off < endExclusive; off += 8)
    {
        if (!reader.TryReadStruct<nint>(baseAddress + off, out var value))
        {
            Console.WriteLine($"  +0x{off:X3}: <unreadable>");
            continue;
        }

        var printable = "";
        if ((long)value > 0x10000 && (long)value < 0x7FFF_FFFF_FFFF)
        {
            var s16 = reader.ReadStringUtf16(value, 128);
            var s8 = reader.ReadStringUtf8(value, 128);
            if (s16.StartsWith("Metadata", StringComparison.Ordinal)) printable = $" UTF16='{s16}'";
            else if (s8.StartsWith("Metadata", StringComparison.Ordinal)) printable = $" UTF8='{s8}'";
        }

        Console.WriteLine($"  +0x{off:X3}: 0x{value:X16}{printable}");
    }
}

static void DumpPathCandidates(MemoryReader reader, nint baseAddress, string label)
{
    Console.WriteLine($"{label} path candidates:");
    for (var off = 0; off < 0x100; off += 8)
    {
        if (!reader.TryReadStruct<nint>(baseAddress + off, out var ptr)) continue;
        if ((long)ptr <= 0x10000 || (long)ptr >= 0x7FFF_FFFF_FFFF) continue;

        var s16 = reader.ReadStringUtf16(ptr, 256);
        var s8 = reader.ReadStringUtf8(ptr, 256);
        if (s16.StartsWith("Metadata", StringComparison.Ordinal))
            Console.WriteLine($"  +0x{off:X3} -> UTF16 {s16}");
        if (s8.StartsWith("Metadata", StringComparison.Ordinal))
            Console.WriteLine($"  +0x{off:X3} -> UTF8 {s8}");
    }
}

// ---

static int RunDefault(string[] args)
{
    var selfTest = args.Contains("--self-test");

    ProcessHandle? process;
    string moduleLabel;

    if (selfTest)
    {
        var self = Process.GetCurrentProcess();
        Console.WriteLine($"--self-test mode: attaching to own process (PID {self.Id}, {self.ProcessName})");
        process = ProcessHandle.AttachToProcess(self.Id, expectedProcessName: null);
        moduleLabel = process.ProcessName;
    }
    else
    {
        process = ProcessHandle.AttachToPoE();
        if (process is null)
        {
            Console.Error.WriteLine("No PoE process found. Start the game and re-run.");
            Console.Error.WriteLine("Searched names: PathOfExile_x64, PathOfExile_x64Steam, PathOfExileSteam, PathOfExile.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Modes:");
            Console.Error.WriteLine("  (default)              attach to PoE and run PE-header smoke tests");
            Console.Error.WriteLine("  --self-test            attach to this process â€” validates plumbing without PoE");
            Console.Error.WriteLine("  --validate-life N      scan PoE memory for player Life component using HP=N as needle");
            Console.Error.WriteLine("                         optional: --mana-current N --es-max N");
            Console.Error.WriteLine("  --validate-core-snapshot");
            Console.Error.WriteLine("                         validate root pointers + entity traversal against POEMCP");
            return 1;
        }
        moduleLabel = process.ProcessName;
    }

    using (process)
    {
        Console.WriteLine($"Attached to {moduleLabel} (PID {process.ProcessId})");
        Console.WriteLine($"  Module path : {process.ModulePath}");
        Console.WriteLine($"  Module base : 0x{process.MainModuleBase:X16}");
        Console.WriteLine($"  Module size : {process.MainModuleSize:N0} bytes ({process.MainModuleSize / (1024.0 * 1024.0):F1} MiB)");
        Console.WriteLine();

        var reader = new MemoryReader(process);

        const ushort ImageDosSignature = 0x5A4D;
        var dosMagic = reader.ReadStruct<ushort>(process.MainModuleBase);
        Console.WriteLine($"[smoke 1] DOS header magic at module base: 0x{dosMagic:X4} ({(dosMagic == ImageDosSignature ? "OK â€” 'MZ'" : "FAIL â€” expected 0x5A4D")})");
        if (dosMagic != ImageDosSignature) { Console.Error.WriteLine("PE header read failed â€” memory plumbing is not working."); return 2; }

        var eLfanew = reader.ReadStruct<int>(process.MainModuleBase + 0x3C);
        var ntHeaderAddr = process.MainModuleBase + eLfanew;
        var ntSignature = reader.ReadStruct<uint>(ntHeaderAddr);
        const uint ImageNtSignature = 0x00004550;
        Console.WriteLine($"[smoke 2] e_lfanew = 0x{eLfanew:X}, NT header magic: 0x{ntSignature:X8} ({(ntSignature == ImageNtSignature ? "OK â€” 'PE\\0\\0'" : "FAIL")})");
        if (ntSignature != ImageNtSignature) { Console.Error.WriteLine("NT header read failed."); return 3; }

        var machine = reader.ReadStruct<ushort>(ntHeaderAddr + 4);
        const ushort ImageFileMachineAmd64 = 0x8664;
        Console.WriteLine($"[smoke 3] PE Machine field: 0x{machine:X4} ({(machine == ImageFileMachineAmd64 ? "OK â€” AMD64 / x64" : "UNEXPECTED")})");

        Span<byte> buffer = stackalloc byte[16];
        var bytesRead = reader.ReadBytes(process.MainModuleBase, buffer);
        Console.Write($"[smoke 4] ReadBytes 16 bytes: {bytesRead} bytes â€” ");
        foreach (var b in buffer) Console.Write($"{b:X2} ");
        Console.WriteLine();

        var ok = reader.TryReadStruct<int>(unchecked((nint)0xDEADBEEFL), out var _);
        Console.WriteLine($"[smoke 5] TryReadStruct at bogus 0xDEADBEEF: returned {ok} (expected False)");

        Console.WriteLine();
        Console.WriteLine($"Stats: {reader.ReadCount} reads, {reader.BytesRead:N0} bytes, {reader.FailedReads} failed");
        Console.WriteLine();
        Console.WriteLine($"V1 plumbing OK against {moduleLabel}.");
        if (!selfTest)
        {
            Console.WriteLine();
            Console.WriteLine("Next: validate the community offset table against live game data.");
            Console.WriteLine("  dotnet run --project src/BubblesBot.Research -- --validate-life <currentHP> [--mana-current N] [--es-max N]");
        }
    }
    return 0;
}

static int RunValidateLife(int hp, int? manaCurrent, int? esMax)
{
    Console.WriteLine($"--validate-life mode: searching PoE memory for VitalStruct with Current={hp}");
    if (manaCurrent.HasValue) Console.WriteLine($"  + filter: Mana.Current = {manaCurrent.Value}");
    if (esMax.HasValue)       Console.WriteLine($"  + filter: EnergyShield.Max = {esMax.Value}");
    Console.WriteLine();

    using var process = ProcessHandle.AttachToPoE();
    if (process is null)
    {
        Console.Error.WriteLine("No PoE process found. Start the game and re-run.");
        return 1;
    }

    Console.WriteLine($"Attached to {process.ProcessName} (PID {process.ProcessId})");
    var reader = new MemoryReader(process);

    var sw = Stopwatch.StartNew();
    var lastReport = TimeSpan.Zero;

    var matches = LifeValidator.FindCandidates(
        reader, hp, manaCurrent, esMax,
        progress =>
        {
            // Report at most ~once per second to avoid spamming the console.
            if (sw.Elapsed - lastReport < TimeSpan.FromMilliseconds(500) && progress.RegionsScanned != progress.TotalRegions) return;
            lastReport = sw.Elapsed;
            Console.Write($"\r  scanningâ€¦ region {progress.RegionsScanned,5}/{progress.TotalRegions}  bytes {progress.BytesScanned / (1024.0 * 1024.0),8:F1} MiB  candidates {progress.CandidatesFound}   ");
        });
    sw.Stop();
    Console.WriteLine();
    Console.WriteLine();
    Console.WriteLine($"Scan complete in {sw.Elapsed.TotalSeconds:F1}s. Found {matches.Count} candidate(s):");
    Console.WriteLine($"  Total reads: {reader.ReadCount:N0}, bytes read: {reader.BytesRead:N0}, failed: {reader.FailedReads:N0}");
    Console.WriteLine();

    if (matches.Count == 0)
    {
        Console.Error.WriteLine("No matches. This means one of:");
        Console.Error.WriteLine("  1. The HP value drifted between runs (heal/hit between when you read it and when we scanned).");
        Console.Error.WriteLine("  2. LifeComponent or VitalStruct offsets in community-offsets.md don't match this PoE build.");
        Console.Error.WriteLine("  3. The player isn't fully loaded into a zone yet.");
        return 4;
    }

    foreach (var m in matches)
    {
        Console.WriteLine(m);
        Console.WriteLine();
    }

    if (matches.Count == 1)
    {
        Console.WriteLine("Single unique match â€” VERY high confidence this is the player's Life component.");
        Console.WriteLine("Layout offsets validated: VitalStruct.Current=0x30, .Max=0x2C, LifeComponent.{Health=0x178, Mana=0x1C8, EnergyShield=0x210}.");
    }
    else
    {
        Console.WriteLine($"{matches.Count} matches. Re-run with more filters (--mana-current, --es-max) to narrow down.");
    }
    return 0;
}

static bool TryGetIntArg(string[] args, string flag, out int value)
{
    value = 0;
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == flag && int.TryParse(args[i + 1], out value)) return true;
    }
    return false;
}

static bool TryGetStringArg(string[] args, string flag, out string value)
{
    value = "";
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == flag && !string.IsNullOrEmpty(args[i + 1]))
        {
            value = args[i + 1];
            return true;
        }
    }
    return false;
}

/// <summary>
/// Scans ServerData memory for known field values to discover correct offsets.
/// Walks the validated IngameState chain to reach ServerData, then uses our safe
/// MemoryReader to scan the first ~32 KiB for the needle values.
/// </summary>
static int RunFindServerDataOffsets(string[] args)
{
    var league = TryGetStringArg(args, "--league", out var lg) ? lg : null;
    var latency = TryGetIntArg(args, "--latency", out var lat) ? lat : (int?)null;

    Console.WriteLine("--find-serverdata-offsets: scanning ServerData for known field values");
    Console.WriteLine();
    if (string.IsNullOrEmpty(league) && !latency.HasValue)
    {
        Console.Error.WriteLine("Pass --league <value> and/or --latency <value> as needles.");
        Console.Error.WriteLine("Grab these from POEMCP before it goes down: curl POST /eval {\"code\":\"IngameState.ServerData.League\"}");
        return 1;
    }
    if (!TryGetIntArg(args, "--hp", out var hp))
    {
        Console.Error.WriteLine("Pass --hp <currentHP> to locate the player Entity via value-scan.");
        return 1;
    }

    Console.WriteLine($"  League needle:  {(league ?? "(not provided)")}");
    Console.WriteLine($"  Latency needle: {(latency.HasValue ? latency.Value.ToString() : "(not provided)")}");
    Console.WriteLine();

    using var process = ProcessHandle.AttachToPoE();
    if (process is null) { Console.Error.WriteLine("No PoE process found."); return 1; }

    var reader = new MemoryReader(process);
    Console.WriteLine($"Attached to {process.ProcessName} (PID {process.ProcessId})");

    // Step 1: Find player LifeComponent via value-scan.
    var manaArg = TryGetIntArg(args, "--mana-current", out var mc) ? mc : (int?)null;
    var esMaxArg = TryGetIntArg(args, "--es-max", out var em) ? em : (int?)null;
    Console.Write("Scanning for player LifeComponent... ");
    var matches = LifeValidator.FindCandidates(reader, hp, manaArg, esMaxArg);
    if (matches.Count == 0) { Console.Error.WriteLine("FAIL â€” no candidates found."); return 4; }
    if (matches.Count > 1) { Console.Error.WriteLine($"FAIL â€” {matches.Count} candidates, need more filters."); return 4; }
    var playerEntity = matches[0].OwnerAddress;
    Console.WriteLine($"player Entity @ 0x{playerEntity:X16}");

    // Step 2: Back-walk to IngameData.
    Console.Write("Back-walking to IngameData... ");
    var hits = AnchorBackWalker.FindIngameDataFromPlayer(reader, playerEntity);
    if (hits.Count == 0) { Console.Error.WriteLine("FAIL â€” no IngameData candidates."); return 4; }
    if (hits.Count > 1) { Console.Error.WriteLine($"FAIL â€” {hits.Count} IngameData candidates."); return 4; }
    var ingameData = hits[0].IngameDataAddress;
    var serverData = hits[0].ServerData;
    Console.WriteLine($"IngameData @ 0x{ingameData:X16}, ServerData @ 0x{serverData:X16}");

    // Step 3: Read a window of ServerData memory and scan for needles.
    Console.WriteLine("Scanning ServerData region for needle values...");
    Console.WriteLine();

    const int windowSize = 0x10000; // 64 KiB
    Span<byte> buf = stackalloc byte[Math.Min(windowSize, 64 * 1024)];
    var actual = reader.TryReadBytes(serverData, buf);
    if (actual == 0) { Console.Error.WriteLine("FAIL â€” could not read ServerData memory."); return 5; }
    Console.WriteLine($"  Read {actual} bytes from ServerData @ 0x{serverData:X16}");

    // Scan for UTF-16 league string
    if (!string.IsNullOrEmpty(league))
    {
        Console.WriteLine();
        Console.WriteLine($"  Searching for UTF-16 string '{league}':");
        var leagueBytes = System.Text.Encoding.Unicode.GetBytes(league);
        for (var i = 0; i + leagueBytes.Length <= actual; i += 2)
        {
            var match = true;
            for (var j = 0; j < leagueBytes.Length; j++)
                if (buf[i + j] != leagueBytes[j]) { match = false; break; }
            if (match)
                Console.WriteLine($"    Candidate: League @ +0x{i:X} (NativeUtf16Text starts here)");
        }
    }

    // Scan for int32 latency value
    if (latency.HasValue)
    {
        Console.WriteLine();
        Console.WriteLine($"  Searching for int32 = {latency.Value}:");
        var latBytes = BitConverter.GetBytes(latency.Value);
        for (var i = 0; i + 4 <= actual; i += 4)
        {
            if (buf[i] == latBytes[0] && buf[i + 1] == latBytes[1] &&
                buf[i + 2] == latBytes[2] && buf[i + 3] == latBytes[3])
            {
                var near = 0;
                for (var k = Math.Max(0, i - 32); k <= Math.Min(actual - 4, i + 32); k += 4)
                    if (k != i) { var v = BitConverter.ToInt32(buf[k..(k + 4)]); if (v >= 0 && v < 5000) near++; }
                Console.WriteLine($"    Candidate: Latency @ +0x{i:X} (nearby plausible latencies: {near})");
            }
        }
    }

    // Scan for TimeInGame pattern: positive int with TimeInGame2 sibling (Â±2 within 8 bytes)
    Console.WriteLine();
    Console.WriteLine("  Positive int32s with TimeInGame2 sibling (Â±2 within 8 bytes):");
    var reported = 0;
    for (var i = 0; i + 12 <= actual; i += 4)
    {
        var v = BitConverter.ToInt32(buf[i..(i + 4)]);
        if (v > 60 && v < 100_000)
        {
            var tig2 = BitConverter.ToInt32(buf[(i + 8)..(i + 12)]);
            if (Math.Abs(tig2 - v) <= 2 && reported < 10)
            {
                Console.WriteLine($"    +0x{i:X} = {v}s  (TimeInGame2 @ +0x{i+8:X} = {tig2}s)");
                reported++;
            }
        }
    }

    Console.WriteLine();
    Console.WriteLine("Done. Update KnownOffsets.ServerData and ServerDataTests with the correct offsets.");
    return 0;
}

// â”€â”€ AOB pattern discovery â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

static async Task<int> RunDiscoverAob()
{
    Console.WriteLine();
    Console.WriteLine("AOB pattern discovery â€” requires PoE + POEMCP running.");
    Console.WriteLine("Paste the output patterns into src/BubblesBot.Core/Game/AobPatterns.cs");
    Console.WriteLine();

    using var process = ProcessHandle.AttachToPoE();
    if (process is null) { Console.Error.WriteLine("No PoE process found."); return 1; }
    Console.WriteLine($"Attached to {process.ProcessName} (PID {process.ProcessId})");
    Console.WriteLine($"Module base: 0x{process.MainModuleBase:X16}  size: 0x{process.MainModuleSize:X}");

    using var poemcp = new PoemcpClient();
    if (!await poemcp.PingAsync())
    {
        Console.Error.WriteLine("POEMCP not reachable. Start PoE + POEMCP and try again.");
        return 1;
    }

    var reader = new MemoryReader(process);

    // Get IngameState address via POEMCP eval
    Console.Write("Fetching IngameState address from POEMCP...");
    var isResult = await poemcp.EvalAsync("((long)IngameState.Address).ToString(\"X16\")");
    if (!isResult.Success)
    {
        Console.Error.WriteLine($"\nPOEMCP eval failed: {isResult.Error}");
        return 1;
    }
    var isHex = isResult.AsString().Trim();
    if (string.IsNullOrWhiteSpace(isHex) || !long.TryParse(isHex, System.Globalization.NumberStyles.HexNumber, null, out var isLong) || isLong == 0)
    {
        Console.Error.WriteLine($"\nFailed to parse IngameState address from POEMCP. Got: {isHex}");
        return 1;
    }
    var ingameStateAddr = (nint)isLong;
    Console.WriteLine($" 0x{ingameStateAddr:X16}");

    // Verify with roundtrip read
    if (!reader.TryReadStruct<nint>(ingameStateAddr + KnownOffsets.IngameState.Data, out var dataAddr) || dataAddr == 0)
    {
        Console.Error.WriteLine("Roundtrip read failed â€” IngameState.Data offset may be wrong.");
        return 1;
    }
    Console.WriteLine($"  â†’ IngameData:     0x{dataAddr:X16}");

    // The startup resolver validates candidates with IngameData's back-pointer to
    // IngameState. This field can drift independently of IngameState.Data, so report
    // every exact qword match in a bounded header scan whenever AOBs are rediscovered.
    var stateBackPointerOffsets = new List<int>();
    for (var offset = 0; offset <= 0x800; offset += sizeof(long))
    {
        if (reader.TryReadStruct<nint>(dataAddr + offset, out var value) && value == ingameStateAddr)
            stateBackPointerOffsets.Add(offset);
    }

    if (stateBackPointerOffsets.Count == 0)
    {
        Console.Error.WriteLine("No IngameData back-pointer to IngameState found in +0x000..+0x800.");
        return 1;
    }

    Console.WriteLine($"  IngameStatePtr offsets: {string.Join(", ", stateBackPointerOffsets.Select(offset => $"0x{offset:X}"))}");

    // Find global pointer slots in PoE.exe image that hold IngameState
    Console.WriteLine();
    Console.WriteLine("Scanning PoE.exe data sections for global slot holding IngameState...");
    var slots = AobScanner.FindGlobalPointerTo(process, reader, ingameStateAddr);
    Console.WriteLine($"  Found {slots.Count} candidate slot(s)");

    if (slots.Count == 0)
    {
        Console.Error.WriteLine("No slots found. The global may be in a dynamically-allocated region.");
        Console.Error.WriteLine("Try scanning with POEMCP: it may expose the static address differently.");
        return 1;
    }

    // For each slot, find .text references
    Console.WriteLine();
    Console.WriteLine("Scanning PoE.exe .text for RIP-relative references to each slot...");
    Console.WriteLine();

    var patternsFound = 0;
    foreach (var slotAddr in slots)
    {
        var slotRva = slotAddr - process.MainModuleBase;
        Console.WriteLine($"  Slot @ 0x{slotAddr:X16} (RVA 0x{slotRva:X})");

        var hits = AobScanner.FindReferencesTo(process, reader, slotAddr, contextBytes: 24);
        Console.WriteLine($"    {hits.Count} code reference(s)");

        foreach (var hit in hits)
        {
            patternsFound++;
            var textRva = hit.SectionBase - process.MainModuleBase + hit.MatchOffset;
            Console.WriteLine($"    â”Œâ”€ Reference @ .text RVA 0x{textRva:X}");
            Console.WriteLine($"    â”‚  Instruction: {FormatHex(hit.Context.AsSpan(hit.InstructionOffset, 7))}");
            Console.WriteLine($"    â”‚  Pattern (8 bytes context before, 4 after, displacement wildcarded):");
            Console.WriteLine($"    â”‚    {hit.FormatPattern(8, 4)}");
            Console.WriteLine($"    â”‚    // DispOffset=11, InstrLen=15  (8 prefix bytes + instruction-relative 3/7)");
            Console.WriteLine($"    â””â”€");
        }
        Console.WriteLine();
    }

    if (patternsFound == 0)
    {
        Console.Error.WriteLine("No patterns found. The code may use a different instruction form.");
        Console.Error.WriteLine("Check AobScanner.FindReferencesTo â€” it currently handles REX.W MOV r64,[RIP+rel32] only.");
        return 1;
    }

    Console.WriteLine($"Found {patternsFound} pattern(s). Copy the best one into AobPatterns.cs:");
    Console.WriteLine();
    Console.WriteLine("  public static readonly Pattern[] IngameStateRefs =");
    Console.WriteLine("  [");
    Console.WriteLine("      new Pattern(");
    Console.WriteLine("          Bytes:       <paste the byte?[] here>,");
    Console.WriteLine("          DispOffset:  11,");
    Console.WriteLine("          InstrLen:    15,");
    Console.WriteLine("          Description: \"IngameState global slot â€” PoE build YYYY-MM-DD\"),");
    Console.WriteLine("  ];");
    return 0;
}

static int RunDiscoverTheGame(string[] args)
{
    Console.WriteLine();
    Console.WriteLine("TheGame discovery — derives TheGame from the resolved IngameState. No POEMCP needed.");
    Console.WriteLine("Paste the output pattern into AobPatterns.TheGameRefs.");
    Console.WriteLine();

    using var process = ProcessHandle.AttachToPoE();
    if (process is null) { Console.Error.WriteLine("No PoE process found."); return 1; }
    Console.WriteLine($"Attached to {process.ProcessName} (PID {process.ProcessId})");

    var reader = new MemoryReader(process);
    var chain = BubblesBot.Research.Probing.Toolkit.ChainResolver.Resolve(process, reader, args);
    if (chain is null || !chain.IsValid) { Console.Error.WriteLine("Could not resolve IngameState."); return 1; }
    Console.WriteLine($"IngameState: 0x{(long)chain.IngameState:X16} (via {chain.ResolvedVia})");

    // TheGame is a heap object whose State4 slot (+0x88) holds the IngameState pointer.
    // Stage 1 assumes the simplest shape: an image global slot holds TheGame directly
    // (same as IngameState's global). Collect every heap-pointer-looking qword in the
    // image data sections (value -> slot addresses) and shape-check each unique value.
    Console.WriteLine();
    Console.WriteLine("Stage 1: scanning PoE.exe data sections for a global holding TheGame directly...");
    var moduleStart = process.MainModuleBase;
    var moduleEnd   = process.MainModuleBase + (nint)process.MainModuleSize;

    // value -> every image slot holding it. Reused by stage 2's indirection join.
    var imagePointers = new Dictionary<nint, List<nint>>();
    long qwords = 0;

    foreach (var (sectionBase, bytes) in AobScanner.ReadDataSections(process, reader))
    {
        for (var i = 0; i + 8 <= bytes.Length; i += 8)
        {
            qwords++;
            var value = (nint)BitConverter.ToInt64(bytes, i);
            if (value < 0x1_0000 || (long)value > 0x7FFF_FFFF_FFFF) continue; // not user-space
            if ((value & 0x7) != 0) continue;                                 // heap objects are 8+ aligned
            if (value >= moduleStart && value < moduleEnd) continue;          // image-internal (vtables etc.)

            if (!imagePointers.TryGetValue(value, out var slotList))
                imagePointers[value] = slotList = new List<nint>();
            slotList.Add(sectionBase + i);
        }
    }

    var slotsByTheGame = new Dictionary<nint, List<nint>>();
    foreach (var (value, slotList) in imagePointers)
        if (TheGameResolver.LooksLikeTheGame(reader, value, chain.IngameState))
            slotsByTheGame[value] = slotList;

    Console.WriteLine($"  {qwords:N0} qwords scanned, {imagePointers.Count:N0} unique pointers probed, {slotsByTheGame.Count} direct TheGame slot(s)");

    var patternsFoundCount = 0;

    if (slotsByTheGame.Count == 0)
    {
        // Stage 2: PoE reaches TheGame through indirection. Find TheGame itself by scanning
        // the whole process for holders of the IngameState pointer (TheGame's State4 slot is
        // one of them), shape-check candidate bases, then find which heap holders of TheGame
        // sit inside an object that an image global points at. That join yields the chain:
        //   image slot -> container object -> +k -> TheGame.
        Console.WriteLine();
        Console.WriteLine("Stage 2: no direct global. Locating TheGame on the heap via its IngameState slot...");

        var theGames = new List<nint>();
        foreach (var holder in BubblesBot.Research.Probing.Toolkit.MemScan.RegionsRefsTo(reader, chain.IngameState, max: 2000))
        {
            var candidate = holder - KnownOffsets.TheGame.IngameState;
            if (TheGameResolver.LooksLikeTheGame(reader, candidate, chain.IngameState) && !theGames.Contains(candidate))
                theGames.Add(candidate);
        }
        Console.WriteLine($"  {theGames.Count} TheGame candidate(s) on the heap");
        if (theGames.Count == 0)
        {
            Console.Error.WriteLine("No heap object passes the TheGame shape check — KnownOffsets.TheGame has");
            Console.Error.WriteLine("likely drifted. Re-validate CurrentStatePtr/state-slot layout first.");
            return 1;
        }

        var sortedImagePtrs = imagePointers.Keys.OrderBy(p => (long)p).ToArray();
        foreach (var theGame in theGames)
        {
            Console.WriteLine();
            Console.WriteLine($"TheGame @ 0x{(long)theGame:X16}");
            PrintStateTable(reader, theGame, chain.IngameState);

            var holders = BubblesBot.Research.Probing.Toolkit.MemScan.RegionsRefsTo(reader, theGame, max: 500);
            Console.WriteLine($"  {holders.Count} holder(s) of TheGame across process memory");

            foreach (var holder in holders)
            {
                // Direct image global (stage 1 would have caught it; belt and braces).
                if (holder >= moduleStart && holder < moduleEnd)
                {
                    Console.WriteLine($"  Image global @ 0x{(long)holder:X16} holds TheGame directly");
                    PrintPatternsForSlot(process, reader, holder, ref patternsFoundCount);
                    continue;
                }

                // Heap holder: find an image global pointing at (or just below) it — that
                // global's target is the container object, and (holder - container) is the
                // field offset of the TheGame pointer inside it.
                foreach (var containerBase in NearestImagePointersAtOrBelow(sortedImagePtrs, holder, maxDistance: 0x4000))
                {
                    var k = (long)(holder - containerBase);
                    Console.WriteLine($"  Chain: image slot -> container 0x{(long)containerBase:X16} -> +0x{k:X} -> TheGame");
                    foreach (var slotAddr in imagePointers[containerBase])
                    {
                        Console.WriteLine($"    container slot @ 0x{(long)slotAddr:X16} (RVA 0x{(long)(slotAddr - process.MainModuleBase):X})  containerOffset=0x{k:X}");
                        PrintPatternsForSlot(process, reader, slotAddr, ref patternsFoundCount);
                    }
                }
            }
        }

        if (patternsFoundCount == 0)
        {
            Console.Error.WriteLine("TheGame located but no image-global chain found within 0x4000 of any holder.");
            Console.Error.WriteLine("Print the holder list above and analyze manually (deeper indirection).");
            return 1;
        }
        Console.WriteLine();
        Console.WriteLine($"Found {patternsFoundCount} pattern(s). Commit the container slot pattern into AobPatterns.TheGameRefs");
        Console.WriteLine("and the containerOffset into KnownOffsets (TheGameResolver follows slot -> container -> +offset).");
        return 0;
    }

    foreach (var (theGame, slots) in slotsByTheGame)
    {
        Console.WriteLine();
        Console.WriteLine($"TheGame @ 0x{(long)theGame:X16}");
        PrintStateTable(reader, theGame, chain.IngameState);

        foreach (var slotAddr in slots)
        {
            var slotRva = slotAddr - process.MainModuleBase;
            Console.WriteLine($"  Global slot @ 0x{(long)slotAddr:X16} (RVA 0x{(long)slotRva:X})");
            PrintPatternsForSlot(process, reader, slotAddr, ref patternsFoundCount);
        }
    }

    if (patternsFoundCount == 0)
    {
        Console.Error.WriteLine("Candidate found but no REX.W MOV r64,[RIP+rel32] references it.");
        Console.Error.WriteLine("The code may use a different instruction form; extend AobScanner.FindReferencesTo.");
        return 1;
    }

    Console.WriteLine();
    Console.WriteLine($"Found {patternsFoundCount} pattern(s). Copy the best one into AobPatterns.TheGameRefs:");
    Console.WriteLine();
    Console.WriteLine("  public static readonly Pattern[] TheGameRefs =");
    Console.WriteLine("  [");
    Console.WriteLine("      new Pattern(");
    Console.WriteLine("          Bytes:       <paste the byte?[] here>,");
    Console.WriteLine("          DispOffset:  11,");
    Console.WriteLine("          InstrLen:    15,");
    Console.WriteLine("          Description: \"TheGame global slot — PoE build YYYY-MM-DD (validated by --discover-thegame)\"),");
    Console.WriteLine("  ];");
    return 0;
}

// Watch GameStateView's reads live so the gate can be observed flipping across zone
// changes / logout. Requires AobPatterns.TheGameRefs to be committed.
static int RunWatchGameState(string[] args)
{
    using var process = ProcessHandle.AttachToPoE();
    if (process is null) { Console.Error.WriteLine("No PoE process found."); return 1; }

    var reader = new MemoryReader(process);
    var chain = BubblesBot.Research.Probing.Toolkit.ChainResolver.Resolve(process, reader, args);
    if (chain is null || !chain.IsValid) { Console.Error.WriteLine("Could not resolve IngameState."); return 1; }

    var slots = TheGameResolver.ResolveAndValidate(process, reader, chain.IngameState);
    if (slots.Count == 0)
    {
        Console.Error.WriteLine("TheGame slots did not resolve via AobPatterns.TheGameRefs.");
        Console.Error.WriteLine("Run --discover-thegame and commit the patterns first.");
        return 1;
    }

    var view = new BubblesBot.Core.Snapshot.GameStateView(reader, slots);
    Console.WriteLine($"{slots.Count} container slot(s); live TheGame 0x{(long)TheGameResolver.TryReadLiveTheGame(reader, slots):X16} — watching (Ctrl+C to stop)...");

    string? last = null;
    nint lastTheGame = -1;
    while (true)
    {
        var kind = view.ReadKind();
        var label = kind.ToString();
        var theGameNow = TheGameResolver.TryReadLiveTheGame(reader, slots);
        if (label != last || theGameNow != lastTheGame)
        {
            var note = theGameNow != lastTheGame && lastTheGame != -1 ? "  (container reallocated)" : "";
            Console.WriteLine($"  {DateTime.Now:HH:mm:ss.fff}  {label,-14} theGame=0x{(long)theGameNow:X16} current=0x{(long)view.CurrentStatePointer:X16}{note}");
            last = label; lastTheGame = theGameNow;
        }
        Thread.Sleep(50);
    }
}

// Deep diagnostic: diff-watch every TheGame heap copy, the game-root container (full window,
// including the embedded live TheGame), the three image slots, and IngameState.Data (which
// provably swaps on zone change), while polling POEMCP /state/ui for ExileCore's isLoading
// as ground truth. Purpose: find which memory actually changes during an area load so the
// game-state gate reads the right signal. Run it, zone once, read the correlated timeline.
static async Task<int> RunWatchTheGameDiff(string[] args)
{
    using var process = ProcessHandle.AttachToPoE();
    if (process is null) { Console.Error.WriteLine("No PoE process found."); return 1; }

    var reader = new MemoryReader(process);
    var chain = BubblesBot.Research.Probing.Toolkit.ChainResolver.Resolve(process, reader, args);
    if (chain is null || !chain.IsValid) { Console.Error.WriteLine("Could not resolve IngameState."); return 1; }
    Console.WriteLine($"IngameState: 0x{(long)chain.IngameState:X16}");

    var seconds = 180;
    var secIdx = Array.IndexOf(args, "--seconds");
    if (secIdx >= 0 && secIdx + 1 < args.Length && int.TryParse(args[secIdx + 1], out var s) && s > 0) seconds = s;

    // Containers + image slots via the committed patterns.
    var slots = new List<nint>();
    var containers = new List<nint>();
    foreach (var pattern in AobPatterns.TheGameRefs)
        foreach (var slot in AobScanner.ScanForResolvedAddresses(process, reader, pattern))
        {
            if (slots.Contains(slot)) continue;
            slots.Add(slot);
            if (reader.TryReadStruct<nint>(slot, out var c) && c != 0 && !containers.Contains(c))
                containers.Add(c);
        }
    Console.WriteLine($"{slots.Count} image slot(s), {containers.Count} container(s)");

    // All heap TheGame copies (including the container-embedded live one).
    var copies = new List<nint>();
    foreach (var holder in BubblesBot.Research.Probing.Toolkit.MemScan.RegionsRefsTo(reader, chain.IngameState, max: 2000))
    {
        var candidate = holder - KnownOffsets.TheGame.IngameState;
        if (TheGameResolver.LooksLikeTheGame(reader, candidate, chain.IngameState) && !copies.Contains(candidate))
            copies.Add(candidate);
    }
    Console.WriteLine($"{copies.Count} TheGame heap cop(ies): {string.Join(", ", copies.Select(a => $"0x{(long)a:X}"))}");

    // Watched windows: name -> (base, size, previous bytes).
    var windows = new List<(string Name, nint Base, byte[] Prev)>();
    foreach (var c in containers) windows.Add(($"container 0x{(long)c:X}", c, new byte[0xB20]));
    foreach (var t in copies)     windows.Add(($"copy 0x{(long)t:X}",      t, new byte[0x110]));

    // Prime baselines.
    foreach (var w in windows) reader.TryReadBytes(w.Base, w.Prev.AsSpan());

    var slotPrev = slots.Select(sl => reader.TryReadStruct<nint>(sl, out var v) ? v : 0).ToArray();
    reader.TryReadStruct<nint>(chain.IngameState + KnownOffsets.IngameState.Data, out var dataPrev);

    using var http = new HttpClient { BaseAddress = new Uri("http://localhost:5999"), Timeout = TimeSpan.FromMilliseconds(900) };
    string uiPrev = ""; var poemcpOk = true;

    var mutes = new Dictionary<string, int>();     // per-offset print counter -> auto-mute noisy fields
    const int MuteAfter = 6;

    Console.WriteLine($"Watching for {seconds}s — zone now. Timestamped changes below.");
    var deadline = DateTime.UtcNow.AddSeconds(seconds);
    var buf = new byte[0xB20];
    var lastHttp = DateTime.MinValue;

    while (DateTime.UtcNow < deadline)
    {
        var now = DateTime.Now;

        // Memory windows.
        foreach (var (name, baseAddr, prev) in windows)
        {
            var span = buf.AsSpan(0, prev.Length);
            if (reader.TryReadBytes(baseAddr, span) != prev.Length) continue;
            for (var off = 0; off + 8 <= prev.Length; off += 8)
            {
                var oldV = BitConverter.ToInt64(prev, off);
                var newV = BitConverter.ToInt64(span[off..]);
                if (oldV == newV) continue;
                var key = $"{name}+0x{off:X}";
                var n = mutes.TryGetValue(key, out var m) ? m : 0;
                mutes[key] = n + 1;
                if (n < MuteAfter)
                    Console.WriteLine($"  {now:HH:mm:ss.fff}  {key,-32} 0x{oldV:X16} -> 0x{newV:X16}{(n == MuteAfter - 1 ? "  (muting)" : "")}");
            }
            span.CopyTo(prev);
        }

        // Image slots.
        for (var i = 0; i < slots.Count; i++)
        {
            if (!reader.TryReadStruct<nint>(slots[i], out var v)) continue;
            if (v != slotPrev[i])
            {
                Console.WriteLine($"  {now:HH:mm:ss.fff}  image slot 0x{(long)slots[i]:X}: 0x{(long)slotPrev[i]:X16} -> 0x{(long)v:X16}");
                slotPrev[i] = v;
            }
        }

        // IngameData pointer — the zone-change marker.
        if (reader.TryReadStruct<nint>(chain.IngameState + KnownOffsets.IngameState.Data, out var dataNow) && dataNow != dataPrev)
        {
            Console.WriteLine($"  {now:HH:mm:ss.fff}  *** IngameState.Data swap: 0x{(long)dataPrev:X16} -> 0x{(long)dataNow:X16} (zone change) ***");
            dataPrev = dataNow;
        }

        // POEMCP ground truth (works unfocused; GET endpoints don't need the game thread).
        if (poemcpOk && (DateTime.UtcNow - lastHttp).TotalMilliseconds >= 250)
        {
            lastHttp = DateTime.UtcNow;
            try
            {
                var json = await http.GetStringAsync("/state/ui");
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var loading = doc.RootElement.TryGetProperty("isLoading", out var l) && l.GetBoolean();
                var inGame  = doc.RootElement.TryGetProperty("inGame", out var g) && g.GetBoolean();
                var ui = $"isLoading={loading} inGame={inGame}";
                if (ui != uiPrev)
                {
                    Console.WriteLine($"  {now:HH:mm:ss.fff}  POEMCP: {ui}");
                    uiPrev = ui;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {now:HH:mm:ss.fff}  POEMCP unreachable ({ex.GetType().Name}) — continuing without ground truth");
                poemcpOk = false;
            }
        }

        await Task.Delay(40);
    }
    Console.WriteLine("Watch window elapsed.");
    return 0;
}

// One-shot: for every mapped panel, dump ExileCore's ground truth (address + IsVisible +
// IsVisibleLocal, via /eval) side by side with our RAW flag chain read from the same
// address. No timing/window — run it once with a panel open, compare to the closed baseline.
// This is what nails the open-state visible-bit encoding without a live loop.
static async Task<int> RunSnapPanelFlags(string[] args)
{
    using var process = ProcessHandle.AttachToPoE();
    if (process is null) { Console.Error.WriteLine("No PoE process found."); return 1; }
    var reader = new MemoryReader(process);
    var chain = BubblesBot.Research.Probing.Toolkit.ChainResolver.Resolve(process, reader, args);
    if (chain is null || !chain.IsValid) { Console.Error.WriteLine("Could not resolve IngameState."); return 1; }
    if (!reader.TryReadStruct<nint>(chain.IngameState + KnownOffsets.IngameState.IngameUi, out var ingameUi) || ingameUi == 0)
    { Console.Error.WriteLine("IngameUi null."); return 1; }

    const int FlagsOff = KnownOffsets.Element.Flags;
    const int ParentOff = KnownOffsets.Element.Parent;

    var panels = new (string Panel, string ExprName, int Offset)[]
    {
        ("InventoryPanel", "InventoryPanel", KnownOffsets.IngameUiElements.InventoryPanel),
        ("StashElement",   "StashElement",   KnownOffsets.IngameUiElements.StashElement),
        ("AtlasPanel",     "AtlasPanel",     KnownOffsets.IngameUiElements.AtlasPanel),
        ("TreePanel",      "TreePanel",      KnownOffsets.IngameUiElements.TreePanel),
        ("NpcDialog",      "NpcDialog",      KnownOffsets.IngameUiElements.NpcDialog),
        ("SellWindow",     "SellWindow",     KnownOffsets.IngameUiElements.SellWindow),
        ("PurchaseWindow", "PurchaseWindow", KnownOffsets.IngameUiElements.PurchaseWindow),
    };

    string ChainDump(nint el)
    {
        var parts = new List<string>();
        var addr = el;
        for (var d = 0; d < 12 && addr != 0; d++)
        {
            if (!reader.TryReadStruct<uint>(addr + FlagsOff, out var flags)) { parts.Add($"0x{(long)addr:X}:??"); break; }
            parts.Add($"0x{(long)addr:X}:{flags:X8}{((flags & 0x800) != 0 ? "*" : "")}");
            if (!reader.TryReadStruct<nint>(addr + ParentOff, out var parent) || parent == addr) break;
            addr = parent;
        }
        return string.Join(" -> ", parts);
    }

    // REST /state/ui works without focus — use it as the open/closed ground truth. /eval
    // (address + IsVisibleLocal) is a bonus when the game happens to be focused.
    string restTruth = "(rest?)";
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        restTruth = await http.GetStringAsync("http://localhost:5999/state/ui");
    }
    catch { }

    using var poemcp = new PoemcpClient();

    Console.WriteLine();
    Console.WriteLine($"POEMCP /state/ui truth: {restTruth}");
    Console.WriteLine();
    Console.WriteLine("Panel snapshot — our raw flag chain (+ ExileCore eval when focused):");
    Console.WriteLine();
    foreach (var (panel, expr, offset) in panels)
    {
        var ours = reader.TryReadStruct<nint>(ingameUi + offset, out var p) ? p : 0;
        var r = await poemcp.EvalAsync($"((long)IngameState.IngameUi.{expr}.Address).ToString(\"X\") + \"|\" + IngameState.IngameUi.{expr}.IsVisible + \"|\" + IngameState.IngameUi.{expr}.IsVisibleLocal");
        var truth = r.Success ? r.AsString().Trim() : "(eval needs focus)";
        Console.WriteLine($"  {panel,-16} ours=0x{(long)ours:X}  exilecore={truth}");
        Console.WriteLine($"      chain: {(ours == 0 ? "(null)" : ChainDump(ours))}");
    }
    Console.WriteLine();
    Console.WriteLine("eval format = Address|IsVisible|IsVisibleLocal. '*' = our raw flags have bit 0x800 on that element.");
    return 0;
}

// Calibration dumper: for each mapped panel that POEMCP reports OPEN, print the raw flags
// (at Element.Flags 0x1D8) of the panel element and every ancestor up to the root, and the
// flags of the OpenLeft/RightPanel targets. Comparing open-vs-closed chains reveals exactly
// which element + bit encodes "visible now" — no guessing. One ~60s pass: open inventory,
// stash, and the passive tree, leaving each up ~3s.
static async Task<int> RunInspectPanelFlags(string[] args)
{
    using var process = ProcessHandle.AttachToPoE();
    if (process is null) { Console.Error.WriteLine("No PoE process found."); return 1; }
    var reader = new MemoryReader(process);
    var chain = BubblesBot.Research.Probing.Toolkit.ChainResolver.Resolve(process, reader, args);
    if (chain is null || !chain.IsValid) { Console.Error.WriteLine("Could not resolve IngameState."); return 1; }

    if (!reader.TryReadStruct<nint>(chain.IngameState + KnownOffsets.IngameState.IngameUi, out var ingameUi) || ingameUi == 0)
    { Console.Error.WriteLine("IngameUi null."); return 1; }

    const int FlagsOff = KnownOffsets.Element.Flags;   // 0x1D8
    const int ParentOff = KnownOffsets.Element.Parent; // 0x1D0

    // Panels POEMCP reports, mapped to our IngameUi offset.
    var map = new (string Panel, string UiKey, int Offset)[]
    {
        ("InventoryPanel", "inventoryVisible",  KnownOffsets.IngameUiElements.InventoryPanel),
        ("StashElement",   "stashVisible",      KnownOffsets.IngameUiElements.StashElement),
        ("AtlasPanel",     "atlasPanelVisible", KnownOffsets.IngameUiElements.AtlasPanel),
        ("NpcDialog",      "npcDialogVisible",  KnownOffsets.IngameUiElements.NpcDialog),
        ("SellWindow",     "sellWindowVisible", KnownOffsets.IngameUiElements.SellWindow),
        ("PurchaseWindow", "purchaseWindowVisible", KnownOffsets.IngameUiElements.PurchaseWindow),
    };

    string ChainDump(nint el)
    {
        var parts = new List<string>();
        var addr = el;
        for (var d = 0; d < 16 && addr != 0; d++)
        {
            if (!reader.TryReadStruct<uint>(addr + FlagsOff, out var flags)) { parts.Add($"0x{(long)addr:X}:??"); break; }
            parts.Add($"0x{(long)addr:X}:{flags:X8}{((flags & 0x800) != 0 ? "*" : "")}");
            if (!reader.TryReadStruct<nint>(addr + ParentOff, out var parent) || parent == addr) break;
            addr = parent;
        }
        return string.Join(" -> ", parts);
    }

    Console.WriteLine();
    Console.WriteLine("Panel-flags calibration — change-triggered, no timing needed. Over the next 5 min,");
    Console.WriteLine("open + close each at your own pace, any order: inventory (I), stash, passive tree (P).");
    Console.WriteLine("Every flag change is captured with its POEMCP state. '*' = bit 0x800 set on that element.");
    Console.WriteLine();

    using var http = new HttpClient { BaseAddress = new Uri("http://localhost:5999"), Timeout = TimeSpan.FromMilliseconds(900) };

    string PoemcpState()
    {
        try
        {
            var ui = System.Text.Json.JsonDocument.Parse(http.GetStringAsync("/state/ui").GetAwaiter().GetResult()).RootElement;
            var on = new List<string>();
            foreach (var pr in ui.EnumerateObject())
                if (pr.Value.ValueKind == System.Text.Json.JsonValueKind.True && pr.Name != "inGame") on.Add(pr.Name);
            return on.Count == 0 ? "(none)" : string.Join(",", on);
        }
        catch { return "(poemcp?)"; }
    }

    // Per-panel last-seen chain signature; print full chain whenever it changes.
    var lastSig = new Dictionary<string, string>();
    foreach (var (panel, _, _) in map) lastSig[panel] = "";

    var deadline = DateTime.UtcNow.AddSeconds(300);
    while (DateTime.UtcNow < deadline)
    {
        foreach (var (panel, uiKey, offset) in map)
        {
            var ptr = reader.TryReadStruct<nint>(ingameUi + offset, out var p) ? p : 0;
            var sig = ptr == 0 ? "(null ptr)" : ChainDump(ptr);
            if (sig == lastSig[panel]) continue;
            lastSig[panel] = sig;
            Console.WriteLine($"  {DateTime.Now:HH:mm:ss.fff}  {panel,-16} poemcp=[{PoemcpState()}]");
            Console.WriteLine($"      {sig}");
        }
        await Task.Delay(150);
    }
    Console.WriteLine("Calibration window elapsed.");
    return 0;
}

// Interactive UI-panel validation: prints a one-pass test script, then logs every panel
// Present/Visible flip (ours) alongside POEMCP /state/ui deltas (ExileCore's verdicts).
// One human session through the checklist validates open-detection, close-detection, and
// per-panel lifecycle (pointer-nulls-on-close vs stays-allocated-hidden) in a single log.
static async Task<int> RunWatchUiPanels(string[] args)
{
    using var process = ProcessHandle.AttachToPoE();
    if (process is null) { Console.Error.WriteLine("No PoE process found."); return 1; }

    var reader = new MemoryReader(process);
    var chain = BubblesBot.Research.Probing.Toolkit.ChainResolver.Resolve(process, reader, args);
    if (chain is null || !chain.IsValid) { Console.Error.WriteLine("Could not resolve IngameState."); return 1; }

    var seconds = 420;
    var secIdx = Array.IndexOf(args, "--seconds");
    if (secIdx >= 0 && secIdx + 1 < args.Length && int.TryParse(args[secIdx + 1], out var s) && s > 0) seconds = s;

    Console.WriteLine();
    Console.WriteLine("UI panel watch — run through this ONCE, ~5 min, any order, pausing ~2s between steps:");
    Console.WriteLine("  1. Inventory:  open (I), close (I)");
    Console.WriteLine("  2. Inventory:  open (I), close with Escape        <- validates Escape-close");
    Console.WriteLine("  3. Character/tree or World Map: open, close       <- left-dock panels");
    Console.WriteLine("  4. Stash:      open (click stash), close");
    Console.WriteLine("  5. Map device: open (click device), close");
    Console.WriteLine("  6. Atlas:      open (G), close");
    Console.WriteLine("  7. Vendor if one is in hideout: talk -> sell window -> close all");
    Console.WriteLine("  8. Right-click a currency item once (price menu), Escape");
    Console.WriteLine("  9. Anything else you fancy — every flip gets logged.");
    Console.WriteLine();

    var view = BubblesBot.Core.Snapshot.OpenPanelsView.FromIngameUi(reader, chain.IngameState);
    var prev = new Dictionary<string, (bool Present, bool Visible)>();
    foreach (var st in view.States) prev[st.Name] = (st.Present, st.Visible);
    Console.WriteLine($"Baseline: {view.States.Count} panels, open now: [{string.Join(", ", view.Open)}]");
    Console.WriteLine($"Watching for {seconds}s...");
    Console.WriteLine();

    using var http = new HttpClient { BaseAddress = new Uri("http://localhost:5999"), Timeout = TimeSpan.FromMilliseconds(900) };
    string poemcpPrev = ""; var poemcpOk = true;
    var lastHttp = DateTime.MinValue;

    var deadline = DateTime.UtcNow.AddSeconds(seconds);
    while (DateTime.UtcNow < deadline)
    {
        var now = DateTime.Now;
        view = BubblesBot.Core.Snapshot.OpenPanelsView.FromIngameUi(reader, chain.IngameState);
        foreach (var st in view.States)
        {
            var p = prev[st.Name];
            if (p.Present == st.Present && p.Visible == st.Visible) continue;
            var verdict = st.IsOpen ? "OPEN" : "closed";
            Console.WriteLine($"  {now:HH:mm:ss.fff}  {st.Name,-30} present {p.Present}->{st.Present}  visible {p.Visible}->{st.Visible}   => {verdict}");
            prev[st.Name] = (st.Present, st.Visible);
        }

        if (poemcpOk && (DateTime.UtcNow - lastHttp).TotalMilliseconds >= 300)
        {
            lastHttp = DateTime.UtcNow;
            try
            {
                var json = await http.GetStringAsync("/state/ui");
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var parts = new List<string>();
                foreach (var prop in doc.RootElement.EnumerateObject())
                    if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.True) parts.Add(prop.Name);
                var line = string.Join(",", parts);
                if (line != poemcpPrev)
                {
                    Console.WriteLine($"  {now:HH:mm:ss.fff}  POEMCP true-flags: [{line}]");
                    poemcpPrev = line;
                }
            }
            catch
            {
                Console.WriteLine($"  {now:HH:mm:ss.fff}  POEMCP unreachable — continuing without cross-check");
                poemcpOk = false;
            }
        }

        await Task.Delay(100);
    }
    Console.WriteLine("Watch window elapsed.");
    return 0;
}

// Discover the memory offset+bit backing ExileCore's Entity.IsHidden by statistical separation:
// POEMCP gives ground-truth (address, IsHidden) for every monster; we scan each entity's bytes
// (and its Render/Targetable/etc. component bytes) for a bit that perfectly separates hidden from
// shown across ALL of them. That bit is IsHidden. Needs a mix of hidden + shown mobs on screen.
static async Task<int> RunFindHiddenFlag(string[] args)
{
    using var process = ProcessHandle.AttachToPoE();
    if (process is null) { Console.Error.WriteLine("No PoE process found."); return 1; }
    var reader = new MemoryReader(process);

    using var poemcp = new PoemcpClient();
    if (!await poemcp.PingAsync()) { Console.Error.WriteLine("POEMCP not reachable (needs game focused)."); return 1; }

    var r = await poemcp.EvalAsync(
        "string.Join(\";\", EntityListWrapper.OnlyValidEntities" +
        ".Where(e => e.Type == ExileCore.Shared.Enums.EntityType.Monster && e.Path != null)" +
        ".Select(e => ((long)e.Address).ToString(\"X\") + \",\" + e.IsHidden))");
    if (!r.Success) { Console.Error.WriteLine("eval failed: " + r.Error); return 1; }

    var mobs = new List<(nint addr, bool hidden)>();
    foreach (var part in r.AsString().Split(';', StringSplitOptions.RemoveEmptyEntries))
    {
        var xy = part.Split(',');
        if (xy.Length == 2 && long.TryParse(xy[0], System.Globalization.NumberStyles.HexNumber, null, out var a))
            mobs.Add(((nint)a, bool.Parse(xy[1])));
    }
    var nHidden = mobs.Count(m => m.hidden);
    var nShown = mobs.Count - nHidden;
    Console.WriteLine($"{mobs.Count} monsters: {nHidden} hidden, {nShown} shown");
    if (nHidden == 0 || nShown == 0) { Console.Error.WriteLine("Need both hidden AND shown mobs on screen for contrast."); return 1; }

    // Scan a memory window per source. Report any (offset,bit) that perfectly separates hidden/shown.
    void Scan(string label, Func<nint, nint> resolve, int window)
    {
        Console.WriteLine($"--- scan {label} (0..0x{window:X}) ---");
        var bufs = new List<(byte[] b, bool hidden)>();
        foreach (var (addr, hidden) in mobs)
        {
            var baseAddr = resolve(addr);
            if (baseAddr == 0) continue;
            var buf = new byte[window];
            if (reader.TryReadBytes(baseAddr, buf.AsSpan()) < window) continue;
            bufs.Add((buf, hidden));
        }
        var hid = bufs.Where(x => x.hidden).ToList();
        var shn = bufs.Where(x => !x.hidden).ToList();
        if (hid.Count == 0 || shn.Count == 0) { Console.WriteLine("  (couldn't read enough)"); return; }
        var hits = 0;
        for (var off = 0; off < window; off++)
            for (var bit = 0; bit < 8; bit++)
            {
                int m = 1 << bit;
                bool h1 = hid.All(x => (x.b[off] & m) != 0), h0 = hid.All(x => (x.b[off] & m) == 0);
                bool s1 = shn.All(x => (x.b[off] & m) != 0), s0 = shn.All(x => (x.b[off] & m) == 0);
                if ((h1 && s0) || (h0 && s1))
                {
                    Console.WriteLine($"  SEP @ +0x{off:X} bit{bit}: hidden={(h1 ? 1 : 0)} shown={(s1 ? 1 : 0)}");
                    if (++hits > 60) { Console.WriteLine("  (too many)"); return; }
                }
            }
        if (hits == 0) Console.WriteLine("  none");
    }

    Scan("entity base", a => a, 0x400);
    Scan("Render comp", a => Comp(reader, a, "Render"), 0x200);
    Scan("Targetable comp", a => Comp(reader, a, "Targetable"), 0x40);
    Scan("Positioned comp", a => Comp(reader, a, "Positioned"), 0x200);

    // Verify the candidate: IsHidden == (EntityFlags@0x8C & 0x400) == 0.
    Console.WriteLine("--- verify: (flags@0x8C & 0x400)==0 as IsHidden ---");
    int match = 0, total = 0;
    foreach (var (addr, hidden) in mobs)
    {
        if (!reader.TryReadStruct<uint>(addr + 0x8C, out var fl)) continue;
        total++;
        var guess = (fl & 0x400) == 0;
        if (guess == hidden) match++;
        else Console.WriteLine($"  MISMATCH @0x{(long)addr:X}: flags=0x{fl:X8} guessHidden={guess} poemcpHidden={hidden}");
    }
    Console.WriteLine($"  {match}/{total} match");
    return 0;
}

// Definitive IsHidden discovery: snapshot each dormant mob's bytes, then watch (via POEMCP) for
// any to flip hidden→shown as the player approaches. Diff the SAME entity before/after — only the
// hidden bit(s) change. A bit that flips on EVERY observed transition (same direction) is IsHidden.
static async Task<int> RunFindHiddenDiff(string[] args)
{
    using var process = ProcessHandle.AttachToPoE();
    if (process is null) { Console.Error.WriteLine("No PoE process found."); return 1; }
    var reader = new MemoryReader(process);
    using var poemcp = new PoemcpClient();
    if (!await poemcp.PingAsync()) { Console.Error.WriteLine("POEMCP not reachable."); return 1; }

    const int Window = 0x400;
    async Task<Dictionary<uint, (nint addr, bool hidden)>> Snap()
    {
        var r = await poemcp.EvalAsync(
            "string.Join(\";\", EntityListWrapper.OnlyValidEntities" +
            ".Where(e => e.Type == ExileCore.Shared.Enums.EntityType.Monster && e.Path != null)" +
            ".Select(e => e.Id + \",\" + ((long)e.Address).ToString(\"X\") + \",\" + e.IsHidden))");
        var map = new Dictionary<uint, (nint, bool)>();
        if (r.Success)
            foreach (var p in r.AsString().Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = p.Split(',');
                if (f.Length == 3 && uint.TryParse(f[0], out var id) && long.TryParse(f[1], System.Globalization.NumberStyles.HexNumber, null, out var a))
                    map[id] = ((nint)a, bool.Parse(f[2]));
            }
        return map;
    }

    var start = await Snap();
    var mem0 = new Dictionary<uint, byte[]>();
    foreach (var (id, (addr, hidden)) in start)
        if (hidden) { var b = new byte[Window]; if (reader.TryReadBytes(addr, b.AsSpan()) == Window) mem0[id] = b; }
    Console.WriteLine($"snapshotted {mem0.Count} hidden mobs. WALK INTO THE DORMANT PACK to wake them. Watching 90s…");

    // (off,bit) -> (flips01 count, flips10 count) across observed hidden->shown transitions
    var tally = new Dictionary<(int off, int bit), (int to1, int to0)>();
    var handled = new HashSet<uint>();
    var transitions = 0;
    var deadline = DateTime.UtcNow.AddSeconds(90);
    while (DateTime.UtcNow < deadline)
    {
        var now = await Snap();
        foreach (var (id, (addr, hiddenNow)) in now)
        {
            if (handled.Contains(id) || !mem0.ContainsKey(id) || hiddenNow) continue;
            // id was hidden, now shown → diff
            var b1 = new byte[Window];
            if (reader.TryReadBytes(addr, b1.AsSpan()) != Window) continue;
            var b0 = mem0[id];
            handled.Add(id); transitions++;
            for (var off = 0; off < Window; off++)
                for (var bit = 0; bit < 8; bit++)
                {
                    int m = 1 << bit;
                    var was = (b0[off] & m) != 0; var isNow = (b1[off] & m) != 0;
                    if (was == isNow) continue;
                    var key = (off, bit);
                    var t = tally.TryGetValue(key, out var v) ? v : (0, 0);
                    if (isNow) t.Item1++; else t.Item2++;
                    tally[key] = t;
                }
            Console.WriteLine($"  transition #{transitions}: mob {id} hidden->shown");
        }
        if (transitions >= 4) break;
        await Task.Delay(500);
    }

    Console.WriteLine($"\n{transitions} transitions observed. Bits that flipped consistently on ALL of them:");
    foreach (var kv in tally.OrderByDescending(k => Math.Max(k.Value.to1, k.Value.to0)))
    {
        var (off, bit) = kv.Key; var (to1, to0) = kv.Value;
        if (to1 == transitions || to0 == transitions)
            Console.WriteLine($"  +0x{off:X} bit {bit} (mask 0x{1 << bit:X}): hidden->shown sets bit to {(to1 == transitions ? 0 : 1)}  [flipped on all {transitions}]");
    }
    return 0;
}

static nint Comp(MemoryReader reader, nint entityAddr, string name)
{
    var map = BubblesBot.Core.Game.EntityComponents.ReadComponentMap(reader, entityAddr);
    return map.TryGetValue(name, out var a) ? a : 0;
}

// Dump the resurrect panel's descendant labels + rects so we confirm the checkpoint button's
// text/location for the revive handler. Direct memory reads — no /eval, works on the death screen.
static int RunInspectResurrect(string[] args)
{
    using var process = ProcessHandle.AttachToPoE();
    if (process is null) { Console.Error.WriteLine("No PoE process found."); return 1; }
    var reader = new MemoryReader(process);
    var chain = BubblesBot.Research.Probing.Toolkit.ChainResolver.Resolve(process, reader, args);
    if (chain is null || !chain.IsValid) { Console.Error.WriteLine("Could not resolve IngameState."); return 1; }

    var rp = BubblesBot.Core.Snapshot.ResurrectPanelView.FromIngameUi(reader, chain.IngameState);
    Console.WriteLine($"ResurrectPanel visible: {rp.IsVisible}");
    Console.WriteLine("Descendants with text:");
    foreach (var (addr, text) in rp.Descendants())
    {
        if (string.IsNullOrWhiteSpace(text)) continue;
        var rect = BubblesBot.Core.Snapshot.ElementGeometry.TryReadRect(reader, addr);
        var rc = rect is { } r ? $"center=({r.CenterX:F0},{r.CenterY:F0}) {r.Width:F0}x{r.Height:F0}" : "(no rect)";
        Console.WriteLine($"  0x{(long)addr:X}  \"{text}\"  {rc}");
    }
    var cp = rp.CheckpointButtonRect();
    var tn = rp.TownButtonRect();
    Console.WriteLine($"Checkpoint button rect: {(cp is { } c ? $"center=({c.CenterX:F0},{c.CenterY:F0})" : "NOT FOUND")}");
    Console.WriteLine($"Town button rect:       {(tn is { } t ? $"center=({t.CenterX:F0},{t.CenterY:F0})" : "NOT FOUND")}");
    return 0;
}

// Dump TheGame's CurrentStatePtr + 12 state slots so a human can eyeball the shape.
static void PrintStateTable(MemoryReader reader, nint theGame, nint ingameState)
{
    reader.TryReadStruct<nint>(theGame + KnownOffsets.TheGame.CurrentStatePtr, out var current);
    Console.WriteLine($"  CurrentStatePtr: 0x{(long)current:X16}");
    for (var s = 0; s < KnownOffsets.TheGame.StateSlotCount; s++)
    {
        var off = KnownOffsets.TheGame.StateSlot0 + s * KnownOffsets.TheGame.StateSlotStride;
        reader.TryReadStruct<nint>(theGame + off, out var state);
        var tag = state == 0 ? "" :
                  state == current && state == ingameState ? "  <-- ACTIVE (IngameState)" :
                  state == current ? "  <-- ACTIVE" :
                  state == ingameState ? "  (IngameState)" : "";
        Console.WriteLine($"    slot[{s,2}] +0x{off:X3}: 0x{(long)state:X16}{tag}");
    }
}

// Find every REX.W MOV r64,[RIP+rel32] referencing the slot and print paste-ready patterns.
// When the strict form finds nothing, fall back to a form-agnostic rel32 sweep and dump the
// surrounding bytes so the instruction can be identified by hand.
static void PrintPatternsForSlot(ProcessHandle process, MemoryReader reader, nint slotAddr, ref int patternsFound)
{
    var hits = AobScanner.FindReferencesTo(process, reader, slotAddr, contextBytes: 24);
    Console.WriteLine($"    {hits.Count} code reference(s)");
    foreach (var hit in hits)
    {
        patternsFound++;
        var textRva = hit.SectionBase - process.MainModuleBase + hit.MatchOffset;
        Console.WriteLine($"    +- Reference @ .text RVA 0x{(long)textRva:X}");
        Console.WriteLine($"    |  Instruction: {FormatHex(hit.Context.AsSpan(hit.InstructionOffset, 7))}");
        Console.WriteLine($"    |  Pattern (8 bytes context before, 4 after, displacement wildcarded):");
        Console.WriteLine($"    |    {hit.FormatPattern(8, 4)}");
        Console.WriteLine($"    |    // DispOffset=11, InstrLen=15  (8 prefix bytes + instruction-relative 3/7)");
        Console.WriteLine($"    +-");
    }
    if (hits.Count > 0) return;

    var generic = AobScanner.FindAnyRipReferencesTo(process, reader, slotAddr, contextBytes: 16);
    Console.WriteLine($"    {generic.Count} generic rel32 reference(s) (identify the opcode manually):");
    foreach (var g in generic)
    {
        patternsFound += 0; // context only — not a paste-ready pattern
        var dispRva = g.SectionBase - process.MainModuleBase + g.DispPos;
        var pre  = g.Context.AsSpan(0, g.ContextDispPos);
        var disp = g.Context.AsSpan(g.ContextDispPos, 4);
        var post = g.Context.AsSpan(g.ContextDispPos + 4);
        Console.WriteLine($"      disp @ RVA 0x{(long)dispRva:X}  tail={g.TailLen}");
        Console.WriteLine($"        {FormatHex(pre)} [{FormatHex(disp)}] {FormatHex(post)}");
    }
}

// Image-global pointer values that sit at or below `addr`, within `maxDistance` bytes.
// Used to find the container object whose field at (addr - value) holds the target.
static IEnumerable<nint> NearestImagePointersAtOrBelow(nint[] sortedValues, nint addr, long maxDistance)
{
    // Binary search for the insertion point of addr, then walk downward.
    var lo = 0; var hi = sortedValues.Length;
    while (lo < hi)
    {
        var mid = (lo + hi) / 2;
        if ((long)sortedValues[mid] <= (long)addr) lo = mid + 1; else hi = mid;
    }
    for (var i = lo - 1; i >= 0; i--)
    {
        var dist = (long)addr - (long)sortedValues[i];
        if (dist > maxDistance) yield break;
        yield return sortedValues[i];
    }
}

static string FormatHex(ReadOnlySpan<byte> bytes)
{
    var sb = new System.Text.StringBuilder();
    foreach (var b in bytes) sb.Append($"{b:X2} ");
    return sb.ToString().TrimEnd();
}
