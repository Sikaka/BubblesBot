using BubblesBot.Core.Game;
using BubblesBot.Research.Probing;

namespace BubblesBot.Research.Probes.Terrain;

/// <summary>
/// Terrain data the pathfinder consumes: the TgtArray (tile structure array) and the packed-grid
/// BytesPerRow. Both values vary by area, so they are validated against one another rather than an
/// area-specific baseline.
/// </summary>
public sealed class TerrainProbe : IProbe
{
    public string Name => "terrain.grid";
    public string Group => "terrain";
    public string Description => "IngameData TgtArray and TerrainBytesPerRow form a coherent rectangular tile grid.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var igd = ctx.Chain.IngameData;

        ProbeResult tgt;
        long tileCount = 0;
        if (ctx.Reader.TryReadStruct<NativePtrArray>(igd + KnownOffsets.IngameData.TgtArray, out var arr))
        {
            // Element stride is 56 bytes (TileStructure); count = byteSpan / 56.
            var byteSpan = (long)arr.Last - (long)arr.First;
            tileCount = byteSpan > 0 ? byteSpan / 56 : 0;
            tgt = Reads.Readable(ctx.Reader, arr.First) && tileCount is > 0 and < 5_000_000
                ? ProbeResult.Pass($"TgtArray@+0x{KnownOffsets.IngameData.TgtArray:X} first=0x{(long)arr.First:X} tiles~{tileCount}")
                : ProbeResult.Fail($"TgtArray@+0x{KnownOffsets.IngameData.TgtArray:X} implausible (first=0x{(long)arr.First:X} span={byteSpan})");
        }
        else tgt = ProbeResult.Fail("TgtArray unreadable");

        ProbeResult bpr;
        if (!ctx.Reader.TryReadStruct<int>(igd + KnownOffsets.IngameData.TerrainBytesPerRow, out var n))
            bpr = ProbeResult.Fail("TerrainBytesPerRow unreadable");
        else
        {
            var cellsPerRow = n * 2;
            var columns = cellsPerRow > 0 && cellsPerRow % KnownOffsets.TileGridCells == 0
                ? cellsPerRow / KnownOffsets.TileGridCells
                : 0;
            bpr = n > 0 && n < 100_000 && columns > 0 && tileCount > 0 && tileCount % columns == 0
                ? ProbeResult.Pass($"TerrainBytesPerRow={n} -> {columns}x{tileCount / columns} tile grid")
                : ProbeResult.Fail($"TerrainBytesPerRow={n} does not form a coherent grid for {tileCount} tiles");
        }

        return ProbeResult.Combine(tgt, bpr);
    }

    public ProbeResult Discover(ProbeContext ctx)
    {
        if (!ctx.Reader.TryReadStruct<NativePtrArray>(
                ctx.Chain.IngameData + KnownOffsets.IngameData.TgtArray, out var arr))
            return ProbeResult.Found("IngameData.TerrainBytesPerRow", []);
        var tileCount = ((long)arr.Last - (long)arr.First) / KnownOffsets.TileStructure.SizeBytes;
        var bytes = new byte[0x1400];
        var read = ctx.Reader.TryReadBytes(ctx.Chain.IngameData, bytes);
        var candidates = new List<OffsetCandidate>();
        for (var offset = 0; offset + 4 <= read; offset += 4)
        {
            var value = BitConverter.ToInt32(bytes, offset);
            var cells = value * 2;
            if (cells <= 0 || cells % KnownOffsets.TileGridCells != 0) continue;
            var columns = cells / KnownOffsets.TileGridCells;
            if (columns > 0 && tileCount > 0 && tileCount % columns == 0)
                candidates.Add(new OffsetCandidate(offset, $"{value} -> {columns}x{tileCount / columns} tiles"));
        }
        return ProbeResult.Found("IngameData.TerrainBytesPerRow", candidates);
    }
}
