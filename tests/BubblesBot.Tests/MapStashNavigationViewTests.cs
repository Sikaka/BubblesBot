using BubblesBot.Core.Snapshot;

namespace BubblesBot.Tests;

public sealed class MapStashNavigationViewTests
{
    [Fact]
    public void Selectors_AreOrderedByConcretePanelIndex()
    {
        var view = new MapStashNavigationView(true, 0, null,
        [
            new MapStashNavigationView.Selector(5, "6", 6, new(400, 400, 60, 28)),
            new MapStashNavigationView.Selector(1, "2", 2, new(200, 400, 60, 28)),
        ]);

        Assert.Equal(new[] { 1, 5 }, view.Selectors.Select(x => x.PanelIndex));
    }
}
