namespace BubblesBot.Core.Game;

/// <summary>
/// Curated map of <b>tile detail names</b> (from <see cref="TgtDetailStruct.Name"/>) to
/// human-readable landmark labels. Detail names are short PoE-internal identifiers like
/// <c>"waypoint"</c>, <c>"forceblank"</c>, <c>"abyssfeature"</c>; the catalog assigns each
/// one a friendly category that the rest of the bot reads.
///
/// <para><b>Mutable.</b> The catalog ships with a base set covering the common cases (zone
/// transitions, league mechanics, navigation anchors). Modes and mechanics that need to
/// recognise additional names register via <see cref="Register"/>. Registrations override
/// existing entries — last write wins. Thread-safe for read; registrations should happen
/// during startup.</para>
///
/// <para><b>Why detail name not path?</b> Tile paths (e.g.
/// <c>Metadata/Terrain/Coast/Caves/AreaTransition_To_MudFlats.tdt</c>) are precise but
/// per-area. Detail names cluster across areas — the same <c>"waypoint"</c> detail name
/// shows up on every waypoint in the game. Catalog keys on the cluster.</para>
/// </summary>
public static class LandmarkCatalog
{
    /// <summary>Friendly category for a landmark — what the bot is supposed to do with it.</summary>
    public enum Kind
    {
        Unknown,
        Waypoint,
        AreaTransition,
        BossArena,
        Mechanic,        // Harvest grove, ultimatum altar, breach origin, etc.
        Quest,
        VendorOrShop,
    }

    public sealed record Entry(string DetailName, Kind Kind, string Label);

    private static readonly Dictionary<string, Entry> _byDetail = new(StringComparer.OrdinalIgnoreCase);

    static LandmarkCatalog()
    {
        // Base set — the obvious ones validated against tile dumps. Add to this directly when
        // a value is universally useful (every map cares about it). For per-mode landmarks,
        // call Register from the mode's setup.
        Add("waypoint",          Kind.Waypoint,         "Waypoint");
        Add("ultimatum_altar",   Kind.Mechanic,         "Ultimatum altar");
        Add("abyssfeature",      Kind.Mechanic,         "Abyss");
        Add("harvest",           Kind.Mechanic,         "Harvest grove");
        Add("breachstone",       Kind.Mechanic,         "Breach origin");
        Add("ritualaltar",       Kind.Mechanic,         "Ritual altar");
        Add("delveentrance",     Kind.Mechanic,         "Delve entrance");
        Add("expeditionrelic",   Kind.Mechanic,         "Expedition relic");
        Add("bossarena",         Kind.BossArena,        "Boss arena");
        // Path-prefix catches will live alongside this in TileMapView; some landmarks only
        // show up via path string (e.g. "Metadata/Terrain/.../AreaTransition_To_X.tdt").
    }

    /// <summary>Register a new detail-name → label mapping. Overwrites if already present.</summary>
    public static void Register(string detailName, Kind kind, string label)
        => Add(detailName, kind, label);

    /// <summary>Lookup by exact detail name. Returns null when not in the catalog.</summary>
    public static Entry? Lookup(string detailName)
        => detailName is not null && _byDetail.TryGetValue(detailName, out var e) ? e : null;

    /// <summary>All registered entries — for diagnostics / UI listings.</summary>
    public static IReadOnlyCollection<Entry> All => _byDetail.Values;

    private static void Add(string detailName, Kind kind, string label)
        => _byDetail[detailName] = new Entry(detailName, kind, label);
}
