using BubblesBot.Core.Game;

namespace BubblesBot.Bot.Systems;

/// <summary>Selects a valid retreat point with maximum clearance from nearby threats.</summary>
public static class RetreatDestinationScoring
{
    private const int DirectionCount = 16;

    public static Vector2i? Choose(
        Vector2i player,
        IReadOnlyList<Vector2i> threats,
        float distance,
        Func<Vector2i, bool> isValid)
    {
        if (threats.Count == 0 || distance <= 0)
            return null;

        Vector2i? best = null;
        long bestNearestThreatD2 = long.MinValue;
        long bestTotalThreatD2 = long.MinValue;
        for (var i = 0; i < DirectionCount; i++)
        {
            var angle = 2.0 * Math.PI * i / DirectionCount;
            var candidate = new Vector2i
            {
                X = player.X + (int)Math.Round(Math.Cos(angle) * distance),
                Y = player.Y + (int)Math.Round(Math.Sin(angle) * distance),
            };
            if (!isValid(candidate)) continue;

            long nearestD2 = long.MaxValue;
            long totalD2 = 0;
            foreach (var threat in threats)
            {
                long dx = candidate.X - threat.X;
                long dy = candidate.Y - threat.Y;
                var d2 = dx * dx + dy * dy;
                nearestD2 = Math.Min(nearestD2, d2);
                totalD2 += d2;
            }

            if (nearestD2 > bestNearestThreatD2
                || (nearestD2 == bestNearestThreatD2 && totalD2 > bestTotalThreatD2))
            {
                best = candidate;
                bestNearestThreatD2 = nearestD2;
                bestTotalThreatD2 = totalD2;
            }
        }
        return best;
    }
}
