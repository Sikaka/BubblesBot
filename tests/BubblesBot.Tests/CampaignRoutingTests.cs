using BubblesBot.Bot.Overlay.Navigation;
using BubblesBot.Core.Game;
using BubblesBot.Core.Pathfinding;

namespace BubblesBot.Tests;

/// <summary>
/// Phase B: the distance-field router, k-means target clusterer, and the off-thread route tracker.
/// All run headless over an in-memory grid.
/// </summary>
public sealed class CampaignRoutingTests
{
    /// <summary>Minimal in-memory <see cref="ICellReader"/>: value 5 = walkable, 0 = wall.</summary>
    private sealed class GridStub : ICellReader
    {
        private readonly byte[] _cells;
        public int Width { get; }
        public int Height { get; }
        public GridStub(int w, int h, bool[]? walls = null)
        {
            Width = w; Height = h;
            _cells = new byte[w * h];
            Array.Fill(_cells, (byte)5);
            if (walls is not null)
                for (var i = 0; i < walls.Length; i++) if (walls[i]) _cells[i] = 0;
        }
        public void Wall(int x, int y) => _cells[y * Width + x] = 0;
        public int Read(int x, int y)
            => (uint)x >= (uint)Width || (uint)y >= (uint)Height ? 0 : _cells[y * Width + x];
    }

    private static Vector2i V(int x, int y) => new() { X = x, Y = y };

    [Fact]
    public void DistanceFieldRoutesToTargetInOpenGrid()
    {
        var grid = new GridStub(10, 10);
        var field = DistanceField.Build(grid, V(0, 0));

        var path = new List<PathCell>();
        Assert.True(field.TryGetPath(V(5, 5), path));
        Assert.Equal(0, path[^1].X);
        Assert.Equal(0, path[^1].Y);
        // Diagonal movement allowed → 5 steps from (5,5) to (0,0).
        Assert.Equal(5, path.Count);
    }

    [Fact]
    public void DistanceFieldRoutesAroundAWall()
    {
        // A vertical wall at x=3 spanning y=0..8, leaving a gap at y=9 so the route must detour down.
        var grid = new GridStub(8, 10);
        for (var y = 0; y <= 8; y++) grid.Wall(3, y);
        var field = DistanceField.Build(grid, V(0, 0));

        var path = new List<PathCell>();
        Assert.True(field.TryGetPath(V(6, 0), path));
        Assert.Equal(V(0, 0).X, path[^1].X);
        Assert.Equal(V(0, 0).Y, path[^1].Y);
        // The only opening is at y=9, so the path must pass through the gap column.
        Assert.Contains(path, c => c.X == 3 && c.Y == 9);
    }

    [Fact]
    public void DistanceFieldReportsUnreachable()
    {
        // Fully wall off the target's cell neighborhood: island at (0,0) surrounded by walls.
        var grid = new GridStub(5, 5);
        grid.Wall(1, 0); grid.Wall(0, 1); grid.Wall(1, 1);
        var field = DistanceField.Build(grid, V(0, 0));

        var path = new List<PathCell>();
        Assert.False(field.TryGetPath(V(4, 4), path));
        Assert.Empty(path);
    }

    [Fact]
    public void ClustererReducesPointsToExpectedCount()
    {
        // Two tight blobs; expect 2 representative centers.
        var points = new List<Vector2i>();
        for (var i = 0; i < 20; i++) { points.Add(V(1 + i % 3, 1 + i % 3)); points.Add(V(40 + i % 3, 40 + i % 3)); }
        var centers = TargetClusterer.Cluster(points, expectedCount: 2);

        Assert.Equal(2, centers.Count);
        // One center near (2,2), one near (41,41).
        Assert.Contains(centers, c => c.X < 20 && c.Y < 20);
        Assert.Contains(centers, c => c.X > 20 && c.Y > 20);
    }

    [Fact]
    public void ClustererPassesThroughWhenFewerThanK()
    {
        var points = new List<Vector2i> { V(1, 1), V(2, 2) };
        var centers = TargetClusterer.Cluster(points, expectedCount: 5);
        Assert.Equal(2, centers.Count);
    }

    [Fact]
    public void ClustererSnapsCentroidToWalkable()
    {
        // Points straddle a wall so the centroid lands on it; snap should move off the wall.
        var grid = new GridStub(10, 10);
        grid.Wall(5, 5);
        var points = new List<Vector2i> { V(4, 4), V(6, 6), V(4, 6), V(6, 4) }; // centroid ≈ (5,5)
        var centers = TargetClusterer.Cluster(points, expectedCount: 1, grid);
        Assert.Single(centers);
        Assert.True(grid.Read(centers[0].X, centers[0].Y) > 0, "center must be walkable");
    }

    [Fact]
    public void RouteTrackerBuildsFieldOffThreadAndPathsFromPlayer()
    {
        var grid = new GridStub(20, 20);
        using var tracker = new RouteTracker();
        tracker.SetTarget(areaKey: 1, target: V(0, 0), grid);

        // Wait for the background field build to complete.
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!tracker.HasRoute && DateTime.UtcNow < deadline) Thread.Sleep(5);
        Assert.True(tracker.HasRoute, "field did not build in time");

        tracker.Update(V(10, 10));
        Assert.NotEmpty(tracker.CurrentPath);
        Assert.Equal(0, tracker.CurrentPath[^1].X);
        Assert.Equal(0, tracker.CurrentPath[^1].Y);
    }

    [Fact]
    public void RouteTrackerClearsPathWhenTargetCleared()
    {
        var grid = new GridStub(10, 10);
        using var tracker = new RouteTracker();
        tracker.SetTarget(1, V(0, 0), grid);
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!tracker.HasRoute && DateTime.UtcNow < deadline) Thread.Sleep(5);

        tracker.SetTarget(1, null, grid);
        tracker.Update(V(5, 5));
        Assert.Empty(tracker.CurrentPath);
        Assert.False(tracker.HasRoute);
    }
}
