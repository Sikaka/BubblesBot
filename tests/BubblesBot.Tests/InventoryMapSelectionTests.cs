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

    // Live capture 2026-07-23: regular Blighted maps carry {10187,10390,10476} without the uber
    // stat; Blight-ravaged adds 14927; non-Blight maps carry none of the three.
    [Fact]
    public void RegularBlightedMapIsABlightMapButNotRavaged()
    {
        var item = new InventoryView.Item(
            0, 0, null, "Metadata/Items/Maps/MapKeyTier16", 1, 1, 1,
            [(10187, 1), (10390, 1), (10476, 1)]);

        Assert.True(InventoryView.IsBlightMap(item));
        Assert.False(InventoryView.IsBlightRavagedMap(item));
    }

    [Fact]
    public void RavagedMapIsAlsoABlightMap()
    {
        var item = new InventoryView.Item(
            0, 0, null, "Metadata/Items/Maps/MapKeyTier15", 1, 1, 1,
            [(10187, 1), (10390, 1), (10476, 9), (InventoryView.UberBlightedMapStatId, 1)]);

        Assert.True(InventoryView.IsBlightMap(item));
        Assert.True(InventoryView.IsBlightRavagedMap(item));
    }

    [Fact]
    public void NonBlightMapsAreNotBlightMaps()
    {
        // Plain white map (no stats) and a fully-rolled rare that shares none of the markers.
        var plain = new InventoryView.Item(0, 0, null, "Metadata/Items/Maps/MapKeyTier16", 1, 1, 1);
        var rolledRare = new InventoryView.Item(
            0, 0, null, "Metadata/Items/Maps/MapKeyTier16", 1, 1, 1,
            [(991, 1), (1026, 38), (1041, 98), (1246, 60), (15645, 70)]);

        Assert.False(InventoryView.IsBlightMap(plain));
        Assert.False(InventoryView.IsBlightMap(rolledRare));
    }

    [Fact]
    public void StashBlightMapAcceptsBothRegularAndRavaged()
    {
        var blighted = new StashInventoryView.Item(
            0, 0, null, "Metadata/Items/Maps/MapKeyTier16", 1, 1, 1,
            [(10187, 1), (10390, 1), (10476, 1)]);
        var ravaged = new StashInventoryView.Item(
            0, 0, null, "Metadata/Items/Maps/MapKeyTier15", 1, 1, 1,
            [(10476, 9), (InventoryView.UberBlightedMapStatId, 1)]);
        var plain = new StashInventoryView.Item(
            0, 0, null, "Metadata/Items/Maps/MapKeyTier16", 1, 1, 1);

        Assert.True(StashInventoryView.IsBlightMap(blighted));
        Assert.True(StashInventoryView.IsBlightMap(ravaged));
        Assert.False(StashInventoryView.IsBlightMap(plain));
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
