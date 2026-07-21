namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Catalog of <see cref="UiPattern"/> definitions — the SHAPES the discovery tool matches
/// against the live UI tree to recover index paths after a patch. Pure data.
///
/// <para>Each pattern paired with a committed path in <see cref="UiIndexPaths"/>. Per-patch
/// flow:
/// <list type="number">
///   <item>Run <c>--discover-ui-paths</c> against live PoE.</item>
///   <item>Diff the discovered paths against committed ones.</item>
///   <item>Commit any updates that the human confirms in-game.</item>
/// </list>
/// </para>
///
/// <para>Adding a panel: define its pattern here, run discovery, paste the resulting path
/// into <see cref="UiIndexPaths"/>. Future patches keep working as long as the pattern
/// still uniquely identifies the panel.</para>
/// </summary>
public static class UiPatterns
{
    /// <summary>
    /// Map device interaction window. Validated layout 2026-05-07: 9 direct children with
    /// the following sub-shape (probed via POEMCP on an open device):
    /// <code>
    /// [0] childCount=3
    /// [1] childCount=2
    /// [2] childCount=6   ← slot row: map + 5 scarabs (each cell has 0 sub-children)
    /// [3] childCount=2   ← Activate button + label
    /// [4] childCount=0
    /// [5] childCount=3
    /// [6] childCount=0
    /// [7] childCount=0
    /// [8] childCount=0
    /// </code>
    /// The exact-match-on-every-child sub-pattern uniquely identifies this panel against
    /// other 9-child elements. Per-patch the discovery tool flags drift in any cell.
    /// </summary>
    public static readonly UiPattern MapDeviceWindow = new(
        Name: "MapDeviceWindow",
        ChildCountExact: 9,
        Children: new[]
        {
            new UiChildSpec(Index: 0, ChildCountExact: 3),
            new UiChildSpec(Index: 1, ChildCountExact: 2),
            new UiChildSpec(Index: 2, ChildCountExact: 6),
            new UiChildSpec(Index: 3, ChildCountExact: 2),
            new UiChildSpec(Index: 4, ChildCountExact: 0),
            new UiChildSpec(Index: 5, ChildCountExact: 3),
            new UiChildSpec(Index: 6, ChildCountExact: 0),
            new UiChildSpec(Index: 7, ChildCountExact: 0),
            new UiChildSpec(Index: 8, ChildCountExact: 0),
        });

    /// <summary>All patterns the discovery tool sweeps. Add new panels by appending here.</summary>
    public static readonly IReadOnlyList<UiPattern> All = new[]
    {
        MapDeviceWindow,
    };
}
