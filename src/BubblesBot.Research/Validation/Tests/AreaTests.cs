using BubblesBot.Core;
using BubblesBot.Core.Game;

namespace BubblesBot.Research.Validation.Tests;

/// <summary>
/// Reads from IngameData â€” the area we're currently in. CurrentArea is a pointer to a struct
/// whose layout we don't fully know, but we know it's reachable from IngameData and can read
/// AreaLevel (single byte) and the area's hash directly.
/// </summary>
public sealed class CurrentAreaLevelTest : ValidationTest
{
    public override string Name => "IngameData.CurrentAreaLevel (+0xCC)";
    public override string? Group => "Area / zone";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(StateKeys.IngameData, out var dObj) || dObj is not nint ingameData)
            return new TestOutcome.Skip(Name, "IngameData not resolved (POEMCP-dependent step skipped)");

        if (!ctx.Reader.TryReadStruct<byte>(ingameData + KnownOffsets.IngameData.CurrentAreaLevel, out var level))
            return new TestOutcome.Fail(Name, "could not read byte at +0xCC");

        var sane = level >= 1 && level <= 100;
        if (!sane) return new TestOutcome.Fail(Name, $"level={level} out of expected 1..100");

        var truth = await ctx.Poemcp.EvalAsync("IngameState.Data.CurrentAreaLevel", ct);
        if (!truth.Success) return new TestOutcome.Pass(Name, $"sanity OK: level={level} (POEMCP unavailable)");
        if (level != truth.AsInt()) return new TestOutcome.Fail(Name, $"ours {level} â‰  truth {truth.AsInt()}");

        return new TestOutcome.Pass(Name, $"matches POEMCP: level={level}");
    }
}

public sealed class CurrentAreaHashTest : ValidationTest
{
    public override string Name => "IngameData.CurrentAreaHash (+0x10C)";
    public override string? Group => "Area / zone";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(StateKeys.IngameData, out var dObj) || dObj is not nint ingameData)
            return new TestOutcome.Skip(Name, "IngameData not resolved");

        if (!ctx.Reader.TryReadStruct<uint>(ingameData + KnownOffsets.IngameData.CurrentAreaHash, out var hash))
            return new TestOutcome.Fail(Name, "could not read uint at +0x10C");

        // Hash should be non-zero in any loaded area. 0 = not in a zone yet.
        if (hash == 0) return new TestOutcome.Skip(Name, "area hash is 0 â€” player may not be in a zone");

        var truth = await ctx.Poemcp.EvalAsync("IngameState.Data.CurrentAreaHash.ToString(\"X\")", ct);
        if (!truth.Success) return new TestOutcome.Pass(Name, $"sanity OK: hash=0x{hash:X8} (POEMCP unavailable)");
        var truthHash = (uint)truth.AsAddress();
        if (hash != truthHash) return new TestOutcome.Fail(Name, $"ours 0x{hash:X8} â‰  truth 0x{truthHash:X8}");

        return new TestOutcome.Pass(Name, $"matches POEMCP: hash=0x{hash:X8}");
    }
}
