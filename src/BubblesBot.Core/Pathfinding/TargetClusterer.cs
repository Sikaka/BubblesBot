using BubblesBot.Core.Game;
using Vec2 = System.Numerics.Vector2;

namespace BubblesBot.Core.Pathfinding;

/// <summary>
/// Reduces the many grid cells that match a target pattern (e.g. hundreds of "transition" tiles)
/// down to <see cref="TargetDescription.ExpectedCount"/> representative, walkable markers — so the
/// overlay shows "the 3 exits" rather than a cloud of cells. Deterministic farthest-point-seeded
/// k-means (ported from the Radar plugin's <c>KMeans</c> + cluster-then-snap), then each centroid is
/// snapped to the nearest walkable cell.
/// </summary>
public static class TargetClusterer
{
    /// <summary>Cluster <paramref name="points"/> into at most <paramref name="expectedCount"/>
    /// walkable representatives. <paramref name="grid"/> is optional; when supplied, centroids snap
    /// to the nearest walkable cell.</summary>
    public static IReadOnlyList<Vector2i> Cluster(IReadOnlyList<Vector2i> points, int expectedCount, ICellReader? grid = null)
    {
        if (points.Count == 0) return Array.Empty<Vector2i>();
        var k = Math.Max(1, expectedCount);

        if (points.Count <= k)
            return points.Select(p => Snap(grid, p)).ToList();

        var data = points.Select(p => new Vec2(p.X, p.Y)).ToArray();
        var assignment = InitClustering(data, k);
        var means = new Vec2[k];

        for (var iter = 0; iter < data.Length * 10; iter++)
        {
            if (!UpdateMeans(data, assignment, means)) break;
            if (!UpdateClustering(data, assignment, means)) break;
        }

        var result = new List<Vector2i>(k);
        for (var c = 0; c < k; c++)
        {
            var centroid = means[c];
            var cell = new Vector2i { X = (int)MathF.Round(centroid.X), Y = (int)MathF.Round(centroid.Y) };
            result.Add(Snap(grid, cell));
        }
        return result;
    }

    private static Vector2i Snap(ICellReader? grid, Vector2i p)
    {
        if (grid is null) return p;
        if ((uint)p.X < (uint)grid.Width && (uint)p.Y < (uint)grid.Height && grid.Read(p.X, p.Y) > 0) return p;
        // Objectives (esp. area transitions) often sit at the map edge with their tile center off the
        // walkable layer; search generously for the nearest reachable approach cell.
        for (var r = 1; r <= 48; r++)
            for (var dy = -r; dy <= r; dy++)
                for (var dx = -r; dx <= r; dx++)
                {
                    if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue;
                    int nx = p.X + dx, ny = p.Y + dy;
                    if ((uint)nx >= (uint)grid.Width || (uint)ny >= (uint)grid.Height) continue;
                    if (grid.Read(nx, ny) > 0) return new Vector2i { X = nx, Y = ny };
                }
        return p;
    }

    private static int[] InitClustering(Vec2[] data, int k)
    {
        var chosen = new List<int> { 0 };
        while (chosen.Count < k)
        {
            var best = -1;
            var bestMinDist = -1f;
            for (var i = 0; i < data.Length; i++)
            {
                if (chosen.Contains(i)) continue;
                var minDist = float.MaxValue;
                foreach (var c in chosen)
                    minDist = MathF.Min(minDist, Vec2.DistanceSquared(data[i], data[c]));
                if (minDist > bestMinDist) { bestMinDist = minDist; best = i; }
            }
            if (best < 0) break;
            chosen.Add(best);
        }

        var assignment = new int[data.Length];
        for (var i = 0; i < data.Length; i++)
        {
            var bestC = 0;
            var bestD = float.MaxValue;
            for (var c = 0; c < chosen.Count; c++)
            {
                var d = Vec2.DistanceSquared(data[i], data[chosen[c]]);
                if (d < bestD) { bestD = d; bestC = c; }
            }
            assignment[i] = bestC;
        }
        return assignment;
    }

    private static bool UpdateMeans(Vec2[] data, int[] assignment, Vec2[] means)
    {
        var counts = new int[means.Length];
        foreach (var a in assignment) counts[a]++;
        for (var c = 0; c < counts.Length; c++) if (counts[c] == 0) return false;

        Array.Fill(means, Vec2.Zero);
        for (var i = 0; i < data.Length; i++) means[assignment[i]] += data[i];
        for (var c = 0; c < means.Length; c++) means[c] /= counts[c];
        return true;
    }

    private static bool UpdateClustering(Vec2[] data, int[] assignment, Vec2[] means)
    {
        var changed = false;
        var counts = new int[means.Length];
        foreach (var a in assignment) counts[a]++;

        for (var i = 0; i < data.Length; i++)
        {
            var bestC = assignment[i];
            var bestD = float.MaxValue;
            for (var c = 0; c < means.Length; c++)
            {
                var d = Vec2.DistanceSquared(data[i], means[c]);
                if (d < bestD) { bestD = d; bestC = c; }
            }
            if (bestC != assignment[i] && counts[assignment[i]] > 1)
            {
                counts[assignment[i]]--;
                counts[bestC]++;
                assignment[i] = bestC;
                changed = true;
            }
        }
        return changed;
    }
}
