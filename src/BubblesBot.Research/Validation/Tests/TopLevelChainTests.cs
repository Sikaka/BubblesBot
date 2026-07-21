using BubblesBot.Core.Game;

namespace BubblesBot.Research.Validation.Tests;

/// <summary>
/// Validates IngameState / IngameData / LocalPlayer offsets by asking POEMCP for the addresses
/// directly, then walking each pointer chain through our reader and asserting we land at the same address.
///
/// Each test stashes the resolved address in TestContext.State so downstream tests don't re-fetch it.
/// </summary>
internal static class StateKeys
{
    public const string IngameState = "addr.IngameState";
    public const string IngameData  = "addr.IngameData";
    public const string LocalPlayer = "addr.LocalPlayer";
    public const string EntityList  = "addr.EntityList";
    public const string ServerData  = "addr.ServerData";
    public const string IngameUi    = "addr.IngameUi";
}

public sealed class IngameStateAddressTest : ValidationTest
{
    public override string Name => "IngameState â€” fetch base address from POEMCP";
    public override string? Group => "Top-level chain";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        var r = await ctx.Poemcp.EvalAsync("IngameState.Address.ToString(\"X\")", ct);
        if (!r.Success) return new TestOutcome.Fail(Name, $"POEMCP error: {r.Error}");

        var addr = r.AsAddress();
        if (addr == 0) return new TestOutcome.Fail(Name, "got null address");

        // Sanity-read 8 bytes â€” should be a non-zero vtable or first field. If unreadable, the
        // address is bogus or the process isn't fully loaded.
        if (!ctx.Reader.TryReadStruct<long>(addr, out var firstQword) || firstQword == 0)
            return new TestOutcome.Fail(Name, $"address 0x{addr:X} not readable / null");

        ctx.State[StateKeys.IngameState] = addr;
        return new TestOutcome.Pass(Name, $"@ 0x{addr:X16}, first qword 0x{firstQword:X16}");
    }
}

public sealed class IngameStateDataOffsetTest : ValidationTest
{
    public override string Name => "IngameState.Data (+0x218) â†’ IngameData";
    public override string? Group => "Top-level chain";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(StateKeys.IngameState, out var sObj) || sObj is not nint ingameState)
            return new TestOutcome.Skip(Name, "IngameState address not resolved");

        // Our reader: walk IngameState + 0x218 â†’ IngameData
        var ours = ctx.Reader.ReadPointer(ingameState + KnownOffsets.IngameState.Data);

        // POEMCP truth: IngameState.Data.Address
        var r = await ctx.Poemcp.EvalAsync("IngameState.Data.Address.ToString(\"X\")", ct);
        if (!r.Success) return new TestOutcome.Fail(Name, $"POEMCP error: {r.Error}");
        var truth = r.AsAddress();

        if (ours != truth)
            return new TestOutcome.Fail(Name, $"ours 0x{ours:X} â‰  truth 0x{truth:X}");

        ctx.State[StateKeys.IngameData] = ours;
        return new TestOutcome.Pass(Name, $"both â†’ 0x{ours:X16}");
    }
}

public sealed class IngameDataLocalPlayerTest : ValidationTest
{
    public override string Name => "IngameData.LocalPlayer (+0x8E8) â†’ player Entity";
    public override string? Group => "Top-level chain";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(StateKeys.IngameData, out var dObj) || dObj is not nint ingameData)
            return new TestOutcome.Skip(Name, "IngameData address not resolved");

        var ours = ctx.Reader.ReadPointer(ingameData + KnownOffsets.IngameData.LocalPlayer);

        var r = await ctx.Poemcp.EvalAsync("Player.Address.ToString(\"X\")", ct);
        if (!r.Success) return new TestOutcome.Fail(Name, $"POEMCP error: {r.Error}");
        var truth = r.AsAddress();

        if (ours != truth)
            return new TestOutcome.Fail(Name, $"ours 0x{ours:X} â‰  truth 0x{truth:X}");

        ctx.State[StateKeys.LocalPlayer] = ours;
        return new TestOutcome.Pass(Name, $"both â†’ 0x{ours:X16}");
    }
}

public sealed class IngameDataEntityListTest : ValidationTest
{
    public override string Name => "IngameData.EntityList (+0x9A0)";
    public override string? Group => "Top-level chain";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(StateKeys.IngameData, out var dObj) || dObj is not nint ingameData)
            return new TestOutcome.Skip(Name, "IngameData not resolved");

        var ours = ctx.Reader.ReadPointer(ingameData + KnownOffsets.IngameData.EntityList);

        var r = await ctx.Poemcp.EvalAsync("EntityListWrapper.EntityList.Address.ToString(\"X\")", ct);
        // Some ExileCore builds expose this differently â€” try alternatives if first fails.
        if (!r.Success)
            r = await ctx.Poemcp.EvalAsync("IngameState.Data.EntityList.Address.ToString(\"X\")", ct);
        if (!r.Success) return new TestOutcome.Fail(Name, $"POEMCP error: {r.Error}");
        var truth = r.AsAddress();

        if (ours != truth)
            return new TestOutcome.Fail(Name, $"ours 0x{ours:X} â‰  truth 0x{truth:X}");

        ctx.State[StateKeys.EntityList] = ours;
        return new TestOutcome.Pass(Name, $"both â†’ 0x{ours:X16}");
    }
}

public sealed class IngameDataServerDataTest : ValidationTest
{
    public override string Name => "IngameData.ServerData (+0x8E0)";
    public override string? Group => "Top-level chain";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(StateKeys.IngameData, out var dObj) || dObj is not nint ingameData)
            return new TestOutcome.Skip(Name, "IngameData not resolved");

        var ours = ctx.Reader.ReadPointer(ingameData + KnownOffsets.IngameData.ServerData);

        var r = await ctx.Poemcp.EvalAsync("IngameState.ServerData.Address.ToString(\"X\")", ct);
        if (!r.Success) return new TestOutcome.Fail(Name, $"POEMCP error: {r.Error}");
        var truth = r.AsAddress();

        if (ours != truth)
            return new TestOutcome.Fail(Name, $"ours 0x{ours:X} â‰  truth 0x{truth:X}");

        ctx.State[StateKeys.ServerData] = ours;
        return new TestOutcome.Pass(Name, $"both â†’ 0x{ours:X16}");
    }
}

public sealed class IngameStateIngameUiTest : ValidationTest
{
    public override string Name => "IngameState.IngameUi (+0x8F0)";
    public override string? Group => "Top-level chain";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(StateKeys.IngameState, out var sObj) || sObj is not nint ingameState)
            return new TestOutcome.Skip(Name, "IngameState not resolved");

        var ours = ctx.Reader.ReadPointer(ingameState + KnownOffsets.IngameState.IngameUi);
        var truth = await ctx.Poemcp.EvalAsync("IngameState.IngameUi.Address.ToString(\"X\")", ct);
        if (!truth.Success)
            return new TestOutcome.Fail(Name, $"POEMCP error: {truth.Error}");

        var expected = truth.AsAddress();
        if (ours != expected)
            return new TestOutcome.Fail(Name, $"ours 0x{ours:X} != truth 0x{expected:X}");

        ctx.State[StateKeys.IngameUi] = ours;
        return new TestOutcome.Pass(Name, $"both -> 0x{ours:X16}");
    }
}
