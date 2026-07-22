using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;

namespace BubblesBot.Tests;

public sealed class ExplorationProgressTests
{
    [Fact]
    public void ConnectedComponent_ExcludesDisconnectedTerrain()
    {
        var walkable = new HashSet<long>
        {
            Q(0, 0), Q(1, 0), Q(2, 0),
            Q(20, 20), Q(21, 20),
        };

        var connected = ExplorationSystem.ConnectedComponent(walkable, 0, 0);

        Assert.Equal(3, connected.Count);
        Assert.DoesNotContain(Q(20, 20), connected);
    }

    [Fact]
    public void ConnectedComponent_AllowsDiagonalQuantumContinuity()
    {
        var walkable = new HashSet<long> { Q(0, 0), Q(1, 1), Q(2, 2) };

        var connected = ExplorationSystem.ConnectedComponent(walkable, 0, 0);

        Assert.Equal(3, connected.Count);
    }

    [Fact]
    public void FarthestQuantum_IsMeasuredFromMapSpawn_NotCurrentPlayer()
    {
        var connected = new HashSet<long>
        {
            Q(0, 0), Q(1, 0), Q(2, 0), Q(3, 0), Q(4, 0),
        };

        var result = ExplorationSystem.SelectFarthestQuantum(
            connected, new Vector2i { X = 5, Y = 5 });

        Assert.Equal((4, 0), result);
    }

    private static long Q(int x, int y) => ExplorationSystem.PackQuantum(x, y);
}
