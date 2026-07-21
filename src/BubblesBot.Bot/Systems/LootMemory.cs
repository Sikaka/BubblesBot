using BubblesBot.Bot.Behaviors;
using BubblesBot.Bot.Behaviors.Loot;
using BubblesBot.Core.Game;

namespace BubblesBot.Bot.Systems;

/// <summary>
/// Per-area registry of accepted ground loot the bot has seen. Ground labels only exist in
/// memory while rendered — an item dropped behind the sweep (minion kills happen off-path)
/// vanishes from the live list the moment it scrolls off screen. This memory is what makes
/// "backtrack for valuable loot" possible: the zone loop drains it before taking the next
/// area transition.
///
/// <para>Valuation runs through the shared <c>ValueFilter</c>. Generic modes may apply the
/// configured backtrack value floor; the stacked-deck preset remembers every item accepted
/// by the player's loot filter because continuing the map must never strand visible drops.
/// Entries are forgotten when the player stands next to the spot and no label remains.</para>
/// </summary>
public sealed class LootMemory
{
    public readonly record struct RememberedLoot(Vector2i Pos, string Reason, float ChaosValue);

    /// <summary>Dedup granularity — labels wobble a cell or two between reads.</summary>
    private const int PosQuantum = 4;
    private const int ForgetProximityGrid = 20;
    /// <summary>Abandonment is a RADIUS, not a quantized key: a wobbling label re-minted a
    /// fresh key after every Forget, and the backtrack drain looped on one spot for a full
    /// 8-minute zone failsafe window (live 2026-07-15).</summary>
    private const int AbandonRadiusGrid = 10;

    private readonly Dictionary<long, RememberedLoot> _items = new();
    private readonly List<Vector2i> _abandonedSpots = new();
    private uint _area;

    public int Count => _items.Count;

    /// <summary>Scan the visible labels and update the registry. Call every tick.</summary>
    public void Track(BehaviorContext ctx)
    {
        if (ctx.Snapshot.AreaHash != _area)
        {
            _items.Clear();
            _abandonedSpots.Clear();
            _area = ctx.Snapshot.AreaHash;
        }

        var filter = LootClosestVisible.SharedValueFilter;
        // The strategy may override the profile's backtrack threshold; 0 = remember every
        // accepted label (stacked decks are individually below any sane profile threshold).
        var minChaos = Math.Max(0f,
            ctx.Strategy?.Loot.BacktrackMinChaosOverride ?? ctx.Settings.LootBacktrackMinChaos);
        if (filter is null)
        {
            if (_items.Count > 0) _items.Clear();
            return;
        }

        foreach (var l in ctx.Snapshot.GroundLabels)
        {
            // Pickup behavior can only act on a rendered label. Remembering hidden-filter
            // entities creates an impossible queue target: we can walk to it forever but can
            // never click it. Keep the memory predicate aligned with LootClosestVisible.
            if (!l.IsItem || !l.IsLabelVisible || l.EntityGridPosition is not { } g) continue;
            var key = Key(g);
            if (IsAbandoned(g)) continue;
            var eval = filter.Evaluate(l, ctx.Settings.Loot);
            var decisionValue = Math.Max(eval.ChaosValue, eval.MaxChaosValue);
            if (!eval.ShouldTake || decisionValue < minChaos) continue;
            _items[key] = new RememberedLoot(g, eval.Reason, eval.ChaosValue);
        }

        // Forget spots we're standing next to that no longer show a label — either we just
        // looted it (the pickup itself is done by the ordinary loot branch once the label
        // is back on screen) or it despawned.
        if (ctx.Live is { } live)
        {
            List<long>? gone = null;
            foreach (var (key, item) in _items)
            {
                long dx = item.Pos.X - live.GridPosition.X, dy = item.Pos.Y - live.GridPosition.Y;
                if (dx * dx + dy * dy > (long)ForgetProximityGrid * ForgetProximityGrid) continue;
                var still = false;
                foreach (var l in ctx.Snapshot.GroundLabels)
                {
                    if (!l.IsItem || !l.IsLabelVisible || l.EntityGridPosition is not { } g) continue;
                    if (Key(g) == key) { still = true; break; }
                }
                if (!still) (gone ??= new List<long>()).Add(key);
            }
            if (gone is not null)
                foreach (var k in gone) _items.Remove(k);
        }
    }

    /// <summary>Nearest remembered valuable item, including its valuation evidence.</summary>
    public RememberedLoot? NearestRemembered(BehaviorContext ctx)
    {
        if (_items.Count == 0 || ctx.Live is null) return null;
        var p = ctx.Live.Value.GridPosition;
        RememberedLoot? best = null;
        var bestD2 = long.MaxValue;
        foreach (var item in _items.Values)
        {
            long dx = item.Pos.X - p.X, dy = item.Pos.Y - p.Y;
            var d2 = dx * dx + dy * dy;
            if (d2 < bestD2) { bestD2 = d2; best = item; }
        }
        return best;
    }

    /// <summary>Nearest remembered valuable item position, or null when drained.</summary>
    public Vector2i? Nearest(BehaviorContext ctx) => NearestRemembered(ctx)?.Pos;

    /// <summary>Forget and suppress one evidenced-unreachable completion-pass target and
    /// everything remembered within <see cref="AbandonRadiusGrid"/> of it.</summary>
    public bool Forget(RememberedLoot item)
    {
        _abandonedSpots.Add(item.Pos);
        var removed = _items.Remove(Key(item.Pos));
        List<long>? near = null;
        foreach (var (key, other) in _items)
            if (WithinAbandonRadius(item.Pos, other.Pos)) (near ??= new List<long>()).Add(key);
        if (near is not null)
            foreach (var k in near) { _items.Remove(k); removed = true; }
        return removed;
    }

    /// <summary>Write off a position (and everything remembered within <see cref="AbandonRadiusGrid"/>
    /// of it) that another system has judged permanently unlootable — e.g. the loot clicker's
    /// persistent-cover give-up. Idempotent; suppresses re-remembering the spot via <see cref="Track"/>.</summary>
    public void AbandonSpot(Vector2i pos)
    {
        if (IsAbandoned(pos)) return;
        _abandonedSpots.Add(pos);
        List<long>? near = null;
        foreach (var (key, other) in _items)
            if (WithinAbandonRadius(pos, other.Pos)) (near ??= new List<long>()).Add(key);
        if (near is not null)
            foreach (var k in near) _items.Remove(k);
    }

    /// <summary>Give up on every remembered drop in this area (drain budget exhausted).
    /// Returns how many entries were dropped.</summary>
    public int AbandonAll()
    {
        var count = _items.Count;
        foreach (var item in _items.Values) _abandonedSpots.Add(item.Pos);
        _items.Clear();
        return count;
    }

    private bool IsAbandoned(Vector2i g)
    {
        foreach (var spot in _abandonedSpots)
            if (WithinAbandonRadius(spot, g)) return true;
        return false;
    }

    private static bool WithinAbandonRadius(Vector2i a, Vector2i b)
    {
        long dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy <= (long)AbandonRadiusGrid * AbandonRadiusGrid;
    }

    public void Reset()
    {
        _items.Clear();
        _abandonedSpots.Clear();
        _area = 0;
    }

    private static long Key(Vector2i g) => ((long)(g.X / PosQuantum) << 32) | (uint)(g.Y / PosQuantum);
}
