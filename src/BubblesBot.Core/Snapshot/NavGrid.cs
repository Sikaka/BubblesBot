using BubblesBot.Core.Game;
using BubblesBot.Core.Pathfinding;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Lazy view of the static terrain layers. Created on first access from
/// <see cref="GameSnapshot.Nav"/> and cached for the snapshot's lifetime, but per-cell
/// memory reads still happen lazily through <see cref="ICellReader"/> — even calling
/// <see cref="IsWalkable"/> a million times for a pathfind only touches the cells that
/// were actually queried.
///
/// The dynamic frame layer (RawFramePathfindingData) and height layer aren't included
/// — both are derived from per-tile descriptions in TerrainData and would require a real
/// reverse-engineering effort. Static layers cover the typical mapping/clearing case.
/// </summary>
public sealed class NavGrid
{
    private readonly MemoryReader _reader;
    private readonly nint _ingameDataAddress;

    private TerrainGridReader.TerrainGridSnapshot? _terrain;
    private TerrainCellReader? _path;
    private TerrainCellReader? _targeting;

    internal NavGrid(MemoryReader reader, nint ingameDataAddress)
    {
        _reader = reader;
        _ingameDataAddress = ingameDataAddress;
    }

    /// <summary>True once the terrain dimensions are resolvable.</summary>
    public bool IsAvailable => Terrain is not null;

    public int Width  => Terrain?.Columns ?? 0;
    public int Height => Terrain?.Rows    ?? 0;

    /// <summary>
    /// Walkability for movement (static layer). Returns 0..5; treat 0 as impassable and
    /// higher values as cheaper to traverse. Out-of-bounds → 0.
    /// </summary>
    public int Walkable(int x, int y) => PathReader?.Read(x, y) ?? 0;

    /// <summary>Convenience: cell is walkable at all.</summary>
    public bool IsWalkable(int x, int y) => Walkable(x, y) > 0;

    /// <summary>
    /// Targeting layer (more permissive than walkable — used for line-of-sight and gap
    /// detection). path=0 ∧ targeting&gt;0 ⇒ jumpable gap; path=0 ∧ targeting=0 ⇒ wall.
    /// </summary>
    public int Targeting(int x, int y) => TargetingReader?.Read(x, y) ?? 0;

    /// <summary>
    /// Underlying cell reader for the walkable layer, suitable for handing to
    /// <see cref="AStar"/>. Returns null if terrain isn't loaded.
    /// </summary>
    public ICellReader? PathReader => _path ??= MakeReader(false);
    public ICellReader? TargetingReader => _targeting ??= MakeReader(true);

    private TerrainGridReader.TerrainGridSnapshot? Terrain
    {
        get
        {
            if (_terrain is not null) return _terrain;
            if (TerrainGridReader.TryReadSnapshot(_reader, _ingameDataAddress, out var t))
                _terrain = t;
            return _terrain;
        }
    }

    private TerrainCellReader? MakeReader(bool targeting)
        => Terrain is { } t ? new TerrainCellReader(_reader, t, targeting) : null;
}
