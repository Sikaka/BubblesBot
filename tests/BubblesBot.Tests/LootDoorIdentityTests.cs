using BubblesBot.Bot.Behaviors.Loot;
using BubblesBot.Core.Snapshot;

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

    [Theory]
    [InlineData(true, DoorBlockageState.Closed, true)]
    [InlineData(true, DoorBlockageState.Open, false)]
    [InlineData(true, DoorBlockageState.Unknown, false)]
    [InlineData(false, DoorBlockageState.Closed, false)]
    public void ManualLootClicksOnlyPositivelyClosedDoors(
        bool identity, DoorBlockageState state, bool expected)
    {
        Assert.Equal(expected, LootClosestVisible.IsActionableDoor(identity, state));
    }
}
