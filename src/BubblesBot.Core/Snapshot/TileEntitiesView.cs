using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// One static tile-entity record. PoE seeds every map with per-tile <i>marker</i> entities —
/// camera zooms, league mechanic hooks (TrialmasterBoss, BlightPump, RitualAltar), area
/// transitions — that exist in the data file before any monsters spawn. These let the bot
/// pathfind toward a feature long before the actual interactable entity streams in.
/// </summary>
public readonly record struct TileEntityEntry(string Path, Vector2i TileGridPosition, nint EntityAddress);

/// <summary>
/// Walks <see cref="KnownOffsets.IngameData.TgtArray"/> and pulls the per-tile entity lists
/// (<c>TileStructure.EntitiesList</c>, a NativePtrArray of Entity addresses). Mirrors
/// ExileCore's <c>IngameState.Data.TileEntities</c> property — same data, same shape, read
/// via direct memory rather than the SDK.
///
/// <para><b>Use case.</b> Locate deterministic map landmarks: <c>CameraZoom/TrialmasterBoss</c>
/// for Ultimatum, <c>CameraZoom/AbyssOrigin</c> for Abyss, <c>CameraZoom/BreachOrigin</c>
/// for Breach, etc. The marker sits at a known grid position from area-load time; the actual
/// interactable entity only streams in once the player gets close, so the marker is the
/// only reliable "where is the X going to be" answer.</para>
///
/// <para><b>Position fidelity.</b> v1 uses the tile's grid center (<c>tile_i % cols × 23</c>,
/// <c>tile_i / cols × 23</c>) — accurate to ±11 grid cells. That's good enough for "walk
/// toward this tile until the interactable streams in." A precise position would require
/// reading each entity's PositionedComponent via its component map (~3× the memory reads);
/// deferred until a use case demands it.</para>
///
/// <para><b>Caching.</b> Per-area like <see cref="TileMapView"/> — tile entities don't move
/// while you're in the area. Cache key is <see cref="AreaHash"/>.</para>
/// </summary>
public sealed class TileEntitiesView
{
    public uint AreaHash { get; }
    public int  TileCount { get; }
    public int  Columns { get; }
    public string LoadError { get; }

    private readonly List<TileEntityEntry> _entries;
    public IReadOnlyList<TileEntityEntry> Entries => _entries;

    public static readonly TileEntitiesView Empty = new(0, 0, 0, "(empty)", new List<TileEntityEntry>());

    private TileEntitiesView(uint areaHash, int tileCount, int cols, string error, List<TileEntityEntry> entries)
    {
        AreaHash = areaHash;
        TileCount = tileCount;
        Columns = cols;
        LoadError = error;
        _entries = entries;
    }

    // ── Cache ────────────────────────────────────────────────────────────

    private static readonly object _cacheLock = new();
    private static TileEntitiesView? _cached;

    public static TileEntitiesView GetForArea(MemoryReader reader, nint ingameDataAddress, uint areaHash)
    {
        if (areaHash == 0) return Empty;
        lock (_cacheLock)
        {
            if (_cached is not null && _cached.AreaHash == areaHash) return _cached;
            _cached = Load(reader, ingameDataAddress, areaHash);
            return _cached;
        }
    }

    public static void ResetCache()
    {
        lock (_cacheLock) _cached = null;
    }

    // ── Loader ───────────────────────────────────────────────────────────

    private static TileEntitiesView Load(MemoryReader reader, nint ingameDataAddress, uint areaHash)
    {
        if (!reader.TryReadStruct<nint>(ingameDataAddress + KnownOffsets.IngameData.TgtArray, out var begin) || begin == 0)
            return new TileEntitiesView(areaHash, 0, 0, "TgtArray begin null", new List<TileEntityEntry>());
        if (!reader.TryReadStruct<nint>(ingameDataAddress + KnownOffsets.IngameData.TgtArray + 8, out var end) || end <= begin)
            return new TileEntitiesView(areaHash, 0, 0, "TgtArray end invalid", new List<TileEntityEntry>());

        var size = KnownOffsets.TileStructure.SizeBytes;
        var byteLen = (long)end - (long)begin;
        var tileCount = (int)(byteLen / size);
        if (tileCount <= 0 || tileCount > 200_000)
            return new TileEntitiesView(areaHash, 0, 0, $"absurd tile count {tileCount}", new List<TileEntityEntry>());

        var cols = 0;
        if (reader.TryReadStruct<int>(ingameDataAddress + KnownOffsets.IngameData.TerrainBytesPerRow, out var bpr) && bpr > 0)
            cols = (bpr * 2) / KnownOffsets.TileGridCells;
        if (cols <= 0) cols = (int)Math.Round(Math.Sqrt(tileCount));

        var entries = new List<TileEntityEntry>(64);

        // Each tile has an EntitiesList at TileStructure+0x10 — an StdVector (begin/end/
        // capacity_end pointers). The container stores 8-byte pointers to <i>wrapper</i>
        // structs; the real entity address lives at <c>wrapper + 0x8</c>. We need both
        // dereferences:
        //
        //   list[i]              → wrapper pointer (8 bytes per slot)
        //   wrapper + 0x8        → entity address
        //   ReadEntityPath(addr) → "Metadata/MiscellaneousObjects/CameraZoom/TrialmasterBoss"
        //
        // Validated 2026-05-18 against TrialmasterBoss in Mesa(83) via POEMCP raw memory
        // dump. Note: slots past lEnd may contain garbage; the path-read silently fails for
        // those so we just skip them.
        const int SlotSize          = 8;
        const int WrapperEntityOffs = 0x8;

        for (var i = 0; i < tileCount; i++)
        {
            try
            {
                var tileAddr = begin + i * size;
                var listAddr = tileAddr + KnownOffsets.TileStructure.EntitiesList;

                if (!reader.TryReadStruct<nint>(listAddr,     out var lBegin) || lBegin == 0) continue;
                if (!reader.TryReadStruct<nint>(listAddr + 8, out var lEnd))                   continue;

                var slotBytes = (long)lEnd - (long)lBegin;
                if (slotBytes <= 0 || slotBytes > 65536) continue;
                var slotCount = (int)(slotBytes / SlotSize);
                if (slotCount <= 0 || slotCount > 1024) continue;

                var tilePos = new Vector2i
                {
                    X = (i % cols) * KnownOffsets.TileGridCells,
                    Y = (i / cols) * KnownOffsets.TileGridCells,
                };

                for (var j = 0; j < slotCount; j++)
                {
                    if (!reader.TryReadStruct<nint>(lBegin + j * SlotSize, out var wrapper) || wrapper == 0) continue;
                    if (!reader.TryReadStruct<nint>(wrapper + WrapperEntityOffs, out var entAddr) || entAddr == 0) continue;
                    var path = EntityListReader.ReadEntityPath(reader, entAddr);
                    if (string.IsNullOrEmpty(path)) continue;
                    entries.Add(new TileEntityEntry(path, tilePos, entAddr));
                }
            }
            catch
            {
                // Bad pointer chase — skip the tile, keep going.
            }
        }

        return new TileEntitiesView(areaHash, tileCount, cols, "", entries);
    }

    // ── Lookups ─────────────────────────────────────────────────────────

    /// <summary>
    /// Find the closest tile entity whose <see cref="TileEntityEntry.Path"/> contains the
    /// given substring (case-insensitive). Returns null when nothing matches in the area.
    /// </summary>
    public TileEntityEntry? FindNearestContaining(string pathSubstring, Vector2i fromGrid)
    {
        TileEntityEntry? best = null;
        long bestD2 = long.MaxValue;
        foreach (var e in _entries)
        {
            if (!e.Path.Contains(pathSubstring, StringComparison.OrdinalIgnoreCase)) continue;
            long dx = e.TileGridPosition.X - fromGrid.X;
            long dy = e.TileGridPosition.Y - fromGrid.Y;
            var d2 = dx * dx + dy * dy;
            if (d2 < bestD2) { bestD2 = d2; best = e; }
        }
        return best;
    }

    /// <summary>All tile entities whose path contains <paramref name="pathSubstring"/>.</summary>
    public IEnumerable<TileEntityEntry> AllContaining(string pathSubstring)
    {
        foreach (var e in _entries)
            if (e.Path.Contains(pathSubstring, StringComparison.OrdinalIgnoreCase))
                yield return e;
    }
}
