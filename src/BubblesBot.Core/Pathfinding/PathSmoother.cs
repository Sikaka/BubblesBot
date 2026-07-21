namespace BubblesBot.Core.Pathfinding;

/// <summary>
/// Line-of-sight path simplification. Walks the cell-by-cell A* output and replaces runs of
/// cells that have a clear line through walkable terrain with their endpoints. Result: a
/// short list of waypoints suitable for cursor-based movement, instead of one waypoint per
/// grid cell.
///
/// "Walkable" here means cell value ≥ <paramref name="minWalkable"/>. Cap segment length to
/// avoid long shortcuts past wall corners that cursor-based movement would clip.
/// </summary>
public static class PathSmoother
{
    private const float MaxSegmentLengthGrid = 100f;

    /// <summary>
    /// Smooth a cell-by-cell path. Blink-action cells are <b>preserved as anchors</b>: the
    /// smoother never spans an LOS shortcut across a blink, since the executor needs to fire
    /// the dash skill at the boundary cell. Result: walk-only segments collapse, blinks
    /// remain on their original cells.
    /// </summary>
    public static IReadOnlyList<PathCell> Smooth(ICellReader pf, IReadOnlyList<PathCell> path, int minWalkable = 4)
    {
        if (path.Count <= 2) return path;

        var result = new List<PathCell>(path.Count / 4) { path[0] };
        var current = 0;
        while (current < path.Count - 1)
        {
            // Locate the next blink anchor — smoothing horizon stops there.
            var horizon = path.Count - 1;
            for (var i = current + 1; i <= horizon; i++)
            {
                if (path[i].Action == StepAction.Blink) { horizon = i; break; }
            }

            var farthest = current + 1;
            for (var i = horizon; i > current + 1; i--)
            {
                var dx = path[i].X - path[current].X;
                var dy = path[i].Y - path[current].Y;
                if (MathF.Sqrt(dx * dx + dy * dy) > MaxSegmentLengthGrid) continue;
                if (HasLineOfSight(pf, path[current], path[i], minWalkable))
                {
                    farthest = i;
                    break;
                }
            }
            result.Add(path[farthest]);
            current = farthest;
        }
        return result;
    }

    /// <summary>
    /// Public LOS check — Bresenham line between two cells against any cell reader. Pass the
    /// targeting reader with <c>minValue=1</c> for "can the bot see/click this point through
    /// the world," or the walkable reader with <c>minValue=4</c> for "is this a clean walking
    /// segment." Used by arrival/click gates to short-circuit only when the player actually
    /// has a clean line, not just when Euclidean distance is short.
    /// </summary>
    public static bool HasLineOfSight(ICellReader cells, int ax, int ay, int bx, int by, int minValue = 1)
        => HasLineOfSight(cells, new PathCell(ax, ay), new PathCell(bx, by), minValue);

    /// <summary>Bresenham line — every cell on the segment must satisfy walkability.</summary>
    private static bool HasLineOfSight(ICellReader pf, PathCell a, PathCell b, int minWalkable)
    {
        int x = a.X, y = a.Y;
        int dx = Math.Abs(b.X - a.X), sx = a.X < b.X ? 1 : -1;
        int dy = -Math.Abs(b.Y - a.Y), sy = a.Y < b.Y ? 1 : -1;
        int err = dx + dy;
        while (true)
        {
            if (pf.Read(x, y) < minWalkable) return false;
            if (x == b.X && y == b.Y) return true;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x += sx; }
            if (e2 <= dx) { err += dx; y += sy; }
        }
    }
}
