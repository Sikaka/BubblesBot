using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;

namespace BubblesBot.Tests;

public sealed class RetreatDestinationScoringTests
{
    [Fact]
    public void Choose_MovesAwayFromThreatCluster()
    {
        var threats = new[] { Point(10, 0), Point(12, 4), Point(12, -4) };

        var result = RetreatDestinationScoring.Choose(Point(0, 0), threats, 40, _ => true);

        Assert.NotNull(result);
        Assert.True(result.Value.X < -30);
    }

    [Fact]
    public void Choose_UsesNextSafestValidDirection()
    {
        var result = RetreatDestinationScoring.Choose(
            Point(0, 0), [Point(10, 0)], 40,
            candidate => !(candidate.X < -30 && Math.Abs(candidate.Y) < 10));

        Assert.NotNull(result);
        Assert.False(result.Value.X < -30 && Math.Abs(result.Value.Y) < 10);
        Assert.True(result.Value.X < 0);
    }

    [Fact]
    public void Choose_NoThreats_ReturnsNull()
        => Assert.Null(RetreatDestinationScoring.Choose(Point(0, 0), [], 40, _ => true));

    private static Vector2i Point(int x, int y) => new() { X = x, Y = y };
}
