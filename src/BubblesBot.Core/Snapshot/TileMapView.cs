using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Static-per-area landmark grid. Built once on first access for an area, cached forever for
/// that <see cref="AreaHash"/>. Each terrain tile (23×23 grid cells) carries a TgtPath
/// (the .tdt file path) and a TgtDetail.Name (semantic landmark id like "waypoint" or
/// "abyssfeature"). The reader walks every tile in <see cref="KnownOffsets.IngameData.TgtArray"/>,
/// decodes both strings, and indexes them by name AND by path so callers can look up
/// landmarks either way.
///
/// <para><b>Why area-cached.</b> Tiles are baked into the area instance at zone load —
/// they don't change while you're in the area. Reloading every snapshot would burn ~20 ms
/// for nothing. The static cache is keyed by <c>AreaHash</c> so an area transition
/// (different hash) automatically triggers a fresh load on next access.</para>
///
/// <para><b>Use.</b> <c>FindNearestNamed("waypoint", playerGrid)</c> for direct lookups,
/// <c>FindByPathPrefix("Metadata/Terrain/Coast/Caves/AreaTransition_To_MudFlats")</c> for
/// per-area transition cells, <c>RareTilesIn(blob, maxOccurrences)</c> for radar-style
/// frontier exploration ("walk toward the unusual tiles I haven't visited").</para>
/// </summary>
public sealed class TileMapView
{
    public uint   AreaHash    { get; }
    public int    TileCount   { get; }
    public int    Columns     { get; }
    public int    Rows        { get; }
    public string LoadError   { get; }

    /// <summary>
    /// Full landmark index. Each entry holds every grid position where that key appeared.
    /// Keys are both detail names ("waypoint") AND tile paths ("Metadata/Terrain/.../X.tdt"),
    /// merged into one map so callers don't need to know which channel produced the hit.
    /// </summary>
    private readonly Dictionary<string, List<Vector2i>> _byKey = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Empty placeholder used when terrain isn't loaded yet (loading screen).</summary>
    public static readonly TileMapView Empty = new(0, 0, 0, 0, "(empty)");

    private TileMapView(uint areaHash, int tileCount, int cols, int rows, string error)
    {
        AreaHash = areaHash;
        TileCount = tileCount;
        Columns = cols;
        Rows = rows;
        LoadError = error;
    }

    // ── Cache ────────────────────────────────────────────────────────────

    private static readonly object _cacheLock = new();
    private static TileMapView? _cached;

    /// <summary>
    /// Load (or fetch from cache) the tile map for the given area. Cache key is areaHash —
    /// passing a different hash triggers a fresh load. Returns <see cref="Empty"/> on failure.
    /// </summary>
    public static TileMapView GetForArea(MemoryReader reader, nint ingameDataAddress, uint areaHash)
    {
        if (areaHash == 0) return Empty;
        lock (_cacheLock)
        {
            // A first read on the loading->world edge can see the full tile array before its
            // TgtFile pointers are populated. Do not cache that zero-key race for the entire
            // area instance; retry on the next snapshot until semantic tile data appears.
            if (_cached is not null && _cached.AreaHash == areaHash
                && (_cached._byKey.Count > 0 || _cached.TileCount == 0 || _cached.LoadError.Length > 0))
                return _cached;
            _cached = Load(reader, ingameDataAddress, areaHash);
            return _cached;
        }
    }

    /// <summary>Drop the cache. Useful in tests or when forcing a re-load.</summary>
    public static void ResetCache()
    {
        lock (_cacheLock) _cached = null;
    }

    // ── Loader ───────────────────────────────────────────────────────────

    private static TileMapView Load(MemoryReader reader, nint ingameDataAddress, uint areaHash)
    {
        // Read the TgtArray bounds. NativePtrArray = (begin, end, capacity-end) of bytes.
        if (!reader.TryReadStruct<nint>(ingameDataAddress + KnownOffsets.IngameData.TgtArray, out var begin) || begin == 0)
            return new TileMapView(areaHash, 0, 0, 0, "TgtArray begin null");
        if (!reader.TryReadStruct<nint>(ingameDataAddress + KnownOffsets.IngameData.TgtArray + 8, out var end) || end <= begin)
            return new TileMapView(areaHash, 0, 0, 0, "TgtArray end invalid");

        var byteLen = (long)end - (long)begin;
        var tileCount = (int)(byteLen / KnownOffsets.TileStructure.SizeBytes);
        if (tileCount <= 0 || tileCount > 200_000)
            return new TileMapView(areaHash, 0, 0, 0, $"absurd tile count {tileCount}");

        // Derive tile-grid columns from the validated bytes-per-row * 2 (cells per row).
        // Cells per row / 23 cells per tile = tile cols. Falls back to sqrt(tileCount) if
        // BytesPerRow read fails (rare; degraded grid math but landmark lookups still work).
        var cols = 0;
        if (reader.TryReadStruct<int>(ingameDataAddress + KnownOffsets.IngameData.TerrainBytesPerRow, out var bpr) && bpr > 0)
        {
            var cellsPerRow = bpr * 2;
            cols = cellsPerRow / KnownOffsets.TileGridCells;
        }
        if (cols <= 0) cols = (int)Math.Round(Math.Sqrt(tileCount));
        var rows = (int)Math.Ceiling(tileCount / (double)cols);

        var view = new TileMapView(areaHash, tileCount, cols, rows, "");
        view.PopulateFrom(reader, begin, tileCount, cols);
        return view;
    }

    private void PopulateFrom(MemoryReader reader, nint begin, int tileCount, int cols)
    {
        var size = KnownOffsets.TileStructure.SizeBytes;
        for (var i = 0; i < tileCount; i++)
        {
            try
            {
                var tileAddr = begin + i * size;
                if (!reader.TryReadStruct<nint>(tileAddr + KnownOffsets.TileStructure.TgtFilePtr, out var tgtFilePtr) || tgtFilePtr == 0)
                    continue;

                var pos = new Vector2i
                {
                    X = (i % cols) * KnownOffsets.TileGridCells,
                    Y = (i / cols) * KnownOffsets.TileGridCells,
                };

                // Tile path.
                var path = ReadNativeString(reader, tgtFilePtr + KnownOffsets.TgtTileStruct.TgtPath);
                if (!string.IsNullOrEmpty(path)) Index(path, pos);

                // Detail name (the semantic id used by the landmark catalog).
                if (reader.TryReadStruct<nint>(tgtFilePtr + KnownOffsets.TgtTileStruct.TgtDetailPtr, out var detailPtr) && detailPtr != 0)
                {
                    var name = ReadNativeString(reader, detailPtr + KnownOffsets.TgtDetailStruct.Name);
                    if (!string.IsNullOrEmpty(name)) Index(name, pos);
                }
            }
            catch
            {
                // Bad pointer → skip this tile; keep going. PoE occasionally has stub tiles.
            }
        }
    }

    private void Index(string key, Vector2i pos)
    {
        if (!_byKey.TryGetValue(key, out var list))
        {
            list = new List<Vector2i>(4);
            _byKey[key] = list;
        }
        list.Add(pos);
    }

    // ── Lookups ──────────────────────────────────────────────────────────

    /// <summary>All indexed keys (detail names AND full tile paths). For pattern/substring matching.</summary>
    public IReadOnlyCollection<string> Keys => _byKey.Keys;

    /// <summary>All grid positions for an exact key (detail name OR full tile path).</summary>
    public IReadOnlyList<Vector2i> Find(string key)
        => _byKey.TryGetValue(key, out var list) ? list : Array.Empty<Vector2i>();

    /// <summary>Union of positions for every key that contains <paramref name="token"/>
    /// (case-insensitive). Used to resolve short target tokens against detail names / tile paths.</summary>
    public IReadOnlyList<Vector2i> FindByKeyContains(string token)
    {
        var hits = new List<Vector2i>();
        foreach (var (k, v) in _byKey)
            if (k.Contains(token, StringComparison.OrdinalIgnoreCase))
                hits.AddRange(v);
        return hits;
    }

    /// <summary>True iff the key has any tiles registered.</summary>
    public bool Has(string key) => _byKey.ContainsKey(key);

    /// <summary>Closest grid position whose key exactly matches. Null when missing.</summary>
    public Vector2i? FindNearest(string key, Vector2i fromGrid)
    {
        var hits = Find(key);
        if (hits.Count == 0) return null;
        Vector2i best = hits[0]; long bestD2 = long.MaxValue;
        foreach (var p in hits)
        {
            long dx = p.X - fromGrid.X, dy = p.Y - fromGrid.Y;
            var d2 = dx * dx + dy * dy;
            if (d2 < bestD2) { bestD2 = d2; best = p; }
        }
        return best;
    }

    /// <summary>
    /// Look up a friendly landmark via <see cref="LandmarkCatalog"/>. Resolves the catalog
    /// kind to a tile name first, then finds the nearest tile of that name. Returns null
    /// when the area doesn't have one.
    /// </summary>
    public Vector2i? FindNearestLandmark(LandmarkCatalog.Kind kind, Vector2i fromGrid)
    {
        Vector2i? best = null; long bestD2 = long.MaxValue;
        foreach (var entry in LandmarkCatalog.All)
        {
            if (entry.Kind != kind) continue;
            var hit = FindNearest(entry.DetailName, fromGrid);
            if (hit is null) continue;
            long dx = hit.Value.X - fromGrid.X, dy = hit.Value.Y - fromGrid.Y;
            var d2 = dx * dx + dy * dy;
            if (d2 < bestD2) { bestD2 = d2; best = hit; }
        }
        return best;
    }

    /// <summary>All tile keys whose path starts with <paramref name="prefix"/>. For matching
    /// per-area transitions (<c>"Metadata/Terrain/.../AreaTransition_To_MudFlats"</c>).</summary>
    public IEnumerable<(string Key, IReadOnlyList<Vector2i> Positions)> FindByPathPrefix(string prefix)
    {
        foreach (var (k, v) in _byKey)
            if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                yield return (k, v);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Read a PoE NativeString (SSO union: ≤7 UTF-16 chars inline at +0x00, otherwise
    /// pointer at +0x00). Length lives at +0x10. Mirrors <c>PlayerView.ReadNativeString</c> —
    /// promote to MemoryReader extension once a third call site appears.
    /// </summary>
    private static string ReadNativeString(MemoryReader reader, nint addr)
    {
        if (!reader.TryReadStruct<int>(addr + 0x10, out var length) || length <= 0 || length > 256)
            return string.Empty;
        try
        {
            if (length < 8) return reader.ReadStringUtf16(addr, length);
            if (!reader.TryReadStruct<nint>(addr, out var ptr) || ptr == 0) return string.Empty;
            return reader.ReadStringUtf16(ptr, length);
        }
        catch { return string.Empty; }
    }
}
