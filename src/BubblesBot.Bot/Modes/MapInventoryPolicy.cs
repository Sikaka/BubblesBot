using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Modes;

/// <summary>Inventory retention for repeat map-farming hideout preflight.</summary>
public static class MapInventoryPolicy
{
    public static bool ShouldRetainForNextRun(
        IReadOnlyList<InventoryView.Item> inventory,
        in InventoryView.Item candidate,
        Strategies.FarmingStrategy? strategy = null)
    {
        if (StackedDeckPolicy.IsCloisterScarab(candidate.Path)) return true;
        var candidatePath = candidate.Path;
        if (strategy?.Supply.Scarabs.Any(line =>
                line.CountPerMap > 0
                && !string.IsNullOrWhiteSpace(line.PathFragment)
                && candidatePath.Contains(line.PathFragment, StringComparison.OrdinalIgnoreCase)) == true)
            return true;
        // Every carried map is supply in this mode. The device still consumes only the exact
        // target identity, but stash cleanup must never move the user's queued maps away just
        // because a patch drifted one optional identity field.
        if (strategy?.Supply.Map.Source == Strategies.MapSource.PlayerInventory
            && InventoryView.IsMap(candidate))
            return true;
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
