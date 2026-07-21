using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Wraps a known <c>MapDeviceWindow</c> address and exposes its slots + activate button as
/// live <see cref="ElementGeometry.Rect"/> reads. <b>All positions come from the parent-walk
/// rect math</b> — never from cached coordinates. The panel and its children move when the
/// user pans the atlas / changes resolution / docks the inventory differently, so we always
/// re-read.
///
/// <para><b>Finding the panel.</b> This view assumes you already have its memory address —
/// see <see cref="GameSnapshot.FindMapDeviceWindow"/> for the lookup, which walks the UI tree
/// after the user has opened the device. Caching the result for the session is fine; the
/// panel's address is stable while it stays open.</para>
/// </summary>
public sealed class MapDeviceView
{
    private readonly MemoryReader _reader;
    public nint WindowAddress { get; }

    /// <summary>True iff the panel pointer is non-null and its visibility flag passes.</summary>
    public bool IsVisible { get; }

    public MapDeviceView(MemoryReader reader, nint windowAddress)
    {
        _reader = reader;
        WindowAddress = windowAddress;
        IsVisible = windowAddress != 0 && IsElementVisible(windowAddress);
    }

    /// <summary>
    /// Resolve via the committed <see cref="UiIndexPaths.MapDeviceWindow"/> path. Returns a
    /// view whose <see cref="IsVisible"/> is false (and slot reads return null) when the
    /// device isn't open — callers can keep one instance and check <see cref="IsVisible"/>
    /// each tick rather than re-resolving.
    /// </summary>
    public static MapDeviceView FromUiTree(MemoryReader reader, nint uiRoot)
    {
        if (UiIndexPaths.MapDeviceWindow.IsUnset) return new MapDeviceView(reader, 0);
        var addr = UiTreeNavigator.Resolve(reader, uiRoot, UiIndexPaths.MapDeviceWindow);
        return new MapDeviceView(reader, addr);
    }

    /// <summary>The "Activate" button rect — clicking this opens the map portal.</summary>
    public ElementGeometry.Rect? ActivateButtonRect
    {
        get
        {
            if (WindowAddress == 0) return null;
            if (!_reader.TryReadStruct<nint>(WindowAddress + KnownOffsets.MapDeviceWindow.ActivateButtonPtr, out var btn) || btn == 0) return null;
            return ElementGeometry.TryReadRect(_reader, btn);
        }
    }

    /// <summary>
    /// The map slot rect (top-center of the slot row). Validated 2026-05-07 to live at
    /// <c>window.Children[2].Children[0]</c>. Re-read every frame the bot interacts with
    /// the device — panel repositions when the atlas pans / windowing changes.
    /// </summary>
    public ElementGeometry.Rect? MapSlotRect => SlotContainerChildRect(0);

    /// <summary>
    /// One of 5 scarab slot rects. <paramref name="index"/> 0..4. Slot container path:
    /// <c>window.Children[2].Children[1 + index]</c>.
    /// </summary>
    public ElementGeometry.Rect? ScarabSlotRect(int index)
    {
        if (index < 0 || index >= KnownOffsets.MapDeviceWindow.ScarabSlotCount) return null;
        return SlotContainerChildRect(1 + index);
    }

    /// <summary>Resolve a child rect under the slot-container element (window.Children[2]).</summary>
    private ElementGeometry.Rect? SlotContainerChildRect(int slotIndex)
    {
        if (WindowAddress == 0) return null;
        // window.Children[2] = the row container that holds [MapSlot, Scarab1..5]
        var slotContainer = ChildAddress(WindowAddress, 2);
        if (slotContainer == 0) return null;
        var slot = ChildAddress(slotContainer, slotIndex);
        if (slot == 0) return null;
        return ElementGeometry.TryReadRect(_reader, slot);
    }

    /// <summary>Read child[index] address from an Element's Childs vector.</summary>
    private nint ChildAddress(nint parentElementAddress, int index)
    {
        if (!_reader.TryReadStruct<nint>(parentElementAddress + KnownOffsets.Element.Childs, out var childsBegin) || childsBegin == 0) return 0;
        if (!_reader.TryReadStruct<nint>(childsBegin + index * 8, out var childPtr)) return 0;
        return childPtr;
    }

    private bool IsElementVisible(nint elementAddress)
        => BubblesBot.Core.Game.ElementReader.IsVisibleDeep(_reader, elementAddress);
}
