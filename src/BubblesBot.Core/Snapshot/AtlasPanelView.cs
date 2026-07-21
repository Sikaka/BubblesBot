using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Wraps PoE's <c>AtlasPanel</c> — the modern combined atlas + map-device UI. Exposes the
/// pre-loaded map storage list, the device's 6 staging slots, and the Activate button as
/// live <see cref="ElementGeometry.Rect"/> reads.
///
/// <para><b>Path tree (validated 2026-05-08 against current PoE):</b>
/// <list type="bullet">
///   <item><c>AtlasPanel.Children[3].Children[0].Children[1]</c> — atlas-side map/item grid. Each
///         child element is a <c>NormalInventoryItem</c> with an <c>Item</c> entity pointer.
///         Index <c>[0]</c> within this is the panel background, indices <c>[1..N]</c> are the
///         actual maps. Always visible while the atlas is open.</item>
///   <item><c>AtlasPanel.Children[7].Children[0]</c> — the device window subtree. Becomes
///         visible after the player selects an atlas node / stages a map.
///     <list type="bullet">
///       <item><c>[7][0][2]</c> — slot row, 6 children = 6 device slots. An occupied slot has
///             <c>ChildCount==2</c> and its <c>Children[1]</c> is the staged item.</item>
///       <item><c>[7][0][3]</c> — Activate button. <c>Children[0].Text</c> reads "activate"
///             when enabled.</item>
///       <item><c>[7][0][1][0][0]</c> — selected-map name text (verifies which atlas node is
///             active for activation).</item>
///     </list>
///   </item>
/// </list></para>
///
/// <para>The committed paths come from AutoExile's <c>MapDeviceSystem.cs</c> — the bot mirrors
/// its UI layout assumptions. If GGG repaints the panel hierarchy these will need a sweep via
/// <c>--discover-ui-paths</c>. The panel rects are recomputed via <see cref="ElementGeometry"/>
/// every read so they track repositioning automatically.</para>
/// </summary>
public sealed class AtlasPanelView
{
    private readonly MemoryReader _reader;
    public nint PanelAddress { get; }

    /// <summary>True iff the atlas panel is open and visible.</summary>
    public bool IsVisible { get; }

    public AtlasPanelView(MemoryReader reader, nint atlasPanelAddress)
    {
        _reader = reader;
        PanelAddress = atlasPanelAddress;
        IsVisible = atlasPanelAddress != 0 && IsElementVisible(atlasPanelAddress);
    }

    public static AtlasPanelView FromIngameUi(MemoryReader reader, nint ingameStateAddress)
    {
        // Direct two-hop read: IngameState → IngameUi → AtlasPanel. Avoids the UI tree walk
        // entirely, which was resolving to the wrong element on current PoE (the legacy
        // MapDeviceWindow path landed on a sibling at UIRoot[1][29][7], not the actual atlas
        // which lives at UIRoot[1][29]; using the IngameUi+0x648 pointer is what ExileCore
        // does internally and skips the tree-shape sensitivity).
        if (!reader.TryReadStruct<nint>(ingameStateAddress + KnownOffsets.IngameState.IngameUi, out var ingameUi) || ingameUi == 0)
            return new AtlasPanelView(reader, 0);
        if (!reader.TryReadStruct<nint>(ingameUi + KnownOffsets.IngameUiElements.AtlasPanel, out var atlasAddr) || atlasAddr == 0)
            return new AtlasPanelView(reader, 0);
        return new AtlasPanelView(reader, atlasAddr);
    }

    // ─── Map storage ─────────────────────────────────────────────────────

    /// <summary>
    /// One stored map / fragment in the atlas storage. Index 0 is the panel background — the
    /// caller sees only slots from index 1 upward.
    /// </summary>
    public sealed record StoredItem(int Index, ElementGeometry.Rect Rect, nint ElementAddress, nint ItemEntityAddress, string Path);

    /// <summary>
    /// All clickable items in the atlas storage. The first child of the storage container is
    /// the panel background and is filtered out — caller gets the "real" map list with
    /// <see cref="StoredItem.Index"/> matching the panel's child index for click verification.
    /// Returns empty if the panel isn't visible / storage container missing.
    /// </summary>
    public IReadOnlyList<StoredItem> StoredItems()
    {
        var result = new List<StoredItem>();
        var storage = ResolveByPath(PanelAddress, 3, 0, 1);
        if (storage == 0) return result;

        if (!_reader.TryReadStruct<nint>(storage + KnownOffsets.Element.Childs, out var childsBegin) || childsBegin == 0)
            return result;
        if (!_reader.TryReadStruct<nint>(storage + KnownOffsets.Element.Childs + 8, out var childsEnd) || childsEnd == 0)
            return result;

        var count = (int)((childsEnd - childsBegin) / 8);
        if (count <= 1 || count > 256) return result;

        for (var i = 1; i < count; i++)   // skip [0] = panel background
        {
            if (!_reader.TryReadStruct<nint>(childsBegin + i * 8, out var childPtr) || childPtr == 0) continue;
            var rect = ElementGeometry.TryReadRect(_reader, childPtr);
            if (rect is null) continue;

            // NormalInventoryItem.Item points at the item's Entity. Read its path (metadata
            // path through the entity's ObjectHeader) for filtering.
            nint itemEntity = 0;
            string path = string.Empty;
            if (_reader.TryReadStruct<nint>(childPtr + KnownOffsets.NormalInventoryItem.Item, out itemEntity)
                && itemEntity != 0)
            {
                path = EntityListReader.ReadEntityPath(_reader, itemEntity) ?? string.Empty;
            }

            result.Add(new StoredItem(i, rect.Value, childPtr, itemEntity, path));
        }
        return result;
    }

    // ─── Device slots + Activate button ──────────────────────────────────

    /// <summary>
    /// Six device staging slots. Index 0..5. Returns <c>(rect, isOccupied)</c>.
    /// An occupied slot has child count == 2 (slot frame + staged item).
    /// </summary>
    public (ElementGeometry.Rect? Rect, bool IsOccupied)? DeviceSlot(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex > 5) return null;
        if (!IsDevicePanelVisible()) return null;
        var slotRow = ResolveByPath(PanelAddress, 7, 0, 2);
        if (slotRow == 0) return null;
        var slot = ChildAddress(slotRow, slotIndex);
        if (slot == 0) return null;

        var rect = ElementGeometry.TryReadRect(_reader, slot);

        // Occupancy: read child count.
        int childCount = 0;
        if (_reader.TryReadStruct<nint>(slot + KnownOffsets.Element.Childs, out var begin)
         && _reader.TryReadStruct<nint>(slot + KnownOffsets.Element.Childs + 8, out var end)
         && begin != 0 && end >= begin)
        {
            childCount = (int)((end - begin) / 8);
        }
        return (rect, childCount >= 2);
    }

    /// <summary>
    /// True when the device sub-panel (atlas child <c>[7]</c>) is visible — i.e. a map node is
    /// selected on the atlas and the staging slots + Activate button are showing. Inserting a
    /// stored map (Ctrl+click) only works once this panel is up. AutoExile gates the same way
    /// (<c>atlas.GetChildAtIndex(7).IsVisible</c>). Note <see cref="DeviceSlot"/> /
    /// <see cref="ActivateButtonRect"/> read into this same subtree, so they only return real
    /// data when this is true.
    /// </summary>
    public bool IsDevicePanelVisible()
    {
        var dev = ChildAddress(PanelAddress, 7);
        return dev != 0 && IsElementVisible(dev);
    }

    /// <summary>The Activate button's rect. Null when the device subtree isn't visible.</summary>
    public ElementGeometry.Rect? ActivateButtonRect()
    {
        if (!IsDevicePanelVisible()) return null;
        var btn = ResolveByPath(PanelAddress, 7, 0, 3);
        if (btn == 0 || !IsElementVisible(btn)) return null;
        return ElementGeometry.TryReadRect(_reader, btn);
    }

    /// <summary>
    /// True when the activate-button label child is visible AND its text equals "activate"
    /// (case-insensitive). PoE re-labels / hides this button when the slot row is empty,
    /// so checking text is the most reliable readiness signal.
    /// </summary>
    public bool IsActivateReady()
    {
        if (!IsDevicePanelVisible()) return false;
        var btn = ResolveByPath(PanelAddress, 7, 0, 3);
        if (btn == 0) return false;
        var label = ChildAddress(btn, 0);
        if (label == 0) return false;
        if (!IsElementVisible(label)) return false;
        var text = NativeString.Read(_reader, label + KnownOffsets.Element.Text);
        return text.Trim().Equals("activate", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Selected Atlas node name shown in the device header.</summary>
    public string SelectedMapName()
    {
        if (!IsDevicePanelVisible()) return string.Empty;
        var label = ResolveByPath(PanelAddress, 7, 0, 1, 0, 0);
        if (label == 0) return string.Empty;
        var text = NativeString.Read(_reader, label + KnownOffsets.Element.TextNoTags);
        if (string.IsNullOrWhiteSpace(text))
            text = NativeString.Read(_reader, label + KnownOffsets.Element.Text);
        return text.Trim();
    }

    /// <summary>
    /// Click rectangle for one direct atlas-canvas child. The caller owns the validated
    /// data-index translation and must verify <see cref="SelectedMapName"/> after clicking.
    /// </summary>
    public ElementGeometry.Rect? AtlasCanvasChildRect(int childIndex)
    {
        if (!IsVisible || childIndex < 0) return null;
        var canvas = ChildAddress(PanelAddress, 0);
        if (canvas == 0) return null;
        var child = ChildAddress(canvas, childIndex);
        return child == 0 ? null : ElementGeometry.TryReadRect(_reader, child);
    }

    /// <summary>
    /// Proves that the element currently under the cursor belongs to one exact direct atlas
    /// canvas child. This is the pre-click identity canary for map-node selection: geometry
    /// alone is insufficient because the inventory panel can cover a perfectly plausible
    /// node rectangle and intercept the click.
    /// </summary>
    public bool HoverBelongsToAtlasCanvasChild(nint hoveredElement, int childIndex)
    {
        if (!IsVisible || hoveredElement == 0 || childIndex < 0) return false;
        var canvas = ChildAddress(PanelAddress, 0);
        if (canvas == 0) return false;
        var target = ChildAddress(canvas, childIndex);
        if (target == 0) return false;

        var current = hoveredElement;
        for (var depth = 0; depth < 24 && current != 0; depth++)
        {
            if (current == target) return true;
            if (!_reader.TryReadStruct<nint>(current + KnownOffsets.Element.Parent, out var parent)
                || parent == current)
                break;
            current = parent;
        }
        return false;
    }

    /// <summary>
    /// Returns the direct atlas-canvas child that owns the hovered element, or -1 when the
    /// cursor is outside the canvas/covered by another panel. Prefix children are canvas
    /// decoration/background; map nodes begin at the caller's current UI prefix.
    /// </summary>
    public int AtlasCanvasDirectChildForHover(nint hoveredElement)
    {
        if (!IsVisible || hoveredElement == 0) return -1;
        var canvas = ChildAddress(PanelAddress, 0);
        if (canvas == 0) return -1;

        var current = hoveredElement;
        var childBelowCanvas = (nint)0;
        for (var depth = 0; depth < 24 && current != 0; depth++)
        {
            if (current == canvas)
            {
                if (childBelowCanvas == 0) return 0; // direct canvas background
                var count = AtlasCanvasChildCount();
                for (var i = 0; i < count; i++)
                    if (ChildAddress(canvas, i) == childBelowCanvas)
                        return i;
                return -1;
            }
            childBelowCanvas = current;
            if (!_reader.TryReadStruct<nint>(current + KnownOffsets.Element.Parent, out var parent)
                || parent == current)
                break;
            current = parent;
        }
        return -1;
    }

    public int AtlasCanvasChildCount()
    {
        if (!IsVisible) return 0;
        var canvas = ChildAddress(PanelAddress, 0);
        if (canvas == 0
            || !_reader.TryReadStruct<nint>(canvas + KnownOffsets.Element.Childs, out var begin)
            || !_reader.TryReadStruct<nint>(canvas + KnownOffsets.Element.Childs + 8, out var end)
            || begin == 0 || end < begin)
            return 0;
        var count = (int)((end - begin) / 8);
        return count is >= 0 and <= 1024 ? count : 0;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private nint ResolveByPath(nint root, params int[] indices)
    {
        var addr = root;
        foreach (var idx in indices)
        {
            addr = ChildAddress(addr, idx);
            if (addr == 0) return 0;
        }
        return addr;
    }

    private nint ChildAddress(nint parent, int index)
    {
        if (!_reader.TryReadStruct<nint>(parent + KnownOffsets.Element.Childs, out var begin) || begin == 0) return 0;
        if (!_reader.TryReadStruct<nint>(begin + index * 8, out var ptr)) return 0;
        return ptr;
    }

    private bool IsElementVisible(nint elementAddress)
        => BubblesBot.Core.Game.ElementReader.IsVisibleDeep(_reader, elementAddress);
}
