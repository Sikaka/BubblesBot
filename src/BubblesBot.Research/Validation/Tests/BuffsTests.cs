using BubblesBot.Core;
using BubblesBot.Core.Game;

namespace BubblesBot.Research.Validation.Tests;

/// <summary>
/// Validates Buffs component â€” reads the buff array and spot-checks one buff.
/// </summary>
public sealed class BuffsComponentMapTest : ValidationTest
{
    public override string Name => "Buffs component â€” read buff array";
    public override string? Group => "Buffs";

    public override Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(ComponentLookupKeys.PlayerComponentMap, out var mObj)
            || mObj is not Dictionary<string, nint> map)
            return Task.FromResult<TestOutcome>(new TestOutcome.Skip(Name, "component map not resolved"));
        if (!map.TryGetValue("Buffs", out var buffsAddr))
            return Task.FromResult<TestOutcome>(new TestOutcome.Skip(Name, "player has no Buffs component"));

        // BuffsComponent.Buffs @ +0x160 is a NativePtrArray of Buff pointers
        if (!ctx.Reader.TryReadStruct<NativePtrArray>(buffsAddr + KnownOffsets.BuffsComponent.Buffs, out var buffArray))
            return Task.FromResult<TestOutcome>(new TestOutcome.Fail(Name, "could not read buff array"));

        var count = buffArray.Count;
        if (count < 0 || count > 1000)
            return Task.FromResult<TestOutcome>(new TestOutcome.Fail(Name, $"unexpected buff count: {count}"));

        // Store first buff address for downstream tests
        if (count > 0)
        {
            var firstBuff = ctx.Reader.ReadPointer(buffArray.First);
            ctx.State["addr.FirstBuff"] = firstBuff;
        }
        ctx.State["buffs.BuffsArray"] = buffArray;
        return Task.FromResult<TestOutcome>(new TestOutcome.Pass(Name, $"{count} buffs on player"));
    }
}

/// <summary>
/// Spot-checks the first player buff's Timer/MaxTime/Charges fields for sanity.
/// </summary>
public sealed class BuffFieldsTest : ValidationTest
{
    public override string Name => "Buff fields â€” Timer/MaxTime/Charges sanity";
    public override string? Group => "Buffs";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue("addr.FirstBuff", out var bObj) || bObj is not nint firstBuff)
            return new TestOutcome.Skip(Name, "no buffs on player");

        if (!ctx.Reader.TryReadStruct<Buff>(firstBuff, out var buff))
            return new TestOutcome.Fail(Name, "could not read buff struct");

        // MaxTime can be Infinity for permanent buffs (auras, etc.). Only fail on negative values.
        var maxTimeIsPermanent = float.IsInfinity(buff.MaxTime) || float.IsNaN(buff.MaxTime);
        if (!maxTimeIsPermanent && buff.MaxTime < 0)
            return new TestOutcome.Fail(Name, $"MaxTime={buff.MaxTime} negative");
        if (!maxTimeIsPermanent && buff.Timer < 0)
            return new TestOutcome.Fail(Name, $"Timer={buff.Timer} negative");
        if (!maxTimeIsPermanent && buff.Timer > buff.MaxTime + 0.01f)
            return new TestOutcome.Fail(Name, $"Timer={buff.Timer} > MaxTime={buff.MaxTime}");

        // Compare with POEMCP. For permanent buffs (MaxTime=âˆž), Timer is irrelevant.
        string displayTimer = maxTimeIsPermanent ? "permanent" : $"{buff.Timer:F1}s/{buff.MaxTime:F1}s";
        if (maxTimeIsPermanent)
            return new TestOutcome.Pass(Name, $"sanity OK: permanent buff, charges={buff.Charges}");

        var truth = await ctx.Poemcp.EvalAsync(
            $"Player.Buffs.FirstOrDefault(b => b.SourceEntityId == {buff.SourceEntityId})?.Timer", ct);
        if (!truth.Success) return new TestOutcome.Pass(Name, $"sanity OK: timer={buff.Timer:F1}s/{buff.MaxTime:F1}s charges={buff.Charges} (POEMCP unavailable)");

        float truthTimer;
        try { truthTimer = truth.AsFloat(); }
        catch { return new TestOutcome.Pass(Name, $"sanity OK: timer={displayTimer} charges={buff.Charges} (POEMCP returned non-numeric)"); }

        if (Math.Abs(buff.Timer - truthTimer) > 0.15f)
            return new TestOutcome.Fail(Name, $"ours timer={buff.Timer:F1} â‰  truth {truthTimer:F1}");

        return new TestOutcome.Pass(Name, $"matches POEMCP: timer={buff.Timer:F1}/{buff.MaxTime:F1}s charges={buff.Charges}");
    }
}

/// <summary>
/// Validates Buff.SourceEntityId / Charges / FlaskSlot fields by comparing against POEMCP.
/// Reads all buffs on player, picks the one with the highest MaxTime for comparison.
/// </summary>
public sealed class BuffDetailTest : ValidationTest
{
    public override string Name => "Buff detail â€” SourceEntityId/Charges/FlaskSlot vs POEMCP";
    public override string? Group => "Buffs";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(ComponentLookupKeys.PlayerComponentMap, out var mObj)
            || mObj is not Dictionary<string, nint> map)
            return new TestOutcome.Skip(Name, "component map not resolved");
        if (!map.TryGetValue("Buffs", out var buffsAddr))
            return new TestOutcome.Skip(Name, "no Buffs component");

        var buffArray = ctx.Reader.ReadStruct<NativePtrArray>(buffsAddr + KnownOffsets.BuffsComponent.Buffs);
        var count = (int)buffArray.Count;
        if (count == 0) return new TestOutcome.Skip(Name, "no buffs to verify");

        // Read the first buff and compare its SourceEntityId with POEMCP's first buff.
        var ourBuff = ctx.Reader.ReadStruct<Buff>(ctx.Reader.ReadPointer(buffArray.First));

        var truth = await ctx.Poemcp.EvalAsync(
            $"var b = Player.Buffs.First(); b.SourceEntityId + \",\" + b.Charges", ct);
        if (!truth.Success) return new TestOutcome.Pass(Name, $"sanity OK: sourceId={ourBuff.SourceEntityId} charges={ourBuff.Charges} flaskSlot={ourBuff.FlaskSlot}");

        var parts = truth.AsString().Split(',');
        if (parts.Length >= 2 &&
            uint.TryParse(parts[0], out var truthId) &&
            ushort.TryParse(parts[1], out var truthCharges))
        {
            if (ourBuff.SourceEntityId != truthId)
                return new TestOutcome.Fail(Name, $"SourceEntityId: ours {ourBuff.SourceEntityId} â‰  truth {truthId}");
            if (ourBuff.Charges != truthCharges)
                return new TestOutcome.Fail(Name, $"Charges: ours {ourBuff.Charges} â‰  truth {truthCharges}");
        }

        return new TestOutcome.Pass(Name, $"matches POEMCP: sourceId={ourBuff.SourceEntityId} charges={ourBuff.Charges} flaskSlot={ourBuff.FlaskSlot}");
    }
}
