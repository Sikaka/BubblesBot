using BubblesBot.Core.Game;
using BubblesBot.Core.Pathfinding;

namespace BubblesBot.Bot.Systems;

public readonly record struct ClosedDoorCandidate(
    uint EntityId, nint LabelAddress, Vector2i Grid);

public readonly record struct RequiredDoorRoute(
    uint EntityId,
    nint LabelAddress,
    Vector2i DoorGrid,
    Vector2i ApproachGrid,
    Vector2i FrontierGrid,
    int TargetingCost);

/// <summary>
/// Finds a closed door only when it lies on the targeting-terrain route from an exhausted
/// movement component to genuinely unvisited terrain. The targeting layer was live-proven to
/// remain connected through a closed Dungeon door while the movement layer was disconnected;
/// that split lets us treat a door as an edge in the exploration graph without opening every
/// visible door as generic loot.
/// </summary>
public static class DoorRoutingPolicy
{
    private const int DoorEdgeRadius = 6;
    private const int DoorTraversalPenalty = 40;
    private const int ApproachSearchRadius = 18;
    private const int NodeCap = 300_000;

    /// <summary>
    /// Route to an already-known objective over a combined movement/targeting graph. Ordinary
    /// movement cells cost one; targeting-only cells are legal solely beside a known closed
    /// door and pay a fixed interaction penalty. A walk-only alternative therefore wins when
    /// reasonable, while a door on the useful route is surfaced as the next transition edge.
    /// </summary>
    public static RequiredDoorRoute? FindRequiredDoorToTarget(
        ICellReader path,
        ICellReader targeting,
        Vector2i player,
        IReadOnlyList<ClosedDoorCandidate> doors,
        Vector2i target)
    {
        var reachable = FloodPath(path, player);
        return FindWeighted(
            path, targeting, player, doors,
            (x, y) => reachable.Contains(Pack(x, y)),
            (x, y, doorIndex) => doorIndex >= 0 && x == target.X && y == target.Y,
            target);
    }

    public static RequiredDoorRoute? FindRequiredDoor(
        ICellReader path,
        ICellReader targeting,
        Vector2i player,
        IReadOnlyList<ClosedDoorCandidate> doors,
        Func<int, int, bool> pathReachable,
        Func<Vector2i, bool> isVisited)
    {
        return FindWeighted(
            path, targeting, player, doors, pathReachable,
            (x, y, doorIndex) => doorIndex >= 0
                && path.Read(x, y) > 0
                && !pathReachable(x, y)
                && !isVisited(new Vector2i { X = x, Y = y }),
            null);
    }

    private static RequiredDoorRoute? FindWeighted(
        ICellReader path,
        ICellReader targeting,
        Vector2i player,
        IReadOnlyList<ClosedDoorCandidate> doors,
        Func<int, int, bool> pathReachable,
        Func<int, int, int, bool> isGoal,
        Vector2i? fixedGoal)
    {
        if (doors.Count == 0 || path.Read(player.X, player.Y) <= 0) return null;

        var queue = new PriorityQueue<(int X, int Y, int Cost, int DoorIndex), int>();
        var best = new Dictionary<long, int>();
        queue.Enqueue((player.X, player.Y, 0, -1), 0);
        best[Pack(player.X, player.Y)] = 0;
        ReadOnlySpan<int> dx = stackalloc[] { 1, -1, 0, 0 };
        ReadOnlySpan<int> dy = stackalloc[] { 0, 0, 1, -1 };
        var nodes = 0;

        while (queue.Count > 0 && nodes++ < NodeCap)
        {
            var current = queue.Dequeue();
            if (best.TryGetValue(Pack(current.X, current.Y), out var known) && current.Cost != known)
                continue;
            if (isGoal(current.X, current.Y, current.DoorIndex))
            {
                var door = doors[current.DoorIndex];
                var approach = FindApproach(path, door.Grid, player, pathReachable);
                if (approach is not { } point) return null;
                var goal = fixedGoal ?? new Vector2i { X = current.X, Y = current.Y };
                return new RequiredDoorRoute(
                    door.EntityId, door.LabelAddress, door.Grid, point, goal, current.Cost);
            }

            for (var i = 0; i < 4; i++)
            {
                var x = current.X + dx[i];
                var y = current.Y + dy[i];
                var doorIndex = current.DoorIndex;
                var stepCost = 1;
                if (path.Read(x, y) <= 0)
                {
                    if (targeting.Read(x, y) <= 0) continue;
                    var besideDoor = DoorNearEdge(doors, current.X, current.Y, x, y);
                    if (besideDoor < 0) continue;
                    if (doorIndex < 0)
                    {
                        doorIndex = besideDoor;
                        stepCost += DoorTraversalPenalty;
                    }
                }

                var cost = current.Cost + stepCost;
                var key = Pack(x, y);
                if (best.TryGetValue(key, out var oldCost) && oldCost <= cost) continue;
                best[key] = cost;
                queue.Enqueue((x, y, cost, doorIndex), cost);
            }
        }
        return null;
    }

    private static HashSet<long> FloodPath(ICellReader path, Vector2i player)
    {
        var reached = new HashSet<long>();
        if (path.Read(player.X, player.Y) <= 0) return reached;
        var queue = new Queue<(int X, int Y)>();
        queue.Enqueue((player.X, player.Y));
        reached.Add(Pack(player.X, player.Y));
        ReadOnlySpan<int> dx = stackalloc[] { 1, -1, 0, 0 };
        ReadOnlySpan<int> dy = stackalloc[] { 0, 0, 1, -1 };
        while (queue.Count > 0 && reached.Count < NodeCap)
        {
            var current = queue.Dequeue();
            for (var i = 0; i < 4; i++)
            {
                var x = current.X + dx[i];
                var y = current.Y + dy[i];
                var key = Pack(x, y);
                if (path.Read(x, y) <= 0 || !reached.Add(key)) continue;
                queue.Enqueue((x, y));
            }
        }
        return reached;
    }

    private static int DoorNearEdge(
        IReadOnlyList<ClosedDoorCandidate> doors,
        int fromX, int fromY, int toX, int toY)
    {
        var radius2 = DoorEdgeRadius * DoorEdgeRadius;
        var best = -1;
        var bestDistance = int.MaxValue;
        for (var i = 0; i < doors.Count; i++)
        {
            var door = doors[i].Grid;
            var fromDx = door.X - fromX;
            var fromDy = door.Y - fromY;
            var toDx = door.X - toX;
            var toDy = door.Y - toY;
            var distance = Math.Min(
                fromDx * fromDx + fromDy * fromDy,
                toDx * toDx + toDy * toDy);
            if (distance > radius2 || distance >= bestDistance) continue;
            bestDistance = distance;
            best = i;
        }
        return best;
    }

    private static Vector2i? FindApproach(
        ICellReader path,
        Vector2i door,
        Vector2i player,
        Func<int, int, bool> pathReachable)
    {
        Vector2i? best = null;
        var bestDoorDistance = int.MaxValue;
        var bestPlayerDistance = long.MaxValue;
        for (var y = door.Y - ApproachSearchRadius; y <= door.Y + ApproachSearchRadius; y++)
        for (var x = door.X - ApproachSearchRadius; x <= door.X + ApproachSearchRadius; x++)
        {
            if (path.Read(x, y) <= 0 || !pathReachable(x, y)) continue;
            var dx = door.X - x;
            var dy = door.Y - y;
            var doorDistance = dx * dx + dy * dy;
            if (doorDistance > ApproachSearchRadius * ApproachSearchRadius) continue;
            long px = player.X - x;
            long py = player.Y - y;
            var playerDistance = px * px + py * py;
            if (doorDistance > bestDoorDistance
                || doorDistance == bestDoorDistance && playerDistance >= bestPlayerDistance)
                continue;
            bestDoorDistance = doorDistance;
            bestPlayerDistance = playerDistance;
            best = new Vector2i { X = x, Y = y };
        }
        return best;
    }

    private static long Pack(int x, int y) => ((long)x << 32) | (uint)y;
}
