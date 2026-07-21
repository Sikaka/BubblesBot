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
    private UiHoverView(nint element, ElementGeometry.Rect? rect)
    {
        Element = element;
        Rect = rect;
    }

    /// <summary>Address of the hovered UI element, or 0 when nothing is hovered.</summary>
    public nint Element { get; }

    /// <summary>Window-relative rect of the hovered element, or null when unreadable.</summary>
    public ElementGeometry.Rect? Rect { get; }

    /// <summary>True when an element is hovered and its geometry read cleanly.</summary>
    public bool HasHover => Element != 0 && Rect is not null;

    public static UiHoverView Read(MemoryReader reader, nint ingameStateAddress)
    {
        reader.TryReadStruct<nint>(ingameStateAddress + KnownOffsets.IngameState.UIHover, out var hover);
        var rect = hover != 0 ? ElementGeometry.TryReadRect(reader, hover) : null;
        return new UiHoverView(hover, rect);
    }
}
