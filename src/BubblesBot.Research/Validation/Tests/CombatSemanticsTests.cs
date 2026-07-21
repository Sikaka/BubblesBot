using BubblesBot.Core.Game;

namespace BubblesBot.Research.Validation.Tests;

public sealed class MonsterCombatSemanticsOracleTest : ValidationTest
{
    public override string Name => "Monster targetable/state-machine semantics";
    public override string? Group => "Combat semantics";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(CoreSnapshotKeys.EntitySnapshots, out var sObj)
            || sObj is not List<EntityListReader.EntitySnapshot> snapshots)
            return new TestOutcome.Skip(Name, "entity snapshots not available");

        var truth = await ctx.Poemcp.EvalAsync(
            """
            string.Join("\n", EntityListWrapper.OnlyValidEntities
                .Where(e => e.Type == ExileCore.Shared.Enums.EntityType.Monster && e.Id > 0)
                .OrderBy(e => e.Id)
                .Take(60)
                .Select(e =>
                {
                    var targetable = e.GetComponent<Targetable>();
                    var state = e.GetComponent<StateMachine>();
                    return e.Id + "|" + e.IsTargetable + "|" +
                        (targetable == null ? "null" : targetable.isTargetable.ToString()) + "|" +
                        (targetable == null ? "null" : targetable.isTargeted.ToString()) + "|" +
                        (state == null ? "null" : state.CanBeTarget.ToString()) + "|" +
                        (state == null ? "null" : state.InTarget.ToString()) + "|" +
                        e.Path;
                }))
            """, ct);

        if (!truth.Success)
            return new TestOutcome.Skip(Name, $"POEMCP unavailable: {truth.Error}");

        var lines = truth.AsString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            return new TestOutcome.Skip(Name, "no monster samples");

        var byId = snapshots.ToDictionary(s => s.Id);
        var checkedTargetable = 0;
        var checkedState = 0;

        foreach (var line in lines)
        {
            var parts = line.Split('|', 7);
            if (parts.Length != 7) continue;
            var id = uint.Parse(parts[0]);
            if (!byId.TryGetValue(id, out var snapshot))
                return new TestOutcome.Fail(Name, $"monster id={id} missing from snapshots");

            if (parts[2] != "null")
            {
                if (snapshot.IsTargetable is not { } targetable)
                    return new TestOutcome.Fail(Name, $"monster id={id} has Targetable in POEMCP but not in snapshot");
                var truthTargetable = bool.Parse(parts[2]);
                if (targetable != truthTargetable)
                    return new TestOutcome.Fail(Name, $"targetable mismatch id={id}: ours={targetable}, POEMCP component={truthTargetable}, path={parts[6]}");
                checkedTargetable++;
            }

            if (parts[4] != "null")
            {
                if (snapshot.StateMachine is not { } state)
                    return new TestOutcome.Fail(Name, $"monster id={id} has StateMachine in POEMCP but not in snapshot");
                var truthCanBeTarget = bool.Parse(parts[4]);
                var truthInTarget = bool.Parse(parts[5]);
                if (state.CanBeTarget != truthCanBeTarget || state.InTarget != truthInTarget)
                    return new TestOutcome.Fail(Name, $"state mismatch id={id}: ours=({state.CanBeTarget},{state.InTarget}), POEMCP=({truthCanBeTarget},{truthInTarget}), path={parts[6]}");
                checkedState++;
            }
        }

        return checkedTargetable == 0 && checkedState == 0
            ? new TestOutcome.Skip(Name, "monster samples had no comparable Targetable/StateMachine components")
            : new TestOutcome.Pass(Name, $"checked targetable={checkedTargetable}, stateMachine={checkedState}");
    }
}

public sealed class MonsterRarityOffsetScanTest : ValidationTest
{
    public override string Name => "Monster rarity offset scan";
    public override string? Group => "Combat semantics";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        var truth = await ctx.Poemcp.EvalAsync(
            """
            string.Join("\n", EntityListWrapper.OnlyValidEntities
                .Where(e => e.Type == ExileCore.Shared.Enums.EntityType.Monster && e.GetComponent<ObjectMagicProperties>() != null)
                .OrderBy(e => e.Rarity)
                .ThenBy(e => e.Id)
                .Take(80)
                .Select(e => e.Address.ToString("X") + "|" + e.Id + "|" + e.Rarity + "|" + e.GetComponent<ObjectMagicProperties>().Address.ToString("X") + "|" + e.Path))
            """, ct);

        if (!truth.Success)
            return new TestOutcome.Skip(Name, $"POEMCP unavailable: {truth.Error}");

        var lines = truth.AsString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            return new TestOutcome.Skip(Name, "no monster samples with ObjectMagicProperties");

        var candidateScores = new Dictionary<int, int>();
        var sampleCount = 0;
        foreach (var line in lines)
        {
            var parts = line.Split('|', 5);
            if (parts.Length != 5) continue;
            if (!TryMapRarity(parts[2], out var rarity)) continue;
            var omp = (nint)long.Parse(parts[3], System.Globalization.NumberStyles.HexNumber);
            sampleCount++;

            for (var off = 0x40; off <= 0x140; off += 4)
            {
                if (ctx.Reader.TryReadStruct<int>(omp + off, out var value) && value == rarity)
                    candidateScores[off] = candidateScores.GetValueOrDefault(off) + 1;
            }
        }

        if (sampleCount == 0)
            return new TestOutcome.Skip(Name, "no rarity-mappable samples");

        var best = candidateScores
            .Where(kv => kv.Value == sampleCount)
            .Select(kv => kv.Key)
            .OrderBy(v => v)
            .ToArray();

        if (best.Length == 1)
            return new TestOutcome.Pass(Name, $"candidate +0x{best[0]:X} matched {sampleCount}/{sampleCount} samples");
        if (best.Length > 1)
            return new TestOutcome.Skip(Name, $"ambiguous candidates [{string.Join(", ", best.Select(o => $"+0x{o:X}"))}] matched {sampleCount}/{sampleCount}");

        var top = candidateScores.OrderByDescending(kv => kv.Value).Take(5).Select(kv => $"+0x{kv.Key:X}:{kv.Value}/{sampleCount}");
        return new TestOutcome.Skip(Name, $"no exact rarity offset candidate; top {string.Join(", ", top)}");
    }

    private static bool TryMapRarity(string value, out int rarity)
    {
        rarity = value switch
        {
            "White" or "Normal" => 0,
            "Magic" => 1,
            "Rare" => 2,
            "Unique" => 3,
            _ => -1,
        };
        return rarity >= 0;
    }
}
