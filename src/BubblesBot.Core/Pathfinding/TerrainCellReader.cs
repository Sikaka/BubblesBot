using BubblesBot.Core.Game;

namespace BubblesBot.Core.Pathfinding;

/// <summary>
/// <see cref="ICellReader"/> backed by a <see cref="TerrainGridReader.TerrainGridSnapshot"/>.
/// Takes one bulk snapshot of the packed 4-bit layer so pathfinding performs no
/// process-memory reads per cell. If the bulk read races an area transition, it falls
/// back to lazy per-cell reads for the reader's lifetime. Don't share readers across
/// areas because the underlying memory may be reused.
///
/// Layer selection chooses either PathfindingData or TerrainTargetingData so the same
/// reader implementation serves both static layers.
/// </summary>
public sealed class TerrainCellReader : ICellReader
{
    private readonly MemoryReader _reader;
    private readonly TerrainGridReader.TerrainGridSnapshot _snapshot;
    private readonly bool _useTargetingLayer;
    private readonly byte[]? _packed;

    // Fallback cache: byte per cell, 0xFF = "not yet read", else 0..15. It is only
    // allocated if the preferred bulk read failed during an area transition.
    private readonly byte[]? _fallbackCache;
    private const byte Sentinel = 0xFF;

    public int Width { get; }
    public int Height { get; }

    public TerrainCellReader(
        MemoryReader reader,
        TerrainGridReader.TerrainGridSnapshot snapshot,
        bool useTargetingLayer = false)
    {
        _reader = reader;
        _snapshot = snapshot;
        _useTargetingLayer = useTargetingLayer;
        Width = snapshot.Columns;
        Height = snapshot.Rows;

        var layer = useTargetingLayer ? snapshot.TerrainTargetingData : snapshot.PathfindingData;
        var byteCount = (long)layer.Last - (long)layer.First;
        var expectedBytes = ((long)Width * Height + 1) / 2;
        if (byteCount >= expectedBytes && expectedBytes > 0 && expectedBytes <= int.MaxValue)
        {
            try
            {
                _packed = reader.ReadArray<byte>(layer.First, (int)expectedBytes);
            }
            catch (InvalidOperationException)
            {
                // The game can replace terrain buffers during an area transition.
                // Retain the old safe behavior for this short-lived snapshot.
            }
        }

        if (_packed is null)
        {
            _fallbackCache = new byte[checked(Width * Height)];
            Array.Fill(_fallbackCache, Sentinel);
        }
    }

    public int Read(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            return 0;

        var idx = y * Width + x;
        if (_packed is not null)
            return DecodePackedCell(_packed, idx);

        var cache = _fallbackCache!;
        var cached = cache[idx];
        if (cached != Sentinel)
            return cached;

        var grid = new Vector2i { X = x, Y = y };
        bool ok = _useTargetingLayer
            ? TerrainGridReader.TryGetTerrainTargetingValue(_reader, _snapshot, grid, out var value)
            : TerrainGridReader.TryGetPathfindingValue(_reader, _snapshot, grid, out value);

        var result = ok ? (byte)value : (byte)0;
        cache[idx] = result;
        return result;
    }

    internal static int DecodePackedCell(ReadOnlySpan<byte> packed, int cellIndex)
    {
        if (cellIndex < 0 || (uint)(cellIndex / 2) >= (uint)packed.Length)
            return 0;

        var value = packed[cellIndex / 2];
        return (cellIndex & 1) == 0 ? value & 0x0F : (value >> 4) & 0x0F;
    }
}
