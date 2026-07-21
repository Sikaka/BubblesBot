namespace BubblesBot.Core.Game;

public static class InventoryReader
{
    public sealed record InventorySnapshot(
        nint Address,
        long ItemCount,
        Vector2i Size,
        IReadOnlyList<VisibleInventoryItemSnapshot> VisibleItems);

    public sealed record VisibleInventoryItemSnapshot(
        nint Address,
        nint ItemEntity,
        int Width,
        int Height);

    public static bool TryGetPlayerInventory(MemoryReader reader, nint inventoryPanel, out nint inventory)
    {
        inventory = 0;
        if (!LooksLikeUserAddress(inventoryPanel))
            return false;
        var list = inventoryPanel + KnownOffsets.InventoryElement.AllInventories;
        return reader.TryReadStruct(list + KnownOffsets.InventoryElement.PlayerInventoryIndex * 8, out inventory)
            && LooksLikeUserAddress(inventory);
    }

    public static InventorySnapshot? TryReadInventory(MemoryReader reader, nint inventoryAddress, int maxVisibleItems = 200)
    {
        if (!LooksLikeUserAddress(inventoryAddress))
            return null;
        if (!reader.TryReadStruct<long>(inventoryAddress + KnownOffsets.Inventory.ItemCount, out var itemCount))
            return null;
        if (!reader.TryReadStruct<Vector2i>(inventoryAddress + KnownOffsets.Inventory.InventorySize, out var size))
            return null;

        var visible = new List<VisibleInventoryItemSnapshot>();
        var element = ElementReader.TryReadSnapshot(reader, inventoryAddress, maxVisibleItems + 10);
        if (element is not null)
        {
            foreach (var child in element.Children)
            {
                if (visible.Count >= maxVisibleItems)
                    break;
                // No children-count gate here: only stacked/decorated items (stack text, charge
                // overlays) have child elements — plain 1x1 items like maps have none. The
                // entity pointer + size checks below are the real item filter.
                if (!reader.TryReadStruct<nint>(child + KnownOffsets.NormalInventoryItem.Item, out var item)
                    || !reader.TryReadStruct<int>(child + KnownOffsets.NormalInventoryItem.Width, out var width)
                    || !reader.TryReadStruct<int>(child + KnownOffsets.NormalInventoryItem.Height, out var height))
                    continue;
                if (LooksLikeUserAddress(item) && width > 0 && width <= 24 && height > 0 && height <= 24)
                    visible.Add(new VisibleInventoryItemSnapshot(child, item, width, height));
            }
        }

        return new InventorySnapshot(inventoryAddress, itemCount, size, visible);
    }

    private static bool LooksLikeUserAddress(nint p)
    {
        var v = (long)p;
        return v > 0x10000 && v < 0x7FFF_FFFF_FFFF;
    }
}
