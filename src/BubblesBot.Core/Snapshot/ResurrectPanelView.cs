using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Reads the "you have died" resurrect panel and locates its buttons by their label text, so the
/// bot can click the right one (Resurrect at Checkpoint → hideout on an atlas map, vs Resurrect
/// in Town). Text-search rather than a committed child-index path: the panel is small and the
/// labels are stable, and this survives layout shuffles across patches.
///
/// <para><see cref="IsVisible"/> = the panel element passes the deep visibility walk — the death
/// signal (also surfaced as <c>OpenPanelsView.IsOpen("ResurrectPanel")</c>). Button rects are
/// window-relative; callers add the window origin at the click site.</para>
/// </summary>
public sealed class ResurrectPanelView
{
    private readonly MemoryReader _reader;
    private readonly nint _panel;

    private ResurrectPanelView(MemoryReader reader, nint panel)
    {
        _reader = reader;
        _panel = panel;
    }

    public bool IsVisible => _panel != 0 && ElementReader.IsVisibleDeep(_reader, _panel);

    public static ResurrectPanelView FromIngameUi(MemoryReader reader, nint ingameStateAddress)
    {
        nint panel = 0;
        if (reader.TryReadStruct<nint>(ingameStateAddress + KnownOffsets.IngameState.IngameUi, out var ui) && ui != 0)
            reader.TryReadStruct<nint>(ui + KnownOffsets.IngameUiElements.ResurrectPanel, out panel);
        return new ResurrectPanelView(reader, panel);
    }

    /// <summary>Rect (window-relative) of the first visible descendant whose label contains
    /// <paramref name="textSubstring"/> (case-insensitive), or null.</summary>
    public ElementGeometry.Rect? FindButtonRect(string textSubstring)
    {
        foreach (var (addr, text) in Descendants())
        {
            if (string.IsNullOrEmpty(text)) continue;
            if (text.IndexOf(textSubstring, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var rect = ElementGeometry.TryReadRect(_reader, addr);
            if (rect is { } r && r.Width > 0 && r.Height > 0) return r;
        }
        return null;
    }

    /// <summary>Resurrect-at-checkpoint button (returns to hideout on an atlas map).</summary>
    public ElementGeometry.Rect? CheckpointButtonRect() => FindButtonRect("checkpoint");

    /// <summary>Resurrect-in-town button.</summary>
    public ElementGeometry.Rect? TownButtonRect() => FindButtonRect("town");

    /// <summary>All descendant (address, text) pairs — for diagnostics / discovery.</summary>
    public IEnumerable<(nint Addr, string Text)> Descendants(int maxNodes = 400, int maxDepth = 12)
    {
        if (_panel == 0) yield break;
        var queue = new Queue<(nint addr, int depth)>();
        queue.Enqueue((_panel, 0));
        var visited = 0;
        while (queue.Count > 0 && visited < maxNodes)
        {
            var (addr, depth) = queue.Dequeue();
            visited++;
            var text = NativeString.Read(_reader, addr + KnownOffsets.Element.Text);
            yield return (addr, text);
            if (depth >= maxDepth) continue;
            var snap = ElementReader.TryReadSnapshot(_reader, addr, maxChildren: 64);
            if (snap is null) continue;
            foreach (var child in snap.Children) queue.Enqueue((child, depth + 1));
        }
    }
}
