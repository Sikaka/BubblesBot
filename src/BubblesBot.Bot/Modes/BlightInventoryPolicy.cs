using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Modes;

/// <summary>Inventory retention rules for the Blight hideout deposit leg.</summary>
public static class BlightInventoryPolicy
{
    /// <summary>
    /// Keep exactly one Portal Scroll stack for emergency/manual portal use. The fullest
    /// stack wins; entity address provides a stable tie-break so every evaluation in one
    /// deposit pass selects the same stack. All additional scroll stacks are deposited.
    /// </summary>
    public static bool ShouldRetainForNextRun(
        IReadOnlyList<InventoryView.Item> inventory,
        in InventoryView.Item candidate)
    {
        // A map can still be carried when a device click is intercepted by an old portal or
        // the process restarts between withdrawal and activation. Preserve exactly one so
        // the supply preflight reuses it instead of dumping it and withdrawing a duplicate.
        if (InventoryView.IsBlightRavagedMap(candidate))
        {
            InventoryView.Item? retainedMap = null;
            foreach (var item in inventory)
            {
                if (!InventoryView.IsBlightRavagedMap(item)) continue;
                if (retainedMap is null || (long)item.ItemEntity < (long)retainedMap.Value.ItemEntity)
                    retainedMap = item;
            }
            return retainedMap is { } map && map.ItemEntity == candidate.ItemEntity;
        }

        if (!IsPortalScroll(candidate)) return false;

        InventoryView.Item? retained = null;
        foreach (var item in inventory)
        {
            if (!IsPortalScroll(item)) continue;
            if (retained is null
                || item.StackSize > retained.Value.StackSize
                || item.StackSize == retained.Value.StackSize
                    && (long)item.ItemEntity < (long)retained.Value.ItemEntity)
                retained = item;
        }
        return retained is { } keep && keep.ItemEntity == candidate.ItemEntity;
    }

    private static bool IsPortalScroll(in InventoryView.Item item)
        => item.Path.Contains(
            InventoryView.PortalScrollPathFragment,
            StringComparison.OrdinalIgnoreCase);
}
