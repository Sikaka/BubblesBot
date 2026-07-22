using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;
using BubblesBot.Core.Pathfinding;

namespace BubblesBot.Tests;

public sealed class DoorRoutingPolicyTests
{
    [Fact]
    public void FindsDoorThatGatesUnvisitedWalkableTerrain()
    {
        var path = DungeonPath();
        var targeting = DungeonTargeting(doorPassable: true);
        var doors = new[] { Door(17, 15, 10) };

        var route = DoorRoutingPolicy.FindRequiredDoor(
            path, targeting, Point(5, 10), doors,
            (x, _) => x < 15,
            point => point.X < 30);

        Assert.NotNull(route);
        Assert.Equal((uint)17, route.Value.EntityId);
        Assert.True(route.Value.ApproachGrid.X < 15);
        Assert.True(route.Value.FrontierGrid.X >= 30);
    }

    [Fact]
    public void IgnoresDoorWhenTargetingTerrainCannotReachBeyondIt()
    {
        var route = DoorRoutingPolicy.FindRequiredDoor(
            DungeonPath(), DungeonTargeting(doorPassable: false), Point(5, 10),
            new[] { Door(17, 15, 10) },
            (x, _) => x < 15,
            _ => false);

        Assert.Null(route);
    }

    [Fact]
    public void IgnoresDoorWhenEverythingBehindItWasAlreadyVisited()
    {
        var route = DoorRoutingPolicy.FindRequiredDoor(
            DungeonPath(), DungeonTargeting(doorPassable: true), Point(5, 10),
            new[] { Door(17, 15, 10) },
            (x, _) => x < 15,
            _ => true);

        Assert.Null(route);
    }

    [Fact]
    public void IgnoresUnrelatedVisibleDoor()
    {
        var route = DoorRoutingPolicy.FindRequiredDoor(
            DungeonPath(), DungeonTargeting(doorPassable: true), Point(5, 10),
            new[] { Door(99, 5, 2) },
            (x, _) => x < 15,
            point => point.X < 30);

        Assert.Null(route);
    }

    [Fact]
    public void RoutesKnownObjectiveThroughWeightedDoorEdge()
    {
        var route = DoorRoutingPolicy.FindRequiredDoorToTarget(
            DungeonPath(), DungeonTargeting(doorPassable: true), Point(5, 10),
            new[] { Door(17, 15, 10) }, Point(30, 10));

        Assert.NotNull(route);
        Assert.Equal((uint)17, route.Value.EntityId);
        Assert.Equal(Point(30, 10), route.Value.FrontierGrid);
    }

    [Fact]
    public void PrefersReasonableWalkOnlyRouteOverOpeningDoor()
    {
        var path = new Grid(40, 20, (x, y) => x == 15 && y == 10 ? 0 : 5);
        var targeting = new Grid(40, 20, (_, _) => 5);

        var route = DoorRoutingPolicy.FindRequiredDoorToTarget(
            path, targeting, Point(5, 10),
            new[] { Door(17, 15, 10) }, Point(30, 10));

        Assert.Null(route);
    }

    private static Grid DungeonPath()
        => new(40, 20, (x, _) => x == 15 ? 0 : 5);

    private static Grid DungeonTargeting(bool doorPassable)
        => new(40, 20, (x, y) =>
            x != 15 ? 5 : doorPassable && y is >= 9 and <= 11 ? 5 : 0);

    private static ClosedDoorCandidate Door(uint id, int x, int y)
        => new(id, (nint)id, Point(x, y));

    private static Vector2i Point(int x, int y) => new() { X = x, Y = y };

    private sealed class Grid(int width, int height, Func<int, int, int> read) : ICellReader
    {
        public int Width { get; } = width;
        public int Height { get; } = height;

        public int Read(int x, int y)
            => x >= 0 && y >= 0 && x < Width && y < Height ? read(x, y) : 0;
    }
}
