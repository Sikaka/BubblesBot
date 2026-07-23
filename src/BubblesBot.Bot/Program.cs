using BubblesBot.Bot;
using BubblesBot.Bot.LiveTests;
using BubblesBot.Bot.Knowledge;
using BubblesBot.Core;
using BubblesBot.Core.Snapshot;

if (args.Length > 0
    && args[0].Equals("--export-atlas-knowledge", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1]))
    {
        Console.Error.WriteLine("Usage: BubblesBot --export-atlas-knowledge <output-json>");
        return 64;
    }
    try
    {
        var observations = AtlasKnowledgePromotion.LoadMerged(
            AtlasKnowledgePromotion.DefaultObservationPath);
        AtlasKnowledgePromotion.WriteCandidate(observations, Path.GetFullPath(args[1]), promotableOnly: true);
        var promoted = AtlasKnowledgePromotion.PromotableOnly(
            AtlasKnowledgePromotion.BuildCandidates(observations));
        Console.WriteLine($"Exported {promoted.Maps.Count} sanitized Atlas map(s) to {Path.GetFullPath(args[1])}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Atlas knowledge export failed: {ex.Message}");
        return 1;
    }
}

var liveTestParse = LiveTestOptions.Parse(args);
if (!liveTestParse.Success)
{
    Console.Error.WriteLine(liveTestParse.Error);
    LiveTestRegistry.PrintUsage(Console.Error);
    return 64;
}

var liveTestOptions = liveTestParse.Options!;
if (liveTestOptions.Command == LiveTestCommand.List)
{
    LiveTestRegistry.PrintCatalog(Console.Out);
    return 0;
}

Console.WriteLine(liveTestOptions.Command == LiveTestCommand.Run
    ? "BubblesBot — guarded live-test host"
    : "BubblesBot — looter test");
Console.WriteLine("===================================");

// ── Attach to PoE ─────────────────────────────────────────────────────────

using var process = ProcessHandle.AttachToPoE();
if (process is null)
{
    Console.Error.WriteLine("PoE not running.");
    return 1;
}
Console.WriteLine($"Attached to {process.ProcessName} (PID {process.ProcessId})");

// Character selection tears down the normal IngameState bootstrap chain. This one narrowly
// scoped visual recovery test therefore runs before memory bootstrap, while still using the
// production InputRouter and strict foreground/identity gates.
if (liveTestOptions.Command == LiveTestCommand.Run
    && string.Equals(liveTestOptions.TestId, CharacterSelectPlayRecoveryLiveTest.TestId, StringComparison.OrdinalIgnoreCase))
{
    return await CharacterSelectRecoveryHost.RunAsync(process, liveTestOptions);
}
if (liveTestOptions.Command == LiveTestCommand.Run
    && string.Equals(liveTestOptions.TestId, CharacterSelectInspectLiveTest.TestId, StringComparison.OrdinalIgnoreCase))
{
    return CharacterSelectRecoveryHost.Inspect(process);
}

var reader = new MemoryReader(process);

// ── Bootstrap IngameData ──────────────────────────────────────────────────

var (ingameDataAddr, ingameStateAddr, theGameSlots) = Bootstrap.ResolveIngameData(process, reader, args);
if (ingameDataAddr == 0)
    return 1;

// ── Canary check ──────────────────────────────────────────────────────────

var canary = CanaryCheck.Run(reader, ingameDataAddr, ingameStateAddr);
if (!canary.Passed)
{
    Console.Error.WriteLine("Canary checks failed — offset table looks stale:");
    foreach (var f in canary.Failures)
        Console.Error.WriteLine($"  - {f}");
    Console.Error.WriteLine("Run BubblesBot.Research to regenerate offsets, then retry.");
    return 2;
}
Console.WriteLine("Canary checks passed.");

if (liveTestOptions.Command == LiveTestCommand.Run)
{
    return await LiveTestHost.RunAsync(
        process,
        reader,
        ingameDataAddr,
        ingameStateAddr,
        theGameSlots,
        liveTestOptions);
}

// ── Run ───────────────────────────────────────────────────────────────────

Console.WriteLine("Starting bot. Configure via web UI at http://localhost:5666");
Console.WriteLine("Press Insert in-game to ARM/DISARM. Press F12 to hide/show the HUD panels (map hack + HP bars stay on); click OPEN WEB UI for the dashboard. Press Ctrl+C in this terminal to exit.");

using var app = new BotApp(process, reader, ingameDataAddr, ingameStateAddr, theGameSlots);

// Wire Ctrl+C → bot shutdown. Setting e.Cancel=true stops the runtime from killing us
// abruptly so the message pump can exit cleanly and Dispose runs.
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("Ctrl+C — shutting down...");
    app.RequestShutdown();
};

app.Run();

Console.WriteLine("Done.");
return 0;
