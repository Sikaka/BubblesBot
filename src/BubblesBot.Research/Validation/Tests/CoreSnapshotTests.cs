using BubblesBot.Core.Game;

namespace BubblesBot.Research.Validation.Tests;

internal static class CoreSnapshotKeys
{
    public const string EntityTraversal = "snapshot.EntityTraversal";
    public const string EntitySnapshots = "snapshot.Entities";
}

public sealed class EntityListTraversalTest : ValidationTest
{
    public override string Name => "EntityList traversal builds entity address set";
    public override string? Group => "Core snapshot";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(StateKeys.EntityList, out var elObj) || elObj is not nint entityList)
            return new TestOutcome.Skip(Name, "EntityList address not resolved");

        var traversal = EntityListReader.EnumerateEntityAddresses(ctx.Reader, entityList);
        ctx.State[CoreSnapshotKeys.EntityTraversal] = traversal;

        if (traversal.HitSafetyLimit)
            return new TestOutcome.Fail(Name, $"hit safety limit after {traversal.NodesVisited} nodes");
        if (traversal.EntityAddresses.Count == 0)
            return new TestOutcome.Fail(Name, $"no entities found (nodes={traversal.NodesVisited}, badReads={traversal.BadReads})");

        var playerPresent = false;
        if (ctx.State.TryGetValue(StateKeys.LocalPlayer, out var pObj)
            && pObj is nint player)
        {
            playerPresent = traversal.EntityAddresses.Contains(player);
        }

        var countTruth = await ctx.Poemcp.EvalAsync("EntityListWrapper.OnlyValidEntities.Count()", ct);
        if (countTruth.Success)
        {
            var truthCount = countTruth.AsInt();
            if (truthCount > 0)
            {
                var ratio = traversal.EntityAddresses.Count / (double)truthCount;
                if (ratio < 0.25 || ratio > 4.0)
                    return new TestOutcome.Fail(Name, $"count looks wrong: ours={traversal.EntityAddresses.Count}, POEMCP valid={truthCount}, ratio={ratio:F2}");

                return new TestOutcome.Pass(Name, $"entities={traversal.EntityAddresses.Count}, POEMCP valid={truthCount}, playerPresent={playerPresent}, nodes={traversal.NodesVisited}, badReads={traversal.BadReads}");
            }
        }

        return new TestOutcome.Pass(Name, $"entities={traversal.EntityAddresses.Count}, playerPresent={playerPresent}, nodes={traversal.NodesVisited}, badReads={traversal.BadReads}");
    }
}

public sealed class CoreEntitySnapshotBuildTest : ValidationTest
{
    public override string Name => "Build basic entity snapshots";
    public override string? Group => "Core snapshot";

    public override Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(CoreSnapshotKeys.EntityTraversal, out var tObj)
            || tObj is not EntityListReader.TraversalResult traversal)
            return Task.FromResult<TestOutcome>(new TestOutcome.Skip(Name, "entity traversal not available"));

        var snapshots = new List<EntityListReader.EntitySnapshot>(traversal.EntityAddresses.Count);
        foreach (var address in traversal.EntityAddresses)
        {
            var snapshot = EntityListReader.TryReadSnapshot(ctx.Reader, address);
            if (snapshot is not null)
                snapshots.Add(snapshot);
        }

        ctx.State[CoreSnapshotKeys.EntitySnapshots] = snapshots;

        if (snapshots.Count == 0)
            return Task.FromResult<TestOutcome>(new TestOutcome.Fail(Name, "all entity snapshots failed"));

        var withGrid = snapshots.Count(s => s.GridPosition.HasValue);
        var withLife = snapshots.Count(s => s.Health.HasValue);
        var withPath = snapshots.Count(s => !string.IsNullOrEmpty(s.Path));
        var monsters = snapshots.Count(s => s.Kind == EntityListReader.EntityKind.Monster);
        var worldItems = snapshots.Count(s => s.Kind == EntityListReader.EntityKind.WorldItem);
        var chests = snapshots.Count(s => s.Kind == EntityListReader.EntityKind.Chest);
        var withPathfinding = snapshots.Count(s => s.Pathfinding is not null);

        if (withGrid == 0)
            return Task.FromResult<TestOutcome>(new TestOutcome.Fail(Name, "no snapshots had Positioned.GridPosition"));

        return Task.FromResult<TestOutcome>(new TestOutcome.Pass(
            Name,
            $"snapshots={snapshots.Count}, grid={withGrid}, life={withLife}, path={withPath}, pathfinding={withPathfinding}, monsters={monsters}, chests={chests}, worldItems={worldItems}"));
    }
}

public sealed class PlayerSnapshotMatchesPoemcpTest : ValidationTest
{
    public override string Name => "Player snapshot matches POEMCP";
    public override string? Group => "Core snapshot";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(StateKeys.LocalPlayer, out var pObj) || pObj is not nint playerAddress)
            return new TestOutcome.Skip(Name, "LocalPlayer not resolved");

        var snapshot = EntityListReader.TryReadSnapshot(ctx.Reader, playerAddress);
        if (snapshot is null)
            return new TestOutcome.Fail(Name, $"could not read player snapshot at 0x{playerAddress:X}");

        var truth = await ctx.Poemcp.EvalAsync(
            "Player.Id + \"|\" + Player.GridPosNum.X + \"|\" + Player.GridPosNum.Y + \"|\" + Player.GetComponent<Life>().CurHP",
            ct);
        if (!truth.Success)
            return new TestOutcome.Fail(Name, $"POEMCP error: {truth.Error}");

        var parts = truth.AsString().Split('|');
        if (parts.Length != 4)
            return new TestOutcome.Fail(Name, $"unexpected POEMCP result: {truth.AsString()}");

        var truthId = uint.Parse(parts[0]);
        var truthX = int.Parse(parts[1]);
        var truthY = int.Parse(parts[2]);
        var truthHp = int.Parse(parts[3]);

        if (snapshot.Id != truthId)
            return new TestOutcome.Fail(Name, $"id mismatch: ours={snapshot.Id}, POEMCP={truthId}");
        if (snapshot.GridPosition is not { } grid)
            return new TestOutcome.Fail(Name, "player snapshot has no grid position");
        if (grid.X != truthX || grid.Y != truthY)
            return new TestOutcome.Fail(Name, $"grid mismatch: ours=({grid.X},{grid.Y}), POEMCP=({truthX},{truthY})");
        if (snapshot.Health is not { } health)
            return new TestOutcome.Fail(Name, "player snapshot has no health");
        if (health.Current != truthHp)
            return new TestOutcome.Fail(Name, $"HP mismatch: ours={health.Current}, POEMCP={truthHp}");

        return new TestOutcome.Pass(Name, $"id={snapshot.Id}, grid=({grid.X},{grid.Y}), HP={health.Current}/{health.Max}");
    }
}

public sealed class NearestHostileMonsterOracleTest : ValidationTest
{
    public override string Name => "Nearest live hostile monster is readable";
    public override string? Group => "Core snapshot";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(CoreSnapshotKeys.EntitySnapshots, out var sObj)
            || sObj is not List<EntityListReader.EntitySnapshot> snapshots)
            return new TestOutcome.Skip(Name, "entity snapshots not available");

        var truth = await ctx.Poemcp.EvalAsync(
            """
            var p = Player.GridPosNum;
            var e = EntityListWrapper.OnlyValidEntities
                .Where(x => x.Type == ExileCore.Shared.Enums.EntityType.Monster && x.IsHostile && x.IsAlive)
                .OrderBy(x => System.Numerics.Vector2.Distance(x.GridPosNum, p))
                .FirstOrDefault();
            e == null ? "none" : e.Address.ToString("X") + "|" + e.Id + "|" + e.GridPosNum.X + "|" + e.GridPosNum.Y + "|" + e.RenderName
            """,
            ct);
        if (!truth.Success)
            return new TestOutcome.Fail(Name, $"POEMCP error: {truth.Error}");

        var text = truth.AsString();
        if (text == "none")
            return new TestOutcome.Skip(Name, "POEMCP found no live hostile monsters in entity list");

        var parts = text.Split('|', 5);
        if (parts.Length < 4)
            return new TestOutcome.Fail(Name, $"unexpected POEMCP result: {text}");

        var truthAddress = ParseHexAddress(parts[0]);
        var truthId = uint.Parse(parts[1]);
        var truthX = int.Parse(parts[2]);
        var truthY = int.Parse(parts[3]);
        var truthName = parts.Length == 5 ? parts[4] : "";

        var ours = snapshots.FirstOrDefault(s => s.Address == truthAddress || s.Id == truthId);
        if (ours is null)
            return new TestOutcome.Fail(Name, $"POEMCP nearest hostile id={truthId} addr=0x{truthAddress:X} was not in our snapshots");

        if (ours.Id != truthId)
            return new TestOutcome.Fail(Name, $"id mismatch at 0x{truthAddress:X}: ours={ours.Id}, POEMCP={truthId}");
        if (ours.GridPosition is not { } grid)
            return new TestOutcome.Fail(Name, $"monster id={truthId} has no grid position in our snapshot");
        if (Math.Abs(grid.X - truthX) > 1 || Math.Abs(grid.Y - truthY) > 1)
            return new TestOutcome.Fail(Name, $"grid mismatch for id={truthId}: ours=({grid.X},{grid.Y}), POEMCP=({truthX},{truthY})");
        if (ours.Health is not { Current: > 0 } health)
            return new TestOutcome.Fail(Name, $"monster id={truthId} has no positive Life.Health.Current in our snapshot");

        return new TestOutcome.Pass(Name, $"id={truthId}, grid=({grid.X},{grid.Y}), HP={health.Current}/{health.Max}, name='{truthName}'");
    }

    private static nint ParseHexAddress(string value)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            value = value[2..];
        return (nint)long.Parse(value, System.Globalization.NumberStyles.HexNumber);
    }
}

public sealed class EntitySemanticsOracleTest : ValidationTest
{
    public override string Name => "Entity semantics match POEMCP sample";
    public override string? Group => "Core snapshot";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(CoreSnapshotKeys.EntitySnapshots, out var sObj)
            || sObj is not List<EntityListReader.EntitySnapshot> snapshots)
            return new TestOutcome.Skip(Name, "entity snapshots not available");

        var byId = snapshots.ToDictionary(s => s.Id);
        var truth = await ctx.Poemcp.EvalAsync(
            """
            string.Join("\n", EntityListWrapper.OnlyValidEntities
                .Where(e => e.Id > 0 && (
                    e.Type == ExileCore.Shared.Enums.EntityType.Monster ||
                    e.Type == ExileCore.Shared.Enums.EntityType.Chest ||
                    e.Type == ExileCore.Shared.Enums.EntityType.WorldItem))
                .OrderBy(e =>
                    e.Type == ExileCore.Shared.Enums.EntityType.Monster ? 0 :
                    e.Type == ExileCore.Shared.Enums.EntityType.Chest ? 1 : 2)
                .ThenBy(e => e.Id)
                .Take(80)
                .Select(e => e.Id + "|" + e.Type + "|" + e.IsAlive + "|" + e.IsHostile + "|" + e.IsTargetable + "|" + e.Rarity))
            """,
            ct);

        if (!truth.Success)
            return new TestOutcome.Fail(Name, $"POEMCP error: {truth.Error}");

        var checkedCount = 0;
        foreach (var line in truth.AsString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|');
            if (parts.Length != 6) continue;
            var id = uint.Parse(parts[0]);
            if (!byId.TryGetValue(id, out var ours))
                return new TestOutcome.Fail(Name, $"POEMCP entity id={id} was not in our snapshots");

            var truthType = parts[1];
            var truthAlive = bool.Parse(parts[2]);
            var truthHostile = bool.Parse(parts[3]);
            var truthTargetable = bool.Parse(parts[4]);
            var truthRarity = NormalizeRarity(parts[5]);

            if (!TypeMatches(ours.Kind, truthType))
                return new TestOutcome.Fail(Name, $"type mismatch id={id}: ours={ours.Kind}, POEMCP={truthType}");
            if (ours.IsAlive != truthAlive)
                return new TestOutcome.Fail(Name, $"alive mismatch id={id}: ours={ours.IsAlive}, POEMCP={truthAlive}");
            if (truthType == "Monster" && ours.IsHostile != truthHostile)
                return new TestOutcome.Fail(Name, $"hostile mismatch id={id}: ours={ours.IsHostile}, POEMCP={truthHostile}, path={ours.Path}");
            if (ours.IsTargetable is { } targetable && targetable != truthTargetable)
                return new TestOutcome.Fail(Name, $"targetable mismatch id={id}: ours={targetable}, POEMCP={truthTargetable}");
            if (ours.Rarity is { } rarity && rarity.ToString() != truthRarity)
                return new TestOutcome.Fail(Name, $"rarity mismatch id={id}: ours={rarity}, POEMCP={truthRarity}");

            checkedCount++;
        }

        return checkedCount == 0
            ? new TestOutcome.Skip(Name, "no comparable entities in POEMCP sample")
            : new TestOutcome.Pass(Name, $"checked {checkedCount} monster/chest/world-item snapshots");
    }

    private static bool TypeMatches(EntityListReader.EntityKind ours, string truth) => truth switch
    {
        "Monster" => ours == EntityListReader.EntityKind.Monster,
        "Chest" => ours == EntityListReader.EntityKind.Chest,
        "WorldItem" => ours == EntityListReader.EntityKind.WorldItem,
        _ => true,
    };

    private static string NormalizeRarity(string value) => value switch
    {
        "White" => nameof(EntityListReader.EntityRarity.Normal),
        _ => value,
    };
}
