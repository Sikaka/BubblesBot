using System.Security.Cryptography;
using System.Text;
using BubblesBot.Core.Game;
using BubblesBot.Core.Pathfinding;

namespace BubblesBot.Bot.Knowledge;

/// <summary>Rotation/reflection-invariant local path-terrain fingerprint.</summary>
public static class TerrainLandmarkSignature
{
    public static string Compute(ICellReader? path, Vector2i center)
    {
        if (path is null) return "unavailable";
        const int size = 25;
        const int step = 4;
        var mask = new bool[size, size];
        var half = size / 2;
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            mask[x, y] = path.Read(center.X + (x - half) * step, center.Y + (y - half) * step) > 0;

        string? canonical = null;
        for (var transform = 0; transform < 8; transform++)
        {
            var bits = new StringBuilder(size * size);
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var (tx, ty) = Transform(x, y, size, transform);
                bits.Append(mask[tx, ty] ? '1' : '0');
            }
            var candidate = bits.ToString();
            if (canonical is null || string.CompareOrdinal(candidate, canonical) < 0)
                canonical = candidate;
        }

        var bytes = SHA256.HashData(Encoding.ASCII.GetBytes(canonical!));
        return Convert.ToHexString(bytes.AsSpan(0, 12));
    }

    private static (int X, int Y) Transform(int x, int y, int size, int transform)
    {
        var n = size - 1;
        return transform switch
        {
            0 => (x, y),
            1 => (n - y, x),
            2 => (n - x, n - y),
            3 => (y, n - x),
            4 => (n - x, y),
            5 => (x, n - y),
            6 => (y, x),
            _ => (n - y, n - x),
        };
    }
}
