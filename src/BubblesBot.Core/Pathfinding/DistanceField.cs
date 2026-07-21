using BubblesBot.Core.Game;

namespace BubblesBot.Core.Pathfinding;

/// <summary>
/// A reverse-Dijkstra flow field toward a single target, ported from the AutoExile Radar plugin's
/// <c>PathFinder</c>. Built once (expensively, off the tick thread) over a walkable grid; afterwards
/// <see cref="TryGetPath"/> from <em>any</em> start cell is O(path length) with no re-search — so as
/// the player moves, the guidance route updates for free. Rebuild only when the target or the area
/// (terrain) changes.
///
/// <para>Walkability comes from an <see cref="ICellReader"/> (value &gt; 0 = walkable); costs are
/// treated as flat (geometric distance), matching Radar's binary-grid behaviour.</para>
/// </summary>
public sealed class DistanceField
{
    // Neighbor order is the encoding basis for the direction grid: dir value (1..8) = index+1.
    private static readonly (int dx, int dy)[] Neighbors =
    {
        ( 1,  0), ( 1,  1), ( 0,  1), (-1,  1),
        (-1,  0), (-1, -1), ( 0, -1), ( 1, -1),
    };
    private const float Sqrt2 = 1.4142136f;

    private readonly int _width;
    private readonly int _height;
    private readonly byte[] _dir; // 0 = unreachable, else 1 + neighbor index pointing toward target

    public Vector2i Target { get; }
    public int Width => _width;
    public int Height => _height;

    private DistanceField(int width, int height, byte[] dir, Vector2i target)
    {
        _width = width;
        _height = height;
        _dir = dir;
        Target = target;
    }

    public bool IsReachable(Vector2i from)
    {
        if ((uint)from.X >= (uint)_width || (uint)from.Y >= (uint)_height) return false;
        if (from.X == Target.X && from.Y == Target.Y) return true;
        return _dir[from.Y * _width + from.X] != 0;
    }

    /// <summary>
    /// Flood outward from <paramref name="target"/> across walkable cells, then bake each reachable
    /// cell's best next-hop into a direction grid. Snaps the target to the nearest walkable cell
    /// within a few cells if it lands on a wall.
    /// </summary>
    public static DistanceField Build(ICellReader grid, Vector2i target)
    {
        int w = grid.Width, h = grid.Height;
        int n = w * h;

        // Snapshot walkability once (each ICellReader.Read may hit memory + cache).
        var walkable = new bool[n];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                walkable[y * w + x] = grid.Read(x, y) > 0;

        target = SnapToWalkable(walkable, w, h, target, maxRadius: 48);

        var dist = new float[n];
        Array.Fill(dist, float.PositiveInfinity);
        var dir = new byte[n];

        if ((uint)target.X >= (uint)w || (uint)target.Y >= (uint)h || !walkable[target.Y * w + target.X])
            return new DistanceField(w, h, dir, target); // unreachable target → empty field

        var tIdx = target.Y * w + target.X;
        dist[tIdx] = 0f;
        var open = new PriorityQueue<int, float>();
        open.Enqueue(tIdx, 0f);

        while (open.TryDequeue(out var idx, out var d))
        {
            if (d > dist[idx]) continue; // stale heap entry
            int cx = idx % w, cy = idx / w;
            for (var k = 0; k < Neighbors.Length; k++)
            {
                var (dx, dy) = Neighbors[k];
                int nx = cx + dx, ny = cy + dy;
                if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                var nIdx = ny * w + nx;
                if (!walkable[nIdx]) continue;
                var step = (dx != 0 && dy != 0) ? Sqrt2 : 1f;
                var nd = d + step;
                if (nd >= dist[nIdx]) continue;
                dist[nIdx] = nd;
                // The neighbor's next hop toward target is back the way we came: the opposite of
                // (dx,dy). Encode that opposite direction so TryGetPath walks nx,ny → cx,cy.
                dir[nIdx] = (byte)(OppositeIndex(k) + 1);
                open.Enqueue(nIdx, nd);
            }
        }

        return new DistanceField(w, h, dir, target);
    }

    /// <summary>
    /// Follow the flow field from <paramref name="start"/> to the target, appending cells to
    /// <paramref name="outPath"/> (excluding the start cell, including the target). Returns false when
    /// the start is unreachable. Guards against cycles with a step cap.
    /// </summary>
    public bool TryGetPath(Vector2i start, List<PathCell> outPath)
    {
        outPath.Clear();
        if (!IsReachable(start)) return false;

        int cx = start.X, cy = start.Y;
        var maxSteps = _width * _height; // hard cap; a valid field never loops
        var steps = 0;
        while (!(cx == Target.X && cy == Target.Y))
        {
            if (steps++ > maxSteps) return false;
            var d = _dir[cy * _width + cx];
            if (d == 0) return false;
            var (dx, dy) = Neighbors[d - 1];
            cx += dx; cy += dy;
            outPath.Add(new PathCell(cx, cy));
        }
        return outPath.Count > 0;
    }

    private static int OppositeIndex(int k)
    {
        // Neighbors are laid out so the opposite of index k is (k + 4) mod 8.
        return (k + 4) & 7;
    }

    private static Vector2i SnapToWalkable(bool[] walkable, int w, int h, Vector2i p, int maxRadius = 8)
    {
        if ((uint)p.X < (uint)w && (uint)p.Y < (uint)h && walkable[p.Y * w + p.X]) return p;
        for (var r = 1; r <= maxRadius; r++)
            for (var dy = -r; dy <= r; dy++)
                for (var dx = -r; dx <= r; dx++)
                {
                    if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue;
                    int nx = p.X + dx, ny = p.Y + dy;
                    if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                    if (walkable[ny * w + nx]) return new Vector2i { X = nx, Y = ny };
                }
        return p;
    }
}
