using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>Structurally validated navigation controls inside the specialized Maps stash tab.</summary>
public sealed class MapStashNavigationView
{
    public sealed record Selector(int PanelIndex, string Label, nint Element, ElementGeometry.Rect Rect);

    public bool IsReadable { get; }
    public int CurrentPanelIndex { get; }
    public ElementGeometry.Rect? Tier16Rect { get; }
    public IReadOnlyList<Selector> Selectors { get; }

    internal MapStashNavigationView(
        bool isReadable, int currentPanelIndex, ElementGeometry.Rect? tier16Rect,
        IReadOnlyList<Selector> selectors)
    {
        IsReadable = isReadable;
        CurrentPanelIndex = currentPanelIndex;
        Tier16Rect = tier16Rect;
        Selectors = selectors.OrderBy(selector => selector.PanelIndex).ToArray();
    }

    public static MapStashNavigationView FromIngameUi(MemoryReader reader, nint ingameState)
    {
        if (!reader.TryReadStruct<nint>(
                ingameState + KnownOffsets.IngameState.IngameUi, out var ui)
            || !reader.TryReadStruct<nint>(
                ui + KnownOffsets.IngameUiElements.StashElement, out var stash)
            || !StashReader.TryGetVisibleStash(reader, stash, out var visible, out _, out _))
            return new MapStashNavigationView(false, -1, null, []);

        ElementGeometry.Rect? tier16 = null;
        if (ElementReader.TryGetChild(reader, visible, 1, out var secondTierRow)
            && ElementReader.TryGetChild(reader, secondTierRow, 6, out var tier16Control))
            tier16 = ElementGeometry.TryReadRect(reader, tier16Control);

        var currentPanel = -1;
        var selectors = new List<Selector>();
        if (!ElementReader.TryGetChild(reader, visible, 3, out var selectorRoot)
            || !ElementReader.TryGetChild(reader, selectorRoot, 0, out var selectorBar)
            || !ElementReader.TryGetChild(reader, selectorRoot, 1, out var panels))
            return new MapStashNavigationView(false, -1, tier16, []);

        var panelSnapshot = ElementReader.TryReadSnapshot(reader, panels, 256);
        if (panelSnapshot is not null)
            for (var i = 0; i < panelSnapshot.Children.Count; i++)
                if (ElementReader.IsVisibleDeep(reader, panelSnapshot.Children[i]))
                { currentPanel = i; break; }

        var barSnapshot = ElementReader.TryReadSnapshot(reader, selectorBar, 256);
        if (barSnapshot is not null)
            for (var i = 0; i < barSnapshot.Children.Count; i++)
            {
                var control = barSnapshot.Children[i];
                if (!ElementReader.IsVisibleDeep(reader, control)
                    || ElementGeometry.TryReadRect(reader, control) is not
                        { Width: > 20, Width: < 100, Height: > 15 } rect)
                    continue;
                var label = ReadFirstText(reader, control);
                if (int.TryParse(label, out var number) && number is >= 1 and <= 99)
                    selectors.Add(new Selector(number - 1, label, control, rect));
            }

        return new MapStashNavigationView(
            currentPanel >= 0 && selectors.Count > 0, currentPanel, tier16,
            selectors.OrderBy(selector => selector.PanelIndex).ToArray());
    }

    private static string ReadFirstText(MemoryReader reader, nint root)
    {
        var queue = new Queue<(nint Address, int Depth)>();
        var seen = new HashSet<nint>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0 && seen.Count < 32)
        {
            var (address, depth) = queue.Dequeue();
            if (!seen.Add(address)) continue;
            var snapshot = ElementReader.TryReadSnapshot(reader, address, 8);
            if (snapshot is null) continue;
            var text = NativeString.Read(reader, address + KnownOffsets.Element.TextNoTags);
            if (string.IsNullOrWhiteSpace(text)) text = NativeString.Read(reader, address + KnownOffsets.Element.Text);
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
            if (depth >= 3) continue;
            foreach (var child in snapshot.Children) queue.Enqueue((child, depth + 1));
        }
        return string.Empty;
    }
}
