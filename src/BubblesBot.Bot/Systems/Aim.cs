using BubblesBot.Bot.Behaviors;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Systems;

/// <summary>
/// Where a skill should be aimed. Resolved per tick into an absolute screen point. Skills that
/// need no aim (self-buffs, warcries) use <see cref="AtSelf"/> — the cursor still parks on the
/// player so the skill targets correctly.
///
/// <para>Behaviors construct one of these by calling the static factories. Each carries the
/// data it needs to resolve to a fresh screen point each tick — <see cref="AtEntity"/> tracks
/// the entity by ID so a moving target stays locked.</para>
/// </summary>
public abstract record Aim
{
    /// <summary>Resolve to an absolute screen point. Returns null when projection failed.</summary>
    public abstract (int X, int Y)? Resolve(BehaviorContext ctx);

    /// <summary>Aim at the player. Self-buffs, ground-target-at-feet skills.</summary>
    public static Aim AtSelf() => new SelfAim();

    /// <summary>Aim at a fixed grid cell. Pre-computed targets, ground-target ability.</summary>
    public static Aim AtGrid(Vector2i grid) => new GridAim(grid);

    /// <summary>Aim at the position of an entity (tracked by ID, re-read each tick).</summary>
    public static Aim AtEntity(uint entityId) => new EntityAim(entityId);

    /// <summary>Aim at the highest-density cluster of hostile mobs in range. AOE skills.</summary>
    public static Aim AtAoeCluster(float maxRangeGrid = 60f, float clusterRadius = 18f)
        => new AoeClusterAim(maxRangeGrid, clusterRadius);

    /// <summary>Aim at the closest hostile mob.</summary>
    public static Aim AtClosestEnemy(float maxRangeGrid = 60f) => new ClosestEnemyAim(maxRangeGrid);

    /// <summary>
    /// Aim at the biggest threat (rarity-weighted, closest as tiebreak). When
    /// <paramref name="requireLos"/> is set, only considers targets with walkable line of sight
    /// — so a ranged build fires at things it can actually hit, not mobs behind a wall.
    /// </summary>
    public static Aim AtBiggestThreat(float maxRangeGrid = 60f, bool requireLos = true)
        => new BiggestThreatAim(maxRangeGrid, requireLos);

    /// <summary>
    /// Try a sequence of aim strategies, fall back to the next on null. Use to make a single
    /// behavior work across contexts: AOE cluster when there's a pack, single enemy if there's
    /// only one, self when there's nobody (e.g. testing in hideout).
    /// </summary>
    public static Aim Composite(params Aim[] aims) => new CompositeAim(aims);

    /// <summary>Cluster → closest enemy → self. The "always resolves to something" default.</summary>
    public static Aim BestEffort(float maxRangeGrid = 60f)
        => Composite(AtAoeCluster(maxRangeGrid), AtClosestEnemy(maxRangeGrid), AtSelf());

    private sealed record CompositeAim(Aim[] Aims) : Aim
    {
        public override (int X, int Y)? Resolve(BehaviorContext ctx)
        {
            foreach (var a in Aims)
            {
                var r = a.Resolve(ctx);
                if (r is not null) return r;
            }
            return null;
        }
    }

    private sealed record SelfAim : Aim
    {
        public override (int X, int Y)? Resolve(BehaviorContext ctx)
        {
            var live = ctx.Live;
            if (live is null) return null;
            var s = ctx.Snapshot.Camera.WorldToScreen(live.Value.WorldPosition);
            if (s is null) return null;
            return ToWindow(ctx, s.Value);
        }
    }

    private sealed record GridAim(Vector2i Cell) : Aim
    {
        public override (int X, int Y)? Resolve(BehaviorContext ctx)
        {
            var live = ctx.Live;
            if (live is null) return null;
            var s = ctx.Snapshot.Camera.GridToScreenAtPlayerZ(Cell, live.Value.WorldPosition.Z);
            if (s is null) return null;
            return ToWindow(ctx, s.Value);
        }
    }

    private sealed record EntityAim(uint EntityId) : Aim
    {
        public override (int X, int Y)? Resolve(BehaviorContext ctx)
        {
            if (ctx.Entities is null) return null;
            if (!ctx.Entities.Entries.TryGetValue(EntityId, out var e)) return null;
            var live = ctx.Live;
            if (live is null) return null;
            var s = ctx.Snapshot.Camera.GridToScreenAtPlayerZ(e.GridPosition, live.Value.WorldPosition.Z);
            if (s is null) return null;
            return ToWindow(ctx, s.Value);
        }
    }

    private sealed record ClosestEnemyAim(float MaxRangeGrid) : Aim
    {
        public override (int X, int Y)? Resolve(BehaviorContext ctx)
        {
            if (ctx.Entities is null || ctx.Live is null) return null;
            var player = ctx.Live.Value.GridPosition;
            EntityCache.Entry? best = null;
            float bestD2 = MaxRangeGrid * MaxRangeGrid;
            foreach (var e in ctx.Entities.Entries.Values)
            {
                if (!TargetEligibility.IsEligible(e)) continue;
                var dx = (float)(e.GridPosition.X - player.X);
                var dy = (float)(e.GridPosition.Y - player.Y);
                var d2 = dx * dx + dy * dy;
                if (d2 < bestD2) { bestD2 = d2; best = e; }
            }
            if (best is null) return null;
            var s = ctx.Snapshot.Camera.GridToScreenAtPlayerZ(best.GridPosition, ctx.Live.Value.WorldPosition.Z);
            if (s is null) return null;
            return ToWindow(ctx, s.Value);
        }
    }

    private sealed record BiggestThreatAim(float MaxRangeGrid, bool RequireLos) : Aim
    {
        public override (int X, int Y)? Resolve(BehaviorContext ctx)
        {
            if (ctx.Live is null) return null;
            var target = Threat.Biggest(ctx, MaxRangeGrid, RequireLos);
            if (target is null) return null;
            var s = ctx.Snapshot.Camera.GridToScreenAtPlayerZ(target.GridPosition, ctx.Live.Value.WorldPosition.Z);
            if (s is null) return null;
            return ToWindow(ctx, s.Value);
        }
    }

    private sealed record AoeClusterAim(float MaxRangeGrid, float ClusterRadius) : Aim
    {
        public override (int X, int Y)? Resolve(BehaviorContext ctx)
        {
            // Pick the in-range mob with the most other in-range mobs within ClusterRadius —
            // a cheap O(N²) pass that's fine at PoE entity densities. Aiming at the cluster's
            // centroid would be marginally better but the densest mob is close enough.
            if (ctx.Entities is null || ctx.Live is null) return null;
            var player = ctx.Live.Value.GridPosition;
            var maxR2 = MaxRangeGrid * MaxRangeGrid;
            var clusR2 = ClusterRadius * ClusterRadius;

            var alive = new List<EntityCache.Entry>();
            foreach (var e in ctx.Entities.Entries.Values)
            {
                if (!TargetEligibility.IsEligible(e)) continue;
                var dxp = (float)(e.GridPosition.X - player.X);
                var dyp = (float)(e.GridPosition.Y - player.Y);
                if (dxp * dxp + dyp * dyp > maxR2) continue;
                alive.Add(e);
            }
            if (alive.Count == 0) return null;

            EntityCache.Entry best = alive[0];
            int bestCount = 0;
            foreach (var a in alive)
            {
                int n = 0;
                foreach (var b in alive)
                {
                    var dx = (float)(b.GridPosition.X - a.GridPosition.X);
                    var dy = (float)(b.GridPosition.Y - a.GridPosition.Y);
                    if (dx * dx + dy * dy <= clusR2) n++;
                }
                if (n > bestCount) { bestCount = n; best = a; }
            }

            var s = ctx.Snapshot.Camera.GridToScreenAtPlayerZ(best.GridPosition, ctx.Live.Value.WorldPosition.Z);
            if (s is null) return null;
            return ToWindow(ctx, s.Value);
        }
    }

    private static (int X, int Y) ToWindow(BehaviorContext ctx, (float X, float Y) windowRel)
    {
        var w = ctx.Snapshot.Window;
        var sx = (int)Math.Clamp(windowRel.X, 0, Math.Max(0, w.Width  - 1));
        var sy = (int)Math.Clamp(windowRel.Y, 0, Math.Max(0, w.Height - 1));
        return w.ToScreen(sx, sy);
    }
}
