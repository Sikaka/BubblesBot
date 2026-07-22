using System.Text;
using BubblesBot.Bot.Behaviors.Loot;
using BubblesBot.Core.Game;
using BubblesBot.Core.Pathfinding;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Read-only capture of a nearby closed map door and both terrain layers around it.</summary>
public sealed class MapDoorInspectLiveTest : ILiveTestCase
{
    public string Id => "map-door-inspect";
    public string Name => "Map door and terrain inspection";
    public string Description => "Reads the nearest visible Door entity, its components/state, and local pathing/targeting terrain without input.";
    public string ManualSetup => "Stand beside a closed door in a loaded map with its Door label visible.";
    public LiveTestMutation Mutation => LiveTestMutation.ReadOnly;
    public bool DrivesInput => false;

    public Task<LiveTestCaseResult> RunAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = context.Snapshot();
        var player = snapshot.Player;
        if (player is null)
            return Task.FromResult(LiveTestCaseResult.Blocked("player unavailable", "PlayerMissing"));

        var candidates = snapshot.GroundLabels
            .Where(IsDoor)
            .Where(label => label.EntityGridPosition is not null)
            .OrderBy(label => label.DistanceToPlayer)
            .ToArray();
        context.Observe("door candidates", candidates.Length == 0
            ? "none"
            : string.Join(" | ", candidates.Select(DescribeDoor)));
        if (candidates.Length == 0)
            return Task.FromResult(LiveTestCaseResult.Blocked(
                "no visible closed Door label found beside the player", "DoorMissing"));

        var door = candidates[0];
        var doorGrid = door.EntityGridPosition!.Value;
        var components = EntityComponents.ReadComponentMap(context.Reader, door.ItemEntityAddress);
        var stateValues = components.TryGetValue("StateMachine", out var stateMachine)
            ? StateMachineView.ReadValues(context.Reader, stateMachine, 16)
            : [];
        byte? targetable = null;
        byte? targeted = null;
        if (components.TryGetValue("Targetable", out var targetableComponent))
        {
            if (context.Reader.TryReadStruct<byte>(
                    targetableComponent + KnownOffsets.TargetableComponent.IsTargetable, out var value))
                targetable = value;
            if (context.Reader.TryReadStruct<byte>(
                    targetableComponent + KnownOffsets.TargetableComponent.IsTargeted, out value))
                targeted = value;
        }

        context.Observe("selected door", DescribeDoor(door), new Dictionary<string, object?>
        {
            ["entityId"] = door.EntityId,
            ["entityAddress"] = $"0x{(long)door.ItemEntityAddress:X}",
            ["labelAddress"] = $"0x{(long)door.LabelAddress:X}",
            ["path"] = door.Path,
            ["displayName"] = door.DisplayName,
            ["renderName"] = door.RenderName,
            ["gridX"] = doorGrid.X,
            ["gridY"] = doorGrid.Y,
            ["distance"] = door.DistanceToPlayer,
            ["components"] = components.Keys.OrderBy(x => x).ToArray(),
            ["stateMachineValues"] = stateValues,
            ["targetable"] = targetable,
            ["targeted"] = targeted,
        });

        var terrain = DoorTerrainCapture.Capture(
            snapshot, player.GridPosition, doorGrid, radius: 45);
        var artifact = System.IO.Path.Combine(context.EvidenceDirectory, "door-terrain-before.txt");
        File.WriteAllText(artifact, terrain.Text);
        context.Observe("terrain capture", terrain.Summary,
            new Dictionary<string, object?>
            {
                ["artifact"] = artifact,
                ["pathReachableCells"] = terrain.PathReachableCells,
                ["targetingReachableCells"] = terrain.TargetingReachableCells,
                ["behindGrid"] = terrain.Behind is { } behind ? $"{behind.X},{behind.Y}" : null,
                ["behindPathReachable"] = terrain.BehindPathReachable,
                ["behindTargetingReachable"] = terrain.BehindTargetingReachable,
            });

        var ok = true;
        ok &= context.Check(door.DistanceToPlayer <= 50, "door proximity",
            $"distance={door.DistanceToPlayer:F1}");
        ok &= context.Check(snapshot.Nav.IsAvailable && snapshot.Nav.PathReader is not null,
            "path terrain", snapshot.Nav.IsAvailable ? "available" : "unavailable");
        ok &= context.Check(snapshot.Nav.TargetingReader is not null,
            "targeting terrain", snapshot.Nav.TargetingReader is not null ? "available" : "unavailable");
        ok &= context.Check(components.Count > 0, "door components",
            components.Count == 0 ? "none" : string.Join(", ", components.Keys.OrderBy(x => x)));

        return Task.FromResult(ok
            ? LiveTestCaseResult.Pass("closed door identity and terrain captured", "DoorTerrainCaptured")
            : LiveTestCaseResult.Fail("door capture was incomplete", "DoorReadIncomplete"));
    }

    internal static bool IsDoor(GroundLabelView label)
        => label.IsLabelVisible
           && LootClosestVisible.IsDoorIdentity(label.Path, label.DisplayName);

    internal static string DescribeDoor(GroundLabelView label)
        => $"id={label.EntityId} path='{label.Path}' text='{label.DisplayName}' render='{label.RenderName}' "
           + $"grid={label.EntityGridPosition} d={label.DistanceToPlayer:F1} visible={label.IsLabelVisible} onScreen={label.IsRectOnScreen}";
}

internal sealed record DoorTerrainCapture(
    string Text,
    string Summary,
    int PathReachableCells,
    int TargetingReachableCells,
    Vector2i? Behind,
    bool BehindPathReachable,
    bool BehindTargetingReachable)
{
    public static DoorTerrainCapture Capture(
        GameSnapshot snapshot, Vector2i player, Vector2i door, int radius)
    {
        var path = snapshot.Nav.PathReader;
        var targeting = snapshot.Nav.TargetingReader;
        if (!snapshot.Nav.IsAvailable || path is null)
            return new("terrain unavailable", "terrain unavailable", 0, 0, null, false, false);

        var minX = Math.Max(0, door.X - radius);
        var maxX = Math.Min(snapshot.Nav.Width - 1, door.X + radius);
        var minY = Math.Max(0, door.Y - radius);
        var maxY = Math.Min(snapshot.Nav.Height - 1, door.Y + radius);
        var pathReachable = Flood(path, player, minX, maxX, minY, maxY);
        var targetingReachable = targeting is null
            ? []
            : Flood(targeting, player, minX, maxX, minY, maxY);
        var behind = FindBehind(path, player, door, minX, maxX, minY, maxY);

        var sb = new StringBuilder();
        sb.AppendLine($"area=0x{snapshot.AreaHash:X8} size={snapshot.Nav.Width}x{snapshot.Nav.Height}");
        sb.AppendLine($"player={player.X},{player.Y} door={door.X},{door.Y} radius={radius}");
        sb.AppendLine($"behind={(behind is { } b ? $"{b.X},{b.Y}" : "none")}");
        sb.AppendLine("legend: P=player D=door B=behind r=path-reachable .=walkable #=blocked");
        sb.AppendLine("PATH");
        AppendLayer(sb, path, pathReachable, player, door, behind, minX, maxX, minY, maxY);
        if (targeting is not null)
        {
            sb.AppendLine("TARGETING");
            AppendLayer(sb, targeting, targetingReachable, player, door, behind,
                minX, maxX, minY, maxY);
        }
        sb.AppendLine("TRANSECT player -> door -> beyond (offset,x,y,path,targeting)");
        AppendTransect(sb, path, targeting, player, door);

        var behindPathReachable = behind is { } bp && pathReachable.Contains(Pack(bp.X, bp.Y));
        var behindTargetingReachable = behind is { } bt
            && targetingReachable.Contains(Pack(bt.X, bt.Y));
        var summary = $"pathReach={pathReachable.Count} targetingReach={targetingReachable.Count} "
                      + $"behind={behind} pathBehindReachable={behindPathReachable} "
                      + $"targetingBehindReachable={behindTargetingReachable}";
        return new(sb.ToString(), summary, pathReachable.Count, targetingReachable.Count,
            behind, behindPathReachable, behindTargetingReachable);
    }

    private static HashSet<long> Flood(
        ICellReader cells, Vector2i start, int minX, int maxX, int minY, int maxY)
    {
        var result = new HashSet<long>();
        if (start.X < minX || start.X > maxX || start.Y < minY || start.Y > maxY
            || cells.Read(start.X, start.Y) <= 0)
            return result;
        var queue = new Queue<Vector2i>();
        queue.Enqueue(start);
        result.Add(Pack(start.X, start.Y));
        ReadOnlySpan<int> dx = stackalloc[] { 1, -1, 0, 0 };
        ReadOnlySpan<int> dy = stackalloc[] { 0, 0, 1, -1 };
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            for (var i = 0; i < 4; i++)
            {
                var x = current.X + dx[i];
                var y = current.Y + dy[i];
                if (x < minX || x > maxX || y < minY || y > maxY) continue;
                var key = Pack(x, y);
                if (result.Contains(key) || cells.Read(x, y) <= 0) continue;
                result.Add(key);
                queue.Enqueue(new Vector2i { X = x, Y = y });
            }
        }
        return result;
    }

    private static Vector2i? FindBehind(
        ICellReader path, Vector2i player, Vector2i door,
        int minX, int maxX, int minY, int maxY)
    {
        var dx = door.X - player.X;
        var dy = door.Y - player.Y;
        var length = Math.Max(1, Math.Sqrt((double)dx * dx + (double)dy * dy));
        var ux = dx / length;
        var uy = dy / length;
        for (var distance = 12; distance <= 35; distance += 3)
        for (var lateral = 0; lateral <= 10; lateral++)
        for (var sign = -1; sign <= 1; sign += 2)
        {
            var x = (int)Math.Round(door.X + ux * distance - uy * lateral * sign);
            var y = (int)Math.Round(door.Y + uy * distance + ux * lateral * sign);
            if (x < minX || x > maxX || y < minY || y > maxY) continue;
            if (path.Read(x, y) > 0) return new Vector2i { X = x, Y = y };
        }
        return null;
    }

    private static void AppendLayer(
        StringBuilder sb, ICellReader cells, HashSet<long> reachable,
        Vector2i player, Vector2i door, Vector2i? behind,
        int minX, int maxX, int minY, int maxY)
    {
        // Two-cell sampling keeps the artifact compact while retaining the door/wall shape.
        for (var y = minY; y <= maxY; y += 2)
        {
            for (var x = minX; x <= maxX; x += 2)
            {
                var ch = cells.Read(x, y) > 0 ? '.' : '#';
                if (reachable.Contains(Pack(x, y))) ch = 'r';
                if (Math.Abs(x - player.X) <= 1 && Math.Abs(y - player.Y) <= 1) ch = 'P';
                if (Math.Abs(x - door.X) <= 1 && Math.Abs(y - door.Y) <= 1) ch = 'D';
                if (behind is { } b && Math.Abs(x - b.X) <= 1 && Math.Abs(y - b.Y) <= 1) ch = 'B';
                sb.Append(ch);
            }
            sb.AppendLine();
        }
    }

    private static void AppendTransect(
        StringBuilder sb, ICellReader path, ICellReader? targeting,
        Vector2i player, Vector2i door)
    {
        var dx = door.X - player.X;
        var dy = door.Y - player.Y;
        var length = Math.Max(1, Math.Sqrt((double)dx * dx + (double)dy * dy));
        var ux = dx / length;
        var uy = dy / length;
        for (var offset = -(int)Math.Min(15, length); offset <= 45; offset++)
        {
            var x = (int)Math.Round(door.X + ux * offset);
            var y = (int)Math.Round(door.Y + uy * offset);
            sb.AppendLine($"{offset,3},{x},{y},{path.Read(x, y)},{targeting?.Read(x, y).ToString() ?? "NA"}");
        }
    }

    private static long Pack(int x, int y) => ((long)x << 32) | (uint)y;
}
