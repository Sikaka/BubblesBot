using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// The UI element currently under the cursor (<c>IngameState.UIHover</c>) plus its window-relative
/// rect. This is the generic hover oracle behind the "prove target identity before a mutating click"
/// discipline (checklist U-02): for panels that don't expose per-element hover state, the hovered
/// element address + geometry is the only pre-click identity signal. Passive-tree nodes in particular
/// are not enumerable panel children, so the hovered node element is the sole in-memory handle on a
/// specific node.
/// </summary>
public sealed class UiHoverView
{
    private UiHoverView(
        nint element,
        ElementGeometry.Rect? rect,
        nint tooltipRoot,
        IReadOnlyList<string> tooltipLines)
    {
        Element = element;
        Rect = rect;
        TooltipRoot = tooltipRoot;
        TooltipLines = tooltipLines;
    }

    /// <summary>Address of the hovered UI element, or 0 when nothing is hovered.</summary>
    public nint Element { get; }

    /// <summary>Window-relative rect of the hovered element, or null when unreadable.</summary>
    public ElementGeometry.Rect? Rect { get; }

    /// <summary>
    /// Root of the currently rendered hover tooltip. PoE stores <see cref="KnownOffsets.Element.Tooltip"/>
    /// as a pointer to a wrapper whose first pointer is the live tooltip element; later wrapper
    /// fields can retain stale tooltip trees and must not be searched.
    /// </summary>
    public nint TooltipRoot { get; }

    /// <summary>Bounded, de-duplicated text lines below <see cref="TooltipRoot"/>.</summary>
    public IReadOnlyList<string> TooltipLines { get; }

    /// <summary>True when an element is hovered and its geometry read cleanly.</summary>
    public bool HasHover => Element != 0 && Rect is not null;

    public static UiHoverView Read(MemoryReader reader, nint ingameStateAddress)
    {
        reader.TryReadStruct<nint>(ingameStateAddress + KnownOffsets.IngameState.UIHover, out var hover);
        var rect = hover != 0 ? ElementGeometry.TryReadRect(reader, hover) : null;
        var tooltipRoot = ReadTooltipRoot(reader, hover);
        var tooltipLines = ReadTooltipLines(reader, tooltipRoot);
        return new UiHoverView(hover, rect, tooltipRoot, tooltipLines);
    }

    private static nint ReadTooltipRoot(MemoryReader reader, nint hover)
    {
        if (hover == 0
            || !reader.TryReadStruct<nint>(hover + KnownOffsets.Element.Tooltip, out var wrapper)
            || wrapper == 0
            || !reader.TryReadStruct<nint>(wrapper, out var root)
            || root == 0)
            return 0;
        return ElementReader.TryReadSnapshot(reader, root, 256) is null ? 0 : root;
    }

    private static IReadOnlyList<string> ReadTooltipLines(MemoryReader reader, nint root)
    {
        if (root == 0) return [];
        var lines = new List<string>();
        var queue = new Queue<(nint Address, int Depth)>();
        var seen = new HashSet<nint>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0 && seen.Count < 512)
        {
            var (address, depth) = queue.Dequeue();
            if (!seen.Add(address)) continue;
            var element = ElementReader.TryReadSnapshot(reader, address, 256);
            if (element is null) continue;
            var text = NativeString.Read(reader, address + KnownOffsets.Element.TextNoTags);
            if (string.IsNullOrWhiteSpace(text))
                text = NativeString.Read(reader, address + KnownOffsets.Element.Text);
            lines.AddRange(text.Split(['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (depth >= 12) continue;
            foreach (var child in element.Children)
                queue.Enqueue((child, depth + 1));
        }
        return lines.Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
