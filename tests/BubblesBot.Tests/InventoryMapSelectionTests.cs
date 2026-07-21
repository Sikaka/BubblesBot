using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Tests;

public sealed class InventoryMapSelectionTests
{
    [Theory]
    [InlineData("Metadata/Items/Maps/MapKeyTier16", true)]
    [InlineData("metadata/items/maps/mapkeytier16", true)]
    [InlineData("Metadata/Items/Currency/CurrencyAfflictionFragment", false)]
    [InlineData("Metadata/Items/Currency/CurrencyPortal", false)]
    public void MapKeyMetadataIsRecognized(string path, bool expected)
    {
        var item = new InventoryView.Item(0, 0, null, path, 1, 1, 1);

        Assert.Equal(expected, InventoryView.IsMap(item));
    }

    [Fact]
    public void MapsAreDepositedByTheGenericLootPolicy()
    {
        var item = new InventoryView.Item(
            0, 0, null, "Metadata/Items/Maps/MapKeyTier16", 1, 1, 1);

        Assert.False(InventoryView.IsRetainedSupply(item));
    }

    [Theory]
    [InlineData("Metadata/Items/Currency/CurrencyPortal", true)]
    [InlineData("Metadata/Items/Currency/CurrencyAfflictionFragment", false)]
    [InlineData("Metadata/Items/Maps/MapKeyTier16", false)]
    public void Generic_deposit_only_retains_portal_scrolls(string path, bool expected)
    {
        var item = new InventoryView.Item(0, 0, null, path, 1, 1, 1);

        Assert.Equal(expected, InventoryView.IsRetainedSupply(item));
    }

    [Fact]
    public void UberBlightedStatPositivelyIdentifiesBlightRavagedMap()
    {
        var item = new InventoryView.Item(
            0, 0, null, "Metadata/Items/Maps/MapKeyTier16", 1, 1, 1,
            [(InventoryView.UberBlightedMapStatId, 1)]);

        Assert.True(InventoryView.IsBlightRavagedMap(item));
    }

    [Fact]
    public void StashItemUsesSamePositiveBlightRavagedIdentity()
    {
        var item = new StashInventoryView.Item(
            0, 0, null, "Metadata/Items/Maps/MapKeyTier16", 1, 1, 1,
            [(InventoryView.UberBlightedMapStatId, 1)]);

        Assert.True(StashInventoryView.IsBlightRavagedMap(item));
    }

    [Fact]
    public void StashMapWithoutSubtypeStatIsRejected()
    {
        var item = new StashInventoryView.Item(
            0, 0, null, "Metadata/Items/Maps/MapKeyTier16", 1, 1, 1);

        Assert.False(StashInventoryView.IsBlightRavagedMap(item));
    }

    [Theory]
    [InlineData("Metadata/Items/Maps/MapKeyTier16", 0)]
    [InlineData("Metadata/Items/Maps/MapKeyTier16", -1)]
    [InlineData("Metadata/Items/Currency/CurrencyPortal", 1)]
    public void MissingFalseOrNonMapStatIsRejected(string path, int value)
    {
        var item = new InventoryView.Item(
            0, 0, null, path, 1, 1, 1,
            [(InventoryView.UberBlightedMapStatId, value)]);

        Assert.False(InventoryView.IsBlightRavagedMap(item));
    }

    [Fact]
    public void ExactWhiteZeroQualityMapIsEligibleForCarriedMapping()
    {
        var item = new InventoryView.Item(
            0, 0, null, "Metadata/Items/Maps/MapKeyTier16", 1, 1, 1,
            BaseName: "Jungle Valley Map");

        Assert.True(InventoryView.IsNormalUnqualifiedMap(item, "Jungle Valley"));
    }

    [Theory]
    [InlineData(EntityListReader.EntityRarity.Magic, 0)]
    [InlineData(EntityListReader.EntityRarity.Rare, 20)]
    public void RolledMapIsRejectedForWhiteMapStrategy(
        EntityListReader.EntityRarity rarity, int quality)
    {
        var item = new InventoryView.Item(
            0, 0, null, "Metadata/Items/Maps/MapKeyTier16", 1, 1, 1,
            BaseName: "Jungle Valley Map", Rarity: rarity, Quality: quality);

        Assert.False(InventoryView.IsNormalUnqualifiedMap(item, "Jungle Valley"));
    }

    [Fact]
    public void QualityDoesNotMakeAnOtherwiseWhiteMapRolled()
    {
        var item = new InventoryView.Item(
            0, 0, null, "Metadata/Items/Maps/MapKeyTier16", 1, 1, 1,
            BaseName: "Jungle Valley Map", Quality: 20);

        Assert.True(InventoryView.IsNormalUnqualifiedMap(item, "Jungle Valley"));
    }

    [Fact]
    public void GenericNormalT16KeyIsValidWhenAtlasNodeOwnsMapIdentity()
    {
        var item = new InventoryView.Item(
            0, 0, null, "Metadata/Items/Maps/MapKeyTier16", 1, 1, 1,
            BaseName: "Map (Tier 16)");

        Assert.True(InventoryView.IsNormalTierMap(item, 16));
        Assert.False(InventoryView.IsNormalTierMap(item, 15));
    }
}
