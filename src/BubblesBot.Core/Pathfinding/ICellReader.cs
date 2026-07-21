namespace BubblesBot.Core.Pathfinding;

/// <summary>
/// Lazy view of a single terrain layer. The pathfinder reads cells through this
/// interface rather than receiving a materialized <c>int[][]</c> grid — even at 713×714
/// cells (~500 KB) the A* loop only touches a few thousand of them, so we read on
/// demand and cache per-cell instead of allocating the whole grid up front.
///
/// Implementations MUST cache reads (the pathfinder will query the same cell as a
/// neighbor of multiple cells); they MUST return 0 for out-of-bounds queries.
/// </summary>
public interface ICellReader
{
    /// <summary>Grid width in cells.</summary>
    int Width { get; }
    /// <summary>Grid height in cells.</summary>
    int Height { get; }

    /// <summary>
    /// Cell value at (x, y). For pathfinding/targeting layers: 0 = impassable,
    /// 1-5 = walkable with cost weight (5 = cheapest). Out-of-bounds returns 0.
    /// </summary>
    int Read(int x, int y);
}
