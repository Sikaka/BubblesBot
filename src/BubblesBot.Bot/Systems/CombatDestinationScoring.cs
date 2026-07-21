using BubblesBot.Core.Game;

namespace BubblesBot.Bot.Systems;

/// <summary>
/// Pure scoring policy for aura/minion builds that kill by occupying useful space. Each
/// visible hostile is a possible destination; its score is the rarity-weighted hostile
/// density around that point, with a small travel penalty to avoid needless zig-zagging.
/// </summary>
public static class CombatDestinationScoring
{
    public readonly record struct Candidate(
        Vector2i Position,
        int RarityRank,
        double StrategyWeight = 0d);
    public readonly record struct Choice(
        int Index, double Score, double DensityWeight, int NearbyCount, float Distance);

    public static Choice? Choose(
        IReadOnlyList<Candidate> candidates,
        Vector2i player,
        float densityRadiusGrid)
    {
        if (candidates.Count == 0) return null;

        var densityRadius2 = densityRadiusGrid * densityRadiusGrid;
        Choice? best = null;
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var densityWeight = 0d;
            var nearbyCount = 0;
            for (var j = 0; j < candidates.Count; j++)
            {
                if (DistanceSquared(candidate.Position, candidates[j].Position) > densityRadius2)
                    continue;
                densityWeight += RarityWeight(candidates[j].RarityRank)
                               + candidates[j].StrategyWeight;
                nearbyCount++;
            }

            var distance = MathF.Sqrt(DistanceSquared(player, candidate.Position));
            var score = densityWeight - distance * 0.06d;
            var choice = new Choice(i, score, densityWeight, nearbyCount, distance);
            if (best is null
                || choice.Score > best.Value.Score
                || (Math.Abs(choice.Score - best.Value.Score) < 0.001
                    && choice.Distance < best.Value.Distance))
                best = choice;
        }
        return best;
    }

    public static double RarityWeight(int rarityRank) => rarityRank switch
    {
        >= 3 => 50d, // bosses/uniques remain the dominant destination
        2 => 12d,
        1 => 3d,
        _ => 1d,
    };

    private static float DistanceSquared(Vector2i a, Vector2i b)
    {
        var dx = (float)(a.X - b.X);
        var dy = (float)(a.Y - b.Y);
        return dx * dx + dy * dy;
    }
}
