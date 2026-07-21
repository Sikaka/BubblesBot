using BubblesBot.Bot.Modes;
using BubblesBot.Bot.Strategies;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Tests;

public sealed class MapInventoryPolicyTests
{
    [Fact]
    public void RetainsOnePortalStackAndCloisterSuppliesButDepositsMapsAndLoot()
    {
        InventoryView.Item[] items =
        [
            Item(10, "Metadata/Items/Currency/CurrencyPortal", 8),
            Item(20, "Metadata/Items/Currency/CurrencyPortal", 40),
            Item(30, "Metadata/Items/Scarabs/ScarabDivinationCardsNew1", 17),
            Item(40, "Metadata/Items/Maps/MapKeyTier16", 1),
            Item(50, "Metadata/Items/Currency/CurrencyRerollRare", 5),
        ];

        Assert.False(MapInventoryPolicy.ShouldRetainForNextRun(items, items[0]));
        Assert.True(MapInventoryPolicy.ShouldRetainForNextRun(items, items[1]));
        Assert.True(MapInventoryPolicy.ShouldRetainForNextRun(items, items[2]));
        Assert.False(MapInventoryPolicy.ShouldRetainForNextRun(items, items[3]));
        Assert.False(MapInventoryPolicy.ShouldRetainForNextRun(items, items[4]));
    }

    [Fact]
    public void CarriedMapStrategyRetainsEveryMapWhileDeviceSelectionStaysExact()
    {
        var strategy = new FarmingStrategy
        {
            Supply = new SupplySection
            {
                Map = new MapSupply { Source = MapSource.PlayerInventory, TargetMapName = "Jungle Valley" }
            }
        };
        InventoryView.Item[] items =
        [
            new(0, 10, null, "Metadata/Items/Maps/MapKeyTier16", 1, 1, 1,
                BaseName: "Jungle Valley Map"),
            new(0, 20, null, "Metadata/Items/Maps/MapKeyTier16", 1, 1, 1,
                BaseName: "Jungle Valley Map", Quality: 20),
            new(0, 30, null, "Metadata/Items/Maps/MapKeyTier16", 1, 1, 1,
                BaseName: "Mesa Map"),
        ];

        Assert.True(MapInventoryPolicy.ShouldRetainForNextRun(items, items[0], strategy));
        Assert.True(MapInventoryPolicy.ShouldRetainForNextRun(items, items[1], strategy));
        Assert.True(MapInventoryPolicy.ShouldRetainForNextRun(items, items[2], strategy));
    }

    private static InventoryView.Item Item(long entity, string path, int stack)
        => new(0, (nint)entity, null, path, stack, 1, 1);
}
