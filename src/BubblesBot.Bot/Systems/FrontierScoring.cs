using BubblesBot.Core.Game;

namespace BubblesBot.Bot.Systems;

/// <summary>Pure scoring policy for wave-style map exploration.</summary>
public static class FrontierScoring
{
    public readonly record struct Candidate(
        Vector2i Position,
        int PathCost,
        int NewCoverage,
        int NearbyHostiles,
        double DirectionAlignment,
        bool WavePreferred = true);

    public readonly record struct Scored(Candidate Candidate, double Score);

    public static double Score(Candidate candidate)
    {
        // Coverage is the primary objective. Dense packs justify a detour, while path cost and
        // reversing direction make repeatedly crossing already-swept ground unattractive.
        var direction = candidate.DirectionAlignment >= 0
            ? candidate.DirectionAlignment * 45.0
            : candidate.DirectionAlignment * 180.0;
        return candidate.NewCoverage * 24.0
             + candidate.NearbyHostiles * 55.0
             + direction
             - candidate.PathCost * 0.65;
    }

    public static Scored? Choose(IReadOnlyList<Candidate> candidates)
    {
        // Preserve the expanding exploration wave when at least one candidate remains near its
        // outer edge. If a winding layout or real dead end offers none, fall back to every
        // candidate so ordinary maps can legitimately reverse and reach another branch.
        var hasWaveCandidate = candidates.Any(candidate => candidate.WavePreferred);
        Scored? best = null;
        foreach (var candidate in candidates)
        {
            if (hasWaveCandidate && !candidate.WavePreferred) continue;
            var scored = new Scored(candidate, Score(candidate));
            if (best is null || scored.Score > best.Value.Score)
                best = scored;
        }
        return best;
    }
}
