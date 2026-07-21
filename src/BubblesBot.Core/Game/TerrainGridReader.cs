namespace BubblesBot.Core.Game;

public static class TerrainGridReader
{
    public sealed record TerrainGridSnapshot(
        int Rows,
        int Columns,
        NativePtrArray PathfindingData,
        NativePtrArray TerrainTargetingData);

    public static bool TryReadSnapshot(MemoryReader reader, nint ingameData, out TerrainGridSnapshot snapshot)
    {
        snapshot = default!;
        if (!reader.TryReadStruct<NativePtrArray>(ingameData + KnownOffsets.IngameData.RawPathfindingData, out var pathfinding))
            return false;
        if (!reader.TryReadStruct<NativePtrArray>(ingameData + KnownOffsets.IngameData.RawTerrainTargetingData, out var targeting))
            return false;

        var pathBytes = (long)pathfinding.Last - (long)pathfinding.First;
        var targetingBytes = (long)targeting.Last - (long)targeting.First;
        if (pathBytes <= 0 || pathBytes != targetingBytes)
            return false;

        // Read the actual BytesPerRow from TerrainData (inline at IngameData+0xC68). With
        // 4-bit-packed cells, columns = bytesPerRow × 2. rows = total bytes ÷ bytesPerRow.
        // Works for any area shape — the previous "guess square dimensions" approach failed
        // on rectangular hideouts and corridors.
        if (!reader.TryReadStruct<int>(ingameData + KnownOffsets.IngameData.TerrainBytesPerRow, out var bytesPerRow))
            return false;
        if (bytesPerRow <= 0 || bytesPerRow > 8192)
            return TryFallbackGuess(pathfinding, targeting, pathBytes, out snapshot);

        var columns = bytesPerRow * 2;
        if (pathBytes % bytesPerRow != 0)
            return TryFallbackGuess(pathfinding, targeting, pathBytes, out snapshot);
        var rows = (int)(pathBytes / bytesPerRow);
        if (rows <= 0 || rows > 8192) return false;

        snapshot = new TerrainGridSnapshot(rows, columns, pathfinding, targeting);
        return true;
    }

    /// <summary>Last-resort dimension guess for the rare case TerrainBytesPerRow misreads.</summary>
    private static bool TryFallbackGuess(NativePtrArray pathfinding, NativePtrArray targeting, long pathBytes, out TerrainGridSnapshot snapshot)
    {
        snapshot = default!;
        var dims = GuessDimensions(pathBytes * 2);
        if (dims is not { } d) return false;
        snapshot = new TerrainGridSnapshot(d.Rows, d.Columns, pathfinding, targeting);
        return true;
    }

    public static bool TryGetPathfindingValue(MemoryReader reader, TerrainGridSnapshot snapshot, Vector2i grid, out int value)
        => TryReadPackedNibble(reader, snapshot.PathfindingData, snapshot.Rows, snapshot.Columns, grid, out value);

    public static bool TryGetTerrainTargetingValue(MemoryReader reader, TerrainGridSnapshot snapshot, Vector2i grid, out int value)
        => TryReadPackedNibble(reader, snapshot.TerrainTargetingData, snapshot.Rows, snapshot.Columns, grid, out value);

    private static bool TryReadPackedNibble(MemoryReader reader, NativePtrArray data, int rows, int columns, Vector2i grid, out int value)
    {
        value = 0;
        if (grid.X < 0 || grid.Y < 0 || grid.X >= columns || grid.Y >= rows)
            return false;

        var cellIndex = grid.Y * columns + grid.X;
        var byteIndex = cellIndex / 2;
        var byteCount = (long)data.Last - (long)data.First;
        if (byteIndex < 0 || byteIndex >= byteCount)
            return false;

        if (!reader.TryReadStruct<byte>(data.First + byteIndex, out var packed))
            return false;

        value = (cellIndex & 1) == 0 ? packed & 0x0F : (packed >> 4) & 0x0F;
        return true;
    }

    private static (int Rows, int Columns)? GuessDimensions(long cells)
    {
        // PoE terrain grids observed through ExileAPI use rows and columns that
        // differ by at most one. The packed buffers store two 4-bit cells/byte.
        var root = (int)Math.Sqrt(cells);
        for (var rows = Math.Max(1, root - 4); rows <= root + 4; rows++)
        {
            if (cells % rows != 0) continue;
            var columns = (int)(cells / rows);
            if (Math.Abs(columns - rows) <= 1)
                return (Math.Min(rows, columns), Math.Max(rows, columns));
        }

        return null;
    }
}
