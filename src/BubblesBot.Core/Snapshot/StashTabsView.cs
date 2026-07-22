using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>Server-authoritative stash-tab names, types, and display indices.</summary>
public sealed class StashTabsView
{
    // Standard migrations can leave an account with hundreds of remove-only tabs.
    // Keep a corruption guard, but do not reject legitimate long-lived accounts.
    // Long-lived Standard accounts can expose thousands of server records because every
    // remove-only tab from past leagues remains in this catalog even though the UI shows only
    // a few hundred. Live capture 2026-07-21: 2,023 structurally valid 0x68-byte records while
    // StashElement reported 238 visible tabs. Keep a corruption guard, but above that reality.
    internal const int MaximumTabCount = 4096;

    public sealed record Tab(string Name, uint Type, int DisplayIndex);

    public IReadOnlyList<Tab> Tabs { get; }

    internal StashTabsView(IReadOnlyList<Tab> tabs) => Tabs = tabs;

    public static StashTabsView FromIngameData(MemoryReader reader, nint ingameData)
    {
        if (!reader.TryReadStruct<nint>(
                ingameData + KnownOffsets.IngameData.ServerData, out var server)
            || server == 0
            || !reader.TryReadStruct<StdVector>(
                server + KnownOffsets.ServerData.PlayerStashTabs, out var vector))
            return new StashTabsView([]);

        var size = KnownOffsets.ServerData.StashTabElementSize;
        var byteCount = vector.ByteCount;
        if (byteCount <= 0 || byteCount % size != 0)
            return new StashTabsView([]);
        var count = (int)(byteCount / size);
        if (!IsPlausibleCount(count))
            return new StashTabsView([]);

        var result = new List<Tab>(count);
        for (var i = 0; i < count; i++)
        {
            var address = vector.First + i * size;
            var name = NativeString.Read(reader, address + 0x08);
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!reader.TryReadStruct<uint>(address + 0x34, out var type)) continue;
            if (!reader.TryReadStruct<ushort>(address + 0x38, out var displayIndex)) continue;
            result.Add(new Tab(name, type, displayIndex));
        }
        return new StashTabsView(result);
    }

    internal static bool IsPlausibleCount(int count) =>
        count is >= 1 and <= MaximumTabCount;

    /// <summary>
    /// Select a named tab. For deposits, prefer a normal/premium/quad tab when duplicate names
    /// exist so a specialized affinity tab cannot reject generic loot.
    /// </summary>
    public Tab? Find(string name, bool requireGeneralPurpose)
    {
        var matches = Tabs.Where(tab => tab.Name.Equals(
            name, StringComparison.OrdinalIgnoreCase));
        if (requireGeneralPurpose)
            matches = matches.Where(tab => tab.Type is 0 or 1 or 7);
        return matches.OrderBy(tab => tab.DisplayIndex).FirstOrDefault();
    }

    /// <summary>
    /// Resolve the selected display index only when that concrete server record matches the
    /// requested identity. Standard migrations commonly leave hidden/remove-only duplicates,
    /// so selecting the lowest same-named index is not equivalent to selecting a visible tab.
    /// </summary>
    public Tab? FindSelected(string name, bool requireGeneralPurpose, int displayIndex)
    {
        var match = Tabs.FirstOrDefault(tab => tab.DisplayIndex == displayIndex
            && tab.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (match is null) return null;
        return !requireGeneralPurpose || match.Type is 0 or 1 or 7 ? match : null;
    }
}
