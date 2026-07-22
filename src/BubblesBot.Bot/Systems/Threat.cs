using BubblesBot.Bot.Behaviors;
using BubblesBot.Core.Game;
using BubblesBot.Core.Pathfinding;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Systems;

/// <summary>
/// Target-selection helpers shared by the aim layer and the push-combat posture logic. "Biggest
/// threat" = highest rarity first (Unique &gt; Rare &gt; Magic &gt; White), closest as the tiebreak —
/// a ranged build wants its arrows on the dangerous thing, not whatever trash is nearest. Danger
/// proximity ("is something about to swarm us") is a separate, distance-only query.
/// </summary>
public static class Threat
{
    public readonly record struct PackSelection(
        EntityCache.Entry Target, double Score, double DensityWeight,
        int NearbyCount, float Distance);

    /// <summary>Rarity → rank. Unique 3, Rare 2, Magic 1, White/unknown 0.</summary>
    public static int RarityRank(EntityCache.Entry e) => e.Rarity switch
    {
        EntityListReader.EntityRarity.Unique => 3,
        EntityListReader.EntityRarity.Rare   => 2,
        EntityListReader.EntityRarity.Magic  => 1,
        _                                    => 0,
    };

    /// <summary>Rare or better — the targets worth stopping to "unload" on.</summary>
    public static bool IsToughTarget(EntityCache.Entry e) => RarityRank(e) >= 2;

    private static bool Valid(EntityCache.Entry e)
        => TargetEligibility.IsEligible(e);
        // Buff-state validity: dormant/essence-frozen mobs are un-fightable RIGHT NOW but
        // become valid the moment their state buff drops (approach wakes constructs,
        // clicking the monolith releases essence mobs) — live read, never a blacklist.

    private static float Dist2(Vector2i a, Vector2i b)
    {
        var dx = (float)(a.X - b.X); var dy = (float)(a.Y - b.Y); return dx * dx + dy * dy;
    }

    /// <summary>
    /// The biggest threat within <paramref name="maxRangeGrid"/>: max rarity, then closest. When
    /// <paramref name="requireLos"/> is set, skips targets with no walkable line of sight (so a
    /// ranged build doesn't fire arrows into a wall). <paramref name="skip"/> excludes ids the
    /// caller has decided are un-hittable right now (un-spawned/dormant mobs whose HP won't drop).
    /// </summary>
    public static EntityCache.Entry? Biggest(BehaviorContext ctx, float maxRangeGrid, bool requireLos, Func<uint, bool>? skip = null)
    {
        if (ctx.Entities is null || ctx.Live is null) return null;
        var p = ctx.Live.Value.GridPosition;
        var maxR2 = maxRangeGrid * maxRangeGrid;
        ICellReader? pf = requireLos && ctx.Snapshot.Nav is { IsAvailable: true } nav ? nav.PathReader : null;

        EntityCache.Entry? best = null;
        var bestRank = -1;
        var bestD2 = float.PositiveInfinity;
        foreach (var e in ctx.Entities.Entries.Values)
        {
            if (!Valid(e)) continue;
            if (skip is not null && skip(e.Id)) continue;
            var d2 = Dist2(e.GridPosition, p);
            if (d2 > maxR2) continue;
            if (pf is not null && !PathSmoother.HasLineOfSight(pf, p.X, p.Y, e.GridPosition.X, e.GridPosition.Y, minValue: 1))
                continue;
            var rank = RarityRank(e);
            if (rank > bestRank || (rank == bestRank && d2 < bestD2))
            {
                bestRank = rank; bestD2 = d2; best = e;
            }
        }
        return best;
    }

    /// <summary>Nearest valid hostile within <paramref name="radius"/> — the swarm/danger check.
    /// Optional <paramref name="skip"/> mirrors <see cref="Biggest"/>: ids the caller has
    /// decided are un-engageable right now (damage-gated, essence-frozen).</summary>
    public static EntityCache.Entry? Nearest(
        BehaviorContext ctx, float radius, Func<uint, bool>? skip = null,
        bool requireLos = false)
    {
        if (ctx.Entities is null || ctx.Live is null) return null;
        var p = ctx.Live.Value.GridPosition;
        var r2 = radius * radius;
        EntityCache.Entry? best = null;
        var bestD2 = float.PositiveInfinity;
        ICellReader? pf = requireLos && ctx.Snapshot.Nav is { IsAvailable: true } nav
            ? nav.PathReader
            : null;
        foreach (var e in ctx.Entities.Entries.Values)
        {
            if (!Valid(e)) continue;
            if (skip is not null && skip(e.Id)) continue;
            var d2 = Dist2(e.GridPosition, p);
            if (pf is not null && !PathSmoother.HasLineOfSight(
                    pf, p.X, p.Y, e.GridPosition.X, e.GridPosition.Y, minValue: 1))
                continue;
            if (d2 <= r2 && d2 < bestD2) { bestD2 = d2; best = e; }
        }
        return best;
    }

    /// <summary>
    /// Best destination for a proximity/aura build: the center candidate with the greatest
    /// rarity-weighted hostile density. Unlike <see cref="Biggest"/>, this chooses where the
    /// character should stand rather than which individual entity an attack should aim at.
    /// </summary>
    public static PackSelection? BestPack(
        BehaviorContext ctx,
        float maxRangeGrid,
        float densityRadiusGrid,
        Func<EntityCache.Entry, bool>? skip = null,
        Func<EntityCache.Entry, double>? strategyWeight = null)
    {
        if (ctx.Entities is null || ctx.Live is null) return null;
        var player = ctx.Live.Value.GridPosition;
        var maxRange2 = maxRangeGrid * maxRangeGrid;
        var entities = new List<EntityCache.Entry>();
        foreach (var entity in ctx.Entities.Entries.Values)
        {
            if (!Valid(entity) || skip?.Invoke(entity) == true) continue;
            if (Dist2(entity.GridPosition, player) <= maxRange2)
                entities.Add(entity);
        }
        if (entities.Count == 0) return null;

        var candidates = entities
            .Select(entity => new CombatDestinationScoring.Candidate(
                entity.GridPosition, RarityRank(entity),
                strategyWeight?.Invoke(entity) ?? 0d))
            .ToArray();
        var choice = CombatDestinationScoring.Choose(candidates, player, densityRadiusGrid);
        if (choice is null) return null;
        return new PackSelection(
            entities[choice.Value.Index], choice.Value.Score,
            choice.Value.DensityWeight, choice.Value.NearbyCount, choice.Value.Distance);
    }
}
