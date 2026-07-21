using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;

namespace BubblesBot.Tests;

public sealed class CombatDestinationScoringTests
{
    [Fact]
    public void DensePackBeatsCloserIsolatedNormal()
    {
        var candidates = new[]
        {
            Candidate(8, 0, 0),
            Candidate(42, 0, 0),
            Candidate(44, 2, 0),
            Candidate(46, -2, 1),
            Candidate(48, 1, 0),
            Candidate(45, 5, 0),
        };

        var chosen = CombatDestinationScoring.Choose(candidates, Point(0, 0), 10);

        Assert.NotNull(chosen);
        Assert.NotEqual(0, chosen.Value.Index);
        Assert.Equal(5, chosen.Value.NearbyCount);
    }

    [Fact]
    public void UniqueBossBeatsLargeNormalPack()
    {
        var candidates = Enumerable.Range(0, 15)
            .Select(i => Candidate(30 + i % 5, i / 5, 0))
            .Append(Candidate(60, 0, 3))
            .ToArray();

        var chosen = CombatDestinationScoring.Choose(candidates, Point(0, 0), 8);

        Assert.NotNull(chosen);
        Assert.Equal(15, chosen.Value.Index);
    }

    [Fact]
    public void EqualDensityPrefersNearerDestination()
    {
        var candidates = new[]
        {
            Candidate(20, 0, 0),
            Candidate(60, 0, 0),
        };

        var chosen = CombatDestinationScoring.Choose(candidates, Point(0, 0), 5);

        Assert.Equal(0, chosen!.Value.Index);
    }

    [Fact]
    public void StrategyWeightedPackBeatsGenericWhitePack()
    {
        var candidates = new[]
        {
            new CombatDestinationScoring.Candidate(Point(20, 0), 0),
            new CombatDestinationScoring.Candidate(Point(22, 0), 0),
            new CombatDestinationScoring.Candidate(Point(40, 0), 0, 12),
            new CombatDestinationScoring.Candidate(Point(42, 0), 0, 12),
        };

        var chosen = CombatDestinationScoring.Choose(candidates, Point(0, 0), 5);

        Assert.NotNull(chosen);
        Assert.True(chosen.Value.Index >= 2);
    }

    private static CombatDestinationScoring.Candidate Candidate(int x, int y, int rarity)
        => new(Point(x, y), rarity);

    private static Vector2i Point(int x, int y) => new() { X = x, Y = y };
}
