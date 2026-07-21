using System.Diagnostics;
using System.Text;
using BubblesBot.Core;
using BubblesBot.Core.Campaign;
using BubblesBot.Core.Game;
using BubblesBot.Core.Pathfinding;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Overlay.Navigation;

/// <summary>
/// The dedicated guidance thread. Owns its own <see cref="MemoryReader"/>, consumes the main
/// thread's <see cref="WorldCursor"/>, and publishes an immutable <see cref="GuidanceSnapshot"/>.
///
/// <para>Per area it finds the key targets — the waypoint and each area transition + curated POIs
/// (Rhoa nests, bosses) — and builds a reverse-Dijkstra flow field <b>to</b> each so the render
/// thread re-walks it from the live player each frame (smooth, no per-move rebuild). Targets are
/// accumulated and their fields cached for the area; the worker re-scans every couple of seconds to
/// pick up targets that only exist as entities and stream in as the player approaches (e.g. the
/// waypoint in zones where it isn't a terrain tile). Fields are built once per unique target.</para>
/// </summary>
public sealed class GuidanceWorker : IDisposable
{
    private readonly MemoryReader _reader;
    private readonly CampaignData _data;
    private readonly Func<bool> _enabled;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _signal = new(false);
    private volatile bool _disposed;

    private WorldCursor _cursor;
    private readonly object _cursorGate = new();

    private volatile GuidanceSnapshot _current = GuidanceSnapshot.Empty;

    private uint _builtAreaHash;
    private bool _hasBuilt;
    private int _retriesLeft;
    private long _lastBuildTicks;

    // Accumulated per-area targets (fields cached). Keyed by kind+quantized position so a target is
    // only built once even though we re-scan to catch streaming entities.
    private readonly List<GuidanceTarget> _targets = new();
    private readonly HashSet<string> _seenKeys = new();

    private const int MaxTargets = 10;
    private static readonly double RescanSeconds = 2.0;

    public GuidanceSnapshot Current => _current;

    public GuidanceWorker(ProcessHandle process, CampaignData data, Func<bool> enabled)
    {
        _reader = new MemoryReader(process);
        _data = data;
        _enabled = enabled;
        _thread = new Thread(Loop) { IsBackground = true, Name = "GuidanceWorker" };
        _thread.Start();
    }

    public void Publish(WorldCursor cursor)
    {
        lock (_cursorGate) _cursor = cursor;
        _signal.Set();
    }

    private void Loop()
    {
        while (!_disposed)
        {
            _signal.Wait();
            if (_disposed) return;
            _signal.Reset();

            WorldCursor cursor;
            lock (_cursorGate) cursor = _cursor;

            if (!_enabled())
            {
                if (_current.AreaHash != 0) _current = GuidanceSnapshot.Empty;
                continue;
            }

            if (cursor.AreaHash == 0) continue;
            var areaChanged = cursor.AreaHash != _builtAreaHash;
            if (areaChanged) _retriesLeft = 30;
            var retry = _current.Targets.Count == 0 && _retriesLeft > 0;
            var periodic = Stopwatch.GetElapsedTime(_lastBuildTicks).TotalSeconds >= RescanSeconds;
            if (!areaChanged && !retry && !periodic && _hasBuilt) continue;
            if (retry) _retriesLeft--;

            try
            {
                _current = Resolve(cursor, areaChanged);
                _builtAreaHash = cursor.AreaHash;
                _hasBuilt = true;
                _lastBuildTicks = Stopwatch.GetTimestamp();
            }
            catch
            {
                // Never let a bad read kill the worker; keep the last good snapshot.
            }
        }
    }

    private GuidanceSnapshot Resolve(WorldCursor cursor, bool areaChanged)
    {
        var route = _data.Route;
        var areaId = AreaIdentityReader.CurrentAreaId(
            _reader, cursor.IngameData, id => route is not null && route.KnowsArea(id));
        if (string.IsNullOrEmpty(areaId))
            return new GuidanceSnapshot(cursor.AreaHash, string.Empty, Array.Empty<GuidanceTarget>(), "area id unavailable");

        if (!TerrainGridReader.TryReadSnapshot(_reader, cursor.IngameData, out var terrain))
            return new GuidanceSnapshot(cursor.AreaHash, areaId, _targets.ToArray(), "terrain not ready");
        var grid = new TerrainCellReader(_reader, terrain);

        if (areaChanged)
        {
            _targets.Clear();
            _seenKeys.Clear();
        }

        foreach (var (pos, label, kind) in FindAreaTargets(cursor, grid, areaId))
        {
            if (_targets.Count >= MaxTargets) break;
            var key = $"{kind}:{pos.X / 8}:{pos.Y / 8}";
            if (!_seenKeys.Add(key)) continue;          // already have a target here
            var field = DistanceField.Build(grid, pos); // built once per unique target
            _targets.Add(new GuidanceTarget(label, kind, field, pos));
        }

        var diag = _targets.Count == 0 ? "no targets found yet" : null;
        return new GuidanceSnapshot(cursor.AreaHash, areaId, _targets.ToArray(), diag);
    }

    private List<(Vector2i Pos, string Label, RouteTokenType Kind)> FindAreaTargets(WorldCursor cursor, ICellReader grid, string areaId)
    {
        var result = new List<(Vector2i, string, RouteTokenType)>();
        var tiles = TileMapView.GetForArea(_reader, cursor.IngameData, cursor.AreaHash);

        // Waypoint: from a terrain tile where the zone has one (detail name or path), else from the
        // live Waypoint entity (which streams in as the player gets near — many zones have no
        // waypoint tile at all).
        var wps = tiles.Find("waypoint");
        if (wps.Count == 0) wps = tiles.FindByKeyContains("waypoint");
        if (wps.Count > 0)
        {
            var c = TargetClusterer.Cluster(wps, 1, grid);
            if (c.Count > 0) result.Add((c[0], "Waypoint", RouteTokenType.WaypointGet));
        }
        else if (ReadWaypointEntity(cursor) is { } wpEntity)
        {
            result.Add((wpEntity, "Waypoint", RouteTokenType.WaypointGet));
        }

        // Curated per-zone POIs (transitions, Rhoa nests, bosses …).
        var poiCount = 0;
        foreach (var poi in _data.Targets.ForAreaSpecific(areaId))
        {
            if (poi.TargetType == TargetKind.Entity) continue;
            if (string.Equals(poi.Name, "waypoint", StringComparison.OrdinalIgnoreCase)) continue;
            var pts = tiles.Find(poi.Name);
            if (pts.Count == 0) pts = tiles.FindByKeyContains(poi.Name);
            if (pts.Count == 0) continue;
            var kind = LooksLikeTransition(poi) ? RouteTokenType.Enter : RouteTokenType.Generic;
            foreach (var c in TargetClusterer.Cluster(pts, Math.Max(1, poi.ExpectedCount), grid))
                result.Add((c, poi.Label, kind));
            poiCount++;
        }

        // Fallback for areas with no curated entries: raw transition tiles, then nearby entities.
        if (poiCount == 0)
        {
            var transitions = 0;
            foreach (var key in tiles.Keys)
            {
                if (key.IndexOf("/Transitions/", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var pts = tiles.Find(key);
                if (pts.Count == 0) continue;
                var c = TargetClusterer.Cluster(pts, 1, grid);
                if (c.Count == 0) continue;
                result.Add((c[0], DestinationLabel(key), RouteTokenType.Enter));
                transitions++;
            }
            if (transitions == 0)
                foreach (var t in ReadTransitions(cursor))
                    result.Add((t, "Area exit", RouteTokenType.Enter));
        }

        return result;
    }

    /// <summary>Nearest live Waypoint entity's grid position, or null when none is in range.</summary>
    private Vector2i? ReadWaypointEntity(WorldCursor cursor)
    {
        if (!_reader.TryReadStruct<nint>(cursor.IngameData + KnownOffsets.IngameData.EntityList, out var listAddr) || listAddr == 0)
            return null;
        Vector2i? best = null;
        long bestD2 = long.MaxValue;
        foreach (var addr in EntityListReader.EnumerateEntityAddresses(_reader, listAddr).EntityAddresses)
        {
            var path = EntityListReader.ReadEntityPath(_reader, addr);
            if (path is null || path.IndexOf("MiscellaneousObjects/Waypoint", StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            var snap = EntityListReader.TryReadSnapshot(_reader, addr);
            if (snap?.GridPosition is not { } g) continue;
            var d2 = Dist2(g, cursor.PlayerGrid);
            if (d2 < bestD2) { bestD2 = d2; best = g; }
        }
        return best;
    }

    private static string DestinationLabel(string key)
    {
        const string marker = "/Transitions/";
        var i = key.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return "Area exit";
        var s = key[(i + marker.Length)..];
        var slash = s.IndexOf('/');
        if (slash >= 0) s = s[..slash];
        if (s.EndsWith(".tdt", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        if (s.Contains('_'))
        {
            var parts = s.Split('_').Where(p => p.Length > 0 && !IsVersionToken(p)).Select(Capitalize);
            s = string.Join(' ', parts);
        }
        else
        {
            s = SplitCamel(s);
        }
        return string.IsNullOrWhiteSpace(s) ? "Area exit" : "→ " + s;
    }

    private static bool IsVersionToken(string p)
        => ((p[0] is 'v' or 'V') && p.Length > 1 && p.AsSpan(1).ToString().All(char.IsDigit))
           || p.All(char.IsDigit);

    private static string Capitalize(string p) => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..];

    private static string SplitCamel(string s)
    {
        var sb = new StringBuilder(s.Length + 4);
        for (var i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i]) && !char.IsUpper(s[i - 1])) sb.Append(' ');
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    private static bool LooksLikeTransition(TargetDescription t)
    {
        var s = (t.Name + " " + (t.DisplayName ?? "")).ToLowerInvariant();
        return s.Contains("transition") || s.Contains("cave") || s.Contains("passage")
            || s.Contains("entrance") || s.Contains("exit") || s.Contains("pool");
    }

    private IReadOnlyList<Vector2i> ReadTransitions(WorldCursor cursor)
    {
        var result = new List<Vector2i>();
        if (!_reader.TryReadStruct<nint>(cursor.IngameData + KnownOffsets.IngameData.EntityList, out var listAddr) || listAddr == 0)
            return result;
        foreach (var addr in EntityListReader.EnumerateEntityAddresses(_reader, listAddr).EntityAddresses)
        {
            var snap = EntityListReader.TryReadSnapshot(_reader, addr);
            if (snap is null || snap.Kind != EntityListReader.EntityKind.AreaTransition) continue;
            if (snap.GridPosition is not { } g) continue;
            if (result.Any(p => Dist2(p, g) <= 25)) continue;
            result.Add(g);
        }
        return result;
    }

    private static long Dist2(Vector2i a, Vector2i b)
    {
        long dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    public void Dispose()
    {
        _disposed = true;
        _signal.Set();
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromMilliseconds(300));
        _signal.Dispose();
    }
}
