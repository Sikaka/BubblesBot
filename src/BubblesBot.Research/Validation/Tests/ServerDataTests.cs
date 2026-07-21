using BubblesBot.Core;
using BubblesBot.Core.Game;

namespace BubblesBot.Research.Validation.Tests;

public sealed class ServerDataLeagueTest : ValidationTest
{
    public override string Name => "ServerData.League";
    public override string? Group => "ServerData";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(StateKeys.ServerData, out var sObj) || sObj is not nint serverData)
            return new TestOutcome.Skip(Name, "ServerData address not resolved");

        if (!ctx.Reader.TryReadStruct<NativeUtf16Text>(serverData + KnownOffsets.ServerData.League, out var league))
            return new TestOutcome.Fail(Name, "could not read NativeUtf16Text");

        var s = ReadInlineOrPointed(ctx.Reader, serverData + KnownOffsets.ServerData.League, league);
        var sane = !string.IsNullOrEmpty(s) && s.Length <= 64 && s.All(c => c >= ' ' && c <= '~');
        if (!sane) return new TestOutcome.Fail(Name, $"unexpected league string '{s}'");

        var truth = await ctx.Poemcp.EvalAsync("IngameState.ServerData.League", ct);
        if (!truth.Success) return new TestOutcome.Pass(Name, $"sanity OK: league='{s}' (POEMCP unavailable)");
        if (s != truth.AsString()) return new TestOutcome.Fail(Name, $"ours '{s}' â‰  truth '{truth.AsString()}'");

        return new TestOutcome.Pass(Name, $"matches POEMCP: league='{s}'");
    }

    private static string ReadInlineOrPointed(MemoryReader reader, nint structAddr, NativeUtf16Text txt)
    {
        if (txt.Length <= 0 || txt.Length > 0x4000) return string.Empty;
        var len = (int)txt.Length;
        if (len <= 7) return reader.ReadStringUtf16(structAddr, len);
        return reader.ReadStringUtf16(txt.Buffer, len);
    }
}

public sealed class ServerDataLatencyTest : ValidationTest
{
    public override string Name => "ServerData.Latency";
    public override string? Group => "ServerData";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(StateKeys.ServerData, out var sObj) || sObj is not nint serverData)
            return new TestOutcome.Skip(Name, "ServerData not resolved");

        if (!ctx.Reader.TryReadStruct<int>(serverData + KnownOffsets.ServerData.Latency, out var lat))
            return new TestOutcome.Fail(Name, "read failed");

        var sane = lat >= 0 && lat < 5000;
        if (!sane) return new TestOutcome.Fail(Name, $"latency={lat}ms out of expected 0..5000");

        var truth = await ctx.Poemcp.EvalAsync("IngameState.ServerData.Latency", ct);
        if (!truth.Success) return new TestOutcome.Pass(Name, $"sanity OK: latency={lat}ms (POEMCP unavailable)");
        var truthVal = truth.AsInt();
        if (Math.Abs(lat - truthVal) > 50) return new TestOutcome.Fail(Name, $"ours {lat} differs from truth {truthVal} by >50ms");

        return new TestOutcome.Pass(Name, $"~matches POEMCP: latency={lat}ms (truth {truthVal})");
    }
}

public sealed class ServerDataTimeInGameTest : ValidationTest
{
    public override string Name => "ServerData.TimeInGame";
    public override string? Group => "ServerData";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(StateKeys.ServerData, out var sObj) || sObj is not nint serverData)
            return new TestOutcome.Skip(Name, "ServerData not resolved");

        if (!ctx.Reader.TryReadStruct<int>(serverData + KnownOffsets.ServerData.TimeInGame, out var tig))
            return new TestOutcome.Fail(Name, "read failed");

        var sane = tig >= 0 && tig < 100_000_000;
        if (!sane) return new TestOutcome.Fail(Name, $"timeInGame={tig} out of expected positive range");

        return new TestOutcome.Pass(Name, $"sanity OK: timeInGame={tig}s");
    }
}
