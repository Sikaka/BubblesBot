using BubblesBot.Core.Game;

namespace BubblesBot.Bot.Systems;

/// <summary>How an in-map area arrival relates to the run's parent map.</summary>
public enum SubAreaArrival
{
    ParentMap,     // returned to the parent map instance (back through the door)
    NewSubArea,    // entered a fresh detached region (grove, boss room) — parent ledger retained
    LeftToHub,     // landed in a safe hub — an unexpected exit mid-run (incident)
    Unknown,       // no positive role evidence yet
}

/// <summary>
/// Tracks in-map area transitions so the map-run keeps its parent ledger across detached regions
/// (Harvest grove, boss arenas) and can tell "went into a sub-area" from "fell back to the parent"
/// and from "unexpectedly left to town." Records the door's grid position as the return anchor.
///
/// <para>Pure and deterministic. Closes the gap where the in-map transition behavior accepted any
/// hash change with no destination proof. Wiring it into the zone loop is a separate
/// live-validated step.</para>
/// </summary>
public sealed class SubAreaTracker
{
    private uint _parentAreaHash;
    private Vector2i _returnAnchor;
    private bool _inSubArea;

    public uint ParentAreaHash => _parentAreaHash;
    public Vector2i ReturnAnchor => _returnAnchor;
    public bool InSubArea => _inSubArea;

    /// <summary>Establish the parent map on map entry (before any in-map door is taken).</summary>
    public void EnterParent(uint parentAreaHash)
    {
        _parentAreaHash = parentAreaHash;
        _inSubArea = false;
        _returnAnchor = default;
    }

    /// <summary>Record that a transition at <paramref name="doorGrid"/> is being taken (the return anchor).</summary>
    public void TakingTransition(Vector2i doorGrid) => _returnAnchor = doorGrid;

    /// <summary>
    /// Classify the area we just arrived in. <paramref name="role"/> comes from the same
    /// <c>WorldAreaClassifier</c> used at hideout↔map boundaries.
    /// </summary>
    public SubAreaArrival Classify(uint newAreaHash, AreaRole role)
    {
        if (role == AreaRole.SafeHub) return SubAreaArrival.LeftToHub;
        if (newAreaHash == _parentAreaHash)
        {
            _inSubArea = false;
            return SubAreaArrival.ParentMap;
        }
        if (role is AreaRole.Map or AreaRole.SubArea or AreaRole.BossArena)
        {
            _inSubArea = true;
            return SubAreaArrival.NewSubArea;
        }
        return SubAreaArrival.Unknown;
    }

    public void Reset()
    {
        _parentAreaHash = 0;
        _returnAnchor = default;
        _inSubArea = false;
    }
}
