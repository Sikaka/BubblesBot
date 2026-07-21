using BubblesBot.Core.Snapshot;

namespace BubblesBot.Tests;

public sealed class StashTabStripViewTests
{
    [Fact]
    public void FindExact_IsCaseInsensitiveAndOrdersDuplicateLabelsLeftToRight()
    {
        var view = new StashTabStripView(
        [
            new StashTabStripView.Control(3, "Dump", new(500, 100, 80, 30)),
            new StashTabStripView.Control(1, "Maps", new(120, 100, 80, 30)),
            new StashTabStripView.Control(2, "DUMP", new(40, 100, 80, 30)),
        ]);

        var matches = view.FindExact("dump");

        Assert.Equal(2, matches.Count);
        Assert.Equal((nint)2, matches[0].Element);
        Assert.Equal((nint)3, matches[1].Element);
    }
}
