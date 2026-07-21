using BubblesBot.Bot.Modes;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Tests;

public sealed class BlightInventoryPolicyTests
{
    [Fact]
    public void Retains_only_the_fullest_portal_scroll_stack()
    {
        InventoryView.Item[] items =
        [
            Item(10, "Metadata/Items/Currency/CurrencyPortal", 8),
            Item(20, "Metadata/Items/Currency/CurrencyPortal", 40),
            Item(30, "Metadata/Items/Currency/CurrencyPortal", 17),
            Item(40, "Metadata/Items/Currency/CurrencyIdentification", 40),
        ];

        Assert.False(BlightInventoryPolicy.ShouldRetainForNextRun(items, items[0]));
        Assert.True(BlightInventoryPolicy.ShouldRetainForNextRun(items, items[1]));
        Assert.False(BlightInventoryPolicy.ShouldRetainForNextRun(items, items[2]));
        Assert.False(BlightInventoryPolicy.ShouldRetainForNextRun(items, items[3]));
    }

    [Fact]
    public void Equal_stacks_use_a_stable_entity_tie_break()
    {
        InventoryView.Item[] items =
        [
            Item(200, "Metadata/Items/Currency/CurrencyPortal", 40),
            Item(100, "Metadata/Items/Currency/CurrencyPortal", 40),
        ];

        Assert.False(BlightInventoryPolicy.ShouldRetainForNextRun(items, items[0]));
        Assert.True(BlightInventoryPolicy.ShouldRetainForNextRun(items, items[1]));
    }

    [Fact]
    public void Retains_exactly_one_carried_ravaged_map_for_device_retry()
    {
        InventoryView.Item[] items =
        [
            RavagedMap(200),
            RavagedMap(100),
            Item(300, "Metadata/Items/Currency/CurrencyIdentification", 20),
        ];

        Assert.False(BlightInventoryPolicy.ShouldRetainForNextRun(items, items[0]));
        Assert.True(BlightInventoryPolicy.ShouldRetainForNextRun(items, items[1]));
        Assert.False(BlightInventoryPolicy.ShouldRetainForNextRun(items, items[2]));
    }

    private static InventoryView.Item Item(long entity, string path, int stack)
        => new(0, (nint)entity, null, path, stack, 1, 1);

    private static InventoryView.Item RavagedMap(long entity)
        => new(0, (nint)entity, null, "Metadata/Items/Maps/MapKey", 1, 1, 1,
            [(InventoryView.UberBlightedMapStatId, 1)]);
}
