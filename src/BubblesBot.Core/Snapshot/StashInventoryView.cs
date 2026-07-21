using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Visible stash-tab item widgets. Stash contents are exposed through a different server
/// inventory container than the player inventory, but the rendered item widgets retain the
/// same entity pointer, dimensions, and clickable element rectangle.
/// </summary>
public sealed class StashInventoryView
{
    public readonly record struct Item(
        nint ElementAddress,
        nint ItemEntity,
        ElementGeometry.Rect? Rect,
        string Path,
        int StackSize,
        int Width,
        int Height,
        IReadOnlyList<(int Id, int Value)>? Stats = null,
        string BaseName = "",
        EntityListReader.EntityRarity Rarity = EntityListReader.EntityRarity.Normal,
        int Quality = 0);

    public bool IsOpen { get; }
    public int VisibleTabIndex { get; }
    public int TotalTabs { get; }
    public IReadOnlyList<Item> Items { get; }

    private StashInventoryView(
        bool isOpen, int visibleTabIndex, int totalTabs, IReadOnlyList<Item> items)
    {
        IsOpen = isOpen;
        VisibleTabIndex = visibleTabIndex;
        TotalTabs = totalTabs;
        Items = items;
    }

    public static StashInventoryView FromIngameUi(
        MemoryReader reader, nint ingameStateAddress, int maxElements = 5_000)
    {
        if (!reader.TryReadStruct<nint>(
                ingameStateAddress + KnownOffsets.IngameState.IngameUi, out var ui)
            || ui == 0
            || !reader.TryReadStruct<nint>(
                ui + KnownOffsets.IngameUiElements.StashElement, out var stash)
            || stash == 0
            || !ElementReader.IsVisibleDeep(reader, stash))
            return new StashInventoryView(false, -1, 0, []);

        if (!StashReader.TryGetVisibleStash(
                reader, stash, out var visible, out var index, out var total))
            return new StashInventoryView(true, -1, 0, []);

        var items = new List<Item>();
        var visited = new HashSet<nint>();
        var queue = new Queue<(nint Address, int Depth)>();
        queue.Enqueue((visible, 0));
        while (queue.Count > 0 && visited.Count < maxElements)
        {
            var (address, depth) = queue.Dequeue();
            if (!visited.Add(address)) continue;
            var element = ElementReader.TryReadSnapshot(reader, address, 2_000);
            if (element is null) continue;

            if (reader.TryReadStruct<nint>(
                    address + KnownOffsets.NormalInventoryItem.Item, out var entity)
                && reader.TryReadStruct<int>(
                    address + KnownOffsets.NormalInventoryItem.Width, out var width)
                && reader.TryReadStruct<int>(
                    address + KnownOffsets.NormalInventoryItem.Height, out var height)
                && LooksLikeUserAddress(entity)
                && ElementReader.IsVisibleDeep(reader, address)
                && width is > 0 and <= 24
                && height is > 0 and <= 24)
            {
                var path = EntityListReader.ReadEntityPath(reader, entity) ?? string.Empty;
                if (path.StartsWith("Metadata/Items/", StringComparison.Ordinal))
                {
                    var (baseName, rarity, quality) = InventoryView.ReadItemIdentity(reader, entity);
                    items.Add(new Item(
                        address,
                        entity,
                        ElementGeometry.TryReadRect(reader, address),
                        path,
                        ReadStackSize(reader, entity),
                        width,
                        height,
                        path.Contains(InventoryView.MapPathFragment, StringComparison.OrdinalIgnoreCase)
                            ? ItemStatsReader.Read(reader, entity)
                            : null,
                        baseName, rarity, quality));
                    // Descendants are stack-count text and decoration, not another item.
                    continue;
                }
            }

            // Special stash tabs add a few wrapper layers around their item widgets. Keep
            // the traversal bounded but deep enough for Delirium/fragment/currency layouts.
            if (depth >= 8) continue;
            foreach (var child in element.Children)
                queue.Enqueue((child, depth + 1));
        }

        return new StashInventoryView(true, index, total, items);
    }

    private static int ReadStackSize(MemoryReader reader, nint entity)
    {
        var components = EntityComponents.ReadComponentMap(reader, entity);
        if (components.TryGetValue("Stack", out var stack)
            && reader.TryReadStruct<int>(
                stack + KnownOffsets.StackComponent.CurrentCount, out var count)
            && count is > 0 and < 100_000)
            return count;
        return 1;
    }

    private static bool LooksLikeUserAddress(nint address)
    {
        var value = (long)address;
        return value > 0x10000 && value < 0x7FFF_FFFF_FFFF;
    }

    /// <summary>
    /// Positive subtype check for a Blight-ravaged map in the visible stash tab. Normal,
    /// Blighted, and Blight-ravaged maps share the MapKey metadata path, so path matching
    /// alone is intentionally insufficient for automated supply withdrawal.
    /// </summary>
    public static bool IsBlightRavagedMap(in Item item)
        => item.Path.Contains(InventoryView.MapPathFragment, StringComparison.OrdinalIgnoreCase)
        && item.Stats is not null
        && item.Stats.Any(stat => stat.Id == InventoryView.UberBlightedMapStatId && stat.Value > 0);

    public static bool IsNormalUnqualifiedMap(in Item item, string targetMapName)
    {
        var inventoryShape = new InventoryView.Item(
            item.ElementAddress, item.ItemEntity, item.Rect, item.Path, item.StackSize,
            item.Width, item.Height, item.Stats, item.BaseName, item.Rarity, item.Quality);
        return InventoryView.IsNormalUnqualifiedMap(inventoryShape, targetMapName);
    }

    public static bool IsNormalTierMap(in Item item, int tier)
    {
        var inventoryShape = new InventoryView.Item(
            item.ElementAddress, item.ItemEntity, item.Rect, item.Path, item.StackSize,
            item.Width, item.Height, item.Stats, item.BaseName, item.Rarity, item.Quality);
        return InventoryView.IsNormalTierMap(inventoryShape, tier);
    }
}
