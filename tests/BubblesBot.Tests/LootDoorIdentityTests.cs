using BubblesBot.Bot.Behaviors.Loot;

namespace BubblesBot.Tests;

public sealed class LootDoorIdentityTests
{
    [Theory]
    [InlineData("Metadata/Chests/Map/ForgeGate", "Door", true)]
    [InlineData("Metadata/MiscellaneousObjects/Labyrinth/Door", "", true)]
    [InlineData("Metadata/MiscellaneousObjects/AreaTransition", "Forge of the Phoenix", false)]
    [InlineData("Metadata/MiscellaneousObjects/MultiplexPortal", "Doorway", false)]
    public void GenericVisibleDoorNameCoversMapGatesWithoutMatchingPortals(
        string path, string displayName, bool expected)
    {
        Assert.Equal(expected, LootClosestVisible.IsDoorIdentity(path, displayName));
    }
}
