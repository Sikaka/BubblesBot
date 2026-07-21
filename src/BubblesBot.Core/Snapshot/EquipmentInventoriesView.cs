using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Read-only discovery view over the inventory panel's inline inventory array and the
/// corresponding server-side PlayerInventories holders. Array/slot meanings are deliberately
/// not baked in here: live research must correlate index, geometry and holder transitions first.
/// </summary>
public sealed class EquipmentInventoriesView
{
    public readonly record struct UiInventory(
        int Index,
        nint Address,
        bool IsVisible,
        ElementGeometry.Rect? Rect,
        long ItemCount,
        Vector2i Size,
        IReadOnlyList<InventoryView.Item> Items);

    public readonly record struct ServerInventory(
        int HolderIndex,
        int HolderId,
        nint Address,
        int InventoryType,
        int InventorySlot,
        int Columns,
        int Rows,
        long ItemCount);

    public bool InventoryPanelOpen { get; }
    public IReadOnlyList<UiInventory> UiInventories { get; }
    public IReadOnlyList<ServerInventory> ServerInventories { get; }

    private EquipmentInventoriesView(
        bool inventoryPanelOpen,
        IReadOnlyList<UiInventory> uiInventories,
        IReadOnlyList<ServerInventory> serverInventories)
    {
        InventoryPanelOpen = inventoryPanelOpen;
        UiInventories = uiInventories;
        ServerInventories = serverInventories;
    }

    public static EquipmentInventoriesView From(GameSnapshot snapshot)
    {
        var reader = snapshot.Reader;
        var uiInventories = new List<UiInventory>();
        var panelOpen = false;

        if (reader.TryReadStruct<nint>(snapshot.IngameStateAddress + KnownOffsets.IngameState.IngameUi, out var ingameUi)
            && LooksLikeUserAddress(ingameUi)
            && reader.TryReadStruct<nint>(ingameUi + KnownOffsets.IngameUiElements.InventoryPanel, out var panel)
            && LooksLikeUserAddress(panel))
        {
            panelOpen = ElementReader.IsVisibleDeep(reader, panel);
            var seen = new HashSet<nint>();
            var first = panel + KnownOffsets.InventoryElement.AllInventories;
            // The validated player-inventory member is index 19. Scan a bounded diagnostic
            // envelope around it; reject anything that does not parse as a plausible inventory.
            for (var index = 0; index < 32; index++)
            {
                if (!reader.TryReadStruct<nint>(first + index * 8, out var address)
                    || !LooksLikeUserAddress(address)
                    || !seen.Add(address))
                    continue;
                var inventory = InventoryReader.TryReadInventory(reader, address);
                if (inventory is null || inventory.ItemCount is < 0 or > 200
                    || inventory.Size.X is < 0 or > 24 || inventory.Size.Y is < 0 or > 24)
                    continue;

                var items = inventory.VisibleItems.Select(item => ToItem(reader, item)).ToArray();
                uiInventories.Add(new UiInventory(
                    index, address, ElementReader.IsVisibleDeep(reader, address),
                    ElementGeometry.TryReadRect(reader, address), inventory.ItemCount,
                    inventory.Size, items));
            }
        }

        var serverInventories = ReadServerInventories(snapshot);
        return new EquipmentInventoriesView(panelOpen, uiInventories, serverInventories);
    }

    private static IReadOnlyList<ServerInventory> ReadServerInventories(GameSnapshot snapshot)
    {
        var reader = snapshot.Reader;
        if (!reader.TryReadStruct<nint>(snapshot.IngameDataAddress + KnownOffsets.IngameData.ServerData, out var serverData)
            || !LooksLikeUserAddress(serverData)
            || !reader.TryReadStruct<StdVector>(serverData + KnownOffsets.ServerData.PlayerInventories, out var vector))
            return Array.Empty<ServerInventory>();

        var byteCount = vector.ByteCount;
        if (!LooksLikeUserAddress(vector.First) || byteCount < 0
            || byteCount % KnownOffsets.InventoryHolder.Size != 0)
            return Array.Empty<ServerInventory>();
        var count = byteCount / KnownOffsets.InventoryHolder.Size;
        if (count is < 1 or > 128)
            return Array.Empty<ServerInventory>();

        var result = new List<ServerInventory>((int)count);
        for (var index = 0; index < count; index++)
        {
            var holder = vector.First + index * KnownOffsets.InventoryHolder.Size;
            if (!reader.TryReadStruct<int>(holder + KnownOffsets.InventoryHolder.Id, out var id)
                || !reader.TryReadStruct<nint>(holder + KnownOffsets.InventoryHolder.InventoryPtr, out var inventory)
                || !LooksLikeUserAddress(inventory))
                continue;
            reader.TryReadStruct<int>(inventory + KnownOffsets.ServerInventory.InventType, out var type);
            reader.TryReadStruct<int>(inventory + KnownOffsets.ServerInventory.InventSlot, out var slot);
            reader.TryReadStruct<int>(inventory + KnownOffsets.ServerInventory.Columns, out var columns);
            reader.TryReadStruct<int>(inventory + KnownOffsets.ServerInventory.Rows, out var rows);
            reader.TryReadStruct<long>(inventory + KnownOffsets.ServerInventory.ItemCount, out var itemCount);
            if (columns is < 0 or > 24 || rows is < 0 or > 24 || itemCount is < 0 or > 200)
                continue;
            result.Add(new ServerInventory(index, id, inventory, type, slot, columns, rows, itemCount));
        }
        return result;
    }

    private static InventoryView.Item ToItem(
        MemoryReader reader,
        InventoryReader.VisibleInventoryItemSnapshot item)
        => new(
            item.Address,
            item.ItemEntity,
            ElementGeometry.TryReadRect(reader, item.Address),
            EntityListReader.ReadEntityPath(reader, item.ItemEntity) ?? string.Empty,
            ReadStackSize(reader, item.ItemEntity),
            item.Width,
            item.Height,
            ItemStatsReader.Read(reader, item.ItemEntity));

    private static int ReadStackSize(MemoryReader reader, nint entity)
    {
        var components = EntityComponents.ReadComponentMap(reader, entity);
        return components.TryGetValue("Stack", out var stack)
            && reader.TryReadStruct<int>(stack + KnownOffsets.StackComponent.CurrentCount, out var count)
            && count is > 0 and < 100_000
                ? count
                : 1;
    }

    private static bool LooksLikeUserAddress(nint address)
        => (long)address is > 0x10000 and < 0x7FFF_FFFF_FFFF;
}
