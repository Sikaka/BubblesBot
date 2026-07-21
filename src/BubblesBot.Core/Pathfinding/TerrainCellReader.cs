using BubblesBot.Core.Game;

namespace BubblesBot.Core.Pathfinding;

/// <summary>
/// <see cref="ICellReader"/> backed by a <see cref="TerrainGridReader.TerrainGridSnapshot"/>.
/// Reads packed 4-bit cells from process memory on demand and caches them for the
/// reader's lifetime. Use one reader per pathfind (or per tick) — don't share across
/// areas because the underlying memory may be reused.
///
/// Layer selection: pass either the snapshot's PathfindingData or TerrainTargetingData
/// pointer so the same reader implementation serves both static layers.
/// </summary>
public sealed class TerrainCellReader : ICellReader
{
    private readonly MemoryReader _reader;
    private readonly TerrainGridReader.TerrainGridSnapshot _snapshot;
    private readonly bool _useTargetingLayer;

    // Cache: byte per cell, 0xFF = "not yet read", else 0..15 (the actual nibble).
    // Sized to total cells (Rows × Columns); 713×714 ≈ 509 KB. Allocated once per reader.
    private readonly byte[] _cache;
    private const byte Sentinel = 0xFF;

    public int Width  { get; }
    public int Height { get; }

    public TerrainCellReader(MemoryReader reader, TerrainGridReader.TerrainGridSnapshot snapshot, bool useTargetingLayer = false)
    {
        _reader = reader;
        _snapshot = snapshot;
        _useTargetingLayer = useTargetingLayer;
        Width  = snapshot.Columns;
        Height = snapshot.Rows;
        _cache = new byte[Width * Height];
        Array.Fill(_cache, Sentinel);
    }

    public int Read(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return 0;
        var idx = y * Width + x;
        var cached = _cache[idx];
        if (cached != Sentinel) return cached;

        // Cache miss → memory read. Inlined from TerrainGridReader to avoid
        // re-validating bounds on every call.
        var grid = new Vector2i { X = x, Y = y };
        bool ok = _useTargetingLayer
            ? TerrainGridReader.TryGetTerrainTargetingValue(_reader, _snapshot, grid, out var v)
            : TerrainGridReader.TryGetPathfindingValue(_reader, _snapshot, grid, out v);

        var value = ok ? (byte)v : (byte)0;
        _cache[idx] = value;
        return value;
    }
}
