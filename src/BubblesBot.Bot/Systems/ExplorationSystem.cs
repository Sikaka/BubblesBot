using BubblesBot.Bot.Behaviors;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Systems;

/// <summary>
/// Tracks which grid cells the player has visited and picks the next unexplored frontier
/// to walk to. The exploration model is intentionally simple:
///
/// <list type="bullet">
///   <item><b>Reveal radius:</b> any cell within network-bubble range of the player's path
///         is marked revealed — discovery (mobs, mechanics, chests) happens at bubble range,
///         not underfoot, so exploration only needs the bubble to sweep the map.</item>
///   <item><b>Frontier:</b> unrevealed ground in the player's connected walkable component.</item>
///   <item><b>Goal selection:</b> a coverage/pack/direction score over reachable BFS candidates.</item>
/// </list>
///
/// <para>This is the foundation that turns "the bot fights what's near it" into "the bot
/// clears a map." Combined with combat + loot, it produces map-running behavior.</para>
///
/// <para><b>Reset on area change.</b> Visited cells are area-scoped — different instances
/// of the same map have completely different layouts.</para>
/// </summary>
public sealed class ExplorationSystem
{
    /// <summary>
    /// Radius around the player considered "revealed" each tick. Discovery happens at
    /// network-bubble range — entities and mechanics enter the client entity list at
    /// ~200 grid — so exploration only needs the BUBBLE to have swept the map, not the
    /// player's feet (2026-07-14 feedback: don't walk every block). Held 40 grid inside
    /// the conservative bubble constant so rim content is reliably cached before its
    /// ground is marked seen.
    /// </summary>
    private const int DefaultRevealRadiusGrid = BubblesBot.Core.Pathfinding.GridConstants.NetworkBubbleGrid - 40;
    private readonly int _revealRadiusGrid;

    /// <summary>Visited cell quantization — store visits at TileGridCells granularity to keep memory small.</summary>
    private const int VisitedQuantum = 23;
    private const float WaveBacktrackToleranceGrid = 100f;

    // Reveal state is kept PER AREA so the zone loop can hop back through a transition into
    // an already-swept zone and immediately see it as exhausted instead of re-walking it.
    // Fresh map instances get fresh area hashes, so stale sets just age out via the cap.
    private readonly Dictionary<uint, HashSet<long>> _visitedByArea = new();
    private HashSet<long> _visited = new();
    private uint _trackingArea;

    /// <summary>
    /// True when the last <see cref="PickFrontier"/> ran with terrain loaded and found
    /// nothing: no unrevealed reachable ground AND no hostile beacon. This is the "zone
    /// finished" signal for the map loop. Cleared on area change.
    /// </summary>
    public bool IsExhausted { get; private set; }

    /// <summary>
    /// Optional id filter for pack-beacon candidates — the owning mode shares its can't-hit
    /// blacklist here so beacons never route back to packs combat already gave up on
    /// (essence-frozen mobs etc.).
    /// </summary>
    public Func<uint, bool>? BeaconSkip { get; set; }
    private Vector2i? _frontierGoal;   // cached BFS frontier; recomputed when reached/visited
    private TimeSpan _nextBfsAt;      // cooldown after a dry BFS flood
    private TimeSpan _lastExhaustLog; // throttle for the exhausted event
    private HashSet<long>? _walkableQuanta;               // all quanta containing walkable cells; lazy per area
    private uint _walkableQuantaArea;
    private HashSet<long>? _connectedWalkableQuanta;
    private long _connectedWalkableRoot;
    // Cells the last DRY BFS flood actually reached — the player's walkable-connected
    // region. Beacon candidates outside it are unreachable (across a gap, separate blob)
    // and would wedge FollowPath in permanent 'no path'.
    private HashSet<long>? _reachableWhenDry;
    private Vector2i? _lastPlayer;
    private double _headingX;
    private double _headingY;
    private Vector2i _waveOrigin;
    private float _maximumWaveRadius;
    private uint _farthestArea;
    private Vector2i _farthestOrigin;
    private Vector2i? _farthestConnectedPoint;

    /// <param name="revealRadiusGrid">
    /// Radius stamped as explored around the player. Mapping should keep the default network-
    /// bubble scale. Small repeatable arenas such as Simulacrum can pass a tighter radius so
    /// every wave physically re-sweeps spawn and loot regions instead of declaring the whole
    /// arena seen from its center.
    /// </param>
    public ExplorationSystem(int revealRadiusGrid = DefaultRevealRadiusGrid)
    {
        _revealRadiusGrid = Math.Clamp(revealRadiusGrid, VisitedQuantum, 500);
    }

    public double LastFrontierScore { get; private set; }
    public string LastFrontierReason { get; private set; } = "none";

    /// <summary>Mark everything in bubble range of the player as revealed. Call every tick from the mode.</summary>
    public void TrackVisit(GameSnapshot snapshot, Vector2i playerGrid)
    {
        if (snapshot.AreaHash != _trackingArea)
        {
            if (_visitedByArea.Count > 16) _visitedByArea.Clear();
            _visited = _visitedByArea.TryGetValue(snapshot.AreaHash, out var known)
                ? known
                : _visitedByArea[snapshot.AreaHash] = new HashSet<long>();
            _frontierGoal = null;
            IsExhausted = false;
            _reachableWhenDry = null;
            _trackingArea = snapshot.AreaHash;
            _lastPlayer = null;
            _headingX = _headingY = 0;
            _waveOrigin = playerGrid;
            _maximumWaveRadius = 0;
        }

        _maximumWaveRadius = Math.Max(_maximumWaveRadius, Distance(_waveOrigin, playerGrid));

        if (_lastPlayer is { } prior)
        {
            var dx = playerGrid.X - prior.X;
            var dy = playerGrid.Y - prior.Y;
            var length = Math.Sqrt((double)dx * dx + (double)dy * dy);
            if (length >= 2)
            {
                // Smooth short combat/reposition movements so the exploration wave keeps its
                // broader heading instead of flipping on every sidestep.
                _headingX = _headingX * 0.8 + dx / length * 0.2;
                _headingY = _headingY * 0.8 + dy / length * 0.2;
            }
        }
        _lastPlayer = playerGrid;
        var rq = _revealRadiusGrid / VisitedQuantum + 1;
        var cx = playerGrid.X / VisitedQuantum;
        var cy = playerGrid.Y / VisitedQuantum;
        long r2 = (long)_revealRadiusGrid * _revealRadiusGrid;
        for (var dy = -rq; dy <= rq; dy++)
            for (var dx = -rq; dx <= rq; dx++)
            {
                // Circular stamp — at bubble scale the square's corners overshoot the true
                // radius by ~40%, which would mark never-discovered pockets as seen.
                long gx = (cx + dx) * VisitedQuantum + VisitedQuantum / 2 - playerGrid.X;
                long gy = (cy + dy) * VisitedQuantum + VisitedQuantum / 2 - playerGrid.Y;
                if (gx * gx + gy * gy > r2) continue;
                _visited.Add(Pack(cx + dx, cy + dy));
            }
    }

    /// <summary>True if a grid cell falls within an already-visited quantum.</summary>
    public bool IsVisited(Vector2i grid)
        => _visited.Contains(Pack(grid.X / VisitedQuantum, grid.Y / VisitedQuantum));

    /// <summary>
    /// Pick the next exploration target from the player's connected walkable terrain.
    /// Static tile landmarks are intentionally not trusted as generic movement goals: maps can
    /// contain disconnected decorative or alternate-zone blobs, and Euclidean-nearest selection
    /// repeatedly wedged Strand against an unreachable waypoint thousands of cells away.
    /// Verified transitions and boss arenas are handled by their dedicated behaviors.
    /// Returns null only when terrain isn't loaded.
    /// </summary>
    public Vector2i? PickFrontier(BehaviorContext ctx)
    {
        var live = ctx.Live;
        if (live is null) return null;
        var player = live.Value.GridPosition;

        // Walkability-aware frontier: the nearest UNVISITED cell reachable over walkable terrain,
        // found by BFS from the player. This is the real map-exploration workhorse — it expands
        // coherently into unexplored reachable space instead of the old 8-direction guess, which
        // ignored terrain and happily pointed into walls (→ no path → jitter / stall).
        var nav = ctx.Snapshot.Nav;
        if (!nav.IsAvailable || nav.PathReader is not { } pf) return null;

        // Reuse the cached goal until we've reached it (it becomes visited) or it's no longer
        // walkable — so the bot commits to a heading rather than re-picking every tick.
        if (_frontierGoal is { } g && !IsVisited(g) && pf.Read(g.X, g.Y) > 0)
        {
            IsExhausted = false;
            return g;
        }

        // The BFS flood is bounded but expensive; after a dry result, retry on a cooldown
        // instead of re-flooding every world tick.
        if (BotMonotonicClock.Now >= _nextBfsAt)
        {
            _frontierGoal = BfsBestUnvisited(ctx, pf, player);
            if (_frontierGoal is null) _nextBfsAt = BotMonotonicClock.Now.Add(TimeSpan.FromSeconds(2));
        }
        else _frontierGoal = null;

        // Pack-beacon fallback. Doctrine: after the map is revealed we detour for DENSE
        // packs (and, later, selected mechanics) — never for stragglers; we don't need to
        // kill every mob on the map. A qualifying pack is direct evidence of worthwhile
        // uncleared content even when the BFS flood finds no unrevealed ground.
        if (_frontierGoal is null)
        {
            _frontierGoal = NearestPackBeacon(ctx, pf, player);
            if ((BotMonotonicClock.Now - _lastExhaustLog).TotalSeconds >= 5)
            {
                Diagnostics.EventLog.Log("explore", _frontierGoal is { } b
                    ? $"BFS frontier dry — walking to pack beacon ({b.X},{b.Y})"
                    : "frontier exhausted: map revealed, no reachable pack worth detouring for");
                _lastExhaustLog = BotMonotonicClock.Now;
            }
        }
        IsExhausted = _frontierGoal is null;
        return _frontierGoal;
    }

    /// <summary>
    /// Nearest reachable PACK worth detouring for: a live, targetable hostile ≥20 grid away,
    /// standing on ground the dry BFS flood actually reached (an unflooded cell is across a
    /// gap / disconnected blob and would wedge FollowPath in 'no path' — live 2026-07-14:
    /// critter across an unjumpable gap), with at least <see cref="BotSettings.MinPackDetourSize"/>
    /// eligible hostiles within 30 grid of it. Cached last-known positions are fine — a
    /// stale pack still points at content we haven't cleared.
    /// </summary>
    private Vector2i? NearestPackBeacon(BehaviorContext ctx, BubblesBot.Core.Pathfinding.ICellReader pf, Vector2i player)
    {
        const long MinBeaconDistGrid = 20;
        const long PackRadiusGrid = 30;
        var minPack = Math.Max(1, ctx.Settings.MinPackDetourSize);
        if (ctx.Entities is null) return null;

        var eligible = new List<Vector2i>();
        foreach (var e in ctx.Entities.Entries.Values)
        {
            if (!TargetEligibility.IsEligible(e)) continue;
            if (BeaconSkip is not null && BeaconSkip(e.Id)) continue;
            if (pf.Read(e.GridPosition.X, e.GridPosition.Y) <= 0) continue;
            if (_reachableWhenDry is { } reach && !reach.Contains(Pack(e.GridPosition.X, e.GridPosition.Y))) continue;
            eligible.Add(e.GridPosition);
        }
        if (eligible.Count == 0) return null;

        Vector2i? bestWave = null, bestFallback = null;
        var bestWaveD2 = long.MaxValue;
        var bestFallbackD2 = long.MaxValue;
        foreach (var pos in eligible)
        {
            long dx = pos.X - player.X, dy = pos.Y - player.Y;
            var d2 = dx * dx + dy * dy;
            if (d2 < MinBeaconDistGrid * MinBeaconDistGrid) continue;
            var packCount = 0;
            foreach (var other in eligible)
            {
                long ox = other.X - pos.X, oy = other.Y - pos.Y;
                if (ox * ox + oy * oy <= PackRadiusGrid * PackRadiusGrid) packCount++;
            }
            if (packCount < minPack) continue;
            if (d2 < bestFallbackD2) { bestFallbackD2 = d2; bestFallback = pos; }
            if (IsWavePreferred(pos) && d2 < bestWaveD2) { bestWaveD2 = d2; bestWave = pos; }
        }
        return bestWave ?? bestFallback;
    }

    /// <summary>
    /// BFS over walkable cells from <paramref name="start"/>; returns the nearest walkable cell
    /// whose visited-quantum is unexplored and is at least <see cref="FrontierMinDistGrid"/> away
    /// (so we don't pick a target on top of ourselves). Expands only through walkable cells, so it
    /// only ever proposes reachable frontiers. Node-capped for worst-case (nearly-cleared map).
    /// </summary>
    private Vector2i? BfsBestUnvisited(BehaviorContext ctx, BubblesBot.Core.Pathfinding.ICellReader pf, Vector2i start)
    {
        // 60k capped the flood at a ~140-grid radius in open terrain — smaller than the
        // visited corridor the bot paints while sweeping, so late-map floods went dry while
        // real frontier existed further out (2026-07-14 stall). 250k reaches ~280 grid;
        // the dry-result cooldown in PickFrontier keeps the worst case off the hot path.
        const int NodeCap = 250_000;
        const int FrontierMinDistGrid = 14;
        var minD2 = (long)FrontierMinDistGrid * FrontierMinDistGrid;

        const int MaxCandidates = 128;
        const int CandidateSearchDepth = 80;
        var q = new Queue<(int x, int y, int cost)>();
        var seen = new HashSet<long>();
        var candidateQuanta = new HashSet<long>();
        var candidates = new List<FrontierScoring.Candidate>(MaxCandidates);
        var hostiles = EligiblePositions(ctx);
        var firstCandidateCost = int.MaxValue;
        q.Enqueue((start.X, start.Y, 0));
        seen.Add(Pack(start.X, start.Y));
        ReadOnlySpan<int> ndx = stackalloc int[] { 1, -1, 0, 0, 1, 1, -1, -1 };
        ReadOnlySpan<int> ndy = stackalloc int[] { 0, 0, 1, -1, 1, -1, 1, -1 };

        var nodes = 0;
        while (q.Count > 0 && nodes < NodeCap)
        {
            var (x, y, cost) = q.Dequeue();
            nodes++;

            if (!IsVisited(new Vector2i { X = x, Y = y }))
            {
                long dx = x - start.X, dy = y - start.Y;
                var position = new Vector2i { X = x, Y = y };
                if (dx * dx + dy * dy >= minD2)
                {
                    firstCandidateCost = Math.Min(firstCandidateCost, cost);
                    var quantum = Pack(x / VisitedQuantum, y / VisitedQuantum);
                    if (candidateQuanta.Add(quantum))
                    {
                        var length = Math.Sqrt((double)dx * dx + (double)dy * dy);
                        var alignment = length > 0 && (_headingX != 0 || _headingY != 0)
                            ? (dx / length * _headingX + dy / length * _headingY)
                              / Math.Max(0.001, Math.Sqrt(_headingX * _headingX + _headingY * _headingY))
                            : 0;
                        candidates.Add(new FrontierScoring.Candidate(
                            position,
                            cost,
                            EstimateCoverageGain(position),
                            NearbyHostiles(position, hostiles),
                            Math.Clamp(alignment, -1, 1),
                            IsWavePreferred(position)));
                    }
                }
            }

            if (candidates.Count >= MaxCandidates
                || (firstCandidateCost != int.MaxValue && cost > firstCandidateCost + CandidateSearchDepth))
                break;

            for (var i = 0; i < 8; i++)
            {
                int nx = x + ndx[i], ny = y + ndy[i];
                var key = Pack(nx, ny);
                if (!seen.Add(key)) continue;
                if (pf.Read(nx, ny) <= 0) continue;   // only expand through walkable terrain
                q.Enqueue((nx, ny, cost + 1));
            }
        }
        if (FrontierScoring.Choose(candidates) is { } chosen)
        {
            LastFrontierScore = chosen.Score;
            LastFrontierReason = $"coverage={chosen.Candidate.NewCoverage} pack={chosen.Candidate.NearbyHostiles} " +
                                 $"path={chosen.Candidate.PathCost} align={chosen.Candidate.DirectionAlignment:F2}";
            _reachableWhenDry = null;
            return chosen.Candidate.Position;
        }
        // Dry flood: everything reachable from the player has been swept. Keep the seen
        // set — it IS the reachability oracle for beacon gating (a hostile whose cell was
        // never flooded sits across a gap / in another blob and can't be walked to).
        _reachableWhenDry = seen;
        return null;
    }

    private int EstimateCoverageGain(Vector2i position)
    {
        var rq = _revealRadiusGrid / VisitedQuantum;
        var cx = position.X / VisitedQuantum;
        var cy = position.Y / VisitedQuantum;
        var gain = 0;
        for (var dy = -rq; dy <= rq; dy++)
            for (var dx = -rq; dx <= rq; dx++)
            {
                if (dx * dx + dy * dy > rq * rq) continue;
                if (!_visited.Contains(Pack(cx + dx, cy + dy))) gain++;
            }
        return gain;
    }

    private static List<Vector2i> EligiblePositions(BehaviorContext ctx)
    {
        var result = new List<Vector2i>();
        if (ctx.Entities is null) return result;
        foreach (var entity in ctx.Entities.Entries.Values)
            if (TargetEligibility.IsEligible(entity)) result.Add(entity.GridPosition);
        return result;
    }

    private static int NearbyHostiles(Vector2i position, IReadOnlyList<Vector2i> hostiles)
    {
        const int radius = 55;
        var count = 0;
        foreach (var hostile in hostiles)
        {
            long dx = hostile.X - position.X, dy = hostile.Y - position.Y;
            if (dx * dx + dy * dy <= radius * radius) count++;
        }
        return count;
    }

    /// <summary>
    /// Reveal progress for the CURRENT walkable area: how many walkable quanta the bubble
    /// has swept vs. the area total. The walkable-quanta census scans the terrain grid once
    /// per area (array-backed reads, strided — a few ms, amortized to zero). Returns
    /// (0, 0) while terrain isn't loaded.
    /// </summary>
    public (int Revealed, int Total) Progress(BehaviorContext ctx)
    {
        var nav = ctx.Snapshot.Nav;
        if (!nav.IsAvailable || nav.PathReader is not { } pf) return (0, 0);

        if (_walkableQuanta is null || _walkableQuantaArea != ctx.Snapshot.AreaHash)
        {
            // Stride 5: a quantum is 23×23 — sampling every 5th cell still hits any quantum
            // with a meaningful walkable region while cutting the scan by 25×.
            var set = new HashSet<long>();
            for (var y = 0; y < nav.Height; y += 5)
                for (var x = 0; x < nav.Width; x += 5)
                    if (pf.Read(x, y) > 0)
                        set.Add(Pack(x / VisitedQuantum, y / VisitedQuantum));
            _walkableQuanta = set;
            _walkableQuantaArea = ctx.Snapshot.AreaHash;
            _connectedWalkableQuanta = null;
            _connectedWalkableRoot = 0;
        }

        // The raw terrain buffer can contain disconnected islands, boss arenas, and padding.
        // Exploration only routes within the player's connected component, so the progress
        // denominator must use the same scope or a fully swept map can misleadingly read low.
        var player = ctx.Live?.GridPosition;
        if (player is null) return (0, 0);
        var rootX = player.Value.X / VisitedQuantum;
        var rootY = player.Value.Y / VisitedQuantum;
        var root = Pack(rootX, rootY);
        if (!_walkableQuanta.Contains(root)
            && pf.Read(player.Value.X, player.Value.Y) > 0)
            _walkableQuanta.Add(root);
        if (_connectedWalkableQuanta is null
            || _connectedWalkableRoot != root
            && !_connectedWalkableQuanta.Contains(root))
        {
            _connectedWalkableQuanta = ConnectedComponent(_walkableQuanta, rootX, rootY);
            _connectedWalkableRoot = root;
        }

        var revealed = 0;
        foreach (var q in _visited)
            if (_connectedWalkableQuanta.Contains(q)) revealed++;
        return (revealed, _connectedWalkableQuanta.Count);
    }

    /// <summary>
    /// Returns the most distant walkable point in the navigation component containing
    /// <paramref name="origin"/>. This is deliberately independent of reveal state: the
    /// network bubble can mark a terminal corridor as explored before the character has
    /// physically reached its arena entrance.
    /// </summary>
    public Vector2i? FarthestConnectedPoint(BehaviorContext ctx, Vector2i origin)
    {
        var nav = ctx.Snapshot.Nav;
        if (!nav.IsAvailable || nav.PathReader is not { } pf) return null;
        if (_farthestArea == ctx.Snapshot.AreaHash
            && _farthestOrigin.X == origin.X && _farthestOrigin.Y == origin.Y)
            return _farthestConnectedPoint;

        EnsureWalkableQuanta(ctx, pf);
        if (_walkableQuanta is null || _walkableQuanta.Count == 0) return null;

        var rootX = origin.X / VisitedQuantum;
        var rootY = origin.Y / VisitedQuantum;
        var root = Pack(rootX, rootY);
        if (!_walkableQuanta.Contains(root))
        {
            // Spawn coordinates can land on a dynamic object cell. Use the nearest terrain
            // quantum, but never jump to an arbitrary disconnected island.
            var nearby = _walkableQuanta
                .Select(key => Unpack(key))
                .Where(q => Math.Abs(q.X - rootX) <= 2 && Math.Abs(q.Y - rootY) <= 2)
                .OrderBy(q => (q.X - rootX) * (q.X - rootX) + (q.Y - rootY) * (q.Y - rootY))
                .ToArray();
            if (nearby.Length == 0) return null;
            var nearest = nearby[0];
            rootX = nearest.X;
            rootY = nearest.Y;
        }

        var farthestQuantum = FarthestNavigableQuantum(
            _walkableQuanta, rootX, rootY, pf, nav.Width, nav.Height, origin);
        if (farthestQuantum is null) return null;

        Vector2i? best = null;
        long bestD2 = -1;
        var minX = Math.Max(0, farthestQuantum.Value.X * VisitedQuantum);
        var minY = Math.Max(0, farthestQuantum.Value.Y * VisitedQuantum);
        var maxX = Math.Min(nav.Width, minX + VisitedQuantum);
        var maxY = Math.Min(nav.Height, minY + VisitedQuantum);
        for (var y = minY; y < maxY; y++)
        for (var x = minX; x < maxX; x++)
        {
            if (pf.Read(x, y) <= 0) continue;
            var candidate = new Vector2i { X = x, Y = y };
            var d2 = DistanceSquared(origin, candidate);
            if (d2 <= bestD2) continue;
            bestD2 = d2;
            best = candidate;
        }

        _farthestArea = ctx.Snapshot.AreaHash;
        _farthestOrigin = origin;
        _farthestConnectedPoint = best;
        return best;
    }

    private void EnsureWalkableQuanta(BehaviorContext ctx, BubblesBot.Core.Pathfinding.ICellReader pf)
    {
        if (_walkableQuanta is not null && _walkableQuantaArea == ctx.Snapshot.AreaHash) return;
        var nav = ctx.Snapshot.Nav;
        var set = new HashSet<long>();
        for (var y = 0; y < nav.Height; y += 5)
        for (var x = 0; x < nav.Width; x += 5)
            if (pf.Read(x, y) > 0)
                set.Add(Pack(x / VisitedQuantum, y / VisitedQuantum));
        _walkableQuanta = set;
        _walkableQuantaArea = ctx.Snapshot.AreaHash;
        _connectedWalkableQuanta = null;
        _connectedWalkableRoot = 0;
    }

    public void Reset()
    {
        _visitedByArea.Clear();
        _visited = new HashSet<long>();
        _trackingArea = 0;
        _frontierGoal = null;
        _nextBfsAt = TimeSpan.Zero;
        _lastExhaustLog = TimeSpan.Zero;
        _walkableQuanta = null;
        _walkableQuantaArea = 0;
        _connectedWalkableQuanta = null;
        _connectedWalkableRoot = 0;
        _reachableWhenDry = null;
        _lastPlayer = null;
        _headingX = _headingY = 0;
        _waveOrigin = default;
        _maximumWaveRadius = 0;
        _farthestArea = 0;
        _farthestOrigin = default;
        _farthestConnectedPoint = null;
        LastFrontierScore = 0;
        LastFrontierReason = "none";
        IsExhausted = false;
    }

    private bool IsWavePreferred(Vector2i candidate)
        => Distance(_waveOrigin, candidate) + WaveBacktrackToleranceGrid >= _maximumWaveRadius;

    private static float Distance(Vector2i a, Vector2i b)
    {
        var dx = (float)(a.X - b.X);
        var dy = (float)(a.Y - b.Y);
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static long Pack(int x, int y) => ((long)x << 32) | (uint)y;

    private static (int X, int Y) Unpack(long value)
        => ((int)(value >> 32), unchecked((int)(uint)value));

    private static long DistanceSquared(Vector2i a, Vector2i b)
    {
        long dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    internal static HashSet<long> ConnectedComponent(HashSet<long> walkable, int startX, int startY)
    {
        var start = Pack(startX, startY);
        if (!walkable.Contains(start)) return [];

        var connected = new HashSet<long> { start };
        var queue = new Queue<(int X, int Y)>();
        queue.Enqueue((startX, startY));
        while (queue.Count > 0)
        {
            var (x, y) = queue.Dequeue();
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                var nx = x + dx;
                var ny = y + dy;
                var key = Pack(nx, ny);
                if (!walkable.Contains(key) || !connected.Add(key)) continue;
                queue.Enqueue((nx, ny));
            }
        }
        return connected;
    }

    internal static long PackQuantum(int x, int y) => Pack(x, y);

    internal static (int X, int Y) SelectFarthestQuantum(
        IEnumerable<long> connected, Vector2i origin)
        => connected
            .Select(Unpack)
            .OrderByDescending(q => DistanceSquared(
                origin,
                new Vector2i { X = q.X * VisitedQuantum + VisitedQuantum / 2,
                               Y = q.Y * VisitedQuantum + VisitedQuantum / 2 }))
            .First();

    private static (int X, int Y)? FarthestNavigableQuantum(
        HashSet<long> walkable,
        int startX,
        int startY,
        BubblesBot.Core.Pathfinding.ICellReader pf,
        int width,
        int height,
        Vector2i origin)
    {
        var start = Pack(startX, startY);
        if (!walkable.Contains(start)) return null;

        var queue = new Queue<(int X, int Y, int Cost)>();
        var seen = new HashSet<long> { start };
        queue.Enqueue((startX, startY, 0));
        var best = (X: startX, Y: startY);
        var bestCost = 0;
        var bestEuclidean = DistanceSquared(origin, QuantumCenter(startX, startY));
        ReadOnlySpan<int> dxs = stackalloc[] { 1, -1, 0, 0 };
        ReadOnlySpan<int> dys = stackalloc[] { 0, 0, 1, -1 };

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var euclidean = DistanceSquared(origin, QuantumCenter(current.X, current.Y));
            if (current.Cost > bestCost || current.Cost == bestCost && euclidean > bestEuclidean)
            {
                best = (current.X, current.Y);
                bestCost = current.Cost;
                bestEuclidean = euclidean;
            }

            for (var i = 0; i < 4; i++)
            {
                var nx = current.X + dxs[i];
                var ny = current.Y + dys[i];
                var key = Pack(nx, ny);
                if (!walkable.Contains(key) || seen.Contains(key)) continue;
                if (!CanCrossQuantumBoundary(
                        current.X, current.Y, nx, ny, pf, width, height)) continue;
                seen.Add(key);
                queue.Enqueue((nx, ny, current.Cost + 1));
            }
        }
        return best;
    }

    private static bool CanCrossQuantumBoundary(
        int x, int y, int nx, int ny,
        BubblesBot.Core.Pathfinding.ICellReader pf, int width, int height)
    {
        if (nx != x)
        {
            var boundaryX = Math.Max(x, nx) * VisitedQuantum;
            if (boundaryX <= 0 || boundaryX >= width) return false;
            var fromY = Math.Max(0, y * VisitedQuantum);
            var toY = Math.Min(height, fromY + VisitedQuantum);
            for (var cy = fromY; cy < toY; cy++)
                if (pf.Read(boundaryX - 1, cy) > 0 && pf.Read(boundaryX, cy) > 0)
                    return true;
            return false;
        }

        var boundaryY = Math.Max(y, ny) * VisitedQuantum;
        if (boundaryY <= 0 || boundaryY >= height) return false;
        var fromX = Math.Max(0, x * VisitedQuantum);
        var toX = Math.Min(width, fromX + VisitedQuantum);
        for (var cx = fromX; cx < toX; cx++)
            if (pf.Read(cx, boundaryY - 1) > 0 && pf.Read(cx, boundaryY) > 0)
                return true;
        return false;
    }

    private static Vector2i QuantumCenter(int x, int y)
        => new()
        {
            X = x * VisitedQuantum + VisitedQuantum / 2,
            Y = y * VisitedQuantum + VisitedQuantum / 2,
        };
}
