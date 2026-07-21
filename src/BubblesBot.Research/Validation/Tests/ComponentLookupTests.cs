using BubblesBot.Core.Game;

namespace BubblesBot.Research.Validation.Tests;

internal static class ComponentLookupKeys
{
    public const string PlayerComponentMap = "map.PlayerComponents";
}

public sealed class PlayerComponentMapTest : ValidationTest
{
    public override string Name => "Player.ComponentMap â€” read full component map";
    public override string? Group => "Component lookup";

    public override Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(StateKeys.LocalPlayer, out var pObj) || pObj is not nint player)
            return Task.FromResult<TestOutcome>(new TestOutcome.Skip(Name, "LocalPlayer not resolved"));

        var map = EntityComponents.ReadComponentMap(ctx.Reader, player);
        if (map.Count == 0)
            return Task.FromResult<TestOutcome>(new TestOutcome.Fail(Name, "empty component map â€” chain is broken somewhere"));

        ctx.State[ComponentLookupKeys.PlayerComponentMap] = map;
        var sample = string.Join(", ", map.Keys.Take(8));
        return Task.FromResult<TestOutcome>(new TestOutcome.Pass(Name, $"{map.Count} components: {sample}{(map.Count > 8 ? ", â€¦" : "")}"));
    }
}

/// <summary>
/// For each named component the player should have, ask POEMCP for its address and assert our map
/// returns the same address. Fails on mismatch *or* missing entry â€” both signal broken lookup chain.
/// </summary>
public sealed class PlayerComponentAddressMatchTest : ValidationTest
{
    private readonly string _component;
    private readonly string _evalExpr;

    public PlayerComponentAddressMatchTest(string componentName, string? poemcpEvalExpr = null)
    {
        _component = componentName;
        _evalExpr = poemcpEvalExpr ?? $"Player.GetComponent<{componentName}>().Address.ToString(\"X\")";
    }

    public override string Name => $"Player.GetComponent<{_component}> matches our lookup";
    public override string? Group => "Component lookup";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(ComponentLookupKeys.PlayerComponentMap, out var mObj)
            || mObj is not Dictionary<string, nint> map)
            return new TestOutcome.Skip(Name, "component map not resolved");

        var r = await ctx.Poemcp.EvalAsync(_evalExpr, ct);
        if (!r.Success) return new TestOutcome.Fail(Name, $"POEMCP error: {r.Error}");
        var truth = r.AsAddress();
        if (truth == 0) return new TestOutcome.Skip(Name, "POEMCP returned null â€” player doesn't have this component");

        if (!map.TryGetValue(_component, out var ours))
            return new TestOutcome.Fail(Name, $"missing in our map (POEMCP says 0x{truth:X}); map keys: {string.Join(",", map.Keys)}");

        if (ours != truth)
            return new TestOutcome.Fail(Name, $"ours 0x{ours:X} â‰  truth 0x{truth:X}");

        return new TestOutcome.Pass(Name, $"both â†’ 0x{ours:X16}");
    }
}

/// <summary>
/// Validates LifeComponent layout via the validated component lookup (i.e. NOT via the value-scan we used initially).
/// This confirms we can route "show me the player's HP" through the same chain a real bot would use.
/// </summary>
public sealed class LifeViaComponentLookupTest : ValidationTest
{
    public override string Name => "Life.CurHP via component lookup matches POEMCP";
    public override string? Group => "Component lookup";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(ComponentLookupKeys.PlayerComponentMap, out var mObj)
            || mObj is not Dictionary<string, nint> map)
            return new TestOutcome.Skip(Name, "component map not resolved");

        if (!map.TryGetValue("Life", out var lifeAddr))
            return new TestOutcome.Fail(Name, "no 'Life' entry in component map");

        if (!ctx.Reader.TryReadStruct<VitalStruct>(lifeAddr + KnownOffsets.LifeComponent.Health, out var health))
            return new TestOutcome.Fail(Name, "could not read Health VitalStruct");

        var r = await ctx.Poemcp.EvalAsync("Player.GetComponent<Life>().CurHP", ct);
        if (!r.Success) return new TestOutcome.Fail(Name, $"POEMCP error: {r.Error}");
        var truth = r.AsInt();

        if (health.Current != truth)
            return new TestOutcome.Fail(Name, $"ours {health.Current} â‰  truth {truth}");

        return new TestOutcome.Pass(Name, $"both â†’ {health.Current} HP");
    }
}
