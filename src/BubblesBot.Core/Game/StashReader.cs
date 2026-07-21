namespace BubblesBot.Core.Game;

public static class StashReader
{
    private static readonly int[] StashInventoryPanelPath = { 2, 0, 0, 1, 1 };
    internal const int MaximumTabElements = 1000;

    public static bool TryGetStashInventoryPanel(MemoryReader reader, nint stashElement, out nint panel)
    {
        panel = stashElement;
        foreach (var index in StashInventoryPanelPath)
        {
            if (!ElementReader.TryGetChild(reader, panel, index, out panel))
                return false;
        }

        return true;
    }

    public static bool TryGetVisibleStash(MemoryReader reader, nint stashElement, out nint visibleStash, out int visibleIndex, out int totalStashes)
    {
        visibleStash = 0;
        visibleIndex = -1;
        totalStashes = 0;
        if (!TryGetStashInventoryPanel(reader, stashElement, out var panel))
            return false;

        // Standard migrations can produce hundreds of remove-only tabs. The element reader
        // rejects a node when its child count exceeds this bound, so this must match the
        // server-tab plausibility guard instead of the old 200-tab assumption.
        var snapshot = ElementReader.TryReadSnapshot(reader, panel, MaximumTabElements);
        if (snapshot is null)
            return false;

        totalStashes = snapshot.Children.Count;
        var fallbackIndex = -1;
        var fallbackAddress = (nint)0;
        for (var i = 0; i < snapshot.Children.Count; i++)
        {
            var child = ElementReader.TryReadSnapshot(reader, snapshot.Children[i], 10);
            if (child is null || child.Children.Count == 0)
                continue;
            fallbackIndex = fallbackIndex < 0 ? i : fallbackIndex;
            fallbackAddress = fallbackAddress == 0 ? child.Children[0] : fallbackAddress;
            if (!child.IsVisibleLocal)
                continue;
            visibleIndex = i;
            visibleStash = child.Children[0];
            return true;
        }

        if (fallbackIndex < 0)
            return false;
        visibleIndex = fallbackIndex;
        visibleStash = fallbackAddress;
        return true;
    }
}
