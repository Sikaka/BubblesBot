using BubblesBot.Core;
using BubblesBot.Core.Game;

namespace BubblesBot.Research.Validation.Tests;

public sealed class GroundLabelRootOracleTest : ValidationTest
{
    public override string Name => "Ground label root and linked labels";
    public override string? Group => "Ground labels";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(StateKeys.IngameUi, out var uiObj) || uiObj is not nint ingameUi)
            return new TestOutcome.Skip(Name, "IngameUi not resolved");

        if (!ctx.Reader.TryReadStruct<nint>(ingameUi + KnownOffsets.IngameUiElements.ItemsOnGroundLabelRoot, out var root)
            || root == 0)
            return new TestOutcome.Fail(Name, "could not read ItemsOnGroundLabelRoot");

        var truth = await ctx.Poemcp.EvalAsync(
            """
            var root = IngameState.IngameUi.ItemsOnGroundLabelElement;
            var labels = IngameState.IngameUi.ItemsOnGroundLabels;
            root.Address.ToString("X") + "|" +
            (labels == null ? -1 : labels.Count) + "|" +
            string.Join(";", (labels ?? new System.Collections.Generic.List<LabelOnGround>())
                .Take(20)
                .Select(l => l.Address.ToString("X") + "," + l.ItemOnGround.Address.ToString("X") + "," + l.Label.Address.ToString("X")))
            """, ct);
        if (!truth.Success)
            return new TestOutcome.Skip(Name, $"POEMCP unavailable: {truth.Error}");

        var parts = truth.AsString().Split('|', 3);
        if (parts.Length != 3)
            return new TestOutcome.Fail(Name, $"unexpected POEMCP result: {truth.AsString()}");

        var truthRoot = (nint)long.Parse(parts[0], System.Globalization.NumberStyles.HexNumber);
        if (root != truthRoot)
        {
            var candidates = new List<string>();
            for (var off = 0; off < 0x1400; off += 8)
            {
                if (ctx.Reader.TryReadStruct<nint>(ingameUi + off, out var candidate) && candidate == truthRoot)
                    candidates.Add($"+0x{off:X}");
            }

            return new TestOutcome.Fail(Name, $"root mismatch ours=0x{root:X}, POEMCP=0x{truthRoot:X}, candidates=[{string.Join(", ", candidates)}]");
        }

        var labels = GroundLabelReader.ReadLabels(ctx.Reader, root);
        var truthCount = int.Parse(parts[1]);
        if (truthCount < 0)
            return new TestOutcome.Pass(Name, $"root matches 0x{root:X16}; POEMCP labels unavailable");
        if (labels.Count != truthCount)
        {
            var candidates = new List<string>();
            for (var off = 0; off < 0x500; off += 8)
            {
                if (!ctx.Reader.TryReadStruct<nint>(root + off, out var sentinel) || !LooksLikeUserAddress(sentinel))
                    continue;

                var count = CountLinkedList(ctx.Reader, sentinel, 2000);
                if (count == truthCount)
                    candidates.Add($"+0x{off:X}");
            }

            return new TestOutcome.Fail(Name, $"label count mismatch ours={labels.Count}, POEMCP={truthCount}, sentinelCandidates=[{string.Join(", ", candidates)}]");
        }

        if (!string.IsNullOrEmpty(parts[2]))
        {
            var byAddress = labels.ToDictionary(l => l.Address);
            foreach (var sample in parts[2].Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = sample.Split(',', 3);
                if (fields.Length != 3) continue;
                var addr = ParseHex(fields[0]);
                var item = ParseHex(fields[1]);
                var label = ParseHex(fields[2]);
                if (!byAddress.TryGetValue(addr, out var ours))
                    return new TestOutcome.Fail(Name, $"missing label node 0x{addr:X}");
                if (ours.ItemEntity != item || ours.LabelElement != label)
                    return new TestOutcome.Fail(Name, $"label node mismatch 0x{addr:X}: ours item=0x{ours.ItemEntity:X}/label=0x{ours.LabelElement:X}, POEMCP item=0x{item:X}/label=0x{label:X}");
            }
        }

        return new TestOutcome.Pass(Name, $"root=0x{root:X16}, labels={labels.Count}");
    }

    private static nint ParseHex(string value)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            value = value[2..];
        return (nint)long.Parse(value, System.Globalization.NumberStyles.HexNumber);
    }

    private static int CountLinkedList(MemoryReader reader, nint sentinel, int max)
    {
        if (!reader.TryReadStruct<nint>(sentinel, out var current))
            return -1;
        var visited = new HashSet<nint>();
        var count = 0;
        while (current != sentinel && LooksLikeUserAddress(current) && visited.Add(current))
        {
            if (++count > max) return -1;
            if (!reader.TryReadStruct<nint>(current, out current))
                return -1;
        }
        return current == sentinel ? count : -1;
    }

    private static bool LooksLikeUserAddress(nint address)
    {
        var value = (long)address;
        return value > 0x10000 && value < 0x7FFF_FFFF_FFFF;
    }
}
