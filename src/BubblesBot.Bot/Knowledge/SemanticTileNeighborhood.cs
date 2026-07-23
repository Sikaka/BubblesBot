using System.Security.Cryptography;
using System.Text;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Knowledge;

/// <summary>
/// A Radar-style semantic template around a transition. It records both TGT detail names and full
/// .tdt paths, including their relative tile offsets, and canonicalizes the group across all eight
/// rotations/reflections. Unlike a walkability fingerprint, this describes the authored tile group
/// that procedural generation placed around the opening.
/// </summary>
internal static class SemanticTileNeighborhood
{
    internal const int RadiusTiles = 3;
    // PoE terrain tiles are 23x23 navigation-grid cells. Keep Bot code independent of the
    // low-level offset table; TileMapView publishes positions in these same grid units.
    private const int TileGridCells = 23;

    internal sealed record CaptureResult(
        string Signature,
        IReadOnlyList<string> Keys,
        IReadOnlyList<string> Components)
    {
        public static readonly CaptureResult Empty = new("", [], []);
    }

    public static CaptureResult Capture(TileMapView tiles, Vector2i center)
    {
        if (tiles.Keys.Count == 0) return CaptureResult.Empty;
        var anchor = TileOrigin(center);
        return Compute(
            tiles.FindWithin(anchor, RadiusTiles * TileGridCells),
            anchor);
    }

    internal static CaptureResult Compute(
        IEnumerable<TileKeyPosition> occurrences,
        Vector2i anchor)
    {
        var source = occurrences
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .Select(item => new Component(
                Normalize(item.Key),
                (item.Position.X - anchor.X) / TileGridCells,
                (item.Position.Y - anchor.Y) / TileGridCells))
            .ToArray();
        if (source.Length == 0) return CaptureResult.Empty;

        string[]? canonical = null;
        for (var transform = 0; transform < 8; transform++)
        {
            var candidate = source
                .Select(item =>
                {
                    var (x, y) = Transform(item.X, item.Y, transform);
                    return $"{x},{y}|{item.Key}";
                })
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (canonical is null || Compare(candidate, canonical) < 0)
                canonical = candidate;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', canonical!)));
        var signature = Convert.ToHexString(bytes.AsSpan(0, 12));
        var keys = source.Select(item => item.Key)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new CaptureResult(signature, keys, canonical!);
    }

    private static Vector2i TileOrigin(Vector2i position)
        => new()
        {
            X = FloorToTile(position.X),
            Y = FloorToTile(position.Y),
        };

    private static int FloorToTile(int value)
    {
        var size = TileGridCells;
        var quotient = Math.DivRem(value, size, out var remainder);
        if (remainder < 0) quotient--;
        return quotient * size;
    }

    private static (int X, int Y) Transform(int x, int y, int transform)
        => transform switch
        {
            0 => (x, y),
            1 => (-y, x),
            2 => (-x, -y),
            3 => (y, -x),
            4 => (-x, y),
            5 => (-y, -x),
            6 => (x, -y),
            7 => (y, x),
            _ => throw new ArgumentOutOfRangeException(nameof(transform)),
        };

    private static int Compare(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        for (var i = 0; i < Math.Min(left.Count, right.Count); i++)
        {
            var comparison = string.CompareOrdinal(left[i], right[i]);
            if (comparison != 0) return comparison;
        }
        return left.Count.CompareTo(right.Count);
    }

    private static string Normalize(string key)
        => key.Trim().Replace('\\', '/').ToLowerInvariant();

    private readonly record struct Component(string Key, int X, int Y);
}
