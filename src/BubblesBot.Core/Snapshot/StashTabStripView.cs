using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Exact, currently clickable stash-tab labels from the stash's tab strip. This is deliberately
/// scoped to the stash subtree: an inventory item or unrelated panel carrying the same text can
/// never become a tab-switch target.
/// </summary>
public sealed class StashTabStripView
{
    private static readonly int[] TabStripPath = { 2, 0, 0, 1, 0 };

    public sealed record Control(nint Element, string Text, ElementGeometry.Rect Rect);

    public IReadOnlyList<Control> Controls { get; }

    internal StashTabStripView(IReadOnlyList<Control> controls) => Controls = controls;

    public IReadOnlyList<Control> FindExact(string text) => Controls
        .Where(control => control.Text.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase))
        .OrderBy(control => control.Rect.X)
        .ThenBy(control => control.Rect.Width)
        .ToArray();

    public static StashTabStripView FromIngameUi(MemoryReader reader, nint ingameState)
    {
        if (!reader.TryReadStruct<nint>(
                ingameState + KnownOffsets.IngameState.IngameUi, out var ui)
            || ui == 0
            || !reader.TryReadStruct<nint>(
                ui + KnownOffsets.IngameUiElements.StashElement, out var stash)
            || stash == 0
            || !ElementReader.IsVisibleDeep(reader, stash))
            return new StashTabStripView([]);

        var strip = stash;
        foreach (var index in TabStripPath)
            if (!ElementReader.TryGetChild(reader, strip, index, out strip))
                return new StashTabStripView([]);

        var snapshot = ElementReader.TryReadSnapshot(reader, strip, StashReader.MaximumTabElements);
        if (snapshot is null) return new StashTabStripView([]);

        var result = new List<Control>();
        foreach (var child in snapshot.Children)
        {
            if (!ElementReader.IsVisibleDeep(reader, child)
                || ElementGeometry.TryReadRect(reader, child) is not { } rect
                || rect.Width < 16 || rect.Height < 12)
                continue;
            var text = FindText(reader, child);
            if (!string.IsNullOrWhiteSpace(text))
                result.Add(new Control(child, text.Trim(), rect));
        }
        return new StashTabStripView(result);
    }

    private static string FindText(MemoryReader reader, nint root)
    {
        var queue = new Queue<(nint Address, int Depth)>();
        var seen = new HashSet<nint>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0 && seen.Count < 100)
        {
            var (address, depth) = queue.Dequeue();
            if (!seen.Add(address)) continue;
            var snapshot = ElementReader.TryReadSnapshot(reader, address, 64);
            if (snapshot is null) continue;
            var text = NativeString.Read(reader, address + KnownOffsets.Element.TextNoTags);
            if (string.IsNullOrWhiteSpace(text))
                text = NativeString.Read(reader, address + KnownOffsets.Element.Text);
            if (!string.IsNullOrWhiteSpace(text)) return text;
            if (depth >= 4) continue;
            foreach (var child in snapshot.Children) queue.Enqueue((child, depth + 1));
        }
        return string.Empty;
    }
}
