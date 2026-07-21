using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Modes;

public readonly record struct BlightDefendGoal(Vector2i Position, string Reason);

/// <summary>Pure defend-position policy so live behavior and replay/tests use identical choices.</summary>
public static class BlightPositioning
{
    /// <summary>
    /// Returns a point on the player-to-target line that remains <paramref name="standoff"/>
    /// grid units from the target. If the player is already inside that bubble, hold the
    /// current position rather than pathing through the monster.
    /// </summary>
    public static Vector2i ApproachAtStandoff(Vector2i player, Vector2i target, float standoff)
    {
        var radius = Math.Max(1f, standoff);
        var dx = (float)(target.X - player.X);
        var dy = (float)(target.Y - player.Y);
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length <= radius || length < 0.001f) return player;

        var travel = length - radius;
        return new Vector2i
        {
            X = player.X + (int)MathF.Round(dx / length * travel),
            Y = player.Y + (int)MathF.Round(dy / length * travel),
        };
    }

    public static BlightDefendGoal? Choose(
        Vector2i pump,
        Vector2i player,
        float defendRadius,
        IEnumerable<EntityCache.Entry> entities)
    {
        var radius = Math.Max(12f, defendRadius);

        EntityCache.Entry? nearestHazard = null;
        var nearestHazardD2 = 24f * 24f;
        foreach (var entity in entities)
        {
            if (!entity.IsHazard || entity.IsStale) continue;
            var d2 = DistanceSquared(player, entity.GridPosition);
            if (d2 < nearestHazardD2)
            {
                nearestHazardD2 = d2;
                nearestHazard = entity;
            }
        }

        if (nearestHazard is not null)
        {
            var goal = PointFromPump(pump, nearestHazard.GridPosition, -radius * 0.55f, player);
            if (DistanceSquared(player, goal) > 6f * 6f)
                return new BlightDefendGoal(goal, $"avoid-hazard:{nearestHazard.Id}");
        }

        if (DistanceSquared(player, pump) > radius * radius)
            return new BlightDefendGoal(pump, "outside-defend-radius");

        // Hold on the side of the pump carrying the greatest immediate lane pressure. Blight
        // monsters path toward the pump, so minimum pump distance is the correct defensive
        // priority; player distance and rarity must never pull us down a remote lane.
        EntityCache.Entry? dangerous = null;
        var bestPumpD2 = float.PositiveInfinity;
        foreach (var entity in entities)
        {
            if (!TargetEligibility.IsEligible(entity)) continue;
            var d2 = DistanceSquared(pump, entity.GridPosition);
            if (d2 < bestPumpD2)
            {
                dangerous = entity;
                bestPumpD2 = d2;
            }
        }

        if (dangerous is not null)
        {
            // Face the threatened lane while staying well inside the pump leash. If the mob
            // is already on top of the pump, do not path through it to reach a nominal point.
            var threatDistance = MathF.Sqrt(bestPumpD2);
            var interceptDistance = MathF.Min(radius * 0.45f, threatDistance * 0.55f);
            var goal = PointFromPump(pump, dangerous.GridPosition, interceptDistance, player);
            if (DistanceSquared(player, goal) > 8f * 8f)
                return new BlightDefendGoal(goal,
                    $"guard-closest-lane:{dangerous.Id}:pumpDist={threatDistance:F0}");
        }

        return null;
    }

    private static Vector2i PointFromPump(Vector2i pump, Vector2i source, float distance, Vector2i fallback)
    {
        var dx = (float)(source.X - pump.X);
        var dy = (float)(source.Y - pump.Y);
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 0.001f)
        {
            dx = fallback.X - pump.X;
            dy = fallback.Y - pump.Y;
            length = MathF.Sqrt(dx * dx + dy * dy);
        }
        if (length < 0.001f) { dx = 1f; dy = 0f; length = 1f; }
        return new Vector2i
        {
            X = pump.X + (int)MathF.Round(dx / length * distance),
            Y = pump.Y + (int)MathF.Round(dy / length * distance),
        };
    }

    private static float DistanceSquared(Vector2i a, Vector2i b)
    {
        var dx = (float)(a.X - b.X);
        var dy = (float)(a.Y - b.Y);
        return dx * dx + dy * dy;
    }
}
