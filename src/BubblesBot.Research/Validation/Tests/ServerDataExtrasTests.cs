using BubblesBot.Core.Game;

namespace BubblesBot.Research.Validation.Tests;

public sealed class ServerDataSkillBarIdsTest : ValidationTest
{
    public override string Name => "ServerData.SkillBarIds";
    public override string? Group => "ServerData";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(StateKeys.ServerData, out var sObj) || sObj is not nint serverData)
            return new TestOutcome.Skip(Name, "ServerData not resolved");

        var ours = new ushort[13];
        for (var i = 0; i < ours.Length; i++)
        {
            if (!ctx.Reader.TryReadStruct<ushort>(serverData + KnownOffsets.ServerData.SkillBarIds + i * 2, out ours[i]))
                return new TestOutcome.Fail(Name, $"could not read skill id #{i}");
        }

        var truth = await ctx.Poemcp.EvalAsync("string.Join(\"|\", IngameState.ServerData.SkillBarIds)", ct);
        if (!truth.Success)
            return new TestOutcome.Skip(Name, $"POEMCP unavailable: {truth.Error}");

        var expected = truth.AsString().Split('|').Select(ushort.Parse).ToArray();
        if (!ours.SequenceEqual(expected))
            return new TestOutcome.Fail(Name, $"ours=[{string.Join(",", ours)}], POEMCP=[{string.Join(",", expected)}]");

        return new TestOutcome.Pass(Name, string.Join(",", ours));
    }
}

public sealed class ServerDataInventoryVectorsTest : ValidationTest
{
    public override string Name => "ServerData inventory/stash vectors";
    public override string? Group => "ServerData";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(StateKeys.ServerData, out var sObj) || sObj is not nint serverData)
            return new TestOutcome.Skip(Name, "ServerData not resolved");

        if (!ctx.Reader.TryReadStruct<StdVector>(serverData + KnownOffsets.ServerData.PlayerInventories, out var inv))
            return new TestOutcome.Fail(Name, "could not read PlayerInventories");
        if (!ctx.Reader.TryReadStruct<StdVector>(serverData + KnownOffsets.ServerData.PlayerStashTabs, out var stash))
            return new TestOutcome.Fail(Name, "could not read PlayerStashTabs");

        var invCount = inv.ByteCount / KnownOffsets.ServerData.PlayerInventoryElementSize;
        var stashCount = stash.ByteCount / KnownOffsets.ServerData.StashTabElementSize;
        if (inv.ByteCount % KnownOffsets.ServerData.PlayerInventoryElementSize != 0 || invCount < 0 || invCount > 1000)
            return new TestOutcome.Fail(Name, $"PlayerInventories byte count out of range: {inv.ByteCount}");
        if (stash.ByteCount % KnownOffsets.ServerData.StashTabElementSize != 0 || stashCount < 0 || stashCount > 1000)
            return new TestOutcome.Fail(Name, $"PlayerStashTabs byte count out of range: {stash.ByteCount}");

        var truth = await ctx.Poemcp.EvalAsync(
            "IngameState.ServerData.PlayerInventories.Count + \"|\" + IngameState.ServerData.PlayerStashTabs.Count", ct);
        if (!truth.Success)
            return new TestOutcome.Skip(Name, $"POEMCP unavailable: {truth.Error}");

        var parts = truth.AsString().Split('|');
        var expectedInv = long.Parse(parts[0]);
        var expectedStash = long.Parse(parts[1]);
        if (invCount != expectedInv || stashCount != expectedStash)
            return new TestOutcome.Fail(Name, $"ours inv/stash={invCount}/{stashCount}, POEMCP={expectedInv}/{expectedStash}");

        return new TestOutcome.Pass(Name, $"inventories={invCount}, stashTabs={stashCount}");
    }
}

public sealed class ServerDataSimpleFieldsTest : ValidationTest
{
    public override string Name => "ServerData simple gameplay fields";
    public override string? Group => "ServerData";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(StateKeys.ServerData, out var sObj) || sObj is not nint serverData)
            return new TestOutcome.Skip(Name, "ServerData not resolved");

        if (!ctx.Reader.TryReadStruct<int>(serverData + KnownOffsets.ServerData.Gold, out var gold)
            || !ctx.Reader.TryReadStruct<byte>(serverData + KnownOffsets.ServerData.MonsterLevel, out var monsterLevel)
            || !ctx.Reader.TryReadStruct<byte>(serverData + KnownOffsets.ServerData.MonstersRemaining, out var monstersRemaining)
            || !ctx.Reader.TryReadStruct<ushort>(serverData + KnownOffsets.ServerData.TradeChatChannel, out var trade)
            || !ctx.Reader.TryReadStruct<ushort>(serverData + KnownOffsets.ServerData.GlobalChatChannel, out var global))
        {
            return new TestOutcome.Fail(Name, "could not read one or more fields");
        }

        var truth = await ctx.Poemcp.EvalAsync(
            """
            IngameState.ServerData.Gold + "|" +
            IngameState.ServerData.MonsterLevel + "|" +
            IngameState.ServerData.MonstersRemaining + "|" +
            IngameState.ServerData.TradeChatChannel + "|" +
            IngameState.ServerData.GlobalChatChannel
            """, ct);
        if (!truth.Success)
            return new TestOutcome.Skip(Name, $"POEMCP unavailable: {truth.Error}");

        var parts = truth.AsString().Split('|');
        var expected = (Gold: int.Parse(parts[0]), MonsterLevel: byte.Parse(parts[1]), MonstersRemaining: byte.Parse(parts[2]), Trade: ushort.Parse(parts[3]), Global: ushort.Parse(parts[4]));
        if (gold != expected.Gold || monsterLevel != expected.MonsterLevel || monstersRemaining != expected.MonstersRemaining || trade != expected.Trade || global != expected.Global)
            return new TestOutcome.Fail(Name, $"ours gold/ml/rem/trade/global={gold}/{monsterLevel}/{monstersRemaining}/{trade}/{global}, POEMCP={expected.Gold}/{expected.MonsterLevel}/{expected.MonstersRemaining}/{expected.Trade}/{expected.Global}");

        return new TestOutcome.Pass(Name, $"gold={gold}, monsterLevel={monsterLevel}, remaining={monstersRemaining}, chat={trade}/{global}");
    }
}

public sealed class ServerDataDynamicVectorsTest : ValidationTest
{
    public override string Name => "ServerData dynamic vector counts";
    public override string? Group => "ServerData";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(StateKeys.ServerData, out var sObj) || sObj is not nint serverData)
            return new TestOutcome.Skip(Name, "ServerData not resolved");

        var vectors = new (string Name, int Offset)[]
        {
            ("NearestPlayers", KnownOffsets.ServerData.NearestPlayers),
            ("MinimapIcons", KnownOffsets.ServerData.MinimapIcons),
            ("MechanicHandlers", KnownOffsets.ServerData.MechanicHandlers),
        };

        var truth = await ctx.Poemcp.EvalAsync(
            """
            IngameState.ServerData.NearestPlayers.Count + "|" +
            IngameState.ServerData.MinimapIcons.Count + "|" +
            IngameState.ServerData.MechanicHandlers.Count
            """, ct);
        if (!truth.Success)
            return new TestOutcome.Skip(Name, $"POEMCP unavailable: {truth.Error}");

        var expected = truth.AsString().Split('|').Select(long.Parse).ToArray();
        var messages = new List<string>();
        for (var i = 0; i < vectors.Length; i++)
        {
            if (!ctx.Reader.TryReadStruct<StdVector>(serverData + vectors[i].Offset, out var vector))
                return new TestOutcome.Fail(Name, $"could not read {vectors[i].Name}");

            if (expected[i] == 0)
            {
                if (vector.ByteCount != 0)
                    return new TestOutcome.Fail(Name, $"{vectors[i].Name}: expected empty but byteCount={vector.ByteCount}");
                messages.Add($"{vectors[i].Name}=0");
                continue;
            }

            if (vector.ByteCount <= 0 || vector.ByteCount % expected[i] != 0)
                return new TestOutcome.Fail(Name, $"{vectors[i].Name}: byteCount={vector.ByteCount}, expectedCount={expected[i]}");
            var elementSize = vector.ByteCount / expected[i];
            if (elementSize <= 0 || elementSize > 0x400)
                return new TestOutcome.Fail(Name, $"{vectors[i].Name}: suspicious element size 0x{elementSize:X}");
            messages.Add($"{vectors[i].Name}={expected[i]}x0x{elementSize:X}");
        }

        return new TestOutcome.Pass(Name, string.Join(", ", messages));
    }
}
