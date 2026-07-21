using BubblesBot.Core.Game;

namespace BubblesBot.Research.Validation.Tests;

/// <summary>
/// POEMCP-free anchor: walks back from a value-scan-found LifeComponent to the player Entity,
/// then to IngameData (search memory for any pointer to LocalPlayer landing 0x8E8 from a base
/// whose other fields look right), then to IngameState. Slow (re-scans memory) but independent.
///
/// Used as a fallback when POEMCP is unreachable â€” without it, we'd have no IngameState address
/// to validate against.
/// </summary>
public sealed class FindLifeViaValueScanTest : ValidationTest
{
    public override string Name => "Find LifeComponent via value scan (POEMCP-free anchor)";
    public override string? Group => "Anchor (POEMCP-free)";

    private readonly int _hp;
    private readonly int? _mana;
    private readonly int? _esMax;

    public FindLifeViaValueScanTest(int hpCurrent, int? manaCurrent = null, int? esMax = null)
    {
        _hp = hpCurrent;
        _mana = manaCurrent;
        _esMax = esMax;
    }

    public override Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        // Skip if POEMCP-derived player address is already available â€” no need to scan.
        if (ctx.State.ContainsKey(StateKeys.LocalPlayer))
            return Task.FromResult<TestOutcome>(new TestOutcome.Skip(Name, "LocalPlayer already resolved via POEMCP"));

        var matches = LifeValidator.FindCandidates(ctx.Reader, _hp, _mana, _esMax);
        if (matches.Count == 0)
            return Task.FromResult<TestOutcome>(new TestOutcome.Fail(Name, "no candidates â€” has HP changed since you launched the test?"));
        if (matches.Count > 1)
            return Task.FromResult<TestOutcome>(new TestOutcome.Fail(Name, $"{matches.Count} candidates â€” pass --mana-current and --es-max to narrow"));

        var match = matches[0];
        ctx.State["addr.LifeComponent"] = match.LifeComponentAddress;
        ctx.State[StateKeys.LocalPlayer] = match.OwnerAddress; // LifeComponent.Owner â†’ player Entity
        return Task.FromResult<TestOutcome>(new TestOutcome.Pass(Name, $"LifeComponent @ 0x{match.LifeComponentAddress:X16}, player Entity @ 0x{match.OwnerAddress:X16}"));
    }
}

/// <summary>
/// POEMCP-free IngameData finder: scans memory for any 8-byte-aligned location holding a pointer
/// equal to the player Entity address; treats (location - 0x8E8) as a candidate IngameData base
/// and validates by checking sibling offsets (EntityList @ +0x9A0, ServerData @ +0x8E0) are also
/// plausible pointers.
/// </summary>
public sealed class FindIngameDataFromPlayerTest : ValidationTest
{
    public override string Name => "Find IngameData via player Entity back-walk";
    public override string? Group => "Anchor (POEMCP-free)";

    public override Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (ctx.State.ContainsKey(StateKeys.IngameData))
            return Task.FromResult<TestOutcome>(new TestOutcome.Skip(Name, "IngameData already resolved via POEMCP"));

        if (!ctx.State.TryGetValue(StateKeys.LocalPlayer, out var pObj) || pObj is not nint player)
            return Task.FromResult<TestOutcome>(new TestOutcome.Skip(Name, "player Entity not yet resolved"));

        var hits = BubblesBot.Core.Game.AnchorBackWalker.FindIngameDataFromPlayer(ctx.Reader, player);
        if (hits.Count == 0) return Task.FromResult<TestOutcome>(new TestOutcome.Fail(Name, "no candidate found"));
        if (hits.Count > 1)
            return Task.FromResult<TestOutcome>(new TestOutcome.Fail(Name, $"{hits.Count} candidates â€” back-walk filter not tight enough"));

        var hit = hits[0];
        ctx.State[StateKeys.IngameData] = hit.IngameDataAddress;
        ctx.State[StateKeys.EntityList] = hit.EntityList;
        ctx.State[StateKeys.ServerData] = hit.ServerData;
        return Task.FromResult<TestOutcome>(new TestOutcome.Pass(Name,
            $"IngameData @ 0x{hit.IngameDataAddress:X16}, EntityList @ 0x{hit.EntityList:X}, ServerData @ 0x{hit.ServerData:X}"));
    }
}
