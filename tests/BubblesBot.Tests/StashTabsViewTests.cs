using BubblesBot.Core.Snapshot;

namespace BubblesBot.Tests;

public sealed class StashTabsViewTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(273, true)]
    [InlineData(1000, true)]
    [InlineData(2023, true)]
    [InlineData(4096, true)]
    [InlineData(0, false)]
    [InlineData(4097, false)]
    public void PlausibleCount_AllowsLargeStandardStashes(int count, bool expected)
    {
        Assert.Equal(expected, StashTabsView.IsPlausibleCount(count));
    }

    [Fact]
    public void Find_DepositPrefersGeneralPurposeDuplicate()
    {
        var tabs = new StashTabsView(
        [
            new StashTabsView.Tab("Dump", Type: 16, DisplayIndex: 17),
            new StashTabsView.Tab("Dump", Type: 7, DisplayIndex: 0),
            new StashTabsView.Tab("Deli", Type: 15, DisplayIndex: 8),
        ]);

        var dump = tabs.Find("dump", requireGeneralPurpose: true);

        Assert.NotNull(dump);
        Assert.Equal(0, dump.DisplayIndex);
        Assert.Equal((uint)7, dump.Type);
    }

    [Fact]
    public void Find_SupplyAllowsSpecializedTab()
    {
        var tabs = new StashTabsView(
        [
            new StashTabsView.Tab("Dump", Type: 7, DisplayIndex: 0),
            new StashTabsView.Tab("Deli", Type: 15, DisplayIndex: 8),
        ]);

        var deli = tabs.Find("DELI", requireGeneralPurpose: false);

        Assert.NotNull(deli);
        Assert.Equal(8, deli.DisplayIndex);
    }

    [Fact]
    public void FindSelected_AcceptsVisibleSameNamedDuplicateInsteadOfLowestIndex()
    {
        var tabs = new StashTabsView(
        [
            new StashTabsView.Tab("Maps", Type: 5, DisplayIndex: 10),
            new StashTabsView.Tab("Maps", Type: 5, DisplayIndex: 20),
        ]);

        var selected = tabs.FindSelected("maps", requireGeneralPurpose: false, displayIndex: 20);

        Assert.NotNull(selected);
        Assert.Equal(20, selected.DisplayIndex);
    }

    [Fact]
    public void FindSelected_StillRejectsSpecializedDuplicateForDeposit()
    {
        var tabs = new StashTabsView(
        [
            new StashTabsView.Tab("Dump", Type: 7, DisplayIndex: 0),
            new StashTabsView.Tab("Dump", Type: 16, DisplayIndex: 31),
        ]);

        Assert.Null(tabs.FindSelected("Dump", requireGeneralPurpose: true, displayIndex: 31));
    }
}
