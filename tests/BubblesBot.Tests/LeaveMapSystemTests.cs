using BubblesBot.Bot.Systems;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Tests;

public sealed class LeaveMapSystemTests
{
    [Fact]
    public void InventoryFallbackChoosesLargestVisiblePortalScrollStack()
    {
        var rect = new ElementGeometry.Rect(10, 20, 30, 40);
        InventoryView.Item[] items =
        [
            new(1, 11, rect, "Metadata/Items/Currency/CurrencyPortal", 8, 1, 1),
            new(2, 22, rect, "Metadata/Items/Currency/CurrencyPortal", 22, 1, 1),
            new(3, 33, rect, "Metadata/Items/Currency/CurrencyIdentification", 40, 1, 1),
        ];

        var selected = LeaveMapSystem.BestPortalScroll(items);
        Assert.NotNull(selected);
        Assert.Equal((nint)22, selected.Value.ItemEntity);
    }
}
