namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Describes the SHAPE of a UI panel for discovery — child counts and per-child sub-shapes.
/// Pure data. Matched against the live UI tree by <see cref="UiPatternMatcher"/>; the result
/// is an <see cref="UiIndexPath"/> the bot stores and uses for fast runtime lookup.
///
/// <para><b>Why patterns instead of memory addresses.</b> Addresses change every PoE
/// session; index paths through the UI tree are stable across sessions and most patches.
/// When GGG restructures a panel (rare), the matcher flags it and the per-patch flow
/// updates the committed path.</para>
/// </summary>
public sealed record UiPattern(
    string Name,
    UiChildSpec[] Children,
    int? ChildCountExact = null,
    int? MinChildCount = null,
    int? MaxChildCount = null);

/// <summary>
/// Constraint on a specific child of the matched element. Index is mandatory; everything
/// else is optional — leave null when the bot doesn't care.
///
/// <para>Sub-children let you match nested structure: <c>"Children[2] has 6 children, the
/// first of which has 3 sub-children"</c>. Each level adds matching strictness.</para>
/// </summary>
public sealed record UiChildSpec(
    int Index,
    int? ChildCountExact = null,
    int? MinChildCount = null,
    int? MaxChildCount = null,
    UiChildSpec[]? Children = null);

/// <summary>
/// A path through the UI tree: <c>UIRoot.Children[a].Children[b].Children[c]...</c>.
/// Stored as the bot's "offset" for a panel. Empty array = the UIRoot itself.
/// </summary>
public readonly struct UiIndexPath
{
    private readonly int[]? _indices;
    /// <summary>Path indices. Always non-null — defaults to an empty array for an unset path.</summary>
    public int[] Indices => _indices ?? Array.Empty<int>();

    /// <summary>True when the path is explicitly empty (i.e. "this is the root itself").</summary>
    public bool IsEmpty => Indices.Length == 0;

    /// <summary>True when the path was never assigned (default <c>UiIndexPath</c>). Distinguishes "unset" from "root."</summary>
    public bool IsUnset => _indices is null;

    public UiIndexPath(params int[] indices) { _indices = indices ?? Array.Empty<int>(); }

    public override string ToString()
        => Indices.Length == 0 ? (IsUnset ? "(unset)" : "[]") : "[" + string.Join(",", Indices) + "]";

    public bool Equals(UiIndexPath other)
    {
        var a = Indices; var b = other.Indices;
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is UiIndexPath other && Equals(other);
    public override int GetHashCode() { var h = 0; foreach (var i in Indices) h = h * 31 + i; return h; }
}

/// <summary>One pattern-matching result: a candidate path with a confidence score 0..1.</summary>
public readonly record struct PatternMatch(UiIndexPath Path, float Confidence, string Notes);
