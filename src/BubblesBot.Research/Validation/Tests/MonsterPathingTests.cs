using BubblesBot.Core.Game;

namespace BubblesBot.Research.Validation.Tests;

public sealed class NearestMonsterPathfindingOracleTest : ValidationTest
{
    public override string Name => "Nearest monster Positioned + Pathfinding state";
    public override string? Group => "Monster pathing";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(CoreSnapshotKeys.EntitySnapshots, out var sObj)
            || sObj is not List<EntityListReader.EntitySnapshot> snapshots)
            return new TestOutcome.Skip(Name, "entity snapshots not available");

        var truth = await ctx.Poemcp.EvalAsync(
            """
            var playerPos = Player.GridPosNum;
            var e = EntityListWrapper.OnlyValidEntities
                .Where(x => x.Type == ExileCore.Shared.Enums.EntityType.Monster && x.IsAlive && x.IsHostile && x.GetComponent<Pathfinding>() != null)
                .OrderByDescending(x => x.GetComponent<Pathfinding>().IsMoving)
                .ThenByDescending(x => x.GetComponent<Pathfinding>().WantMoveToPosition.X != 0 || x.GetComponent<Pathfinding>().WantMoveToPosition.Y != 0)
                .ThenBy(x => System.Numerics.Vector2.Distance(x.GridPosNum, playerPos))
                .FirstOrDefault();
            e == null ? "none" :
                e.Address.ToString("X") + "|" + e.Id + "|" +
                e.GetComponent<Positioned>().GridX + "," + e.GetComponent<Positioned>().GridY + "|" +
                e.GetComponent<Positioned>().Reaction + "|" +
                e.GetComponent<Pathfinding>().TargetMovePos.X + "," + e.GetComponent<Pathfinding>().TargetMovePos.Y + "|" +
                e.GetComponent<Pathfinding>().PreviousMovePos.X + "," + e.GetComponent<Pathfinding>().PreviousMovePos.Y + "|" +
                e.GetComponent<Pathfinding>().WantMoveToPosition.X + "," + e.GetComponent<Pathfinding>().WantMoveToPosition.Y + "|" +
                e.GetComponent<Pathfinding>().IsMoving + "|" + e.GetComponent<Pathfinding>().StayTime.ToString("F3") + "|" +
                e.GetComponent<Pathfinding>().DestinationNodes + "|" +
                string.Join(";", e.GetComponent<Pathfinding>().PathingNodes.Select(n => n.X + "," + n.Y)) + "|" +
                e.Path
            """, ct);

        if (!truth.Success)
            return new TestOutcome.Skip(Name, $"POEMCP unavailable: {truth.Error}");

        var text = truth.AsString();
        if (text == "none")
            return new TestOutcome.Skip(Name, "no live hostile monster with Pathfinding component");

        var parts = text.Split('|', 12);
        if (parts.Length != 12)
            return new TestOutcome.Fail(Name, $"unexpected POEMCP result: {text}");

        var entity = (nint)long.Parse(parts[0], System.Globalization.NumberStyles.HexNumber);
        var truthId = uint.Parse(parts[1]);
        var components = EntityComponents.ReadComponentMap(ctx.Reader, entity);
        if (!components.TryGetValue("Positioned", out var positioned))
            return new TestOutcome.Fail(Name, "monster has no Positioned component in our map");
        if (!components.TryGetValue("Pathfinding", out var pathfinding))
            return new TestOutcome.Fail(Name, "monster has no Pathfinding component in our map");

        if (!ctx.Reader.TryReadStruct<Vector2i>(positioned + KnownOffsets.PositionedComponent.GridPosition, out var grid))
            return new TestOutcome.Fail(Name, "could not read Positioned.GridPosition");
        if (!ctx.Reader.TryReadStruct<byte>(positioned + KnownOffsets.PositionedComponent.Reaction, out var reaction))
            return new TestOutcome.Fail(Name, "could not read Positioned.Reaction");
        if (!ctx.Reader.TryReadStruct<Vector2i>(pathfinding + KnownOffsets.PathfindingComponent.WantMoveToPosition, out var wanted))
            return new TestOutcome.Fail(Name, "could not read Pathfinding.WantMoveToPosition");
        if (!ctx.Reader.TryReadStruct<byte>(pathfinding + KnownOffsets.PathfindingComponent.IsMoving, out var isMovingRaw))
            return new TestOutcome.Fail(Name, "could not read Pathfinding.IsMoving");
        if (!ctx.Reader.TryReadStruct<float>(pathfinding + KnownOffsets.PathfindingComponent.StayTime, out var stayTime))
            return new TestOutcome.Fail(Name, "could not read Pathfinding.StayTime");
        if (!ctx.Reader.TryReadStruct<int>(pathfinding + KnownOffsets.PathfindingComponent.DestinationNodes, out var destinationNodes))
            return new TestOutcome.Fail(Name, "could not read Pathfinding.DestinationNodes");

        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var directMoving = isMovingRaw != 0;
        var expectedMoving = bool.Parse(parts[7]);
        var expectedStayTime = float.Parse(parts[8], culture);
        var expectedDestinationNodes = int.Parse(parts[9], culture);
        var expectedPathingNodes = parts[10].Split(';', StringSplitOptions.RemoveEmptyEntries);

        if ($"{grid.X},{grid.Y}" != parts[2])
            return new TestOutcome.Fail(Name, $"grid mismatch ours {grid.X},{grid.Y} vs POEMCP {parts[2]} for {parts[9]}");
        if (reaction.ToString(culture) != parts[3])
            return new TestOutcome.Fail(Name, $"reaction mismatch ours {reaction} vs POEMCP {parts[3]} for {parts[11]}");
        if ($"{wanted.X},{wanted.Y}" != parts[6])
            return new TestOutcome.Fail(Name, $"wanted mismatch ours {wanted.X},{wanted.Y} vs POEMCP {parts[6]} for {parts[11]}");
        if (destinationNodes != expectedDestinationNodes)
            return new TestOutcome.Fail(Name, $"destinationNodes mismatch ours {destinationNodes} vs POEMCP {expectedDestinationNodes} for {parts[11]}");

        if (expectedPathingNodes.Length > 0)
        {
            var directNodes = new string[expectedPathingNodes.Length];
            for (var i = 0; i < directNodes.Length; i++)
            {
                var offset = KnownOffsets.PathfindingComponent.PathingNodes + (directNodes.Length - 1 - i) * 8;
                if (!ctx.Reader.TryReadStruct<Vector2i>(pathfinding + offset, out var node))
                    return new TestOutcome.Fail(Name, $"could not read PathingNodes[{i}] at +0x{offset:X}");
                directNodes[i] = $"{node.X},{node.Y}";
            }

            var directNodeText = string.Join(";", directNodes);
            if (directNodeText != parts[10])
                return new TestOutcome.Fail(Name, $"pathingNodes mismatch ours [{directNodeText}] vs POEMCP [{parts[10]}] for {parts[11]}");
        }

        var snapshot = snapshots.FirstOrDefault(s => s.Address == entity || s.Id == truthId);
        if (snapshot?.Pathfinding is null)
            return new TestOutcome.Fail(Name, $"snapshot for id={truthId} has no Pathfinding");
        if ($"{snapshot.Pathfinding.WantMoveToPosition.X},{snapshot.Pathfinding.WantMoveToPosition.Y}" != parts[6])
            return new TestOutcome.Fail(Name, $"snapshot wanted mismatch {snapshot.Pathfinding.WantMoveToPosition.X},{snapshot.Pathfinding.WantMoveToPosition.Y} vs POEMCP {parts[6]}");
        if (snapshot.Pathfinding.DestinationNodes != expectedDestinationNodes)
            return new TestOutcome.Fail(Name, $"snapshot destinationNodes mismatch {snapshot.Pathfinding.DestinationNodes} vs POEMCP {expectedDestinationNodes}");
        if (expectedPathingNodes.Length > 0)
        {
            var snapshotNodes = string.Join(";", snapshot.Pathfinding.PathingNodes.Select(n => $"{n.X},{n.Y}"));
            if (snapshotNodes != parts[10])
                return new TestOutcome.Fail(Name, $"snapshot pathingNodes mismatch [{snapshotNodes}] vs POEMCP [{parts[10]}]");
        }

        // Movement state can legitimately flip between the POEMCP eval and our direct read.
        // StayTime is also a live timer. Keep those comparisons tolerant so this test
        // validates the offsets without failing on a frame-boundary race.
        var movingDetail = directMoving == expectedMoving
            ? $"moving={parts[7]}"
            : $"moving changed during sample (POEMCP={expectedMoving}, direct={directMoving})";
        var stayDelta = Math.Abs(stayTime - expectedStayTime);
        if (stayDelta > 0.75f)
            return new TestOutcome.Fail(Name, $"stayTime mismatch ours {stayTime:F3} vs POEMCP {expectedStayTime:F3} for {parts[11]}");

        var targetNote = parts[4] == "0,0" && parts[5] == "0,0"
            ? "; target-node offsets still need a moving sample"
            : $"; POEMCP target={parts[4]}, previous={parts[5]}, wanted={parts[6]}";
        var nodeDetail = expectedPathingNodes.Length > 0 ? $", nodes={expectedPathingNodes.Length}" : "";
        return new TestOutcome.Pass(Name, $"matches POEMCP: id={parts[1]}, grid={parts[2]}, {movingDetail}, wanted={parts[6]}, stay={stayTime:F3}, destNodes={destinationNodes}{nodeDetail}{targetNote}");
    }
}
