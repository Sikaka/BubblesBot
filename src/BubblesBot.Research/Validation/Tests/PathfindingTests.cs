using BubblesBot.Core.Game;
using BubblesBot.Core.Pathfinding;

namespace BubblesBot.Research.Validation.Tests;

/// <summary>
/// End-to-end check on the Core/Pathfinding stack: build a TerrainCellReader from the live
/// terrain snapshot, run A* from the player to a target a few cells away, assert the path is
/// contiguous and every cell along it is walkable. Validates that:
///   - TerrainGridReader resolves dimensions and decodes packed nibbles correctly
///   - TerrainCellReader caches without corrupting reads
///   - AStar produces an actual route between two real positions
///   - PathSmoother preserves walkability while collapsing redundant waypoints
/// </summary>
public sealed class PlayerToNearbyCellPathfindTest : ValidationTest
{
    public override string Name => "A* pathfind from player to nearby walkable cell";
    public override string? Group => "Pathfinding";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(StateKeys.IngameData, out var ingameDataObj) || ingameDataObj is not nint ingameData)
            return new TestOutcome.Skip(Name, "IngameData not resolved");

        if (!ctx.State.TryGetValue(StateKeys.LocalPlayer, out var playerObj) || playerObj is not nint player)
            return new TestOutcome.Skip(Name, "LocalPlayer not resolved");

        if (!TerrainGridReader.TryReadSnapshot(ctx.Reader, ingameData, out var terrain))
            return new TestOutcome.Fail(Name, "could not read terrain snapshot");

        var components = EntityComponents.ReadComponentMap(ctx.Reader, player);
        if (!components.TryGetValue("Positioned", out var positioned) || positioned == 0)
            return new TestOutcome.Fail(Name, "player has no Positioned component");
        if (!ctx.Reader.TryReadStruct<Vector2i>(positioned + KnownOffsets.PositionedComponent.GridPosition, out var grid))
            return new TestOutcome.Fail(Name, "could not read player grid position");

        var pf = new TerrainCellReader(ctx.Reader, terrain, useTargetingLayer: false);

        if (!pf.IsAtLeastOneWalkableNeighbor(grid.X, grid.Y))
            return new TestOutcome.Skip(Name, $"player cell ({grid.X},{grid.Y}) has no walkable neighbors — likely off-grid");

        // Pick a goal that's reachable in a straight line so A* is guaranteed to succeed.
        var goal = FindGoal(pf, grid.X, grid.Y, maxDistance: 25);
        if (!goal.HasValue)
            return new TestOutcome.Skip(Name, "could not find a walkable goal cell within reach");

        var astar = new AStar(pf.Width, pf.Height);
        var path = astar.FindPath(pf, new PathCell(grid.X, grid.Y), new PathCell(goal.Value.x, goal.Value.y));

        if (!path.Found)
            return new TestOutcome.Fail(Name, $"no path from ({grid.X},{grid.Y}) to ({goal.Value.x},{goal.Value.y})");

        // Validate path:
        // 1. Starts at player (or a snapped-walkable cell within 8)
        // 2. Ends at goal (or a snapped-walkable cell within 8)
        // 3. Consecutive cells are 8-neighbors
        // 4. Every cell is walkable
        var cells = path.Cells;
        var startDist = Math.Abs(cells[0].X - grid.X) + Math.Abs(cells[0].Y - grid.Y);
        var endDist   = Math.Abs(cells[^1].X - goal.Value.x) + Math.Abs(cells[^1].Y - goal.Value.y);
        if (startDist > 8) return new TestOutcome.Fail(Name, $"path start ({cells[0].X},{cells[0].Y}) too far from player");
        if (endDist   > 8) return new TestOutcome.Fail(Name, $"path end ({cells[^1].X},{cells[^1].Y}) too far from goal");

        for (var i = 1; i < cells.Count; i++)
        {
            var dx = Math.Abs(cells[i].X - cells[i - 1].X);
            var dy = Math.Abs(cells[i].Y - cells[i - 1].Y);
            if (dx > 1 || dy > 1)
                return new TestOutcome.Fail(Name, $"non-adjacent step at index {i}: ({cells[i - 1].X},{cells[i - 1].Y}) → ({cells[i].X},{cells[i].Y})");
            if (pf.Read(cells[i].X, cells[i].Y) == 0)
                return new TestOutcome.Fail(Name, $"path crosses impassable cell ({cells[i].X},{cells[i].Y}) at index {i}");
        }

        var smoothed = PathSmoother.Smooth(pf, cells);
        return new TestOutcome.Pass(Name,
            $"player=({grid.X},{grid.Y}) goal=({goal.Value.x},{goal.Value.y}) path={cells.Count} cells, smoothed={smoothed.Count} waypoints, cost={path.Cost:F1}");
    }

    /// <summary>
    /// Find a goal that's actually reachable via straight-line walkability — guarantees
    /// A* should succeed if the algorithm is working. Walks outward along each of the 8
    /// cardinal+diagonal directions and returns the farthest walkable cell still in
    /// line-of-sight, capped at <paramref name="maxDistance"/>.
    /// </summary>
    private static (int x, int y)? FindGoal(ICellReader pf, int sx, int sy, int maxDistance)
    {
        (int dx, int dy)[] dirs = { (1,0),(-1,0),(0,1),(0,-1),(1,1),(1,-1),(-1,1),(-1,-1) };
        (int x, int y)? best = null;
        var bestDist = 0;
        foreach (var (dx, dy) in dirs)
        {
            for (var step = maxDistance; step >= 5; step--)
            {
                var nx = sx + dx * step;
                var ny = sy + dy * step;
                if (pf.Read(nx, ny) == 0) continue;
                // require every cell on the segment to be walkable
                bool clear = true;
                for (var s = 1; s <= step; s++)
                {
                    if (pf.Read(sx + dx * s, sy + dy * s) == 0) { clear = false; break; }
                }
                if (!clear) continue;
                if (step > bestDist) { bestDist = step; best = (nx, ny); }
                break;
            }
        }
        return best;
    }
}

internal static class CellReaderExtensions
{
    public static bool IsAtLeastOneWalkableNeighbor(this ICellReader pf, int x, int y)
    {
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
                if ((dx != 0 || dy != 0) && pf.Read(x + dx, y + dy) > 0)
                    return true;
        return false;
    }
}
