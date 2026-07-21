using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;
using BubblesBot.Research.Probing;
using BubblesBot.Research.Probing.Toolkit;

namespace BubblesBot.Research.Probes.Chain;

/// <summary>
/// The game-state gate: <c>AobPatterns.TheGameRefs</c> must resolve TheGame, and the resolved
/// object's state table must be coherent (CurrentStatePtr matches a slot; the State4 slot is
/// the same IngameState the chain resolved independently). This is the per-patch health check
/// for the bot's loading-screen gate — if it fails, the bot refuses to boot until
/// <c>--discover-thegame</c> produces fresh patterns.
///
/// <para>Structural only: no baseline facts needed. The cross-check against
/// <c>ctx.Chain.IngameState</c> (resolved via a different AOB + roundtrip validation) is the
/// authority — a stale TheGame pattern cannot accidentally agree with it.</para>
/// </summary>
public sealed class TheGameProbe : IProbe
{
    public string Name => "thegame.gate";
    public string Group => "chain";
    public string Description => "TheGameRefs AOB resolves TheGame; state table coherent; gate reads InGame.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx)
    {
        if (AobPatterns.TheGameRefs.Length == 0)
            return ProbeResult.Fail("no TheGameRefs patterns committed — run --discover-thegame");

        var slots = TheGameResolver.ResolveSlotsViaAob(ctx.Reader.Process, ctx.Reader);
        if (slots.Count == 0)
            return ProbeResult.Fail("TheGameRefs patterns matched nothing — run --discover-thegame");

        // The container is reallocated per zone change; follow the live chain fresh.
        var theGame = TheGameResolver.TryReadLiveTheGame(ctx.Reader, slots);
        if (theGame == 0)
            return ProbeResult.Fail($"{slots.Count} slot(s) resolved but the live chain is null — "
                + "either mid-transition (retry) or ContainerTheGamePtr drifted");

        if (!TheGameResolver.LooksLikeTheGame(ctx.Reader, theGame, ctx.Chain.IngameState))
            return ProbeResult.Fail($"live TheGame@0x{(long)theGame:X} failed shape check vs chain IngameState");

        // ChainResolver only succeeds with a live IngameState, so the expected kind here is
        // InGame; anything else means the CurrentStatePtr/slot layout drifted.
        var kind = new GameStateView(ctx.Reader, slots).ReadKind();
        return kind == GameStateKind.InGame
            ? ProbeResult.Pass($"{slots.Count} slot(s); live TheGame = 0x{(long)theGame:X}; gate reads {kind}")
            : ProbeResult.Fail($"live TheGame = 0x{(long)theGame:X} resolved but gate reads {kind} while chain is live");
    }

    public ProbeResult Discover(ProbeContext ctx)
    {
        // Locate TheGame on the heap by its IngameState slot (same derivation as stage 2 of
        // --discover-thegame). This proves whether the STRUCT layout still holds — pattern
        // regeneration itself stays in the CLI command, which also prints paste-ready output.
        var found = new List<nint>();
        foreach (var holder in MemScan.RegionsRefsTo(ctx.Reader, ctx.Chain.IngameState, max: 2000))
        {
            var candidate = holder - KnownOffsets.TheGame.IngameState;
            if (TheGameResolver.LooksLikeTheGame(ctx.Reader, candidate, ctx.Chain.IngameState) && !found.Contains(candidate))
                found.Add(candidate);
        }
        return found.Count > 0
            ? ProbeResult.Pass($"layout holds — {found.Count} TheGame candidate(s) on heap "
                + $"({string.Join(", ", found.Select(a => $"0x{(long)a:X}"))}); run --discover-thegame for patterns")
            : ProbeResult.Fail("no heap object passes the TheGame shape check — state-slot layout drifted");
    }
}
