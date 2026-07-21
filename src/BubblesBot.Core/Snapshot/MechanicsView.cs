using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>The map mechanic types the bot recognizes by entity path.</summary>
public enum MechanicKind
{
    Shrine,
    EldritchAltar,
    RitualRune,
    BlightPump,
    DeliriumMirror,
    /// <summary>Atlas Memory tear (BeginMemoryLineObject): click → vanishes → drops an item
    /// a few seconds later. Captured live 2026-07-15 via capture.nearby.</summary>
    MemoryTear,
    // Strongbox / breach / heist marker / etc — add as we wire them.
}

public enum MechanicStatus
{
    Unknown,
    Available,
    Active,
    Completed,
}

/// <summary>One detected map-mechanic entity.</summary>
public sealed record MechanicEntry(
    MechanicKind Kind,
    EntityCache.Entry Entry,
    MechanicStatus Status)
{
    public uint     Id          => Entry.Id;
    public Vector2i GridPosition => Entry.GridPosition;
    public string   Name         => Entry.Name; // shrines: "Impenetrable Shrine"; others may be empty
    public string   Path         => Entry.Path;
    public bool IsAvailable => Status == MechanicStatus.Available;
    public bool IsActive => Status == MechanicStatus.Active;
    public bool IsActivated => Status == MechanicStatus.Completed;
}

/// <summary>
/// Enumerates map-mechanic entities from the published <see cref="EntityCache"/>. This view
/// intentionally uses only universal cached state; mechanic-specific controllers own their
/// validated state-machine observations.
/// <para>This is intentionally read-side only — no interactions, no decisions. The
/// behaviors layer composes the entries with click + verify primitives.</para>
/// </summary>
public sealed class MechanicsView
{
    private readonly List<MechanicEntry> _entries;
    public IReadOnlyList<MechanicEntry> Entries => _entries;

    /// <summary>Cache-only view for overlay publication; performs no game-memory reads.</summary>
    public MechanicsView(EntityCache cache) : this(cache.Entries.Values) { }

    /// <summary>Pure projection used by replay and unit tests.</summary>
    public MechanicsView(IEnumerable<EntityCache.Entry> entries)
    {
        _entries = new List<MechanicEntry>();
        foreach (var entry in entries)
        {
            var kind = ClassifyByPath(entry.Path);
            if (kind is null) continue;
            _entries.Add(new MechanicEntry(kind.Value, entry, ObserveStatus(kind.Value, entry)));
        }
    }

    /// <summary>All mechanics of <paramref name="kind"/> that are still usable.</summary>
    public IEnumerable<MechanicEntry> Available(MechanicKind kind)
    {
        foreach (var e in _entries)
            if (e.Kind == kind && e.IsAvailable && e.Entry.IsAlive) yield return e;
    }

    /// <summary>Closest available mechanic of any kind (or filtered) within range.</summary>
    public MechanicEntry? Closest(Vector2i fromGrid, float maxRangeGrid = 200f, MechanicKind? kind = null)
    {
        MechanicEntry? best = null;
        long bestD2 = (long)maxRangeGrid * (long)maxRangeGrid;
        foreach (var e in _entries)
        {
            if (!e.IsAvailable) continue;
            if (kind is { } k && e.Kind != k) continue;
            long dx = e.GridPosition.X - fromGrid.X;
            long dy = e.GridPosition.Y - fromGrid.Y;
            var d2 = dx * dx + dy * dy;
            if (d2 < bestD2) { bestD2 = d2; best = e; }
        }
        return best;
    }

    // ── Path classification ──────────────────────────────────────────

    private static MechanicKind? ClassifyByPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (path.StartsWith("Metadata/Shrines/", StringComparison.Ordinal))
            return MechanicKind.Shrine;
        if (path.Contains("PrimordialBosses/TangleAltar", StringComparison.Ordinal)
         || path.Contains("PrimordialBosses/CleansingFireAltar", StringComparison.Ordinal))
            return MechanicKind.EldritchAltar;
        // Ritual: only the *Interactable* is the click target — Object/Light are visual.
        if (path.Contains("/Ritual/RitualRuneInteractable", StringComparison.Ordinal))
            return MechanicKind.RitualRune;
        // Blight pump — the central object you click to start the encounter. AutoExile
        // matches on Path.EndsWith("/BlightPump"); same shape here.
        if (path.EndsWith("/BlightPump", StringComparison.Ordinal))
            return MechanicKind.BlightPump;
        if (path.EndsWith("/Affliction/AfflictionInitiator", StringComparison.Ordinal))
            return MechanicKind.DeliriumMirror;
        if (path.Contains("MapAtlasMemory/Objects/BeginMemoryLineObject", StringComparison.Ordinal))
            return MechanicKind.MemoryTear;
        return null;
    }

    private static MechanicStatus ObserveStatus(MechanicKind kind, EntityCache.Entry entry)
    {
        if (kind == MechanicKind.Shrine)
        {
            if (!entry.ShrineAvailable.IsKnown) return MechanicStatus.Unknown;
            return entry.ShrineAvailable.IsTrue ? MechanicStatus.Available : MechanicStatus.Completed;
        }

        if (kind == MechanicKind.RitualRune)
        {
            if (!entry.RitualCurrentState.IsKnown) return MechanicStatus.Unknown;
            return entry.RitualCurrentState.Value switch
            {
                1 when entry.RitualInteractionEnabled is { IsKnown: true, Value: 1 }
                    => MechanicStatus.Available,
                2 => MechanicStatus.Active,
                3 => MechanicStatus.Completed,
                _ => MechanicStatus.Unknown,
            };
        }


        if (kind == MechanicKind.DeliriumMirror)
        {
            if (!entry.DeliriumMirrorInteracted.IsKnown) return MechanicStatus.Unknown;
            return entry.DeliriumMirrorInteracted.Value == 0
                ? MechanicStatus.Available
                : MechanicStatus.Completed;
        }

        // Until their dedicated state contracts are committed, preserve the existing
        // targetability fallback for Eldritch altars and the Blight pump.
        if (!entry.Targetability.IsKnown) return MechanicStatus.Unknown;
        return entry.Targetability.IsTrue ? MechanicStatus.Available : MechanicStatus.Completed;
    }

}
