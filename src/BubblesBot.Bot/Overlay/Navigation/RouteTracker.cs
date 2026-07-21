using BubblesBot.Core.Game;
using BubblesBot.Core.Pathfinding;

namespace BubblesBot.Bot.Overlay.Navigation;

/// <summary>
/// Cheap per-tick guidance route maintenance on top of <see cref="BackgroundReplanner"/>. Set the
/// desired target (and the area's grid) when it changes; the tracker requests an off-thread field
/// rebuild only on a target/area change, and every tick reads the current route from the player's
/// cell in O(path length) with no pathfinding. This is the "cheap maintain / expensive replan
/// off-thread" split from POE2Radar.
/// </summary>
public sealed class RouteTracker : IDisposable
{
    private readonly BackgroundReplanner _replanner = new();
    private readonly List<PathCell> _path = new();

    private long _key;
    private Vector2i? _target;

    /// <summary>The current guidance route (player → target), empty when none is available yet.</summary>
    public IReadOnlyList<PathCell> CurrentPath => _path;

    /// <summary>True once a field for the current target/area has finished building.</summary>
    public bool HasRoute => _target is { } t && FieldMatches(t);

    /// <summary>
    /// Declare the desired guidance target for the current area. A change (different target cell or a
    /// different area key) triggers one off-thread field rebuild; unchanged calls are no-ops.
    /// Passing a null target clears guidance. <paramref name="grid"/> is used only by the worker.
    /// </summary>
    public void SetTarget(long areaKey, Vector2i? target, ICellReader grid)
    {
        var changed = areaKey != _key
            || (_target is null) != (target is null)
            || (_target is { } a && target is { } b && (a.X != b.X || a.Y != b.Y));
        if (!changed) return;

        _key = areaKey;
        _target = target;
        _path.Clear();
        if (target is { } tgt)
            _replanner.Submit(new BackgroundReplanner.Request(areaKey, tgt, grid));
    }

    /// <summary>Refresh the route from the player's current cell (cheap; no pathfinding).</summary>
    public void Update(Vector2i playerCell)
    {
        if (_target is not { } tgt || !FieldMatches(tgt))
        {
            _path.Clear();
            return;
        }
        _replanner.Current!.TryGetPath(playerCell, _path);
    }

    private bool FieldMatches(Vector2i target)
    {
        var field = _replanner.Current;
        return field is not null
            && _replanner.CurrentKey == _key
            && _replanner.CurrentTarget.X == target.X
            && _replanner.CurrentTarget.Y == target.Y;
    }

    public void Dispose() => _replanner.Dispose();
}
