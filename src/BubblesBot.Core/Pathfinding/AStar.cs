namespace BubblesBot.Core.Pathfinding;

/// <summary>One step in a path. <see cref="Action"/> tells executors how to perform the step.</summary>
public readonly record struct PathCell(int X, int Y, StepAction Action = StepAction.Walk);

/// <summary>How a step is to be performed by the executor.</summary>
public enum StepAction
{
    /// <summary>Reachable by holding the walk skill — normal terrain.</summary>
    Walk,
    /// <summary>Crossed by firing a Dash-class skill (Flame Dash, Frostblink, Lightning Warp).</summary>
    Blink,
}

public readonly record struct Path(bool Found, float Cost, IReadOnlyList<PathCell> Cells)
{
    public static readonly Path NoPath = new(false, 0f, Array.Empty<PathCell>());
}

/// <summary>
/// Optional gap-crossing knobs for <see cref="AStar.FindPath"/>. When enabled, A* expands
/// "blink" edges from cells bordering pf=0 — scanning across the gap on the targeting layer
/// to find a walkable landing cell within range. Each blink is charged a flat
/// <see cref="BlinkPenalty"/> on top of the linear gap distance, so longer gaps cost more
/// and the planner prefers walking around when walking is competitive.
///
/// <para><b>Charge budget.</b> The caller computes <see cref="BlinkPenalty"/> from the
/// current charge state — many ready charges → low penalty (planner welcomes blinks); few or
/// zero ready charges → high penalty (planner avoids them, accepting the longer walk). v1
/// applies the same penalty to every blink in the path; charge-state-aware per-position
/// state expansion is a future refinement.</para>
/// </summary>
public sealed class GapPlan
{
    /// <summary>Max scan distance (grid cells) across a gap to a landing cell.</summary>
    public int BlinkRange { get; init; } = 18;
    /// <summary>Fixed cost added to every blink edge. Caller-computed from charge readiness.</summary>
    public float BlinkPenalty { get; init; } = 8f;
    /// <summary>Cells past the gap to push the landing — avoid landing right on the edge.</summary>
    public int LandingBuffer { get; init; } = 3;
    /// <summary>Disable the whole gap-expansion code path. Equivalent to passing null.</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// A* pathfinder over a grid exposed via <see cref="ICellReader"/>. Per-instance buffers are
/// kept and reused via a generation stamp — no per-call buffer alloc.
///
/// <para>Cost model: walking onto a cell with value v ∈ [1..5] costs <c>(6 - v) × stepDistance</c>.
/// Diagonal steps cost √2 × the cardinal cost. Optional gap-crossing edges cost
/// <c>blinkDistance + GapPlan.BlinkPenalty + gapWidth × 5</c> — the per-cell-of-gap term
/// strongly prefers perpendicular crossings over diagonal ones across the same wall.</para>
/// </summary>
public sealed class AStar
{
    // 8-neighbor offsets: (dx, dy, baseStepCost). Stack-friendly fixed-size data.
    private static readonly (int dx, int dy, float cost)[] Neighbors =
    {
        ( 1,  0, 1f),
        (-1,  0, 1f),
        ( 0,  1, 1f),
        ( 0, -1, 1f),
        ( 1,  1, 1.4142136f),
        ( 1, -1, 1.4142136f),
        (-1,  1, 1.4142136f),
        (-1, -1, 1.4142136f),
    };

    private readonly int _width;
    private readonly int _height;
    private readonly float[] _gScore;
    private readonly int[]   _cameFrom;
    private readonly int[]   _generation;
    private readonly byte[]  _stepAction;   // 0 = walk, 1 = blink — how this cell was reached
    private int _currentGen;

    public AStar(int width, int height)
    {
        _width  = width;
        _height = height;
        var n = width * height;
        _gScore     = new float[n];
        _cameFrom   = new int[n];
        _generation = new int[n];
        _stepAction = new byte[n];
    }

    /// <summary>
    /// Pathfind from <paramref name="start"/> to <paramref name="goal"/>. Snaps either end
    /// to the nearest walkable cell within ~8 cells if needed. Returns <see cref="Path.NoPath"/>
    /// after <paramref name="maxNodes"/> dequeues.
    ///
    /// <para>Pass a non-null <paramref name="gap"/> with an optional <paramref name="targeting"/>
    /// reader to enable blink-across-gap edges. Targeting reader missing → falls back to no
    /// blinks even if <paramref name="gap"/> is enabled.</para>
    /// </summary>
    public Path FindPath(
        ICellReader pf, PathCell start, PathCell goal,
        int maxNodes = 200_000, bool flatCost = false,
        GapPlan? gap = null, ICellReader? targeting = null)
    {
        if (pf.Width != _width || pf.Height != _height)
            throw new ArgumentException($"Reader dims {pf.Width}x{pf.Height} != A* dims {_width}x{_height}");

        var (sx, sy) = (Math.Clamp(start.X, 0, _width - 1), Math.Clamp(start.Y, 0, _height - 1));
        var (gx, gy) = (Math.Clamp(goal .X, 0, _width - 1), Math.Clamp(goal .Y, 0, _height - 1));

        if (pf.Read(sx, sy) == 0) (sx, sy) = SnapToWalkable(pf, sx, sy);
        if (pf.Read(gx, gy) == 0) (gx, gy) = SnapToWalkable(pf, gx, gy);
        if (pf.Read(sx, sy) == 0 || pf.Read(gx, gy) == 0) return Path.NoPath;

        unchecked { _currentGen++; }
        if (_currentGen == 0) { Array.Clear(_generation); _currentGen = 1; }

        var open = new PriorityQueue<int, float>();
        var startIdx = sy * _width + sx;
        var goalIdx  = gy * _width + gx;

        _gScore    [startIdx] = 0f;
        _cameFrom  [startIdx] = -1;
        _stepAction[startIdx] = 0;
        _generation[startIdx] = _currentGen;
        open.Enqueue(startIdx, Heuristic(sx, sy, gx, gy));

        var canBlink = gap is { Enabled: true } && targeting is not null;

        var dequeued = 0;
        while (open.TryDequeue(out var currentIdx, out _) && dequeued++ < maxNodes)
        {
            if (currentIdx == goalIdx)
                return ReconstructPath(currentIdx, _gScore[currentIdx]);

            var cx = currentIdx % _width;
            var cy = currentIdx / _width;
            var currentG = _gScore[currentIdx];
            var hasGapNeighbor = false;

            // Standard 8-neighbor walk expansion.
            foreach (var (dx, dy, baseCost) in Neighbors)
            {
                var nx = cx + dx;
                var ny = cy + dy;
                if ((uint)nx >= (uint)_width || (uint)ny >= (uint)_height) continue;

                var cellValue = pf.Read(nx, ny);
                if (cellValue == 0)
                {
                    // Walkable=0 neighbor → maybe a gap; mark for the blink-expansion below.
                    hasGapNeighbor = true;
                    continue;
                }

                var stepCost  = flatCost ? baseCost : baseCost * (6 - cellValue);
                var tentative = currentG + stepCost;
                var nIdx = ny * _width + nx;

                var seen = _generation[nIdx] == _currentGen;
                if (seen && tentative >= _gScore[nIdx]) continue;

                _gScore    [nIdx] = tentative;
                _cameFrom  [nIdx] = currentIdx;
                _stepAction[nIdx] = 0;
                _generation[nIdx] = _currentGen;
                open.Enqueue(nIdx, tentative + Heuristic(nx, ny, gx, gy));
            }

            // Blink expansion: from a cell that borders a gap, scan in 8 directions through
            // pf=0/tgt>0 cells looking for landing cells. Each landing becomes a candidate
            // neighbor with a flat-penalty cost.
            if (canBlink && hasGapNeighbor && gap is not null)
            {
                foreach (var landing in ScanBlinkLandings(pf, targeting!, cx, cy, gap))
                {
                    var dist = MathF.Sqrt((landing.x - cx) * (landing.x - cx) + (landing.y - cy) * (landing.y - cy));
                    var blinkCost  = dist + gap.BlinkPenalty + landing.gapWidth * 5f;
                    var tentative  = currentG + blinkCost;
                    var nIdx = landing.y * _width + landing.x;

                    var seen = _generation[nIdx] == _currentGen;
                    if (seen && tentative >= _gScore[nIdx]) continue;

                    _gScore    [nIdx] = tentative;
                    _cameFrom  [nIdx] = currentIdx;
                    _stepAction[nIdx] = 1;
                    _generation[nIdx] = _currentGen;
                    open.Enqueue(nIdx, tentative + Heuristic(landing.x, landing.y, gx, gy));
                }
            }
        }

        return Path.NoPath;
    }

    /// <summary>
    /// Scan from a cell that borders a gap in each of 8 directions through pf=0/tgt&gt;0
    /// cells, looking for the first walkable landing within <see cref="GapPlan.BlinkRange"/>.
    /// Pushes the landing further into safe terrain by <see cref="GapPlan.LandingBuffer"/>
    /// cells so the bot doesn't end up perched on the edge.
    /// </summary>
    private IEnumerable<(int x, int y, int gapWidth)> ScanBlinkLandings(
        ICellReader pf, ICellReader tgt, int bx, int by, GapPlan gap)
    {
        foreach (var (dx, dy, _) in Neighbors)
        {
            var firstX = bx + dx;
            var firstY = by + dy;
            if ((uint)firstX >= (uint)_width || (uint)firstY >= (uint)_height) continue;
            if (pf.Read(firstX, firstY) != 0) continue; // only follow directions that start in a gap

            int x = firstX, y = firstY, steps = 1;
            while (true)
            {
                var actualDist = MathF.Sqrt((x - bx) * (x - bx) + (y - by) * (y - by));
                if (actualDist > gap.BlinkRange) break;
                if ((uint)x >= (uint)_width || (uint)y >= (uint)_height) break;
                if (tgt.Read(x, y) == 0) break; // wall — not jumpable

                if (pf.Read(x, y) > 0)
                {
                    // Found a walkable landing. Push past the edge.
                    int lx = x, ly = y;
                    for (var b = 0; b < gap.LandingBuffer; b++)
                    {
                        var nx = lx + dx;
                        var ny = ly + dy;
                        if ((uint)nx >= (uint)_width || (uint)ny >= (uint)_height) break;
                        if (pf.Read(nx, ny) < 3) break; // stop at fringe / walls
                        if (MathF.Sqrt((nx - bx) * (nx - bx) + (ny - by) * (ny - by)) > gap.BlinkRange) break;
                        lx = nx; ly = ny;
                    }
                    yield return (lx, ly, steps);
                    break;
                }
                x += dx; y += dy; steps++;
            }
        }
    }

    /// <summary>
    /// Octile distance with a tiny inflation. The pure octile heuristic is admissible (never
    /// overestimates) but produces enormous fans of equal-cost cells in open terrain — A*
    /// expands all of them. Multiplying by ~1.001 breaks the ties without meaningfully
    /// affecting path optimality (worst case 0.1 % longer than optimal) and dramatically cuts
    /// node count in long-distance searches across PoE maps. Standard "weighted A*" trick.
    /// </summary>
    private static float Heuristic(int x, int y, int gx, int gy)
    {
        var dx = Math.Abs(x - gx);
        var dy = Math.Abs(y - gy);
        var octile = (dx + dy) + (1.4142136f - 2f) * Math.Min(dx, dy);
        return octile * 1.001f;
    }

    private Path ReconstructPath(int goalIdx, float cost)
    {
        var cells = new List<PathCell>();
        var idx = goalIdx;
        while (idx != -1)
        {
            var action = _stepAction[idx] == 1 ? StepAction.Blink : StepAction.Walk;
            cells.Add(new PathCell(idx % _width, idx / _width, action));
            idx = _cameFrom[idx];
        }
        cells.Reverse();
        return new Path(true, cost, cells);
    }

    private (int x, int y) SnapToWalkable(ICellReader pf, int x, int y, int maxRadius = 8)
    {
        for (var r = 1; r <= maxRadius; r++)
        {
            for (var dy = -r; dy <= r; dy++)
            {
                for (var dx = -r; dx <= r; dx++)
                {
                    if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue;
                    var nx = x + dx;
                    var ny = y + dy;
                    if ((uint)nx >= (uint)_width || (uint)ny >= (uint)_height) continue;
                    if (pf.Read(nx, ny) > 0) return (nx, ny);
                }
            }
        }
        return (x, y);
    }
}
