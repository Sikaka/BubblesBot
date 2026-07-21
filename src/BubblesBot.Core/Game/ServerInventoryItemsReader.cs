namespace BubblesBot.Core.Game;

/// <summary>Bounded native hash-map reader for item entities held by a ServerInventory.</summary>
public static class ServerInventoryItemsReader
{
    public sealed record Item(nint SlotItemAddress, nint EntityAddress, int MinX, int MinY, int MaxX, int MaxY);

    public static IReadOnlyList<Item> Read(MemoryReader reader, nint inventory, int maximumNodes = 256)
    {
        if (!LooksLikeUserAddress(inventory)
            || !reader.TryReadStruct<nint>(inventory + KnownOffsets.ServerInventory.InventorySlotItemsHash, out var sentinel)
            || !LooksLikeUserAddress(sentinel)
            || !reader.TryReadStruct<nint>(sentinel + KnownOffsets.ServerInventoryHashNode.Root, out var root)
            || !LooksLikeUserAddress(root))
            return [];

        var result = new List<Item>();
        var pending = new Stack<nint>();
        var seen = new HashSet<nint>();
        pending.Push(root);
        while (pending.Count > 0 && seen.Count < maximumNodes)
        {
            var node = pending.Pop();
            if (!LooksLikeUserAddress(node) || !seen.Add(node)) continue;
            reader.TryReadStruct<byte>(node + KnownOffsets.ServerInventoryHashNode.IsNull, out var isNull);
            reader.TryReadStruct<nint>(node + KnownOffsets.ServerInventoryHashNode.Previous, out var previous);
            reader.TryReadStruct<nint>(node + KnownOffsets.ServerInventoryHashNode.Next, out var next);
            if (isNull == 0
                && reader.TryReadStruct<nint>(node + KnownOffsets.ServerInventoryHashNode.Value, out var slot)
                && TryReadSlot(reader, slot, out var item))
                result.Add(item);
            if (LooksLikeUserAddress(previous)) pending.Push(previous);
            if (LooksLikeUserAddress(next)) pending.Push(next);
        }
        return result.GroupBy(x => x.SlotItemAddress).Select(x => x.First()).ToArray();
    }

    private static bool TryReadSlot(MemoryReader reader, nint slot, out Item item)
    {
        item = default!;
        if (!LooksLikeUserAddress(slot)
            || !reader.TryReadStruct<nint>(slot + KnownOffsets.ServerInventorySlotItem.Entity, out var entity)
            || !LooksLikeUserAddress(entity)
            || string.IsNullOrWhiteSpace(EntityListReader.ReadEntityPath(reader, entity)))
            return false;
        reader.TryReadStruct<int>(slot + KnownOffsets.ServerInventorySlotItem.MinX, out var minX);
        reader.TryReadStruct<int>(slot + KnownOffsets.ServerInventorySlotItem.MinY, out var minY);
        reader.TryReadStruct<int>(slot + KnownOffsets.ServerInventorySlotItem.MaxX, out var maxX);
        reader.TryReadStruct<int>(slot + KnownOffsets.ServerInventorySlotItem.MaxY, out var maxY);
        if (minX is < 0 or > 24 || minY is < 0 or > 24 || maxX < minX || maxX > 24 || maxY < minY || maxY > 24)
            return false;
        item = new Item(slot, entity, minX, minY, maxX, maxY);
        return true;
    }

    private static bool LooksLikeUserAddress(nint address)
        => (long)address is > 0x10000 and < 0x7FFF_FFFF_FFFF;
}
